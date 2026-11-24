using Href.Core.Api.Common.Results;

namespace Href.Core.Api.Modules.Auth;

public static class AuthErrors
{
    public static readonly Error InvalidCredentials = Error.Unauthorized(
        "Auth.InvalidCredentials",
        "The email or the password is incorrect.");

    public static readonly Error EmailNotVerified = Error.Forbidden(
        "Auth.EmailNotVerified",
        "The email of the user is not verified.");

    public static readonly Error UserLocked = Error.Forbidden(
        "Auth.UserLocked",
        "The user is locked.");

    public static readonly Error NoAccessToApplication = Error.Forbidden(
        "Auth.NoAccessToApplication",
        "The user has no access to the calling application.");

    public static readonly Error SelfRegistrationDisabled = Error.Forbidden(
        "Auth.SelfRegistrationDisabled",
        "The calling application does not allow self-registration.");

    public static readonly Error EmailAlreadyRegistered = Error.Conflict(
        "Auth.EmailAlreadyRegistered",
        "A user with this email is already registered.");

    public static readonly Error InvalidCallbackUrl = Error.Validation(
        "Auth.InvalidCallbackUrl",
        "The page URL must be an absolute https URL (http only for localhost) of up to 2048 characters, without user info, fragment or a 'token' query parameter.");

    public static readonly Error InvalidOneTimeToken = Error.Validation(
        "Auth.InvalidOneTimeToken",
        "The token is invalid, expired or already used.");

    public static readonly Error InvalidRefreshToken = Error.Unauthorized(
        "Auth.InvalidRefreshToken",
        "The refresh token is missing or unknown.");

    public static readonly Error RefreshTokenReused = Error.Unauthorized(
        "Auth.RefreshTokenReused",
        "The refresh token was already used; the session has been revoked.");

    public static readonly Error RefreshTokenRevoked = Error.Unauthorized(
        "Auth.RefreshTokenRevoked",
        "The refresh token has been revoked.");

    public static readonly Error RefreshTokenExpired = Error.Unauthorized(
        "Auth.RefreshTokenExpired",
        "The refresh token has expired.");

    /// <summary>
    /// Too many login attempts for one account (<see cref="LoginAttemptThrottle"/>). It uses the same code as the IP rate
    /// limits, so satellites handle a single <c>429</c> error.
    /// </summary>
    public static Error LoginAttemptsExceeded(TimeSpan retryAfter) => Error.TooManyRequests(
        AuthRateLimiting.ErrorCode,
        "Too many login attempts for this account. Try again later.",
        retryAfter);
}
