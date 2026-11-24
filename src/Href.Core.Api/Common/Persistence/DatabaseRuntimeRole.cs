using System.Text.RegularExpressions;
using Npgsql;

namespace Href.Core.Api.Common.Persistence;

/// <summary>
/// Least-privilege database role for the running API. The schema owner (<c>ConnectionStrings:Migrations</c>, or
/// <c>DefaultConnection</c> when it is not set) applies the migrations and then grants the role named by
/// <see cref="RoleKey"/>: read and write on every table of the <c>public</c> schema, only <c>SELECT</c> and <c>INSERT</c>
/// on <c>audit_logs</c>, nothing on the DbUp journal <c>schemaversions</c>, and never <c>TRUNCATE</c>, <c>REFERENCES</c> or
/// <c>TRIGGER</c>. The grants run on every startup with migrations, so tables added by later migrations are covered.
/// The role itself is created by hand (it needs a password), and <c>DefaultConnection</c> then connects as that role.
/// </summary>
public static partial class DatabaseRuntimeRole
{
    public const string RoleKey = "Database:RuntimeRole";

    public const string MigrationsConnectionName = "Migrations";

    private const string AuditLogsTable = "audit_logs";

    private const string JournalTable = "schemaversions";

    /// <summary>
    /// A role name is accepted only as a lowercase unquoted PostgreSQL identifier, so it can be embedded in the
    /// <c>GRANT</c> statements (which take no parameters) without any risk of injection.
    /// </summary>
    public static bool IsValidRoleName(string? roleName) =>
        roleName is not null && RoleNamePattern().IsMatch(roleName);

    /// <summary>
    /// The connection that owns the schema: <c>ConnectionStrings:Migrations</c> when set, otherwise <c>DefaultConnection</c>.
    /// </summary>
    public static string? GetOwnerConnectionString(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var migrations = configuration.GetConnectionString(MigrationsConnectionName);
        return string.IsNullOrWhiteSpace(migrations) ? configuration.GetConnectionString("DefaultConnection") : migrations;
    }

    /// <summary>
    /// Grants the runtime privileges to <paramref name="roleName"/> over the owner connection, in one transaction.
    /// Throws <see cref="InvalidOperationException"/> when the name is invalid, the role does not exist, is a superuser
    /// or is the owner connection's own user.
    /// </summary>
    public static void GrantPrivileges(string ownerConnectionString, string roleName, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerConnectionString);
        ArgumentNullException.ThrowIfNull(logger);

        if (!IsValidRoleName(roleName))
        {
            throw new InvalidOperationException(
                $"{RoleKey} must be a lowercase PostgreSQL identifier (a-z, 0-9 and _, starting with a letter or _, up to 63 characters).");
        }

        using var connection = new NpgsqlConnection(ownerConnectionString);
        connection.Open();

        using (var check = new NpgsqlCommand(
            "SELECT r.rolsuper, r.rolname = current_user FROM pg_roles r WHERE r.rolname = @role",
            connection))
        {
            check.Parameters.AddWithValue("role", roleName);
            using var reader = check.ExecuteReader();

            if (!reader.Read())
            {
                throw new InvalidOperationException(
                    $"The database role '{roleName}' configured in {RoleKey} does not exist. Create it before starting the API.");
            }

            if (reader.GetBoolean(0))
            {
                throw new InvalidOperationException(
                    $"The database role '{roleName}' configured in {RoleKey} is a superuser; the runtime role must not be one.");
            }

            if (reader.GetBoolean(1))
            {
                throw new InvalidOperationException(
                    $"The database role '{roleName}' configured in {RoleKey} is the migration connection's own user; use a separate role.");
            }
        }

        var role = $"\"{roleName}\"";
        string[] statements =
        [
            $"GRANT USAGE ON SCHEMA public TO {role}",
            $"GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO {role}",
            $"REVOKE TRUNCATE, REFERENCES, TRIGGER ON ALL TABLES IN SCHEMA public FROM {role}",
            $"REVOKE UPDATE, DELETE ON {AuditLogsTable} FROM {role}",
            $"REVOKE ALL ON {JournalTable} FROM {role}",
        ];

        using var transaction = connection.BeginTransaction();

        foreach (var statement in statements)
        {
            using var command = new NpgsqlCommand(statement, connection, transaction);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
        logger.LogInformation("Granted the runtime database privileges to role {RuntimeRole}", roleName);
    }

    /// <summary>
    /// Outside Development, logs a warning when the API's own connection (<c>DefaultConnection</c>) is a superuser or
    /// owns <c>audit_logs</c>: such a user can disable the trigger that keeps the audit trail append-only.
    /// </summary>
    public static void WarnIfRuntimeConnectionIsPrivileged(IConfiguration configuration, IHostEnvironment environment, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(logger);

        var connectionString = configuration.GetConnectionString("DefaultConnection");

        if (environment.IsDevelopment() || string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        try
        {
            using var connection = new NpgsqlConnection(connectionString);
            connection.Open();
            using var command = new NpgsqlCommand(
                "SELECT r.rolsuper OR EXISTS (SELECT 1 FROM pg_tables t WHERE t.schemaname = 'public' AND t.tablename = @table AND t.tableowner = current_user) FROM pg_roles r WHERE r.rolname = current_user",
                connection);
            command.Parameters.AddWithValue("table", AuditLogsTable);

            if (command.ExecuteScalar() is true)
            {
                logger.LogWarning(
                    "The API connects to PostgreSQL as a superuser or as the owner of {Table}; configure {RoleKey} and connect DefaultConnection as that role",
                    AuditLogsTable,
                    RoleKey);
            }
        }
        catch (Exception exception) when (exception is NpgsqlException or ArgumentException or InvalidOperationException or TimeoutException)
        {
            logger.LogWarning(exception, "Could not check the privileges of the API database connection");
        }
    }

    [GeneratedRegex(@"^[a-z_][a-z0-9_]{0,62}\z", RegexOptions.CultureInvariant)]
    private static partial Regex RoleNamePattern();
}
