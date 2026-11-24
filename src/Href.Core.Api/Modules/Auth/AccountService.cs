using System.Globalization;
using Href.Core.Api.Common.Persistence;
using Href.Core.Api.Common.Results;
using Href.Core.Api.Modules.Applications;
using Href.Core.Api.Modules.Audit;
using Href.Core.Api.Modules.Authorization;
using Href.Core.Api.Modules.Notifications;
using Href.Core.Api.Modules.Users;
using Microsoft.AspNetCore.Identity;
using NHibernate.Linq;
using ISession = NHibernate.ISession;

namespace Href.Core.Api.Modules.Auth;

/// <summary>
/// Self-service account flows called by satellite backends with their M2M credentials: registration, email verification,
/// resending the verification email, and password recovery. Emails are queued through <see cref="NotificationService"/> inside the same
/// transaction as the account change, so a failure leaves neither the change nor the email.
/// </summary>
public sealed class AccountService(
    ISession session,
    IPasswordHasher<User> passwordHasher,
    NotificationService notificationService,
    IAuditService auditService,
    TimeProvider timeProvider)
{
    public const string VerifyEmailTemplateKey = "verify-email";
    public const string WelcomeUserTemplateKey = "welcome-user";
    public const string ResetPasswordTemplateKey = "reset-password";

    public static readonly TimeSpan ResendCooldown = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Registers a user for the calling application: the user is created unverified, gets access with the application's
    /// default role (no role if there is none) and receives the <c>verify-email</c> email with a 24 h token.
    /// </summary>
    public async Task<Result<RegisterResponse>> RegisterAsync(
        Guid applicationId,
        string appKey,
        RegisterRequest request,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var application = await session.GetAsync<Application>(applicationId, cancellationToken);

        if (application is null || !application.AllowSelfRegistration)
        {
            return AuthErrors.SelfRegistrationDisabled;
        }

        if (!CallbackUrl.TryParse(request.VerifyEmailUrl, out var verifyEmailUrl))
        {
            return AuthErrors.InvalidCallbackUrl;
        }

        var created = User.Create(request.Email, request.FullName, request.Password, passwordHasher);

        if (created.IsFailure)
        {
            return created.Error;
        }

        var user = created.Value;

        try
        {
            using var transaction = session.BeginTransaction();

            if (await session.Query<User>().AnyAsync(existing => existing.Email == user.Email, cancellationToken))
            {
                return AuthErrors.EmailAlreadyRegistered;
            }

            var defaultRole = await session.Query<Role>()
                .SingleOrDefaultAsync(role => role.ApplicationId == applicationId && role.IsDefault, cancellationToken);
            var roles = defaultRole is null ? Array.Empty<Role>() : new[] { defaultRole };
            var access = UserApplicationAccess.Grant(user.Id, applicationId, roles).Value;
            var issued = UserToken.Issue(
                user.Id,
                applicationId,
                UserTokenPurpose.EmailVerification,
                timeProvider.GetUtcNow().UtcDateTime,
                UserToken.EmailVerificationLifetime);

            await session.SaveAsync(user, cancellationToken);
            await session.SaveAsync(access, cancellationToken);
            await session.SaveAsync(issued.Token, cancellationToken);
            await auditService.RecordUserChangeAsync(AuditActions.UserRegistered, null, UserAuditSnapshot.From(user), cancellationToken);
            await auditService.RecordAccessChangeAsync(AuditActions.AccessGranted, appKey, null, AccessAuditSnapshot.From(access), cancellationToken);

            var queued = await QueueVerificationEmailAsync(appKey, application, user, verifyEmailUrl, issued.PlainToken, correlationId, cancellationToken);

            if (queued.IsFailure)
            {
                return queued.Error;
            }

            await session.FlushAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception exception) when (PostgresErrors.IsUniqueViolation(exception, User.EmailUniqueIndexName))
        {
            return AuthErrors.EmailAlreadyRegistered;
        }

        return new RegisterResponse(user.Id);
    }

    /// <summary>
    /// Verifies the email of the token's user and consumes the token. The token must be an email verification token of
    /// the calling application. Lock order (shared by every account flow): the user row, then the token row, both
    /// <c>FOR UPDATE</c> and read after the lock, so two concurrent uses cannot both succeed and a concurrent resend cannot
    /// deadlock with it. A user that was already verified only gets the token consumed; otherwise <c>EMAIL_VERIFIED</c> is
    /// recorded and, when the user is active, <c>welcome-user</c> is queued.
    /// </summary>
    public async Task<Result> VerifyEmailAsync(
        Guid applicationId,
        string appKey,
        VerifyEmailRequest request,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var tokenHash = UserToken.HashToken(request.Token);

        if (tokenHash is null)
        {
            return AuthErrors.InvalidOneTimeToken;
        }

        using var transaction = session.BeginTransaction();

        var target = await session.Query<UserToken>()
            .Where(candidate => candidate.TokenHash == tokenHash
                && candidate.ApplicationId == applicationId
                && candidate.Purpose == UserTokenPurpose.EmailVerification)
            .Select(candidate => new { candidate.Id, candidate.UserId })
            .SingleOrDefaultAsync(cancellationToken);

        if (target is null)
        {
            return AuthErrors.InvalidOneTimeToken;
        }

        var user = await session.GetAsync<User>(target.UserId, NHibernate.LockMode.Upgrade, cancellationToken);
        var token = await session.GetAsync<UserToken>(target.Id, NHibernate.LockMode.Upgrade, cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var consumed = token.Consume(now);

        if (consumed.IsFailure)
        {
            return consumed.Error;
        }

        var verifiedNow = user.EmailVerifiedAtUtc is null;

        if (verifiedNow)
        {
            var before = UserAuditSnapshot.From(user);
            user.MarkEmailVerified(now);
            await auditService.RecordUserChangeAsync(AuditActions.EmailVerified, before, UserAuditSnapshot.From(user), cancellationToken);
        }

        if (verifiedNow && user.Status == UserStatus.Active)
        {
            var application = await session.GetAsync<Application>(applicationId, cancellationToken);
            var welcome = await notificationService.QueueTemplateAsync(
                appKey,
                new EmailRecipientRequest(user.Email, user.FullName),
                WelcomeUserTemplateKey,
                new Dictionary<string, string> { ["UserName"] = user.FullName, ["AppName"] = application.Name },
                notifyOnFailureTo: null,
                correlationId,
                cancellationToken);

            if (welcome.IsFailure)
            {
                return welcome.Error;
            }
        }

        await session.FlushAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Result.Success();
    }

    /// <summary>
    /// Sends a new verification email and revokes the user's previous verification tokens of every application (verification
    /// is per user). The caller always answers 202: nothing is sent when the email is unknown, the user is locked or
    /// already verified, has no access to the calling application, or got a verification token less than
    /// <see cref="ResendCooldown"/> ago (so a recipient's inbox cannot be flooded). Only an invalid page URL is reported,
    /// because it does not depend on the user. Lock order: the user row, then the token rows.
    /// </summary>
    public async Task<Result> ResendVerificationAsync(
        Guid applicationId,
        string appKey,
        ResendVerificationRequest request,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!CallbackUrl.TryParse(request.VerifyEmailUrl, out var verifyEmailUrl))
        {
            return AuthErrors.InvalidCallbackUrl;
        }

        var email = User.NormalizeEmail(request.Email);

        if (email.IsFailure)
        {
            return Result.Success();
        }

        using var transaction = session.BeginTransaction();

        var userId = await session.Query<User>()
            .Where(candidate => candidate.Email == email.Value)
            .Select(candidate => (Guid?)candidate.Id)
            .SingleOrDefaultAsync(cancellationToken);

        if (userId is null)
        {
            return Result.Success();
        }

        var user = await session.GetAsync<User>(userId.Value, NHibernate.LockMode.Upgrade, cancellationToken);
        var hasAccess = await session.Query<UserApplicationAccess>()
            .AnyAsync(access => access.UserId == user.Id && access.ApplicationId == applicationId, cancellationToken);

        if (user.Status != UserStatus.Active || user.EmailVerifiedAtUtc is not null || !hasAccess)
        {
            return Result.Success();
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var previous = await session.Query<UserToken>()
            .Where(token => token.UserId == user.Id
                && token.Purpose == UserTokenPurpose.EmailVerification
                && token.ConsumedAtUtc == null
                && token.RevokedAtUtc == null)
            .ToListAsync(cancellationToken);

        if (previous.Any(token => token.CreatedAtUtc > now - ResendCooldown))
        {
            return Result.Success();
        }

        foreach (var token in previous)
        {
            token.Revoke(now);
        }

        var issued = UserToken.Issue(user.Id, applicationId, UserTokenPurpose.EmailVerification, now, UserToken.EmailVerificationLifetime);
        await session.SaveAsync(issued.Token, cancellationToken);

        var application = await session.GetAsync<Application>(applicationId, cancellationToken);
        var queued = await QueueVerificationEmailAsync(appKey, application, user, verifyEmailUrl, issued.PlainToken, correlationId, cancellationToken);

        if (queued.IsFailure)
        {
            return queued.Error;
        }

        await session.FlushAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Result.Success();
    }

    /// <summary>
    /// Emails a 30 min password reset link and revokes the user's previous reset tokens of every application. The caller
    /// always answers 202: nothing is sent when the email is unknown, the user is locked or has no access to the calling
    /// application. Only an invalid page URL is reported, because it does not depend on the user. Lock order: the user
    /// row, then the token rows.
    /// </summary>
    public async Task<Result> ForgotPasswordAsync(
        Guid applicationId,
        string appKey,
        ForgotPasswordRequest request,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!CallbackUrl.TryParse(request.ResetPasswordUrl, out var resetPasswordUrl))
        {
            return AuthErrors.InvalidCallbackUrl;
        }

        var email = User.NormalizeEmail(request.Email);

        if (email.IsFailure)
        {
            return Result.Success();
        }

        using var transaction = session.BeginTransaction();

        var userId = await session.Query<User>()
            .Where(candidate => candidate.Email == email.Value)
            .Select(candidate => (Guid?)candidate.Id)
            .SingleOrDefaultAsync(cancellationToken);

        if (userId is null)
        {
            return Result.Success();
        }

        var user = await session.GetAsync<User>(userId.Value, NHibernate.LockMode.Upgrade, cancellationToken);
        var hasAccess = await session.Query<UserApplicationAccess>()
            .AnyAsync(access => access.UserId == user.Id && access.ApplicationId == applicationId, cancellationToken);

        if (user.Status != UserStatus.Active || !hasAccess)
        {
            return Result.Success();
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        await RevokeOpenTokensAsync(user.Id, UserTokenPurpose.PasswordReset, exceptTokenId: null, now, cancellationToken);

        var issued = UserToken.Issue(user.Id, applicationId, UserTokenPurpose.PasswordReset, now, UserToken.PasswordResetLifetime);
        await session.SaveAsync(issued.Token, cancellationToken);
        await auditService.RecordUserChangeAsync(AuditActions.PasswordResetRequested, null, UserAuditSnapshot.From(user), cancellationToken);

        var application = await session.GetAsync<Application>(applicationId, cancellationToken);
        var queued = await notificationService.QueueTemplateAsync(
            appKey,
            new EmailRecipientRequest(user.Email, user.FullName),
            ResetPasswordTemplateKey,
            new Dictionary<string, string>
            {
                ["UserName"] = user.FullName,
                ["AppName"] = application.Name,
                ["ResetUrl"] = CallbackUrl.WithToken(resetPasswordUrl, issued.PlainToken),
                ["ExpiresInMinutes"] = ((int)UserToken.PasswordResetLifetime.TotalMinutes).ToString(CultureInfo.InvariantCulture),
            },
            notifyOnFailureTo: null,
            correlationId,
            cancellationToken);

        if (queued.IsFailure)
        {
            return queued.Error;
        }

        await session.FlushAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Result.Success();
    }

    /// <summary>
    /// Sets a new password with a reset token of the calling application: regenerates the security stamp, consumes the
    /// token, revokes the user's other reset tokens and every session of every application (<c>PasswordReset</c>), and
    /// records <c>PASSWORD_RESET</c> (plus <c>SESSIONS_REVOKED</c> when a session was revoked). A weak password is rejected
    /// before the token is touched, so the link can be used again. Lock order, the same as login and refresh: the per-user
    /// refresh token advisory lock, then the user row, then the token row, both <c>FOR UPDATE</c> and read after the lock.
    /// </summary>
    public async Task<Result> ResetPasswordAsync(
        Guid applicationId,
        ResetPasswordRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var tokenHash = UserToken.HashToken(request.Token);

        if (tokenHash is null)
        {
            return AuthErrors.InvalidOneTimeToken;
        }

        var policy = PasswordPolicy.Validate(request.NewPassword);

        if (policy.IsFailure)
        {
            return policy.Error;
        }

        using var transaction = session.BeginTransaction();

        var target = await session.Query<UserToken>()
            .Where(candidate => candidate.TokenHash == tokenHash
                && candidate.ApplicationId == applicationId
                && candidate.Purpose == UserTokenPurpose.PasswordReset)
            .Select(candidate => new { candidate.Id, candidate.UserId })
            .SingleOrDefaultAsync(cancellationToken);

        if (target is null)
        {
            return AuthErrors.InvalidOneTimeToken;
        }

        await RefreshTokenWrites.LockUserAsync(session, target.UserId, cancellationToken);
        var user = await session.GetAsync<User>(target.UserId, NHibernate.LockMode.Upgrade, cancellationToken);
        var token = await session.GetAsync<UserToken>(target.Id, NHibernate.LockMode.Upgrade, cancellationToken);

        if (user.Status != UserStatus.Active)
        {
            return AuthErrors.InvalidOneTimeToken;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var consumed = token.Consume(now);

        if (consumed.IsFailure)
        {
            return consumed.Error;
        }

        var before = UserAuditSnapshot.From(user);
        var changed = user.SetPassword(request.NewPassword, passwordHasher);

        if (changed.IsFailure)
        {
            return changed.Error;
        }

        await RevokeOpenTokensAsync(user.Id, UserTokenPurpose.PasswordReset, token.Id, now, cancellationToken);
        var revokedSessions = await RefreshTokenWrites.RevokeActiveSessionsAsync(
            session,
            user.Id,
            applicationId: null,
            now,
            RefreshTokenRevocationReason.PasswordReset,
            cancellationToken);

        await auditService.RecordUserChangeAsync(AuditActions.PasswordReset, before, UserAuditSnapshot.From(user), cancellationToken);

        if (revokedSessions > 0)
        {
            await auditService.RecordSessionsRevokedAsync(user.Id, null, RefreshTokenRevocationReason.PasswordReset, revokedSessions, cancellationToken);
        }

        await session.FlushAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Result.Success();
    }

    private async Task RevokeOpenTokensAsync(
        Guid userId,
        UserTokenPurpose purpose,
        Guid? exceptTokenId,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var open = await session.Query<UserToken>()
            .Where(token => token.UserId == userId
                && token.Purpose == purpose
                && token.ConsumedAtUtc == null
                && token.RevokedAtUtc == null)
            .ToListAsync(cancellationToken);

        foreach (var token in open.Where(token => token.Id != exceptTokenId))
        {
            token.Revoke(nowUtc);
        }
    }

    private Task<Result<Notification>> QueueVerificationEmailAsync(
        string appKey,
        Application application,
        User user,
        Uri verifyEmailUrl,
        string plainToken,
        string? correlationId,
        CancellationToken cancellationToken) =>
        notificationService.QueueTemplateAsync(
            appKey,
            new EmailRecipientRequest(user.Email, user.FullName),
            VerifyEmailTemplateKey,
            new Dictionary<string, string>
            {
                ["UserName"] = user.FullName,
                ["AppName"] = application.Name,
                ["VerificationUrl"] = CallbackUrl.WithToken(verifyEmailUrl, plainToken),
                ["ExpiresInHours"] = ((int)UserToken.EmailVerificationLifetime.TotalHours).ToString(CultureInfo.InvariantCulture),
            },
            notifyOnFailureTo: null,
            correlationId,
            cancellationToken);
}
