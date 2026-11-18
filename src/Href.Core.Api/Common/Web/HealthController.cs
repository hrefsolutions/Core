using Href.Core.Api.Common.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Href.Core.Api.Common.Web;

[ApiController]
[AllowAnonymous]
[Route("health")]
public sealed class HealthController(DatabaseHealthService databaseHealthService) : ControllerBase
{
    /// <summary>
    /// Returns 200 when the API is alive and PostgreSQL answers <c>SELECT 1</c>; otherwise 503.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken) =>
        await databaseHealthService.IsDatabaseHealthyAsync(cancellationToken)
            ? Ok(new HealthResponse("Healthy", "Healthy"))
            : StatusCode(StatusCodes.Status503ServiceUnavailable, new HealthResponse("Unhealthy", "Unhealthy"));

    public sealed record HealthResponse(string Status, string Database);
}
