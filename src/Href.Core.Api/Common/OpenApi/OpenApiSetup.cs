using Href.Core.Api.Modules.Applications;
using Href.Core.Api.Modules.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.OpenApi;
using Scalar.AspNetCore;

namespace Href.Core.Api.Common.OpenApi;

/// <summary>
/// OpenAPI document (<c>/openapi/v1.json</c>) and Scalar reference (<c>/scalar</c>), both anonymous and mapped only when
/// <c>OpenApi:Enabled</c> is true (the default). The document declares the M2M schemes (<c>AppKey</c> and <c>AppSecret</c>
/// headers) and the admin <c>Bearer</c> scheme; each operation gets the requirement of its authorization policy, the
/// <c>401</c> / <c>403</c> responses of that scheme and, when rate limited, <c>429</c>. <see cref="ProblemDetails"/> schemas
/// document the <c>errorCode</c> and <c>correlationId</c> extensions.
/// </summary>
public static class OpenApiSetup
{
    public const string EnabledKey = "OpenApi:Enabled";
    public const string DocumentName = "v1";
    public const string BearerSchemeName = "Bearer";
    public const string AppKeySchemeName = "AppKey";
    public const string AppSecretSchemeName = "AppSecret";

    private const string ProblemContentType = "application/problem+json";

    public static IServiceCollection AddCoreOpenApi(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOpenApi(DocumentName, options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                DescribeDocument(document);
                return Task.CompletedTask;
            });
            options.AddOperationTransformer(DescribeOperationSecurityAsync);
            options.AddSchemaTransformer((schema, context, _) =>
            {
                if (typeof(ProblemDetails).IsAssignableFrom(context.JsonTypeInfo.Type))
                {
                    DescribeProblemExtensions(schema);
                }

                return Task.CompletedTask;
            });
        });

        return services;
    }

    public static WebApplication MapCoreOpenApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        if (!app.Configuration.GetValue(EnabledKey, defaultValue: true))
        {
            return app;
        }

        app.MapOpenApi().AllowAnonymous();
        app.MapScalarApiReference(options => options
                .WithTitle("Href.Core API")
                .AddPreferredSecuritySchemes([BearerSchemeName]))
            .AllowAnonymous();

        return app;
    }

    private static void DescribeDocument(OpenApiDocument document)
    {
        document.Info ??= new OpenApiInfo();
        document.Info.Title = "Href.Core API";
        document.Info.Version = typeof(OpenApiSetup).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        document.Info.Description =
            "Central identity, authorization, notification and audit API of the Href ecosystem. "
            + "Satellite backends call it with their M2M credentials (`X-App-Key` + `X-App-Secret`); the admin frontend uses a "
            + "bearer access token of the reserved `core` application with the `core.admin` permission. Errors are RFC 7807 "
            + "`ProblemDetails` with an `errorCode`. Integration guide: `docs/integration-guide.md` in the repository.";

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[AppKeySchemeName] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Header,
            Name = AppKeyAuthentication.AppKeyHeaderName,
            Description = "M2M: the application key. Always sent together with `X-App-Secret`.",
        };
        document.Components.SecuritySchemes[AppSecretSchemeName] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Header,
            Name = AppKeyAuthentication.AppSecretHeaderName,
            Description = "M2M: the application client secret (shown once when the application is created or its secret rotated).",
        };
        document.Components.SecuritySchemes[BearerSchemeName] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description = "Admin: access token from `POST /api/auth/login` for the `core` application, with the `core.admin` permission.",
        };
    }

    private static async Task DescribeOperationSecurityAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        var metadata = context.Description.ActionDescriptor.EndpointMetadata;
        var anonymous = metadata.OfType<IAllowAnonymous>().Any();
        var policies = metadata.OfType<IAuthorizeData>().Select(data => data.Policy).ToList();
        operation.Responses ??= new OpenApiResponses();

        if (!anonymous && policies.Contains(AdminAuthentication.PolicyName))
        {
            operation.Security = [Requirement(context.Document!, BearerSchemeName)];
            await AddProblemResponseAsync(
                operation,
                context,
                StatusCodes.Status401Unauthorized,
                $"Missing, invalid, expired or foreign bearer token: `{AdminAuthentication.InvalidTokenError.Code}`.",
                AdminAuthentication.InvalidTokenError.Code,
                cancellationToken);
            await AddProblemResponseAsync(
                operation,
                context,
                StatusCodes.Status403Forbidden,
                $"The token lacks the `core.admin` permission: `{AdminAuthentication.AdminRequiredError.Code}`.",
                AdminAuthentication.AdminRequiredError.Code,
                cancellationToken);
        }
        else if (!anonymous && policies.Contains(AppKeyAuthentication.PolicyName))
        {
            operation.Security = [Requirement(context.Document!, AppKeySchemeName, AppSecretSchemeName)];
            await AddProblemResponseAsync(
                operation,
                context,
                StatusCodes.Status401Unauthorized,
                $"Missing or invalid `X-App-Key` / `X-App-Secret`: `{AppKeyAuthentication.InvalidCredentialsError.Code}`.",
                AppKeyAuthentication.InvalidCredentialsError.Code,
                cancellationToken);
            await AddProblemResponseAsync(
                operation,
                context,
                StatusCodes.Status403Forbidden,
                $"The calling application is suspended: `{AppKeyAuthentication.ApplicationSuspendedError.Code}`.",
                AppKeyAuthentication.ApplicationSuspendedError.Code,
                cancellationToken);
        }

        if (metadata.OfType<IRequestSizeLimitMetadata>().LastOrDefault()?.MaxRequestBodySize is { } maxBodyBytes)
        {
            operation.Responses.TryAdd(
                StatusCodes.Status413PayloadTooLarge.ToString(System.Globalization.CultureInfo.InvariantCulture),
                new OpenApiResponse { Description = $"The request body is larger than {maxBodyBytes} bytes." });
        }

        if (metadata.OfType<EnableRateLimitingAttribute>().Any())
        {
            await AddProblemResponseAsync(
                operation,
                context,
                StatusCodes.Status429TooManyRequests,
                $"Rate limit exceeded: `{AuthRateLimiting.ErrorCode}`. Wait the seconds of the `Retry-After` header.",
                AuthRateLimiting.ErrorCode,
                cancellationToken);
        }
    }

    private static OpenApiSecurityRequirement Requirement(OpenApiDocument document, params string[] schemeNames)
    {
        var requirement = new OpenApiSecurityRequirement();

        foreach (var schemeName in schemeNames)
        {
            requirement[new OpenApiSecuritySchemeReference(schemeName, document)] = [];
        }

        return requirement;
    }

    private static async Task AddProblemResponseAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        int statusCode,
        string description,
        string errorCode,
        CancellationToken cancellationToken)
    {
        var key = statusCode.ToString(System.Globalization.CultureInfo.InvariantCulture);

        if (operation.Responses!.TryGetValue(key, out var existing))
        {
            if (existing is OpenApiResponse declared
                && declared.Description?.Contains(errorCode, StringComparison.Ordinal) != true)
            {
                declared.Description = string.IsNullOrWhiteSpace(declared.Description)
                    ? description
                    : $"{declared.Description} Also: {description}";
            }

            return;
        }

        var schema = await context.GetOrCreateSchemaAsync(typeof(ProblemDetails), cancellationToken: cancellationToken);
        operation.Responses[key] = new OpenApiResponse
        {
            Description = description,
            Content = new Dictionary<string, OpenApiMediaType>
            {
                [ProblemContentType] = new OpenApiMediaType { Schema = schema },
            },
        };
    }

    private static void DescribeProblemExtensions(OpenApiSchema schema)
    {
        schema.Properties ??= new Dictionary<string, IOpenApiSchema>();
        schema.Properties["errorCode"] = new OpenApiSchema
        {
            Type = JsonSchemaType.String,
            Description = "Stable machine-readable error code, for example `Users.EmailTaken` (absent on framework model-binding errors).",
        };
        schema.Properties["correlationId"] = new OpenApiSchema
        {
            Type = JsonSchemaType.String,
            Description = "Correlation id of the request (also in the `X-Correlation-Id` response header).",
        };
    }
}
