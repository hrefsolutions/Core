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
public sealed class AuthController(AuthService authService, AccountService accountService) : ControllerBase
{
    public const int RequestSizeLimitBytes = 64 * 1024;

    /// <summary>
    /// Registers a user for the calling application (only with <c>AllowSelfRegistration</c>) and emails a verification link.
    /// </summary>
    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetApplication(out var applicationId, out var appKey))
        {
            return AppKeyAuthentication.InvalidCredentialsError.ToProblem(this);
        }

        var result = await accountService.RegisterAsync(applicationId, appKey, request, GetCorrelationId(), cancellationToken);

        return result.IsFailure
            ? result.ToProblem(this)
            : StatusCode(StatusCodes.Status201Created, result.Value);
    }

    /// <summary>
    /// Verifies an email with the token from the verification link.
    /// </summary>
    [HttpPost("verify-email")]
    public async Task<IActionResult> VerifyEmail([FromBody] VerifyEmailRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetApplication(out var applicationId, out var appKey))
        {
            return AppKeyAuthentication.InvalidCredentialsError.ToProblem(this);
        }

        var result = await accountService.VerifyEmailAsync(applicationId, appKey, request, GetCorrelationId(), cancellationToken);

        return result.IsFailure ? result.ToProblem(this) : NoContent();
    }

    /// <summary>
    /// Sends a new verification email when the account can receive one. Always 202, except for an invalid page URL.
    /// </summary>
    [HttpPost("resend-verification")]
    public async Task<IActionResult> ResendVerification([FromBody] ResendVerificationRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetApplication(out var applicationId, out var appKey))
        {
            return AppKeyAuthentication.InvalidCredentialsError.ToProblem(this);
        }

        var result = await accountService.ResendVerificationAsync(applicationId, appKey, request, GetCorrelationId(), cancellationToken);

        return result.IsFailure ? result.ToProblem(this) : Accepted();
    }

    /// <summary>
    /// Emails a password reset link when the account can receive one. Always 202, except for an invalid page URL.
    /// </summary>
    [HttpPost("forgot-password")]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetApplication(out var applicationId, out var appKey))
        {
            return AppKeyAuthentication.InvalidCredentialsError.ToProblem(this);
        }

        var result = await accountService.ForgotPasswordAsync(applicationId, appKey, request, GetCorrelationId(), cancellationToken);

        return result.IsFailure ? result.ToProblem(this) : Accepted();
    }

    /// <summary>
    /// Sets a new password with the token from the reset link and ends every session of the user.
    /// </summary>
    [HttpPost("reset-password")]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetApplication(out var applicationId, out _))
        {
            return AppKeyAuthentication.InvalidCredentialsError.ToProblem(this);
        }

        var result = await accountService.ResetPasswordAsync(applicationId, request, cancellationToken);

        return result.IsFailure ? result.ToProblem(this) : NoContent();
    }

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

    private string? GetCorrelationId() => HttpContext.Items[CorrelationIdMiddleware.HeaderName] as string;

    private AuthClientInfo GetClientInfo() =>
        new(HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString());
}
