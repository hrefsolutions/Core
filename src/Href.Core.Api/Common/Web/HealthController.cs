using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ISession = NHibernate.ISession;

namespace Href.Core.Api.Common.Web;

[ApiController]
[AllowAnonymous]
[Route("health")]
public sealed class HealthController(ISession session, ILogger<HealthController> logger) : ControllerBase
{
    /// <summary>
    /// Returns 200 when the API is alive and PostgreSQL answers <c>SELECT 1</c>; otherwise 503.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        try
        {
            await session.CreateSQLQuery("SELECT 1").UniqueResultAsync<object>(cancellationToken);
            return Ok(new HealthResponse("Healthy", "Healthy"));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Database health check failed");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new HealthResponse("Unhealthy", "Unhealthy"));
        }
    }

    public sealed record HealthResponse(string Status, string Database);
}
