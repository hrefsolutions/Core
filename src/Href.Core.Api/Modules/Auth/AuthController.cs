using Href.Core.Api.Common.Results;
using Href.Core.Api.Common.Web;
using Href.Core.Api.Modules.Applications;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

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
    /// <response code="201">`{ userId }` of the new, still unverified user.</response>
    /// <response code="400">Validation failed: `Auth.InvalidCallbackUrl`, `Users.InvalidEmail`, `Users.InvalidFullName` or `Users.WeakPassword`.</response>
    /// <response code="403">`Auth.SelfRegistrationDisabled` (the calling application does not allow self-registration) or `Auth.ApplicationSuspended`.</response>
    /// <response code="409">`Auth.EmailAlreadyRegistered` (the email exists in any application, in any case).</response>
    [HttpPost("register")]
    [EnableRateLimiting(AuthRateLimiting.StrictPolicy)]
    [ProducesResponseType<RegisterResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
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
    /// <response code="204">The token was consumed and the email is verified.</response>
    /// <response code="400">`Auth.InvalidOneTimeToken`: the token is missing, unknown, used, expired, revoked, of another application or not an email verification token.</response>
    [HttpPost("verify-email")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
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
    /// <response code="202">Accepted; the response does not say whether an email was sent.</response>
    /// <response code="400">`Auth.InvalidCallbackUrl` for an unsafe `verifyEmailUrl`.</response>
    [HttpPost("resend-verification")]
    [EnableRateLimiting(AuthRateLimiting.StrictPolicy)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
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
    /// <response code="202">Accepted; the response does not say whether an email was sent.</response>
    /// <response code="400">`Auth.InvalidCallbackUrl` for an unsafe `resetPasswordUrl`.</response>
    [HttpPost("forgot-password")]
    [EnableRateLimiting(AuthRateLimiting.StrictPolicy)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
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
    /// <response code="204">The password was changed and every active session of the user was revoked.</response>
    /// <response code="400">`Users.WeakPassword` (checked before the token, so the link can be used again) or `Auth.InvalidOneTimeToken`.</response>
    [HttpPost("reset-password")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
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
    /// <response code="200">`{ accessToken, tokenType, expiresIn, refreshToken, refreshTokenExpiresAtUtc }` with `Cache-Control: no-store`.</response>
    /// <response code="400">A missing or empty body returns the framework `ValidationProblemDetails`.</response>
    /// <response code="401">`Auth.InvalidCredentials` for an unknown email or a wrong password.</response>
    /// <response code="403">After a correct password: `Auth.UserLocked`, `Auth.EmailNotVerified` or `Auth.NoAccessToApplication` (in that order); `Auth.ApplicationSuspended` for a suspended calling application.</response>
    [HttpPost("login")]
    [EnableRateLimiting(AuthRateLimiting.StrictPolicy)]
    [ProducesResponseType<TokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
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
    /// <response code="200">The same response as the login, with `Cache-Control: no-store`; the old refresh token is rotated.</response>
    /// <response code="400">A missing or empty body returns the framework `ValidationProblemDetails`.</response>
    /// <response code="401">`Auth.InvalidRefreshToken`, `Auth.RefreshTokenReused` (the whole session is revoked), `Auth.RefreshTokenRevoked` or `Auth.RefreshTokenExpired`.</response>
    /// <response code="403">`Auth.UserLocked`, `Auth.EmailNotVerified` or `Auth.NoAccessToApplication` (the token is not rotated); `Auth.ApplicationSuspended` for a suspended calling application.</response>
    [HttpPost("refresh")]
    [EnableRateLimiting(AuthRateLimiting.RefreshPolicy)]
    [ProducesResponseType<TokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
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
    /// <response code="204">Always, for a known, unknown, blank or already revoked token, so the response says nothing about the token.</response>
    /// <response code="400">A missing or empty body returns the framework `ValidationProblemDetails`.</response>
    [HttpPost("logout")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
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
