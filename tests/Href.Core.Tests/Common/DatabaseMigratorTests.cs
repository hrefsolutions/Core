using Href.Core.Api.Common.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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

    [Theory]
    [InlineData("href_core_app", true)]
    [InlineData(null, false)]
    public void MigrateIfEnabled_WhenDisabledWithARuntimeRole_WarnsThatNothingIsGranted(string? runtimeRole, bool expectWarning)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [DatabaseMigrator.MigrateOnStartupKey] = "false",
                [DatabaseRuntimeRole.RoleKey] = runtimeRole,
            })
            .Build();
        var logger = new RecordingLogger();

        DatabaseMigrator.MigrateIfEnabled(configuration, new StubHostEnvironment(Environments.Production), logger);

        Assert.Equal(expectWarning, logger.Levels.Contains(LogLevel.Warning));
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Levels.Add(logLevel);
    }

    private sealed class StubHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "Href.Core.Api";

        public string ContentRootPath { get; set; } = string.Empty;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
