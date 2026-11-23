using Href.Core.Api.Common.Results;
using Href.Core.Api.Common.Web;
using Href.Core.Api.Modules.Applications;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Href.Core.Api.Modules.Auth;

[ApiController]
[Route("api/auth")]
[Authorize(Policy = AppKeyAuthentication.PolicyName)]
[RequestSizeLimit(RequestSizeLimitBytes)]
public sealed class AuthController(AuthService authService) : ControllerBase
{
    public const int RequestSizeLimitBytes = 64 * 1024;

    /// <summary>
    /// Logs a user in for the calling application (taken from the M2M credentials) and returns an access token and a refresh token.
    /// </summary>
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetApplication(out var applicationId, out var appKey))
        {
            return AppKeyAuthentication.InvalidCredentialsError.ToProblem(this);
        }

        var result = await authService.LoginAsync(applicationId, appKey, request, GetClientInfo(), cancellationToken);
        return ToTokenResult(result);
    }

    /// <summary>
    /// Rotates a refresh token of the calling application and returns new tokens.
    /// </summary>
    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh([FromBody] RefreshRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetApplication(out var applicationId, out var appKey))
        {
            return AppKeyAuthentication.InvalidCredentialsError.ToProblem(this);
        }

        var result = await authService.RefreshAsync(applicationId, appKey, request, GetClientInfo(), cancellationToken);
        return ToTokenResult(result);
    }

    /// <summary>
    /// Ends the session of a refresh token of the calling application. Always 204, whether or not the token was known.
    /// </summary>
    [HttpPost("logout")]
    public async Task<IActionResult> Logout([FromBody] RefreshRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetApplication(out var applicationId, out _))
        {
            return AppKeyAuthentication.InvalidCredentialsError.ToProblem(this);
        }

        await authService.LogoutAsync(applicationId, request, cancellationToken);
        return NoContent();
    }

    private IActionResult ToTokenResult(Result<TokenResponse> result)
    {
        Response.Headers.CacheControl = "no-store";
        return result.IsFailure ? result.ToProblem(this) : Ok(result.Value);
    }

    private bool TryGetApplication(out Guid applicationId, out string appKey)
    {
        var id = User.GetAppId();
        appKey = User.GetAppKey() ?? string.Empty;
        applicationId = id ?? Guid.Empty;
        return id is not null && appKey.Length > 0;
    }

    private AuthClientInfo GetClientInfo() =>
        new(HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString());
}
