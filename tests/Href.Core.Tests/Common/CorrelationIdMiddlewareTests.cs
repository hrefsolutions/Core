using Href.Core.Api.Common.Web;
using Microsoft.AspNetCore.Http;

namespace Href.Core.Tests.Common;

public sealed class CorrelationIdMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_WhenHeaderIsMissing_GeneratesCorrelationId()
    {
        var context = new DefaultHttpContext();
        var middleware = new CorrelationIdMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context);

        var correlationId = Assert.IsType<string>(context.Items[CorrelationIdMiddleware.HeaderName]);
        Assert.Equal(32, correlationId.Length);
    }

    [Fact]
    public async Task InvokeAsync_WhenHeaderIsValid_PreservesCorrelationId()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[CorrelationIdMiddleware.HeaderName] = "req-123_ABC";
        var middleware = new CorrelationIdMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context);

        Assert.Equal("req-123_ABC", context.Items[CorrelationIdMiddleware.HeaderName]);
    }

    [Theory]
    [InlineData("invalid header with spaces")]
    [InlineData("invalid/slash")]
    [InlineData("01234567890123456789012345678901234567890123456789012345678901234")]
    public async Task InvokeAsync_WhenHeaderIsInvalid_ReplacesCorrelationId(string invalidHeader)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[CorrelationIdMiddleware.HeaderName] = invalidHeader;
        var middleware = new CorrelationIdMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context);

        var correlationId = Assert.IsType<string>(context.Items[CorrelationIdMiddleware.HeaderName]);
        Assert.NotEqual(invalidHeader, correlationId);
        Assert.Equal(32, correlationId.Length);
    }
}
