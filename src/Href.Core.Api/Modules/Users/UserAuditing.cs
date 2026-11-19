using Href.Core.Api.Modules.Audit;

namespace Href.Core.Api.Modules.Users;

public static class UserAuditing
{
    public const string EntityType = "User";

    /// <summary>
    /// Records a user change on the caller's session and transaction. Pass <c>null</c> as <paramref name="before"/>
    /// for a creation. Users are global, so the audit <c>app_key</c> stays the caller's application.
    /// </summary>
    public static Task<AuditLog> RecordUserChangeAsync(
        this IAuditService auditService,
        string action,
        UserAuditSnapshot? before,
        UserAuditSnapshot? after,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auditService);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);

        var entityId = AuditSnapshotIds.Resolve(before?.Id, after?.Id);

        return auditService.RecordAsync(
            new AuditEntry(
                action,
                EntityType: EntityType,
                EntityId: entityId.ToString(),
                OldValues: before,
                NewValues: after),
            cancellationToken);
    }
}
