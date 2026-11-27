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
    /// <summary>
    /// Returns one page of the audit trail, newest first (<c>occurredAtUtc DESC, id DESC</c>), filtered by the optional
    /// query parameters combined with AND.
    /// </summary>
    /// <response code="200">`{ items, page, pageSize, totalCount }` with `Cache-Control: no-store`.</response>
    /// <response code="400">`AuditLogs.InvalidPage`, `AuditLogs.InvalidPageSize`, `AuditLogs.InvalidDate`, `AuditLogs.InvalidRange`, `AuditLogs.InvalidActorType`, `AuditLogs.InvalidApplicationId` or `AuditLogs.InvalidFilter`; unparsable numbers return the framework `ValidationProblemDetails` without `errorCode`.</response>
    [HttpGet]
    [ProducesResponseType<AuditLogPageResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    public async Task<IActionResult> List(
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery] string? appKey,
        [FromQuery] string? applicationId,
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

        var filter = new AuditLogFilterRequest(from, to, appKey, actorType, actorId, action, entityType, entityId, correlationId, applicationId);
        var result = await queryService.ListAsync(new ListAuditLogsQuery(filter, page, pageSize), cancellationToken);

        return result.IsFailure
            ? result.ToProblem(this)
            : Ok(result.Value);
    }

    /// <summary>
    /// Downloads every row matching the filters of <see cref="List"/> (no paging), oldest first, as <c>format=csv</c> or
    /// <c>format=json</c>.
    /// </summary>
    /// <response code="200">The streamed file (`text/csv; charset=utf-8` with BOM, or a JSON array as `application/json; charset=utf-8`) with `Content-Disposition: attachment` and `Cache-Control: no-store`.</response>
    /// <response code="400">`AuditLogs.InvalidFormat`, `AuditLogs.ExportTooLarge`, or the filter codes of the list: `AuditLogs.InvalidDate`, `AuditLogs.InvalidRange`, `AuditLogs.InvalidActorType`, `AuditLogs.InvalidApplicationId` or `AuditLogs.InvalidFilter`.</response>
    [HttpGet("export")]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK, "text/csv", "application/json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    public async Task<IActionResult> Export(
        [FromQuery] string? format,
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery] string? appKey,
        [FromQuery] string? applicationId,
        [FromQuery] string? actorType,
        [FromQuery] string? actorId,
        [FromQuery] string? action,
        [FromQuery] string? entityType,
        [FromQuery] string? entityId,
        [FromQuery] string? correlationId,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        var filter = new AuditLogFilterRequest(from, to, appKey, actorType, actorId, action, entityType, entityId, correlationId, applicationId);
        var result = await exportService.PrepareAsync(new ExportAuditLogsQuery(format, filter), cancellationToken);

        return result.IsFailure
            ? result.ToProblem(this)
            : new AuditLogExportResult(result.Value, exportService);
    }
}
