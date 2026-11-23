using Href.Core.Api.Common.Web;
using Microsoft.AspNetCore.Mvc;

namespace Href.Core.Api.Modules.Auth;

[ApiController]
[Route("api/users/{userId:guid}/sessions")]
public sealed class UserSessionsController(UserSessionsService userSessionsService) : ControllerBase
{
    public const string AppKeyQueryName = "appKey";

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
    /// Revokes every active session of a user, or only those of the application in the <c>appKey</c> query parameter.
    /// The raw query is read on purpose: model binding turns an empty value into <c>null</c>, which would widen the
    /// scope to every application; a present but blank key is passed through and matches no application.
    /// </summary>
    [HttpDelete]
    public async Task<IActionResult> Revoke(Guid userId, CancellationToken cancellationToken)
    {
        string? appKey = Request.Query.TryGetValue(AppKeyQueryName, out var values) ? values.ToString() : null;
        var result = await userSessionsService.RevokeAsync(userId, appKey, cancellationToken);

        return result.IsFailure
            ? result.ToProblem(this)
            : Ok(result.Value);
    }
}
