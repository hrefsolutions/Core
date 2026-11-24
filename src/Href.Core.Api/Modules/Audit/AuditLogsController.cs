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
public sealed class AuditLogsController(AuditLogQueryService queryService) : ControllerBase
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
}
