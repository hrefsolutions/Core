using Href.Core.Api.Common.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Href.Core.Tests.Common;

public sealed class DatabaseMigratorTests
{
    [Fact]
    public void ShouldMigrateOnStartup_WhenNotConfigured_DefaultsToTrue()
    {
        var configuration = new ConfigurationBuilder().Build();

        Assert.True(DatabaseMigrator.ShouldMigrateOnStartup(configuration));
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void ShouldMigrateOnStartup_WhenConfigured_ReturnsConfiguredValue(string value, bool expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [DatabaseMigrator.MigrateOnStartupKey] = value,
            })
            .Build();

        Assert.Equal(expected, DatabaseMigrator.ShouldMigrateOnStartup(configuration));
    }

    [Fact]
    public void MigrateIfEnabled_WhenDisabled_SkipsWithoutRequiringConnectionString()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [DatabaseMigrator.MigrateOnStartupKey] = "false",
            })
            .Build();

        var exception = Record.Exception(() =>
            DatabaseMigrator.MigrateIfEnabled(
                configuration,
                new StubHostEnvironment(Environments.Production),
                NullLogger.Instance));

        Assert.Null(exception);
    }

    [Fact]
    public void MigrateIfEnabled_WhenEnabledAndConnectionStringIsMissing_ThrowsInvalidOperationException()
    {
        var configuration = new ConfigurationBuilder().Build();

        Assert.Throws<InvalidOperationException>(() =>
            DatabaseMigrator.MigrateIfEnabled(
                configuration,
                new StubHostEnvironment(Environments.Production),
                NullLogger.Instance));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Migrate_WhenConnectionStringIsWhitespace_ThrowsArgumentException(string connectionString)
    {
        Assert.Throws<ArgumentException>(() =>
            DatabaseMigrator.Migrate(connectionString, NullLogger.Instance));
    }

    [Fact]
    public void ApiAssembly_EmbedsBaselineMigrationScript()
    {
        var resources = typeof(DatabaseMigrator).Assembly
            .GetManifestResourceNames()
            .Where(DatabaseMigrator.IsDefaultMigrationScript)
            .ToArray();

        Assert.Contains(resources, name => name.EndsWith(".Database.Migrations.0000_baseline.sql", StringComparison.Ordinal));
    }

    private sealed class StubHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "Href.Core.Api";

        public string ContentRootPath { get; set; } = string.Empty;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
