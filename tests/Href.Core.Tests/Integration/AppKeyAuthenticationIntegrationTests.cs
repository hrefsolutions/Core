using System.Net;
using System.Net.Http.Json;
using Href.Core.Api.Modules.Applications;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Href.Core.Tests.Integration;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AppKeyAuthenticationIntegrationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task GetMe_WithValidAppKeyAndSecret_Returns200WithCurrentApplication()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();
        var appKey = $"m2m-{Guid.NewGuid():N}"[..20];

        var createResponse = await client.PostAsJsonAsync(
            "/api/apps",
            new CreateApplicationRequest(appKey, "M2M Service", "Satellite backend", false),
            TestContext.Current.CancellationToken);
        var created = await createResponse.Content.ReadFromJsonAsync<ApplicationCreatedResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(created);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/apps/me");
        request.Headers.Add(AppKeyAuthentication.AppKeyHeaderName, appKey);
        request.Headers.Add(AppKeyAuthentication.AppSecretHeaderName, created.ClientSecret);

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var current = await response.Content.ReadFromJsonAsync<CurrentApplicationResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(current);
        Assert.Equal(appKey, current.AppKey);
        Assert.Equal("M2M Service", current.Name);
        Assert.Equal("Active", current.Status);
    }

    [Fact]
    public async Task GetMe_WithValidCredentials_AddsAppKeyToRequestCompletionLog()
    {
        var sink = new CapturingLogSink();
        await using var baseFactory = new ApiFactory(postgres.ConnectionString);
        await using var factory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSingleton<ILogEventSink>(sink)));
        using var client = factory.CreateClient();
        var appKey = $"log-{Guid.NewGuid():N}"[..20];

        var createResponse = await client.PostAsJsonAsync(
            "/api/apps",
            new CreateApplicationRequest(appKey, "Log App", null, false),
            TestContext.Current.CancellationToken);
        var created = await createResponse.Content.ReadFromJsonAsync<ApplicationCreatedResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(created);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/apps/me");
        request.Headers.Add(AppKeyAuthentication.AppKeyHeaderName, appKey);
        request.Headers.Add(AppKeyAuthentication.AppSecretHeaderName, created.ClientSecret);

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var completion = await sink.WaitForAsync(
            logEvent => logEvent.MessageTemplate.Text.StartsWith("HTTP ", StringComparison.Ordinal)
                && logEvent.Properties.TryGetValue("RequestPath", out var path)
                && path is ScalarValue { Value: "/api/apps/me" },
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.True(completion.Properties.TryGetValue(AppKeyLogContextMiddleware.PropertyName, out var value));
        Assert.Equal(appKey, Assert.IsType<ScalarValue>(value).Value);
    }

    [Fact]
    public async Task RequestCompletionLog_GoesToTheHostLoggerEvenWhenTheStaticLoggerIsReplaced()
    {
        var sink = new CapturingLogSink();
        await using var baseFactory = new ApiFactory(postgres.ConnectionString);
        await using var factory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSingleton<ILogEventSink>(sink)));
        using var client = factory.CreateClient();
        var previousStaticLogger = Log.Logger;
        Log.Logger = new LoggerConfiguration().CreateLogger();

        try
        {
            var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            await sink.WaitForAsync(
                logEvent => logEvent.MessageTemplate.Text.StartsWith("HTTP ", StringComparison.Ordinal)
                    && logEvent.Properties.TryGetValue("RequestPath", out var path)
                    && path is ScalarValue { Value: "/health" },
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);
        }
        finally
        {
            Log.Logger = previousStaticLogger;
        }
    }

    [Fact]
    public async Task GetMe_WhenHeadersAreMissing_Returns401UnauthorizedProblemDetails()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/apps/me", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(TestContext.Current.CancellationToken);
        Assert.NotNull(problem);
        Assert.Equal(AppKeyAuthentication.InvalidCredentialsError.Code, problem.Extensions["errorCode"]?.ToString());
        Assert.Equal(AppKeyAuthentication.InvalidCredentialsError.Message, problem.Detail);
    }

    [Fact]
    public async Task GetMe_WithWrongSecretOrUnknownKey_Returns401WithIdenticalProblemDetails()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();
        var appKey = $"auth-{Guid.NewGuid():N}"[..20];

        await client.PostAsJsonAsync(
            "/api/apps",
            new CreateApplicationRequest(appKey, "Auth Test App", null, false),
            TestContext.Current.CancellationToken);

        using var wrongSecretRequest = new HttpRequestMessage(HttpMethod.Get, "/api/apps/me");
        wrongSecretRequest.Headers.Add(AppKeyAuthentication.AppKeyHeaderName, appKey);
        wrongSecretRequest.Headers.Add(AppKeyAuthentication.AppSecretHeaderName, "wrong-secret-value");

        using var unknownKeyRequest = new HttpRequestMessage(HttpMethod.Get, "/api/apps/me");
        unknownKeyRequest.Headers.Add(AppKeyAuthentication.AppKeyHeaderName, $"unknown-{Guid.NewGuid():N}"[..20]);
        unknownKeyRequest.Headers.Add(AppKeyAuthentication.AppSecretHeaderName, "wrong-secret-value");

        var wrongSecretResponse = await client.SendAsync(wrongSecretRequest, TestContext.Current.CancellationToken);
        var unknownKeyResponse = await client.SendAsync(unknownKeyRequest, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, wrongSecretResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownKeyResponse.StatusCode);

        var wrongSecretProblem = await wrongSecretResponse.Content.ReadFromJsonAsync<ProblemDetails>(TestContext.Current.CancellationToken);
        var unknownKeyProblem = await unknownKeyResponse.Content.ReadFromJsonAsync<ProblemDetails>(TestContext.Current.CancellationToken);
        Assert.NotNull(wrongSecretProblem);
        Assert.NotNull(unknownKeyProblem);
        Assert.Equal(AppKeyAuthentication.InvalidCredentialsError.Code, wrongSecretProblem.Extensions["errorCode"]?.ToString());
        Assert.Equal(wrongSecretProblem.Extensions["errorCode"]?.ToString(), unknownKeyProblem.Extensions["errorCode"]?.ToString());
        Assert.Equal(wrongSecretProblem.Title, unknownKeyProblem.Title);
        Assert.Equal(wrongSecretProblem.Detail, unknownKeyProblem.Detail);
    }

    [Fact]
    public async Task GetMe_WhenApplicationIsSuspended_Returns403ForbiddenProblemDetails()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();
        var appKey = $"susp-{Guid.NewGuid():N}"[..20];

        var createResponse = await client.PostAsJsonAsync(
            "/api/apps",
            new CreateApplicationRequest(appKey, "Suspended App", null, false),
            TestContext.Current.CancellationToken);
        var created = await createResponse.Content.ReadFromJsonAsync<ApplicationCreatedResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(created);

        var suspendResponse = await client.PatchAsJsonAsync(
            $"/api/apps/{appKey}/status",
            new UpdateApplicationStatusRequest("Suspended"),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, suspendResponse.StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/apps/me");
        request.Headers.Add(AppKeyAuthentication.AppKeyHeaderName, appKey);
        request.Headers.Add(AppKeyAuthentication.AppSecretHeaderName, created.ClientSecret);

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(TestContext.Current.CancellationToken);
        Assert.NotNull(problem);
        Assert.Equal(AppKeyAuthentication.ApplicationSuspendedError.Code, problem.Extensions["errorCode"]?.ToString());
        Assert.Equal(AppKeyAuthentication.ApplicationSuspendedError.Message, problem.Detail);
    }
}
