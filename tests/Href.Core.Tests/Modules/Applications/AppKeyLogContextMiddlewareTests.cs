using System.Security.Claims;
using Href.Core.Api.Modules.Applications;
using Microsoft.AspNetCore.Http;
using Serilog;
using Serilog.Events;

namespace Href.Core.Tests.Modules.Applications;

public sealed class AppKeyLogContextMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_WhenApplicationIsAuthenticated_AddsAppKeyToLogContextAndDiagnosticContext()
    {
        var sink = new CapturingLogSink();
        using var logger = new LoggerConfiguration().Enrich.FromLogContext().WriteTo.Sink(sink).CreateLogger();
        var diagnosticContext = new RecordingDiagnosticContext();
        var middleware = new AppKeyLogContextMiddleware(_ =>
        {
            logger.Information("Inside the endpoint");
            return Task.CompletedTask;
        });
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(AppKeyAuthentication.AppKeyClaimType, "billing-app")],
                AppKeyAuthentication.SchemeName)),
        };

        await middleware.InvokeAsync(httpContext, diagnosticContext);

        var logEvent = Assert.Single(sink.Events);
        Assert.True(logEvent.Properties.TryGetValue(AppKeyLogContextMiddleware.PropertyName, out var value));
        Assert.Equal("billing-app", Assert.IsType<ScalarValue>(value).Value);
        Assert.Equal("billing-app", diagnosticContext.Properties[AppKeyLogContextMiddleware.PropertyName]);
    }

    [Fact]
    public async Task InvokeAsync_WhenRequestIsAnonymous_DoesNotAddAppKey()
    {
        var sink = new CapturingLogSink();
        using var logger = new LoggerConfiguration().Enrich.FromLogContext().WriteTo.Sink(sink).CreateLogger();
        var diagnosticContext = new RecordingDiagnosticContext();
        var middleware = new AppKeyLogContextMiddleware(_ =>
        {
            logger.Information("Inside the endpoint");
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(new DefaultHttpContext(), diagnosticContext);

        var logEvent = Assert.Single(sink.Events);
        Assert.False(logEvent.Properties.ContainsKey(AppKeyLogContextMiddleware.PropertyName));
        Assert.Empty(diagnosticContext.Properties);
    }

    private sealed class RecordingDiagnosticContext : IDiagnosticContext
    {
        public Dictionary<string, object?> Properties { get; } = [];

        public void Set(string propertyName, object? value, bool destructureObjects = false) =>
            Properties[propertyName] = value;

        public void SetException(Exception exception)
        {
        }
    }
}
