using System.Globalization;
using Href.Core.Api.Common.Domain;
using Href.Core.Api.Common.Results;

namespace Href.Core.Api.Modules.Audit;

/// <summary>
/// Validated audit log filters, combined with AND. Dates are inclusive UTC bounds; text filters match exactly after trimming.
/// </summary>
public sealed record AuditLogFilter(
    DateTime? FromUtc,
    DateTime? ToUtc,
    string? AppKey,
    AuditActorType? ActorType,
    string? ActorId,
    string? Action,
    string? EntityType,
    string? EntityId,
    string? CorrelationId)
{
    private static readonly string[] DateFormats =
    [
        "yyyy-MM-dd",
        "yyyy-MM-dd'T'HH:mmK",
        "yyyy-MM-dd'T'HH:mm:ssK",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
    ];

    public static Result<AuditLogFilter> Create(AuditLogFilterRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!TryParseDate(request.From, out var fromUtc) || !TryParseDate(request.To, out var toUtc))
        {
            return AuditLogErrors.InvalidDate;
        }

        if (fromUtc is not null && toUtc is not null && fromUtc > toUtc)
        {
            return AuditLogErrors.InvalidRange;
        }

        if (!TryParseActorType(request.ActorType, out var actorType))
        {
            return AuditLogErrors.InvalidActorType;
        }

        if (!TryNormalize(request.AppKey, AuditLog.AppKeyMaxLength, out var appKey)
            || !TryNormalize(request.ActorId, AuditLog.ActorIdMaxLength, out var actorId)
            || !TryNormalize(request.Action, AuditLog.ActionMaxLength, out var action)
            || !TryNormalize(request.EntityType, AuditLog.EntityTypeMaxLength, out var entityType)
            || !TryNormalize(request.EntityId, AuditLog.EntityIdMaxLength, out var entityId)
            || !TryNormalize(request.CorrelationId, AuditLog.CorrelationIdMaxLength, out var correlationId))
        {
            return AuditLogErrors.InvalidFilter;
        }

        return new AuditLogFilter(fromUtc, toUtc, appKey, actorType, actorId, action, entityType, entityId, correlationId);
    }

    public IQueryable<AuditLog> Apply(IQueryable<AuditLog> query)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (FromUtc is { } fromUtc)
        {
            query = query.Where(auditLog => auditLog.OccurredAtUtc >= fromUtc);
        }

        if (ToUtc is { } toUtc)
        {
            query = query.Where(auditLog => auditLog.OccurredAtUtc <= toUtc);
        }

        if (AppKey is { } appKey)
        {
            query = query.Where(auditLog => auditLog.AppKey == appKey);
        }

        if (ActorType is { } actorType)
        {
            query = query.Where(auditLog => auditLog.ActorType == actorType);
        }

        if (ActorId is { } actorId)
        {
            query = query.Where(auditLog => auditLog.ActorId == actorId);
        }

        if (Action is { } action)
        {
            query = query.Where(auditLog => auditLog.Action == action);
        }

        if (EntityType is { } entityType)
        {
            query = query.Where(auditLog => auditLog.EntityType == entityType);
        }

        if (EntityId is { } entityId)
        {
            query = query.Where(auditLog => auditLog.EntityId == entityId);
        }

        if (CorrelationId is { } correlationId)
        {
            query = query.Where(auditLog => auditLog.CorrelationId == correlationId);
        }

        return query;
    }

    private static bool TryParseDate(string? value, out DateTime? utc)
    {
        utc = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (!DateTimeOffset.TryParseExact(
                value.Trim(),
                DateFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            return false;
        }

        utc = parsed.UtcDateTime;
        return true;
    }

    private static bool TryParseActorType(string? value, out AuditActorType? actorType)
    {
        actorType = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var trimmed = value.Trim();

        foreach (var candidate in Enum.GetValues<AuditActorType>())
        {
            if (string.Equals(candidate.ToString(), trimmed, StringComparison.Ordinal))
            {
                actorType = candidate;
                return true;
            }
        }

        return false;
    }

    private static bool TryNormalize(string? value, int maxLength, out string? normalized)
    {
        normalized = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var trimmed = value.Trim();

        if (trimmed.Length > maxLength || TextRules.HasControlOrLineSeparator(trimmed))
        {
            return false;
        }

        normalized = trimmed;
        return true;
    }
}
