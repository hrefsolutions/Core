using System.Net;
using System.Net.Http.Json;
using Href.Core.Api.Common.Persistence;
using Href.Core.Api.Modules.Applications;
using Href.Core.Api.Modules.Audit;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Href.Core.Tests.Integration;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class DatabaseRuntimeRoleIntegrationTests(PostgresFixture postgres)
{
    private const string RolePassword = "runtime-role-password";

    [Fact]
    public async Task GrantPrivileges_RuntimeRoleCanAppendAuditButNotRewriteIt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var role = await CreateRoleAsync(cancellationToken);
        DatabaseRuntimeRole.GrantPrivileges(postgres.ConnectionString, role, NullLogger.Instance);
        var runtimeConnectionString = RuntimeConnectionString(role);

        using (var sessionFactory = NHibernateExtensions.BuildSessionFactory(runtimeConnectionString, TimeProvider.System))
        using (var session = sessionFactory.OpenSession())
        {
            var auditService = new AuditService(session, new StubHttpContextAccessor(), TimeProvider.System);
            using var transaction = session.BeginTransaction();
            await auditService.RecordAsync(
                new AuditEntry(AuditActions.ApplicationCreated, EntityType: "Application", EntityId: $"runtime-{role}"),
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        await using var connection = new NpgsqlConnection(runtimeConnectionString);
        await connection.OpenAsync(cancellationToken);

        Assert.Equal(1L, (long)(await ScalarAsync(connection, $"SELECT count(*) FROM audit_logs WHERE entity_id = 'runtime-{role}'", cancellationToken))!);

        foreach (var statement in new[]
        {
            "UPDATE audit_logs SET action = 'TAMPERED'",
            "DELETE FROM audit_logs",
            "TRUNCATE audit_logs",
            "ALTER TABLE audit_logs DISABLE TRIGGER ALL",
            "DROP TRIGGER audit_logs_block_mutation ON audit_logs",
            "SELECT count(*) FROM schemaversions",
            "TRUNCATE applications",
        })
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteInRolledBackTransactionAsync(connection, statement, cancellationToken));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
        }

        await ExecuteInRolledBackTransactionAsync(connection, "UPDATE applications SET name = name", cancellationToken);
        await ExecuteInRolledBackTransactionAsync(connection, "DELETE FROM user_tokens WHERE false", cancellationToken);
    }

    [Fact]
    public async Task GrantPrivileges_RejectsInvalidMissingSuperuserAndOwnRoles()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var missing = $"missing_{Guid.NewGuid():N}"[..20];
        var superuser = await CreateRoleAsync(cancellationToken, superuser: true);

        foreach (var invalid in new[] { "Runtime", "runtime;drop table users", "\"quoted\"", "1runtime", "", new string('a', 64) })
        {
            Assert.Throws<InvalidOperationException>(() => DatabaseRuntimeRole.GrantPrivileges(postgres.ConnectionString, invalid, NullLogger.Instance));
        }

        var missingError = Assert.Throws<InvalidOperationException>(() => DatabaseRuntimeRole.GrantPrivileges(postgres.ConnectionString, missing, NullLogger.Instance));
        Assert.Contains("does not exist", missingError.Message, StringComparison.Ordinal);

        var superuserError = Assert.Throws<InvalidOperationException>(() => DatabaseRuntimeRole.GrantPrivileges(postgres.ConnectionString, superuser, NullLogger.Instance));
        Assert.Contains("superuser", superuserError.Message, StringComparison.Ordinal);

        var ownRole = await CreateRoleAsync(cancellationToken);
        var ownError = Assert.Throws<InvalidOperationException>(() => DatabaseRuntimeRole.GrantPrivileges(RuntimeConnectionString(ownRole), ownRole, NullLogger.Instance));
        Assert.Contains("own user", ownError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Api_WithRuntimeRole_MigratesAsOwnerAndServesAuditedWrites()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var role = await CreateRoleAsync(cancellationToken);
        await using var factory = new ApiFactory(
            RuntimeConnectionString(role),
            migrateOnStartup: true,
            overrides: new Dictionary<string, string?>
            {
                [$"ConnectionStrings:{DatabaseRuntimeRole.MigrationsConnectionName}"] = postgres.ConnectionString,
                [DatabaseRuntimeRole.RoleKey] = role,
            });
        using var admin = factory.CreateAdminClient();
        var appKey = $"rt-{Guid.NewGuid():N}"[..20];

        var health = await admin.GetAsync("/health", cancellationToken);
        var created = await admin.PostAsJsonAsync("/api/apps", new CreateApplicationRequest(appKey, "Runtime role app", null), cancellationToken);
        var createdBody = await created.Content.ReadFromJsonAsync<ApplicationCreatedResponse>(cancellationToken);
        var rotated = await admin.PostAsync($"/api/apps/{appKey}/secret/rotate", null, cancellationToken);
        var rotatedBody = await rotated.Content.ReadFromJsonAsync<RotateClientSecretResponse>(cancellationToken);
        using var m2m = factory.CreateClient();
        m2m.DefaultRequestHeaders.Add(AppKeyAuthentication.AppKeyHeaderName, appKey);
        m2m.DefaultRequestHeaders.Add(AppKeyAuthentication.AppSecretHeaderName, rotatedBody!.ClientSecret);
        var me = await m2m.GetAsync("/api/apps/me", cancellationToken);
        var audit = await admin.GetFromJsonAsync<AuditLogPageProbe>($"/api/audit-logs?entityId={createdBody!.Id}", cancellationToken);

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.NotNull(audit);
        Assert.Equal(
            [AuditActions.ApplicationSecretRotated, AuditActions.ApplicationCreated],
            audit.Items.Select(item => item.Action));

        await using var connection = new NpgsqlConnection(RuntimeConnectionString(role));
        await connection.OpenAsync(cancellationToken);
        var truncate = await Assert.ThrowsAsync<PostgresException>(() => ExecuteInRolledBackTransactionAsync(connection, "TRUNCATE audit_logs", cancellationToken));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, truncate.SqlState);
    }

    private async Task<string> CreateRoleAsync(CancellationToken cancellationToken, bool superuser = false)
    {
        var role = $"href_rt_{Guid.NewGuid():N}"[..20];
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await ExecuteAsync(
            connection,
            $"CREATE ROLE \"{role}\" LOGIN PASSWORD '{RolePassword}'{(superuser ? " SUPERUSER" : string.Empty)}",
            cancellationToken);
        return role;
    }

    private string RuntimeConnectionString(string role) =>
        new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { Username = role, Password = RolePassword }.ConnectionString;

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync(cancellationToken);
    }

    private static async Task ExecuteInRolledBackTransactionAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            await transaction.RollbackAsync(cancellationToken);
        }
    }

    private sealed record AuditLogPageProbe(IReadOnlyList<AuditLogItemProbe> Items);

    private sealed record AuditLogItemProbe(string Action);

    private sealed class StubHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }
}
