using Href.Core.Api.Common.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
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
