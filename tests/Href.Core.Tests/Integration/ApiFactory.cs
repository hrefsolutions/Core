using Href.Core.Api.Common.Persistence;
using Href.Core.Tests.Modules.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Href.Core.Tests.Integration;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly bool _migrateOnStartup;
    private readonly IReadOnlyDictionary<string, string?> _overrides;
    private readonly string? _environment;

    public ApiFactory(
        string connectionString,
        bool migrateOnStartup = false,
        IReadOnlyDictionary<string, string?>? overrides = null,
        string? environment = null)
    {
        _connectionString = connectionString;
        _migrateOnStartup = migrateOnStartup;
        _overrides = overrides ?? new Dictionary<string, string?>();
        _environment = environment;
        Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", connectionString);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        if (_environment is not null)
        {
            builder.UseEnvironment(_environment);
        }

        builder.ConfigureAppConfiguration((_, configurationBuilder) =>
        {
            configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = _connectionString,
                [DatabaseMigrator.MigrateOnStartupKey] = _migrateOnStartup ? "true" : "false",
                ["Smtp:Host"] = "localhost",
                ["Smtp:Port"] = "1025",
                ["Smtp:SecureSocket"] = "None",
                ["Smtp:Username"] = null,
                ["Smtp:Password"] = null,
                ["Smtp:FromEmail"] = "notifier@example.test",
                ["Smtp:FromName"] = "Href Tests",
                ["Notifications:Worker:Enabled"] = "false",
                ["Jwt:Issuer"] = "http://localhost",
                ["Jwt:SigningKey:KeyId"] = "tests-current",
                ["Jwt:SigningKey:PrivateKeyPem"] = JwtTestKeys.CurrentPrivateKeyPem,
            });
            configurationBuilder.AddInMemoryCollection(_overrides);
        });
    }
}
