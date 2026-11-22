using Href.Core.Api.Common.Results;
using Href.Core.Api.Modules.Audit;
using Href.Core.Api.Modules.Authorization;
using Href.Core.Api.Modules.Users;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using NHibernate.Linq;
using ISession = NHibernate.ISession;

namespace Href.Core.Api.Modules.Auth;

/// <summary>
/// Login and refresh for satellite backends authenticated with their M2M credentials. Every transaction that issues,
/// rotates or revokes refresh tokens first takes a per-user PostgreSQL advisory lock and reads the tokens and the user
/// again after it, so reuse detection and user checks cannot race another token write of the same user. This relies on
/// READ COMMITTED (each statement after the lock sees the commits made before it was granted); the only reads before
/// the lock are scalar projections or an entity that is evicted, never cached state used after it.
/// </summary>
public sealed class AuthService(
    ISession session,
    IPasswordHasher<User> passwordHasher,
    DummyPasswordHash dummyPasswordHash,
    AccessTokenFactory accessTokenFactory,
    IOptions<JwtOptions> jwtOptions,
    IAuditService auditService,
    TimeProvider timeProvider)
{
    public const string TokenType = "Bearer";
    public const string RefreshTokenFamilyEntityType = "RefreshTokenFamily";

    /// <summary>
    /// Checks the credentials and issues an access token and a new refresh token family for the calling application.
    /// A malformed or unknown email, an empty password and a wrong password return the same
    /// <see cref="AuthErrors.InvalidCredentials"/> after exactly one password hash verification; the account state is
    /// reported only after a correct password. The password is verified outside any transaction or lock (on a user that is
    /// then evicted), so a password spray cannot hold the per-user lock or a connection; the transaction then takes the
    /// lock, loads the user again <c>FOR UPDATE</c> (serialized with the admin user writes) and verifies again only if the
    /// stored hash changed in between or needs an upgrade.
    /// </summary>
    public async Task<Result<TokenResponse>> LoginAsync(
        Guid applicationId,
        string appKey,
        LoginRequest request,
        AuthClientInfo client,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(client);

        var email = User.NormalizeEmail(request.Email);

        if (email.IsFailure || string.IsNullOrEmpty(request.Password))
        {
            dummyPasswordHash.Verify(request.Password);
            return AuthErrors.InvalidCredentials;
        }

        var candidate = await session.Query<User>()
            .SingleOrDefaultAsync(existing => existing.Email == email.Value, cancellationToken);

        if (candidate is null)
        {
            dummyPasswordHash.Verify(request.Password);
            return AuthErrors.InvalidCredentials;
        }

        var verifiedHash = candidate.PasswordHash;
        var passwordValid = candidate.VerifyPassword(request.Password, passwordHasher);
        var rehashPending = candidate.PasswordHash != verifiedHash;
        session.Evict(candidate);

        if (!passwordValid)
        {
            return AuthErrors.InvalidCredentials;
        }

        using var transaction = session.BeginTransaction();

        await LockUserTokensAsync(candidate.Id, cancellationToken);
        var user = await session.GetAsync<User>(candidate.Id, NHibernate.LockMode.Upgrade, cancellationToken);

        if ((rehashPending || user.PasswordHash != verifiedHash) && !user.VerifyPassword(request.Password, passwordHasher))
        {
            return AuthErrors.InvalidCredentials;
        }

        var accessResult = await CheckUserCanSignInAsync(user, applicationId, cancellationToken);

        if (accessResult.IsFailure)
        {
            return accessResult.Error;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var issued = RefreshToken.Issue(user.Id, applicationId, now, RefreshTokenLifetime, client.IpAddress, client.UserAgent);
        await session.SaveAsync(issued.Token, cancellationToken);
        user.RegisterLogin(now);
        await auditService.RecordAsync(
            new AuditEntry(
                AuditActions.LoginSucceeded,
                EntityType: nameof(User),
                EntityId: user.Id.ToString(),
                NewValues: new LoginAuditSnapshot(user.Id, user.Email, issued.Token.FamilyId),
                ActorTypeOverride: AuditActorType.User,
                ActorIdOverride: user.Id.ToString()),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return CreateResponse(user, appKey, accessResult.Value, issued);
    }

    /// <summary>
    /// Rotates a refresh token of the calling application and issues a new access token with the roles and permissions
    /// read again. A token of another application is treated as unknown. A rotated token presented again is a reuse:
    /// the whole family is revoked as <see cref="RefreshTokenRevocationReason.ReuseDetected"/> and audited.
    /// </summary>
    public async Task<Result<TokenResponse>> RefreshAsync(
        Guid applicationId,
        string appKey,
        RefreshRequest request,
        AuthClientInfo client,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(client);

        var tokenHash = RefreshToken.HashToken(request.RefreshToken);

        if (tokenHash is null)
        {
            return AuthErrors.InvalidRefreshToken;
        }

        using var transaction = session.BeginTransaction();

        var userId = await session.Query<RefreshToken>()
            .Where(candidate => candidate.TokenHash == tokenHash && candidate.ApplicationId == applicationId)
            .Select(candidate => (Guid?)candidate.UserId)
            .SingleOrDefaultAsync(cancellationToken);

        if (userId is null)
        {
            return AuthErrors.InvalidRefreshToken;
        }

        await LockUserTokensAsync(userId.Value, cancellationToken);
        var token = await session.Query<RefreshToken>()
            .SingleAsync(candidate => candidate.TokenHash == tokenHash, cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var rotation = token.Rotate(now, RefreshTokenLifetime, client.IpAddress, client.UserAgent);

        if (rotation.IsFailure)
        {
            if (rotation.Error == AuthErrors.RefreshTokenReused)
            {
                await RevokeFamilyAfterReuseAsync(token, now, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }

            return rotation.Error;
        }

        var user = await session.GetAsync<User>(token.UserId, cancellationToken);
        var accessResult = await CheckUserCanSignInAsync(user, applicationId, cancellationToken);

        if (accessResult.IsFailure)
        {
            return accessResult.Error;
        }

        await session.SaveAsync(rotation.Value.Token, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return CreateResponse(user, appKey, accessResult.Value, rotation.Value);
    }

    private TimeSpan RefreshTokenLifetime => TimeSpan.FromDays(jwtOptions.Value.RefreshTokenDays);

    private async Task<Result<UserApplicationAccess>> CheckUserCanSignInAsync(
        User user,
        Guid applicationId,
        CancellationToken cancellationToken)
    {
        if (user.Status == UserStatus.Locked)
        {
            return AuthErrors.UserLocked;
        }

        if (user.EmailVerifiedAtUtc is null)
        {
            return AuthErrors.EmailNotVerified;
        }

        var access = await session.Query<UserApplicationAccess>()
            .SingleOrDefaultAsync(
                candidate => candidate.UserId == user.Id && candidate.ApplicationId == applicationId,
                cancellationToken);

        if (access is null)
        {
            return AuthErrors.NoAccessToApplication;
        }

        return access;
    }

    private async Task RevokeFamilyAfterReuseAsync(RefreshToken token, DateTime now, CancellationToken cancellationToken)
    {
        var family = await session.Query<RefreshToken>()
            .Where(candidate => candidate.FamilyId == token.FamilyId)
            .ToListAsync(cancellationToken);
        var revokedCount = RefreshToken.RevokeFamily(family, now, RefreshTokenRevocationReason.ReuseDetected);
        await auditService.RecordAsync(
            new AuditEntry(
                AuditActions.RefreshTokenReuseDetected,
                EntityType: RefreshTokenFamilyEntityType,
                EntityId: token.FamilyId.ToString(),
                NewValues: new RefreshTokenReuseAuditSnapshot(token.UserId, token.FamilyId, revokedCount)),
            cancellationToken);
    }

    private TokenResponse CreateResponse(User user, string appKey, UserApplicationAccess access, IssuedRefreshToken refreshToken)
    {
        var accessToken = accessTokenFactory.Create(new AccessTokenSubject(
            user.Id,
            user.Email,
            user.FullName,
            appKey,
            access.GetRoleNames(),
            access.GetPermissionKeys()));

        return new TokenResponse(
            accessToken.Token,
            TokenType,
            accessToken.ExpiresInSeconds,
            refreshToken.PlainToken,
            refreshToken.Token.ExpiresAtUtc);
    }

    private Task LockUserTokensAsync(Guid userId, CancellationToken cancellationToken) =>
        session.CreateSQLQuery("SELECT 1 FROM pg_advisory_xact_lock(hashtextextended('refresh_tokens:' || :userId, 0))")
            .SetParameter("userId", userId.ToString())
            .UniqueResultAsync(cancellationToken);

}
