using Href.Core.Api.Modules.Audit;

namespace Href.Core.Api.Modules.Applications;

/// <summary>
/// Audit snapshot of a client application. It never contains the client secret or its hash; a rotation shows up as a new
/// <c>clientSecretRotatedAtUtc</c>.
/// </summary>
public sealed record ApplicationAuditSnapshot(
    Guid Id,
    string AppKey,
    string Name,
    string? Description,
    string Status,
    bool AllowSelfRegistration,
    DateTime ClientSecretRotatedAtUtc)
{
    /// <summary>
    /// Copies the current values of <paramref name="application"/>.
    /// </summary>
    public static ApplicationAuditSnapshot From(Application application)
    {
        ArgumentNullException.ThrowIfNull(application);

        return new ApplicationAuditSnapshot(
            application.Id,
            application.AppKey,
            application.Name,
            application.Description,
            application.Status.ToString(),
            application.AllowSelfRegistration,
            application.ClientSecretRotatedAtUtc);
    }
}

/// <summary>
/// Snapshot of <c>NOTIFICATIONS_CANCELLED</c>: the pending or in-progress notifications of an application that were
/// cancelled, and why.
/// </summary>
public sealed record NotificationsCancelledAuditSnapshot(
    Guid ApplicationId,
    string AppKey,
    string Reason,
    int CancelledCount,
    IReadOnlyList<Guid> NotificationIds)
{
    public const string ApplicationDeletedReason = "ApplicationDeleted";
}

/// <summary>
/// Audit helper for application changes. It runs on the caller's session and transaction and records the
/// <c>AppKey</c> of the affected application (not the caller's), like the authorization helpers.
/// </summary>
public static class ApplicationAuditing
{
    public const string EntityType = nameof(Application);

    /// <summary>
    /// Records an application change. Pass <c>null</c> as <paramref name="before"/> for a creation and as
    /// <paramref name="after"/> for a deletion.
    /// </summary>
    public static Task<AuditLog> RecordApplicationChangeAsync(
        this IAuditService auditService,
        string action,
        ApplicationAuditSnapshot? before,
        ApplicationAuditSnapshot? after,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auditService);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);

        var current = after ?? before ?? throw new ArgumentException("Either before or after is required.", nameof(after));
        var entityId = AuditSnapshotIds.Resolve(before?.Id, after?.Id);

        return auditService.RecordAsync(
            new AuditEntry(
                action,
                EntityType: EntityType,
                EntityId: entityId.ToString(),
                OldValues: before,
                NewValues: after,
                AppKeyOverride: current.AppKey,
                ApplicationIdOverride: current.Id),
            cancellationToken);
    }

    /// <summary>
    /// Records <c>NOTIFICATIONS_CANCELLED</c> for the notifications of an application cancelled because it was deleted.
    /// </summary>
    public static Task<AuditLog> RecordNotificationsCancelledAsync(
        this IAuditService auditService,
        ApplicationAuditSnapshot application,
        IReadOnlyList<Guid> notificationIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auditService);
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(notificationIds);

        return auditService.RecordAsync(
            new AuditEntry(
                AuditActions.NotificationsCancelled,
                EntityType: EntityType,
                EntityId: application.Id.ToString(),
                NewValues: new NotificationsCancelledAuditSnapshot(
                    application.Id,
                    application.AppKey,
                    NotificationsCancelledAuditSnapshot.ApplicationDeletedReason,
                    notificationIds.Count,
                    notificationIds),
                AppKeyOverride: application.AppKey,
                ApplicationIdOverride: application.Id),
            cancellationToken);
    }
}
