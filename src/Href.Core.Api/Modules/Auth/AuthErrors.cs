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
}
