namespace Href.Core.Api.Modules.Audit;

public sealed record ExportAuditLogsQuery(string? Format, AuditLogFilterRequest Filter);

/// <summary>
/// A validated export ready to stream: rows of <see cref="Filter"/> recorded before <see cref="CutoffUtc"/>, oldest first.
/// </summary>
public sealed record AuditLogExport(
    AuditLogExportFormat Format,
    AuditLogFilter Filter,
    DateTime CutoffUtc,
    long RowCount)
{
    public string ContentType => Format == AuditLogExportFormat.Csv
        ? "text/csv; charset=utf-8"
        : "application/json; charset=utf-8";

    public string FileName => $"audit-logs-{CutoffUtc:yyyyMMdd-HHmmss}.{(Format == AuditLogExportFormat.Csv ? "csv" : "json")}";
}

public enum AuditLogExportFormat
{
    Csv,
    Json,
}

/// <summary>
/// One exported row. <c>OldValues</c> and <c>NewValues</c> are the stored JSON text.
/// </summary>
public sealed record AuditLogExportRow(
    Guid Id,
    DateTime OccurredAtUtc,
    string Action,
    string? EntityType,
    string? EntityId,
    string ActorType,
    string? ActorId,
    string? AppKey,
    string? OldValues,
    string? NewValues,
    string? CorrelationId,
    string? IpAddress,
    string? UserAgent);

/// <summary>
/// Snapshot of <c>AUDIT_LOG_EXPORTED</c>: the format, the validated filters, the cutoff and the number of matching rows.
/// </summary>
public sealed record AuditLogExportAuditSnapshot(
    string Format,
    DateTime? FromUtc,
    DateTime? ToUtc,
    string? AppKey,
    string? ActorType,
    string? ActorId,
    string? Action,
    string? EntityType,
    string? EntityId,
    string? CorrelationId,
    DateTime CutoffUtc,
    long RowCount);
