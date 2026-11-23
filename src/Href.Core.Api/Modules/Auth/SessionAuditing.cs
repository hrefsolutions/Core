using Href.Core.Api.Modules.Audit;
using Href.Core.Api.Modules.Users;

namespace Href.Core.Api.Modules.Auth;

/// <summary>
/// Audit helper for bulk session revocations (admin revoke, user lock and, later, password reset).
/// </summary>
public static class SessionAuditing
{
    /// <summary>
    /// Records <c>SESSIONS_REVOKED</c> for a user (<c>EntityType = User</c>) in the caller's transaction.
    /// </summary>
    public static Task RecordSessionsRevokedAsync(
        this IAuditService auditService,
        Guid userId,
        string? appKey,
        RefreshTokenRevocationReason reason,
        int revokedSessions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auditService);

        return auditService.RecordAsync(
            new AuditEntry(
                AuditActions.SessionsRevoked,
                EntityType: nameof(User),
                EntityId: userId.ToString(),
                NewValues: new SessionsRevokedAuditSnapshot(userId, appKey, reason.ToString(), revokedSessions)),
            cancellationToken);
    }
}
