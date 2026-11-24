using Href.Core.Api.Common.Web;
using Href.Core.Api.Modules.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Href.Core.Api.Modules.Authorization;

/// <summary>
/// Admin endpoints for the users that can access one application. Requires the <c>Admin</c> policy (bearer token with <c>core.admin</c>).
/// </summary>
[ApiController]
[Route("api/apps/{appKey}/users")]
[Authorize(Policy = AdminAuthentication.PolicyName)]
public sealed class ApplicationUsersController(ApplicationAccessService accessService) : ControllerBase
{
    /// <summary>
    /// Lists the users with access to the application, ordered by <c>email</c>, with their roles.
    /// </summary>
    /// <response code="200">`{ items, page, pageSize, totalCount }`.</response>
    /// <response code="400">`Authorization.InvalidPage` or `Authorization.InvalidPageSize`; unparsable numbers return the framework `ValidationProblemDetails`.</response>
    /// <response code="404">`Applications.NotFound`.</response>
    [HttpGet]
    [ProducesResponseType<ApplicationUserPageResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
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

    /// <summary>
    /// Sets the roles of a user in the application, granting access when the user had none.
    /// </summary>
    /// <response code="200">The user with its roles in the application.</response>
    /// <response code="400">`Authorization.InvalidRoleIds` or `Authorization.UnknownRoles` (unknown ids or roles of another application, listed); a missing or malformed body returns the framework `ValidationProblemDetails`.</response>
    /// <response code="404">`Applications.NotFound` or `Users.NotFound`.</response>
    /// <response code="409">`Authorization.AccessAlreadyGranted` when a concurrent request granted the access first.</response>
    [HttpPut("{userId:guid}/roles")]
    [ProducesResponseType<ApplicationUserResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
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

    /// <summary>
    /// Revokes the access of a user to the application.
    /// </summary>
    /// <response code="204">The access was revoked.</response>
    /// <response code="404">`Applications.NotFound` or `Authorization.AccessNotFound`.</response>
    [HttpDelete("{userId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> Revoke(string appKey, Guid userId, CancellationToken cancellationToken)
    {
        var result = await accessService.RevokeAsync(appKey, userId, cancellationToken);

        return result.IsFailure
            ? result.ToProblem(this)
            : NoContent();
    }
}
