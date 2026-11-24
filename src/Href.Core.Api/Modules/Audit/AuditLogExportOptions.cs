using System.ComponentModel.DataAnnotations;

namespace Href.Core.Api.Modules.Audit;

/// <summary>
/// Limits of <c>GET /api/audit-logs/export</c>, bound from the <c>AuditLogs:Export</c> section and validated on startup.
/// </summary>
public sealed class AuditLogExportOptions
{
    public const string SectionName = "AuditLogs:Export";

    /// <summary>
    /// Maximum number of rows one export may contain; a larger match is rejected with <c>AuditLogs.ExportTooLarge</c>.
    /// </summary>
    [Range(1, 100_000)]
    public int MaxRows { get; set; } = 100_000;

    /// <summary>
    /// Rows read from the database per query while streaming the export.
    /// </summary>
    [Range(1, 10_000)]
    public int ChunkSize { get; set; } = 1_000;
}
