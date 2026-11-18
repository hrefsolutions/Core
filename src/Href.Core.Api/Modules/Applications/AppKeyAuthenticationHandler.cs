using System.Security.Claims;
using System.Text.Encodings.Web;
using Href.Core.Api.Common.Results;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Serilog.Context;

namespace Href.Core.Api.Modules.Applications;

/// <summary>
/// Authenticates machine-to-machine requests sent by satellite backends using <c>X-App-Key</c> and <c>X-App-Secret</c>.
/// </summary>
public sealed class AppKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ApplicationService applicationService,
    IProblemDetailsService problemDetailsService) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private const string FailureErrorItemKey = "Href.Core.AppKeyAuthError";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var hasAppKey = Request.Headers.TryGetValue(AppKeyAuthentication.AppKeyHeaderName, out var appKeyValues);
        var hasAppSecret = Request.Headers.TryGetValue(AppKeyAuthentication.AppSecretHeaderName, out var appSecretValues);

        if (!hasAppKey && !hasAppSecret)
        {
            return AuthenticateResult.NoResult();
        }

        var appKey = appKeyValues.ToString().Trim();
        var appSecret = appSecretValues.ToString();

        if (string.IsNullOrEmpty(appKey) || string.IsNullOrEmpty(appSecret))
        {
            Context.Items[FailureErrorItemKey] = AppKeyAuthentication.InvalidCredentialsError;
            return AuthenticateResult.Fail(AppKeyAuthentication.InvalidCredentialsError.Message);
        }

        var authResult = await applicationService.AuthenticateAsync(appKey, appSecret, Context.RequestAborted);

        if (authResult.IsFailure)
        {
            Context.Items[FailureErrorItemKey] = authResult.Error;
            return AuthenticateResult.Fail(authResult.Error.Message);
        }

        var application = authResult.Value;
        var claims = new[]
        {
            new Claim(AppKeyAuthentication.AppIdClaimType, application.Id.ToString()),
            new Claim(AppKeyAuthentication.AppKeyClaimType, application.AppKey),
        };
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        Context.Response.RegisterForDispose(LogContext.PushProperty("AppKey", application.AppKey));

        return AuthenticateResult.Success(ticket);
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        if (Context.Items.TryGetValue(FailureErrorItemKey, out var item)
            && item is Error { Type: ErrorType.Forbidden } forbiddenError)
        {
            await WriteProblemAsync(
                StatusCodes.Status403Forbidden,
                "Forbidden.",
                "https://tools.ietf.org/html/rfc9110#section-15.5.4",
                forbiddenError);
            return;
        }

        await WriteProblemAsync(
            StatusCodes.Status401Unauthorized,
            "Unauthorized.",
            "https://tools.ietf.org/html/rfc9110#section-15.5.2",
            AppKeyAuthentication.InvalidCredentialsError);
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        WriteProblemAsync(
            StatusCodes.Status403Forbidden,
            "Forbidden.",
            "https://tools.ietf.org/html/rfc9110#section-15.5.4",
            AppKeyAuthentication.ApplicationSuspendedError);

    private async Task WriteProblemAsync(int statusCode, string title, string type, Error error)
    {
        Response.StatusCode = statusCode;

        var problemDetails = new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Type = type,
            Detail = error.Message,
        };
        problemDetails.Extensions["errorCode"] = error.Code;

        await problemDetailsService.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = Context,
            ProblemDetails = problemDetails,
        });
    }
}
