using Href.Core.Api.Common.Results;

namespace Href.Core.Api.Modules.Auth;

public static class AuthErrors
{
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
