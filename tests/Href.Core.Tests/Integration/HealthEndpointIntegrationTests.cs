using System.Net;
using System.Net.Http.Json;
using Href.Core.Api.Common.Web;

namespace Href.Core.Tests.Integration;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class HealthEndpointIntegrationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task GetHealth_WhenDatabaseIsReachable_Returns200Healthy()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<HealthController.HealthResponse>(TestContext.Current.CancellationToken);
        Assert.Equal(new HealthController.HealthResponse("Healthy", "Healthy"), body);
    }

    [Fact]
    public async Task GetHealth_WhenDatabaseIsUnreachable_Returns503Unhealthy()
    {
        const string unreachableConnectionString = "Host=127.0.0.1;Port=1;Database=href_core;Username=href;Password=href;Timeout=1";
        await using var factory = new ApiFactory(unreachableConnectionString, migrateOnStartup: false);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<HealthController.HealthResponse>(TestContext.Current.CancellationToken);
        Assert.Equal(new HealthController.HealthResponse("Unhealthy", "Unhealthy"), body);
    }
}
