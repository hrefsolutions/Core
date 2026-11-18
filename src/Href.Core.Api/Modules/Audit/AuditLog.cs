using Href.Core.Api.Common.Domain;

namespace Href.Core.Api.Modules.Audit;

/// <summary>
/// Immutable, append-only audit trail entry (ISO 9001).
/// </summary>
public class AuditLog : Entity
{
    public const int ActionMaxLength = 100;
    public const int EntityTypeMaxLength = 100;
    public const int EntityIdMaxLength = 100;
    public const int ActorTypeMaxLength = 20;
    public const int ActorIdMaxLength = 100;
    public const int AppKeyMaxLength = 50;
    public const int CorrelationIdMaxLength = 64;
    public const int IpAddressMaxLength = 45;
    public const int UserAgentMaxLength = 500;

    protected AuditLog()
    {
    }

    public virtual DateTime OccurredAtUtc { get; protected set; }

    public virtual string Action { get; protected set; } = string.Empty;

    public virtual string? EntityType { get; protected set; }

    public virtual string? EntityId { get; protected set; }

    public virtual AuditActorType ActorType { get; protected set; }

    public virtual string? ActorId { get; protected set; }

    public virtual string? AppKey { get; protected set; }

    public virtual string? OldValues { get; protected set; }

    public virtual string? NewValues { get; protected set; }

    public virtual string? CorrelationId { get; protected set; }

    public virtual string? IpAddress { get; protected set; }

    public virtual string? UserAgent { get; protected set; }

    public static AuditLog Create(
        DateTime occurredAtUtc,
        string action,
        AuditActorType actorType,
        string? actorId = null,
        string? appKey = null,
        string? entityType = null,
        string? entityId = null,
        string? oldValues = null,
        string? newValues = null,
        string? correlationId = null,
        string? ipAddress = null,
        string? userAgent = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);

        return new AuditLog
        {
            OccurredAtUtc = occurredAtUtc,
            Action = Truncate(action.Trim(), ActionMaxLength)!,
            ActorType = actorType,
            ActorId = Truncate(actorId, ActorIdMaxLength),
            AppKey = Truncate(appKey, AppKeyMaxLength),
            EntityType = Truncate(entityType, EntityTypeMaxLength),
            EntityId = Truncate(entityId, EntityIdMaxLength),
            OldValues = oldValues,
            NewValues = newValues,
            CorrelationId = Truncate(correlationId, CorrelationIdMaxLength),
            IpAddress = Truncate(ipAddress, IpAddressMaxLength),
            UserAgent = Truncate(userAgent, UserAgentMaxLength),
        };
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
