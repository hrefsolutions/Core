using Href.Core.Api.Common.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Href.Core.Tests.Common;

public sealed class GlobalExceptionHandlerTests
{
    [Fact]
    public async Task TryHandleAsync_InProduction_OmitsExceptionMessageFromDetail()
    {
        var problemDetailsService = new CapturingProblemDetailsService();
        var handler = new GlobalExceptionHandler(
            problemDetailsService,
            new StubHostEnvironment(Environments.Production),
            NullLogger<GlobalExceptionHandler>.Instance);
        var context = new DefaultHttpContext();

        var handled = await handler.TryHandleAsync(context, new InvalidOperationException("Secret failure"), CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.NotNull(problemDetailsService.CapturedContext);
        Assert.Equal(StatusCodes.Status500InternalServerError, problemDetailsService.CapturedContext.ProblemDetails.Status);
        Assert.Null(problemDetailsService.CapturedContext.ProblemDetails.Detail);
    }

    [Fact]
    public async Task TryHandleAsync_InDevelopment_IncludesExceptionMessageInDetail()
    {
        var problemDetailsService = new CapturingProblemDetailsService();
        var handler = new GlobalExceptionHandler(
            problemDetailsService,
            new StubHostEnvironment(Environments.Development),
            NullLogger<GlobalExceptionHandler>.Instance);
        var context = new DefaultHttpContext();

        var handled = await handler.TryHandleAsync(context, new InvalidOperationException("Detailed failure"), CancellationToken.None);

        Assert.True(handled);
        Assert.NotNull(problemDetailsService.CapturedContext);
        Assert.Equal("Detailed failure", problemDetailsService.CapturedContext.ProblemDetails.Detail);
    }

    [Fact]
    public async Task TryHandleAsync_DoesNotLogTheRequestMethodOrPath()
    {
        var logger = new CapturingLogger();
        var handler = new GlobalExceptionHandler(
            new CapturingProblemDetailsService(),
            new StubHostEnvironment(Environments.Production),
            logger);
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/api/forged\n[12:00:00 INF] Forged entry";

        await handler.TryHandleAsync(context, new InvalidOperationException("Failure"), CancellationToken.None);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.NotNull(entry.Exception);
        Assert.DoesNotContain("forged", entry.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("GET", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(entry.State, pair => pair.Value is PathString || Equals(pair.Value, "GET"));
    }

    [Fact]
    public async Task TryHandleAsync_WithABodyOverTheSizeLimit_Returns413RequestTooLargeAndLogsAWarning()
    {
        var problemDetailsService = new CapturingProblemDetailsService();
        var logger = new CapturingLogger();
        var handler = new GlobalExceptionHandler(problemDetailsService, new StubHostEnvironment(Environments.Production), logger);
        var context = new DefaultHttpContext();

        var handled = await handler.TryHandleAsync(
            context,
            new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge),
            CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, context.Response.StatusCode);
        Assert.NotNull(problemDetailsService.CapturedContext);
        var problem = problemDetailsService.CapturedContext.ProblemDetails;
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, problem.Status);
        Assert.Equal(GlobalExceptionHandler.RequestTooLargeErrorCode, problem.Extensions["errorCode"]);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Null(entry.Exception);
    }

    [Fact]
    public async Task TryHandleAsync_WithAnotherUnreadableRequest_KeepsItsClientErrorStatus()
    {
        var problemDetailsService = new CapturingProblemDetailsService();
        var handler = new GlobalExceptionHandler(
            problemDetailsService,
            new StubHostEnvironment(Environments.Production),
            NullLogger<GlobalExceptionHandler>.Instance);
        var context = new DefaultHttpContext();

        await handler.TryHandleAsync(context, new BadHttpRequestException("Unexpected end of request content."), CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.NotNull(problemDetailsService.CapturedContext);
        Assert.Equal(GlobalExceptionHandler.InvalidRequestErrorCode, problemDetailsService.CapturedContext.ProblemDetails.Extensions["errorCode"]);
    }

    private sealed class CapturingLogger : ILogger<GlobalExceptionHandler>
    {
        public List<(LogLevel Level, string Message, Exception? Exception, IReadOnlyList<KeyValuePair<string, object?>> State)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var pairs = state as IReadOnlyList<KeyValuePair<string, object?>> ?? Array.Empty<KeyValuePair<string, object?>>();
            Entries.Add((logLevel, formatter(state, exception), exception, pairs));
        }
    }

    private sealed class CapturingProblemDetailsService : IProblemDetailsService
    {
        public ProblemDetailsContext? CapturedContext { get; private set; }

        public ValueTask WriteAsync(ProblemDetailsContext context)
        {
            CapturedContext = context;
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> TryWriteAsync(ProblemDetailsContext context)
        {
            CapturedContext = context;
            return ValueTask.FromResult(true);
        }
    }

    private sealed class StubHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "Href.Core.Api";

        public string ContentRootPath { get; set; } = string.Empty;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
