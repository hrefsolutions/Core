using System.Globalization;
using Href.Core.Api.Common.Domain;
using Href.Core.Api.Common.Results;

namespace Href.Core.Api.Modules.Audit;

/// <summary>
/// Validated audit log filters, combined with AND. Dates are inclusive UTC bounds (a date-only <c>to</c> covers that whole
/// day); text filters match exactly after trimming. Only <see cref="Create"/> builds one, so every instance is validated.
/// </summary>
public sealed class AuditLogFilter
{
    private const string DateOnlyFormat = "yyyy-MM-dd";

    private static readonly TimeSpan LastMicrosecondOfDay = TimeSpan.FromDays(1) - TimeSpan.FromMicroseconds(1);

    private static readonly string[] DateTimeFormats =
    [
        "yyyy-MM-dd'T'HH:mmK",
        "yyyy-MM-dd'T'HH:mm:ssK",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
    ];

    private AuditLogFilter(
        DateTime? fromUtc,
        DateTime? toUtc,
        string? appKey,
        AuditActorType? actorType,
        string? actorId,
        string? action,
        string? entityType,
        string? entityId,
        string? correlationId,
        Guid? applicationId)
    {
        FromUtc = fromUtc;
        ToUtc = toUtc;
        AppKey = appKey;
        ActorType = actorType;
        ActorId = actorId;
        Action = action;
        EntityType = entityType;
        EntityId = entityId;
        CorrelationId = correlationId;
        ApplicationId = applicationId;
    }

    public DateTime? FromUtc { get; }

    /// <summary>
    /// Inclusive upper bound. A date-only <c>to</c> becomes the last microsecond of that day (PostgreSQL stores microseconds).
    /// </summary>
    public DateTime? ToUtc { get; }

    public string? AppKey { get; }

    public AuditActorType? ActorType { get; }

    public string? ActorId { get; }

    public string? Action { get; }

    public string? EntityType { get; }

    public string? EntityId { get; }

    public string? CorrelationId { get; }

    /// <summary>
    /// Exact application id. Use it, not <see cref="AppKey"/>, to read the history of one application: a key freed by a
    /// deleted application can be taken by a new one, and the key filter returns the entries of both.
    /// </summary>
    public Guid? ApplicationId { get; }

    public static Result<AuditLogFilter> Create(AuditLogFilterRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!TryParseDate(request.From, endOfDay: false, out var fromUtc)
            || !TryParseDate(request.To, endOfDay: true, out var toUtc))
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

        if (!TryParseApplicationId(request.ApplicationId, out var applicationId))
        {
            return AuditLogErrors.InvalidApplicationId;
        }

        return new AuditLogFilter(fromUtc, toUtc, appKey, actorType, actorId, action, entityType, entityId, correlationId, applicationId);
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

        if (ApplicationId is { } applicationId)
        {
            query = query.Where(auditLog => auditLog.ApplicationId == applicationId);
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

    private static bool TryParseDate(string? value, bool endOfDay, out DateTime? utc)
    {
        utc = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var trimmed = value.Trim();

        if (DateTime.TryParseExact(trimmed, DateOnlyFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            var startOfDay = DateTime.SpecifyKind(date, DateTimeKind.Utc);
            utc = endOfDay ? startOfDay + LastMicrosecondOfDay : startOfDay;
            return true;
        }

        if (!DateTimeOffset.TryParseExact(
                trimmed,
                DateTimeFormats,
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

    /// <summary>
    /// Accepts only the canonical form (<c>xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx</c>); blank means no filter.
    /// </summary>
    private static bool TryParseApplicationId(string? value, out Guid? applicationId)
    {
        applicationId = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (!Guid.TryParseExact(value.Trim(), "D", out var parsed) || parsed == Guid.Empty)
        {
            return false;
        }

        applicationId = parsed;
        return true;
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
