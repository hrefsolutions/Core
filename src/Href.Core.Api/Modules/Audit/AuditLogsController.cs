using Href.Core.Api.Common.Web;
using Href.Core.Api.Modules.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Href.Core.Api.Modules.Audit;

/// <summary>
/// Admin read access to the immutable audit trail (ISO 9001). Responses are never cached.
/// </summary>
[ApiController]
[Route("api/audit-logs")]
[Authorize(Policy = AdminAuthentication.PolicyName)]
public sealed class AuditLogsController(
    AuditLogQueryService queryService,
    AuditLogExportService exportService) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery] string? appKey,
        [FromQuery] string? actorType,
        [FromQuery] string? actorId,
        [FromQuery] string? action,
        [FromQuery] string? entityType,
        [FromQuery] string? entityId,
        [FromQuery] string? correlationId,
        CancellationToken cancellationToken,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = AuditLogQueryService.DefaultPageSize)
    {
        Response.Headers.CacheControl = "no-store";

        var filter = new AuditLogFilterRequest(from, to, appKey, actorType, actorId, action, entityType, entityId, correlationId);
        var result = await queryService.ListAsync(new ListAuditLogsQuery(filter, page, pageSize), cancellationToken);

        return result.IsFailure
            ? result.ToProblem(this)
            : Ok(result.Value);
    }

    /// <summary>
    /// Downloads every row matching the filters of <see cref="List"/> (no paging), oldest first, as <c>format=csv</c> or
    /// <c>format=json</c>.
    /// </summary>
    [HttpGet("export")]
    public async Task<IActionResult> Export(
        [FromQuery] string? format,
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery] string? appKey,
        [FromQuery] string? actorType,
        [FromQuery] string? actorId,
        [FromQuery] string? action,
        [FromQuery] string? entityType,
        [FromQuery] string? entityId,
        [FromQuery] string? correlationId,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        var filter = new AuditLogFilterRequest(from, to, appKey, actorType, actorId, action, entityType, entityId, correlationId);
        var result = await exportService.PrepareAsync(new ExportAuditLogsQuery(format, filter), cancellationToken);

        return result.IsFailure
            ? result.ToProblem(this)
            : new AuditLogExportResult(result.Value, exportService);
    }
}
