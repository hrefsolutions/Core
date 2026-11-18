using Href.Core.Api.Common.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Href.Core.Tests.Integration;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly bool _migrateOnStartup;

    public ApiFactory(string connectionString, bool migrateOnStartup = false)
    {
        _connectionString = connectionString;
        _migrateOnStartup = migrateOnStartup;
        Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", connectionString);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, configurationBuilder) =>
        {
            configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = _connectionString,
                [DatabaseMigrator.MigrateOnStartupKey] = _migrateOnStartup ? "true" : "false",
            });
        });
    }
}
