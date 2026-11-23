using System.Security.Claims;
using Href.Core.Api.Common.Results;
using Href.Core.Api.Modules.Applications;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Href.Core.Api.Modules.Auth;

/// <summary>
/// Bearer authentication of the admin endpoints with the Core's own access tokens: issuer <c>Jwt:Issuer</c>, audience
/// <c>core</c>, <c>typ: at+jwt</c>, RS256 with the published keys, and policy <see cref="PolicyName"/> requiring the
/// <c>core.admin</c> permission. Failures are RFC 7807 responses with <c>errorCode</c>: <c>401 Auth.InvalidToken</c>
/// without a valid token and <c>403 Auth.AdminRequired</c> with a valid token lacking <c>core.admin</c>.
/// </summary>
public static class AdminAuthentication
{
    public const string SchemeName = JwtBearerDefaults.AuthenticationScheme;
    public const string PolicyName = "Admin";

    public static readonly Error InvalidTokenError = Error.Unauthorized(
        "Auth.InvalidToken",
        "A valid bearer access token of the core application is required.");

    public static readonly Error AdminRequiredError = Error.Forbidden(
        "Auth.AdminRequired",
        "The core.admin permission is required.");

    public static AuthenticationBuilder AddAdminBearer(this AuthenticationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddJwtBearer(SchemeName, _ => { });
        builder.Services
            .AddOptions<JwtBearerOptions>(SchemeName)
            .Configure<JwtKeyProvider, IOptions<JwtOptions>>((options, keyProvider, jwtOptions) =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = jwtOptions.Value.Issuer,
                    ValidAudience = CoreApplication.AppKey,
                    IssuerSigningKeys = keyProvider.ValidationKeys,
                    ValidTypes = [AccessTokenFactory.AccessTokenType],
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                    NameClaimType = AccessTokenFactory.NameClaimType,
                    RoleClaimType = AccessTokenFactory.RolesClaimType,
                    ClockSkew = TimeSpan.FromMinutes(1),
                };
                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = AddAppKeyClaim,
                    OnChallenge = async context =>
                    {
                        context.HandleResponse();
                        context.Response.Headers.WWWAuthenticate = "Bearer";
                        await WriteProblemAsync(context.HttpContext, StatusCodes.Status401Unauthorized, InvalidTokenError);
                    },
                    OnForbidden = context =>
                        WriteProblemAsync(context.HttpContext, StatusCodes.Status403Forbidden, AdminRequiredError),
                };
            });

        return builder;
    }

    /// <summary>
    /// Adds the <c>app_key</c> claim (from <c>client_id</c>) so the audit log and the request logs show <c>core</c>.
    /// </summary>
    private static Task AddAppKeyClaim(TokenValidatedContext context)
    {
        var clientId = context.Principal?.FindFirstValue(AccessTokenFactory.ClientIdClaimType);

        if (!string.IsNullOrEmpty(clientId) && context.Principal?.Identity is ClaimsIdentity identity)
        {
            identity.AddClaim(new Claim(AppKeyAuthentication.AppKeyClaimType, clientId));
        }

        return Task.CompletedTask;
    }

    private static Task WriteProblemAsync(HttpContext httpContext, int statusCode, Error error)
    {
        httpContext.Response.StatusCode = statusCode;

        var problemDetails = new ProblemDetails
        {
            Status = statusCode,
            Title = statusCode == StatusCodes.Status401Unauthorized ? "Unauthorized." : "Forbidden.",
            Type = statusCode == StatusCodes.Status401Unauthorized
                ? "https://tools.ietf.org/html/rfc9110#section-15.5.2"
                : "https://tools.ietf.org/html/rfc9110#section-15.5.4",
            Detail = error.Message,
        };
        problemDetails.Extensions["errorCode"] = error.Code;

        return httpContext.RequestServices.GetRequiredService<IProblemDetailsService>()
            .WriteAsync(new ProblemDetailsContext { HttpContext = httpContext, ProblemDetails = problemDetails })
            .AsTask();
    }
}
