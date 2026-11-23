using Href.Core.Api.Common.Results;
using Href.Core.Api.Modules.Applications;
using Href.Core.Api.Modules.Audit;
using Href.Core.Api.Modules.Users;
using NHibernate.Linq;
using ISession = NHibernate.ISession;

namespace Href.Core.Api.Modules.Auth;

/// <summary>
/// Admin use cases for the sessions (refresh token families) of a user: list the active ones and revoke them all or
/// those of one application. Revocation follows the refresh token write rule: one transaction that takes the per-user
/// advisory lock before reading the tokens.
/// </summary>
public sealed class UserSessionsService(ISession session, IAuditService auditService, TimeProvider timeProvider)
{
    /// <summary>
    /// Lists the active sessions (current token neither revoked nor expired), most recently used first.
    /// </summary>
    public async Task<Result<IReadOnlyList<UserSessionResponse>>> ListAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (!await session.Query<User>().AnyAsync(user => user.Id == userId, cancellationToken))
        {
            return UserErrors.NotFound;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var active = await session.Query<RefreshToken>()
            .Where(token => token.UserId == userId && token.RevokedAtUtc == null && token.ExpiresAtUtc > now)
            .ToListAsync(cancellationToken);

        if (active.Count == 0)
        {
            return Result.Success<IReadOnlyList<UserSessionResponse>>([]);
        }

        var familyIds = active.Select(token => token.FamilyId).Distinct().ToList();
        var starts = await session.Query<RefreshToken>()
            .Where(token => familyIds.Contains(token.FamilyId))
            .GroupBy(token => token.FamilyId)
            .Select(group => new { FamilyId = group.Key, StartedAtUtc = group.Min(token => token.CreatedAtUtc) })
            .ToListAsync(cancellationToken);
        var startsByFamily = starts.ToDictionary(start => start.FamilyId, start => DateTime.SpecifyKind(start.StartedAtUtc, DateTimeKind.Utc));

        var applicationIds = active.Select(token => token.ApplicationId).Distinct().ToList();
        var appKeys = await session.Query<Application>()
            .Where(application => applicationIds.Contains(application.Id))
            .Select(application => new { application.Id, application.AppKey })
            .ToListAsync(cancellationToken);
        var appKeysById = appKeys.ToDictionary(application => application.Id, application => application.AppKey);

        IReadOnlyList<UserSessionResponse> sessions = active
            .Select(token => new UserSessionResponse(
                token.FamilyId,
                token.ApplicationId,
                appKeysById[token.ApplicationId],
                startsByFamily[token.FamilyId],
                token.CreatedAtUtc,
                token.ExpiresAtUtc,
                token.CreatedByIp,
                token.UserAgent))
            .OrderByDescending(item => item.LastRotatedAtUtc)
            .ThenBy(item => item.FamilyId)
            .ToList();

        return Result.Success(sessions);
    }

    /// <summary>
    /// Revokes the active sessions of a user as <see cref="RefreshTokenRevocationReason.AdminRevoked"/>, all of them or
    /// only those of <paramref name="appKey"/> (exact key), and records <c>SESSIONS_REVOKED</c> when at least one was revoked.
    /// </summary>
    public async Task<Result<RevokeSessionsResponse>> RevokeAsync(Guid userId, string? appKey, CancellationToken cancellationToken)
    {
        if (!await session.Query<User>().AnyAsync(user => user.Id == userId, cancellationToken))
        {
            return UserErrors.NotFound;
        }

        Guid? applicationId = null;

        if (appKey is not null)
        {
            applicationId = await session.Query<Application>()
                .Where(application => application.AppKey == appKey)
                .Select(application => (Guid?)application.Id)
                .FirstOrDefaultAsync(cancellationToken);

            if (applicationId is null)
            {
                return ApplicationErrors.NotFound;
            }
        }

        using var transaction = session.BeginTransaction();

        await RefreshTokenWrites.LockUserAsync(session, userId, cancellationToken);
        var revoked = await RefreshTokenWrites.RevokeActiveSessionsAsync(
            session,
            userId,
            applicationId,
            timeProvider.GetUtcNow().UtcDateTime,
            RefreshTokenRevocationReason.AdminRevoked,
            cancellationToken);

        if (revoked > 0)
        {
            await auditService.RecordSessionsRevokedAsync(
                userId,
                appKey,
                RefreshTokenRevocationReason.AdminRevoked,
                revoked,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new RevokeSessionsResponse(revoked);
    }
}
