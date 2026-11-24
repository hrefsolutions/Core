using Href.Core.Api.Common.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Href.Core.Api.Modules.Auth;

[ApiController]
[Route("api/users/{userId:guid}/sessions")]
[Authorize(Policy = AdminAuthentication.PolicyName)]
public sealed class UserSessionsController(UserSessionsService userSessionsService) : ControllerBase
{
    public const string AppKeyQueryName = "appKey";

    /// <summary>
    /// Lists the active sessions of a user, most recently used first.
    /// </summary>
    /// <response code="200">The active sessions (refresh token families whose current token is neither revoked nor expired).</response>
    /// <response code="404">`Users.NotFound`.</response>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<UserSessionResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> List(Guid userId, CancellationToken cancellationToken)
    {
        var result = await userSessionsService.ListAsync(userId, cancellationToken);

        return result.IsFailure
            ? result.ToProblem(this)
            : Ok(result.Value);
    }

    /// <summary>
    /// Revokes every active session of a user, or only those of the application in the <c>appKey</c> query parameter.
    /// The raw query is read on purpose: model binding turns an empty value into <c>null</c>, which would widen the
    /// scope to every application; a present but blank key is passed through and matches no application.
    /// </summary>
    /// <response code="200">`{ revokedSessions }` with the number of revoked sessions (also `0`).</response>
    /// <response code="404">`Users.NotFound`, or `Applications.NotFound` for an unknown, empty or blank `appKey`.</response>
    [HttpDelete]
    [ProducesResponseType<RevokeSessionsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> Revoke(Guid userId, CancellationToken cancellationToken)
    {
        string? appKey = Request.Query.TryGetValue(AppKeyQueryName, out var values) ? values.ToString() : null;
        var result = await userSessionsService.RevokeAsync(userId, appKey, cancellationToken);

        return result.IsFailure
            ? result.ToProblem(this)
            : Ok(result.Value);
    }
}
