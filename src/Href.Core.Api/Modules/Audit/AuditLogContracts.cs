using System.Text.Json;

namespace Href.Core.Api.Modules.Audit;

/// <summary>
/// Raw audit log filters as received from the query string. Every filter is optional; blank values are ignored.
/// </summary>
public sealed record AuditLogFilterRequest(
    string? From = null,
    string? To = null,
    string? AppKey = null,
    string? ActorType = null,
    string? ActorId = null,
    string? Action = null,
    string? EntityType = null,
    string? EntityId = null,
    string? CorrelationId = null,
    string? ApplicationId = null);

public sealed record ListAuditLogsQuery(AuditLogFilterRequest Filter, int Page, int PageSize);

/// <summary>
/// An audit log entry as returned by the admin API. <c>OldValues</c> and <c>NewValues</c> are the stored JSON snapshots.
/// </summary>
public sealed record AuditLogResponse(
    Guid Id,
    DateTime OccurredAtUtc,
    string Action,
    string? EntityType,
    string? EntityId,
    string ActorType,
    string? ActorId,
    string? AppKey,
    Guid? ApplicationId,
    JsonElement? OldValues,
    JsonElement? NewValues,
    string? CorrelationId,
    string? IpAddress,
    string? UserAgent)
{
    public static AuditLogResponse From(AuditLog auditLog) =>
        new(
            auditLog.Id,
            auditLog.OccurredAtUtc,
            auditLog.Action,
            auditLog.EntityType,
            auditLog.EntityId,
            auditLog.ActorType.ToString(),
            auditLog.ActorId,
            auditLog.AppKey,
            auditLog.ApplicationId,
            ParseJson(auditLog.OldValues),
            ParseJson(auditLog.NewValues),
            auditLog.CorrelationId,
            auditLog.IpAddress,
            auditLog.UserAgent);

    private static JsonElement? ParseJson(string? json) =>
        json is null ? null : JsonSerializer.Deserialize<JsonElement>(json);
}

public sealed record AuditLogPageResponse(IReadOnlyList<AuditLogResponse> Items, int Page, int PageSize, long TotalCount);
