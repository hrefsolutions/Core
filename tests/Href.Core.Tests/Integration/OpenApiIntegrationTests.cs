using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Href.Core.Api.Common.OpenApi;
using Href.Core.Api.Common.Web;
using Href.Core.Api.Modules.Applications;
using Href.Core.Api.Modules.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Href.Core.Tests.Integration;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed partial class OpenApiIntegrationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Document_IsAnonymousAndDeclaresTheM2MAndBearerSecuritySchemes()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var document = await GetDocumentAsync(factory.CreateClient());

        var schemes = document.RootElement.GetProperty("components").GetProperty("securitySchemes");
        var appKey = schemes.GetProperty(OpenApiSetup.AppKeySchemeName);
        Assert.Equal("apiKey", appKey.GetProperty("type").GetString());
        Assert.Equal("header", appKey.GetProperty("in").GetString());
        Assert.Equal(AppKeyAuthentication.AppKeyHeaderName, appKey.GetProperty("name").GetString());
        var appSecret = schemes.GetProperty(OpenApiSetup.AppSecretSchemeName);
        Assert.Equal("apiKey", appSecret.GetProperty("type").GetString());
        Assert.Equal("header", appSecret.GetProperty("in").GetString());
        Assert.Equal(AppKeyAuthentication.AppSecretHeaderName, appSecret.GetProperty("name").GetString());
        var bearer = schemes.GetProperty(OpenApiSetup.BearerSchemeName);
        Assert.Equal("http", bearer.GetProperty("type").GetString());
        Assert.Equal("bearer", bearer.GetProperty("scheme").GetString());
        Assert.Equal("JWT", bearer.GetProperty("bearerFormat").GetString());
        Assert.Equal("Href.Core API", document.RootElement.GetProperty("info").GetProperty("title").GetString());
    }

    [Fact]
    public async Task EveryControllerEndpoint_IsDocumentedWithTheSecurityAndAuthResponsesOfItsPolicy()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var document = await GetDocumentAsync(factory.CreateClient());
        var paths = document.RootElement.GetProperty("paths");
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<ControllerActionDescriptor>() is not null)
            .ToList();

        Assert.NotEmpty(endpoints);

        foreach (var endpoint in endpoints)
        {
            var path = "/" + RouteConstraint().Replace(endpoint.RoutePattern.RawText!, "{$1}");
            var method = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Single().ToLowerInvariant();
            var name = $"{method.ToUpperInvariant()} {path}";

            Assert.True(paths.TryGetProperty(path, out var pathItem), $"{name} is missing from the document.");
            Assert.True(pathItem.TryGetProperty(method, out var operation), $"{name} is missing from the document.");

            var responses = operation.GetProperty("responses");
            Assert.True(
                responses.EnumerateObject().Any(response => response.Name.StartsWith('2')),
                $"{name} documents no success response.");
            Assert.All(
                responses.EnumerateObject(),
                response => Assert.False(
                    string.IsNullOrWhiteSpace(response.Value.GetProperty("description").GetString()),
                    $"{name} response {response.Name} has no description."));

            var anonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;
            var policies = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(data => data.Policy).ToList();
            string[] expectedSchemes = anonymous
                ? []
                : policies.Contains(AdminAuthentication.PolicyName)
                    ? [OpenApiSetup.BearerSchemeName]
                    : [OpenApiSetup.AppKeySchemeName, OpenApiSetup.AppSecretSchemeName];

            var documentedSchemes = operation.TryGetProperty("security", out var security)
                ? security.EnumerateArray().SelectMany(requirement => requirement.EnumerateObject().Select(scheme => scheme.Name)).Order().ToArray()
                : [];
            Assert.True(
                expectedSchemes.Order().SequenceEqual(documentedSchemes),
                $"{name} documents [{string.Join(", ", documentedSchemes)}] instead of [{string.Join(", ", expectedSchemes)}].");

            if (!anonymous)
            {
                Assert.True(responses.TryGetProperty("401", out _), $"{name} does not document 401.");
                Assert.True(responses.TryGetProperty("403", out _), $"{name} does not document 403.");
            }

            if (endpoint.Metadata.GetMetadata<IRequestSizeLimitMetadata>()?.MaxRequestBodySize is not null)
            {
                Assert.True(responses.TryGetProperty("413", out _), $"{name} limits the body size but does not document 413.");
            }

            if (endpoint.Metadata.GetMetadata<EnableRateLimitingAttribute>() is not null)
            {
                Assert.True(responses.TryGetProperty("429", out _), $"{name} is rate limited but does not document 429.");
            }
        }
    }

    [Fact]
    public async Task AdminAndM2MOperations_DescribeTheirAuthErrorCodes()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var document = await GetDocumentAsync(factory.CreateClient());
        var paths = document.RootElement.GetProperty("paths");

        var adminList = paths.GetProperty("/api/users").GetProperty("get").GetProperty("responses");
        Assert.Contains(AdminAuthentication.InvalidTokenError.Code, adminList.GetProperty("401").GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Contains(AdminAuthentication.AdminRequiredError.Code, adminList.GetProperty("403").GetProperty("description").GetString(), StringComparison.Ordinal);

        var login = paths.GetProperty("/api/auth/login").GetProperty("post").GetProperty("responses");
        Assert.Contains(AppKeyAuthentication.InvalidCredentialsError.Code, login.GetProperty("401").GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Contains(AppKeyAuthentication.ApplicationSuspendedError.Code, login.GetProperty("403").GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Contains(AuthRateLimiting.ErrorCode, login.GetProperty("429").GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Contains(AuthController.RequestSizeLimitBytes.ToString(System.Globalization.CultureInfo.InvariantCulture), login.GetProperty("413").GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Contains(GlobalExceptionHandler.RequestTooLargeErrorCode, login.GetProperty("413").GetProperty("description").GetString(), StringComparison.Ordinal);
        var queueEmail413 = paths.GetProperty("/api/notifications/email").GetProperty("post").GetProperty("responses").GetProperty("413");
        Assert.Single(
            System.Text.RegularExpressions.Regex.Matches(
                queueEmail413.GetProperty("description").GetString()!,
                System.Text.RegularExpressions.Regex.Escape(GlobalExceptionHandler.RequestTooLargeErrorCode)));
        Assert.True(queueEmail413.GetProperty("content").TryGetProperty("application/problem+json", out _));
        Assert.Single(
            System.Text.RegularExpressions.Regex.Matches(
                login.GetProperty("403").GetProperty("description").GetString()!,
                System.Text.RegularExpressions.Regex.Escape(AppKeyAuthentication.ApplicationSuspendedError.Code)));
    }

    [Fact]
    public async Task ProblemDetailsSchema_DocumentsErrorCodeAndCorrelationId()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var document = await GetDocumentAsync(factory.CreateClient());

        var properties = document.RootElement.GetProperty("components").GetProperty("schemas").GetProperty("ProblemDetails").GetProperty("properties");

        Assert.True(properties.TryGetProperty("errorCode", out _));
        Assert.True(properties.TryGetProperty("correlationId", out _));
    }

    [Fact]
    public async Task Scalar_IsAnonymousAndServesTheReferencePage()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/scalar", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Href.Core API", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenDisabled_NeitherTheDocumentNorScalarIsMapped()
    {
        await using var factory = new ApiFactory(
            postgres.ConnectionString,
            overrides: new Dictionary<string, string?> { [OpenApiSetup.EnabledKey] = "false" });
        using var client = factory.CreateClient();

        var document = await client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
        var scalar = await client.GetAsync("/scalar", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, document.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, scalar.StatusCode);
    }

    [Fact]
    public async Task BehindTheRailwayProxy_TheDocumentedServerUsesHttps()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/openapi/v1.json");
        request.Headers.Add("X-Forwarded-For", "203.0.113.7");
        request.Headers.Add("X-Forwarded-Proto", "https");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var servers = document.RootElement.GetProperty("servers").EnumerateArray().Select(server => server.GetProperty("url").GetString()).ToList();
        Assert.NotEmpty(servers);
        Assert.All(servers, url => Assert.StartsWith("https://", url, StringComparison.Ordinal));
    }

    private static async Task<JsonDocument> GetDocumentAsync(HttpClient client)
    {
        var response = await client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [GeneratedRegex(@"\{\*{0,2}(\w+)(?::[^}]*)?\??\}")]
    private static partial Regex RouteConstraint();
}
