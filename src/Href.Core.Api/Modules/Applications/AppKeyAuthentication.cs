using Href.Core.Api.Common.Results;

namespace Href.Core.Api.Modules.Applications;

public static class AppKeyAuthentication
{
    public const string SchemeName = "AppKey";
    public const string PolicyName = "Application";
    public const string AppKeyHeaderName = "X-App-Key";
    public const string AppSecretHeaderName = "X-App-Secret";
    public const string AppKeyClaimType = "app_key";
    public const string AppIdClaimType = "app_id";

    public static readonly Error InvalidCredentialsError = Error.Unauthorized(
        "Auth.InvalidAppCredentials",
        "Valid X-App-Key and X-App-Secret headers are required.");

    public static readonly Error ApplicationSuspendedError = Error.Forbidden(
        "Auth.ApplicationSuspended",
        "The client application is suspended.");
}
