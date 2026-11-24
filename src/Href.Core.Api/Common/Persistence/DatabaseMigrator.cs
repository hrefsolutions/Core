using System.Globalization;
using System.Reflection;
using DbUp;
using DbUp.Engine.Output;

namespace Href.Core.Api.Common.Persistence;

/// <summary>
/// Applies forward-only SQL migrations embedded in the assembly using DbUp.
/// </summary>
public static class DatabaseMigrator
{
    public const string MigrateOnStartupKey = "Database:MigrateOnStartup";

    public static bool ShouldMigrateOnStartup(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration.GetValue(MigrateOnStartupKey, defaultValue: true);
    }

    public static void MigrateIfEnabled(IConfiguration configuration, IHostEnvironment environment, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(logger);

        if (!ShouldMigrateOnStartup(configuration))
        {
            logger.LogInformation("Database migrations on startup are disabled ({ConfigKey}=false)", MigrateOnStartupKey);

            if (!string.IsNullOrWhiteSpace(configuration[DatabaseRuntimeRole.RoleKey]))
            {
                logger.LogWarning(
                    "{RoleKey} is set but migrations are disabled, so no privileges are granted to the runtime role on this startup",
                    DatabaseRuntimeRole.RoleKey);
            }

            return;
        }

        var connectionString = DatabaseRuntimeRole.GetOwnerConnectionString(configuration);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
        }

        Migrate(connectionString, logger, ensureDatabase: environment.IsDevelopment());

        var runtimeRole = configuration[DatabaseRuntimeRole.RoleKey];

        if (!string.IsNullOrWhiteSpace(runtimeRole))
        {
            DatabaseRuntimeRole.GrantPrivileges(connectionString, runtimeRole, logger);
        }
    }

    public static void Migrate(
        string connectionString,
        ILogger logger,
        bool ensureDatabase = false,
        Assembly? scriptsAssembly = null,
        Func<string, bool>? scriptFilter = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(logger);

        var upgradeLog = new UpgradeLogAdapter(logger);

        if (ensureDatabase)
        {
            EnsureDatabase.For.PostgresqlDatabase(connectionString, upgradeLog);
        }

        var assembly = scriptsAssembly ?? typeof(DatabaseMigrator).Assembly;
        var filter = scriptFilter ?? IsDefaultMigrationScript;

        var upgrader = DeployChanges.To
            .PostgresqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(assembly, filter)
            .WithTransactionPerScript()
            .LogTo(upgradeLog)
            .Build();

        var result = upgrader.PerformUpgrade();

        if (!result.Successful)
        {
            var scriptName = result.ErrorScript?.Name ?? "unknown";
            logger.LogError(result.Error, "Database migration failed on script {ScriptName}", scriptName);
            throw new InvalidOperationException($"Database migration failed on script '{scriptName}'.", result.Error);
        }
    }

    internal static bool IsDefaultMigrationScript(string resourceName) =>
        resourceName.Contains(".Database.Migrations.", StringComparison.Ordinal)
        && resourceName.EndsWith(".sql", StringComparison.OrdinalIgnoreCase);

    private sealed class UpgradeLogAdapter(ILogger logger) : IUpgradeLog
    {
        public void LogTrace(string format, params object[] args) =>
            logger.LogTrace("{Message}", Format(format, args));

        public void LogDebug(string format, params object[] args) =>
            logger.LogDebug("{Message}", Format(format, args));

        public void LogInformation(string format, params object[] args) =>
            logger.LogInformation("{Message}", Format(format, args));

        public void LogWarning(string format, params object[] args) =>
            logger.LogWarning("{Message}", Format(format, args));

        public void LogError(string format, params object[] args) =>
            logger.LogError("{Message}", Format(format, args));

        public void LogError(Exception ex, string format, params object[] args) =>
            logger.LogError(ex, "{Message}", Format(format, args));

        private static string Format(string format, object[] args) =>
            args.Length == 0 ? format : string.Format(CultureInfo.InvariantCulture, format, args);
    }
}
