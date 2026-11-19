using Href.Core.Api.Modules.Audit;

namespace Href.Core.Api.Modules.Authorization;

/// <summary>
/// Audit helpers for roles, permissions and accesses. They run on the caller's session and transaction, and record the
/// <c>AppKey</c> of the affected application (not the caller's) so each application can read its own authorization history.
/// Pass <c>null</c> as <c>before</c> for a creation and as <c>after</c> for a deletion.
/// </summary>
public static class AuthorizationAuditing
{
    public const string RoleEntityType = "Role";
    public const string PermissionEntityType = "Permission";
    public const string AccessEntityType = "UserApplicationAccess";

    public static Task<AuditLog> RecordRoleChangeAsync(
        this IAuditService auditService,
        string action,
        string appKey,
        RoleAuditSnapshot? before,
        RoleAuditSnapshot? after,
        CancellationToken cancellationToken = default) =>
        RecordAsync(auditService, action, appKey, RoleEntityType, before?.Id, after?.Id, before, after, cancellationToken);

    public static Task<AuditLog> RecordPermissionChangeAsync(
        this IAuditService auditService,
        string action,
        string appKey,
        PermissionAuditSnapshot? before,
        PermissionAuditSnapshot? after,
        CancellationToken cancellationToken = default) =>
        RecordAsync(auditService, action, appKey, PermissionEntityType, before?.Id, after?.Id, before, after, cancellationToken);

    public static Task<AuditLog> RecordAccessChangeAsync(
        this IAuditService auditService,
        string action,
        string appKey,
        AccessAuditSnapshot? before,
        AccessAuditSnapshot? after,
        CancellationToken cancellationToken = default) =>
        RecordAsync(auditService, action, appKey, AccessEntityType, before?.Id, after?.Id, before, after, cancellationToken);

    private static Task<AuditLog> RecordAsync(
        IAuditService auditService,
        string action,
        string appKey,
        string entityType,
        Guid? beforeId,
        Guid? afterId,
        object? before,
        object? after,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auditService);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(appKey);

        var entityId = AuditSnapshotIds.Resolve(beforeId, afterId);

        return auditService.RecordAsync(
            new AuditEntry(
                action,
                EntityType: entityType,
                EntityId: entityId.ToString(),
                OldValues: before,
                NewValues: after,
                AppKeyOverride: appKey),
            cancellationToken);
    }
}
