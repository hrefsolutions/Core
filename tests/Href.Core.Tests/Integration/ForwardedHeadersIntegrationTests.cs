using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Href.Core.Tests.Integration;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ForwardedHeadersIntegrationTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData("203.0.113.9", "203.0.113.9")]
    [InlineData("198.51.100.77, 203.0.113.9", "203.0.113.9")]
    public async Task Request_WithXForwardedFor_UsesTheAddressAddedByTheProxy(string forwardedFor, string expectedRemoteIp)
    {
        var capture = new RemoteIpCapture();
        await using var baseFactory = new ApiFactory(postgres.ConnectionString);
        await using var factory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSingleton<IStartupFilter>(capture)));
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Add("X-Forwarded-For", forwardedFor);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(IPAddress.Parse(expectedRemoteIp), capture.RemoteIpAddress);
    }

    private sealed class RemoteIpCapture : IStartupFilter
    {
        public IPAddress? RemoteIpAddress { get; private set; }

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((HttpContext context, RequestDelegate nextMiddleware) =>
            {
                context.Response.OnStarting(() =>
                {
                    RemoteIpAddress = context.Connection.RemoteIpAddress;
                    return Task.CompletedTask;
                });

                return nextMiddleware(context);
            });
            next(app);
        };
    }
}
