using Href.Core.Api.Common.Web;
using Href.Core.Api.Modules.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Href.Core.Api.Modules.Authorization;

/// <summary>
/// Admin endpoints for the users that can access one application. Public until admin authentication exists (#53).
/// </summary>
[ApiController]
[Route("api/apps/{appKey}/users")]
[Authorize(Policy = AdminAuthentication.PolicyName)]
public sealed class ApplicationUsersController(ApplicationAccessService accessService) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        string appKey,
        CancellationToken cancellationToken,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = ApplicationAccessService.DefaultPageSize)
    {
        var result = await accessService.ListAsync(appKey, page, pageSize, cancellationToken);

        return result.IsFailure
            ? result.ToProblem(this)
            : Ok(result.Value);
    }

    [HttpPut("{userId:guid}/roles")]
    public async Task<IActionResult> ReplaceRoles(
        string appKey,
        Guid userId,
        [FromBody] ReplaceUserRolesRequest request,
        CancellationToken cancellationToken)
    {
        var result = await accessService.ReplaceRolesAsync(appKey, userId, request, cancellationToken);

        return result.IsFailure
            ? result.ToProblem(this)
            : Ok(result.Value);
    }

    [HttpDelete("{userId:guid}")]
    public async Task<IActionResult> Revoke(string appKey, Guid userId, CancellationToken cancellationToken)
    {
        var result = await accessService.RevokeAsync(appKey, userId, cancellationToken);

        return result.IsFailure
            ? result.ToProblem(this)
            : NoContent();
    }
}
