namespace Href.Core.Api.Modules.Audit;

public sealed record AuditEntry(
    string Action,
    string? EntityType = null,
    string? EntityId = null,
    object? OldValues = null,
    object? NewValues = null,
    AuditActorType? ActorTypeOverride = null,
    string? ActorIdOverride = null,
    string? AppKeyOverride = null);

public interface IAuditService
{
    Task<AuditLog> RecordAsync(AuditEntry entry, CancellationToken cancellationToken = default);
}
