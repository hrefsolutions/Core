using Href.Core.Api.Common.Web;
using Microsoft.AspNetCore.Mvc;

namespace Href.Core.Api.Modules.Auth;

[ApiController]
[Route("api/users/{userId:guid}/sessions")]
public sealed class UserSessionsController(UserSessionsService userSessionsService) : ControllerBase
{
    /// <summary>
    /// Lists the active sessions of a user, most recently used first.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List(Guid userId, CancellationToken cancellationToken)
    {
        var result = await userSessionsService.ListAsync(userId, cancellationToken);

        return result.IsFailure
            ? result.ToProblem(this)
            : Ok(result.Value);
    }

    /// <summary>
    /// Revokes every active session of a user, or only those of the application <paramref name="appKey"/>.
    /// </summary>
    [HttpDelete]
    public async Task<IActionResult> Revoke(Guid userId, [FromQuery] string? appKey, CancellationToken cancellationToken)
    {
        var result = await userSessionsService.RevokeAsync(userId, appKey, cancellationToken);

        return result.IsFailure
            ? result.ToProblem(this)
            : Ok(result.Value);
    }
}
