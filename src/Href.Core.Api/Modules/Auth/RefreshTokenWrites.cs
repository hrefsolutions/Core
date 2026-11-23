using NHibernate.Linq;
using ISession = NHibernate.ISession;

namespace Href.Core.Api.Modules.Auth;

/// <summary>
/// Shared persistence steps for every transaction that issues, rotates or revokes refresh tokens. Callers take
/// <see cref="LockUserAsync"/> first, inside their transaction, and read tokens and the user only after it. When the
/// user row is also locked (<c>FOR UPDATE</c>), the advisory lock comes first, so two writers never wait on each other in
/// opposite order.
/// </summary>
internal static class RefreshTokenWrites
{
    /// <summary>
    /// Takes the per-user transaction-scoped advisory lock that serializes the refresh token writes of one user.
    /// </summary>
    public static Task LockUserAsync(ISession session, Guid userId, CancellationToken cancellationToken) =>
        session.CreateSQLQuery("SELECT 1 FROM pg_advisory_xact_lock(hashtextextended('refresh_tokens:' || :userId, 0))")
            .SetParameter("userId", userId.ToString())
            .UniqueResultAsync(cancellationToken);

    /// <summary>
    /// Revokes the active sessions (tokens neither revoked nor expired) of a user, optionally only for one application,
    /// and returns how many were revoked. Each family has at most one unrevoked token, so the count is the number of
    /// sessions. The caller must hold <see cref="LockUserAsync"/>.
    /// </summary>
    public static async Task<int> RevokeActiveSessionsAsync(
        ISession session,
        Guid userId,
        Guid? applicationId,
        DateTime nowUtc,
        RefreshTokenRevocationReason reason,
        CancellationToken cancellationToken)
    {
        var query = session.Query<RefreshToken>()
            .Where(token => token.UserId == userId && token.RevokedAtUtc == null && token.ExpiresAtUtc > nowUtc);

        if (applicationId is { } id)
        {
            query = query.Where(token => token.ApplicationId == id);
        }

        var tokens = await query.ToListAsync(cancellationToken);
        return tokens.Count(token => token.Revoke(nowUtc, reason));
    }
}
