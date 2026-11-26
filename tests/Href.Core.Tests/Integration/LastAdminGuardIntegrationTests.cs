using System.Net;
using System.Net.Http.Json;
using Href.Core.Api.Common.Persistence;
using Href.Core.Api.Common.Results;
using Href.Core.Api.Modules.Audit;
using Href.Core.Api.Modules.Auth;
using Href.Core.Api.Modules.Authorization;
using Href.Core.Api.Modules.Users;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Href.Core.Tests.Integration;

/// <summary>
/// Each test runs on its own database created in the shared container, because the guard looks for any active admin in
/// the whole database.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class LastAdminGuardIntegrationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Lock_OfTheOnlyAdmin_Returns409AndChangesNothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await CreateDatabaseAsync(cancellationToken);
        var admin = await SaveUserAsync(connectionString, "only-admin@example.test", cancellationToken);
        await SetCoreRolesAsync(connectionString, admin.Id, [CoreApplication.AdminRoleId], cancellationToken);

        await using var factory = new ApiFactory(connectionString);
        using var client = factory.CreateAdminClient();

        var response = await client.PostAsJsonAsync($"/api/users/{admin.Id}/lock", new LockUserRequest("test"), cancellationToken);

        await AssertProblemAsync(response, HttpStatusCode.Conflict, AuthorizationErrors.LastAdmin);
        Assert.Equal("Active", await StatusAsync(connectionString, admin.Id, cancellationToken));
        Assert.Equal(0L, await CountAuditAsync(connectionString, AuditActions.UserLocked, cancellationToken));
    }

    [Fact]
    public async Task UpdateEmail_OfTheOnlyAdmin_Returns409ButANameChangeSucceeds()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await CreateDatabaseAsync(cancellationToken);
        var admin = await SaveUserAsync(connectionString, "only-admin@example.test", cancellationToken);
        await SetCoreRolesAsync(connectionString, admin.Id, [CoreApplication.AdminRoleId], cancellationToken);

        await using var factory = new ApiFactory(connectionString);
        using var client = factory.CreateAdminClient();

        var emailChange = await client.PutAsJsonAsync(
            $"/api/users/{admin.Id}",
            new UpdateUserRequest("moved-admin@example.test", "Only Admin"),
            cancellationToken);
        var nameChange = await client.PutAsJsonAsync(
            $"/api/users/{admin.Id}",
            new UpdateUserRequest("only-admin@example.test", "Renamed Admin"),
            cancellationToken);

        await AssertProblemAsync(emailChange, HttpStatusCode.Conflict, AuthorizationErrors.LastAdmin);
        Assert.Equal(HttpStatusCode.OK, nameChange.StatusCode);
        Assert.Equal(
            "only-admin@example.test",
            await TextAsync(connectionString, "SELECT email FROM users WHERE id = @id", admin.Id, cancellationToken));
        Assert.Equal(0L, await CountAuditAsync(connectionString, AuditActions.UserEmailChanged, cancellationToken));
    }

    [Fact]
    public async Task RevokeCoreAccessAndClearCoreRoles_OfTheOnlyAdmin_Return409()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await CreateDatabaseAsync(cancellationToken);
        var admin = await SaveUserAsync(connectionString, "only-admin@example.test", cancellationToken);
        await SetCoreRolesAsync(connectionString, admin.Id, [CoreApplication.AdminRoleId], cancellationToken);

        await using var factory = new ApiFactory(connectionString);
        using var client = factory.CreateAdminClient();

        var revoke = await client.DeleteAsync($"/api/apps/core/users/{admin.Id}", cancellationToken);
        var clearRoles = await client.PutAsJsonAsync(
            $"/api/apps/core/users/{admin.Id}/roles",
            new ReplaceUserRolesRequest([]),
            cancellationToken);

        await AssertProblemAsync(revoke, HttpStatusCode.Conflict, AuthorizationErrors.LastAdmin);
        await AssertProblemAsync(clearRoles, HttpStatusCode.Conflict, AuthorizationErrors.LastAdmin);
        Assert.True(await IsActiveAdminAsync(connectionString, admin.Id, cancellationToken));
        Assert.Equal(0L, await CountAuditAsync(connectionString, AuditActions.AccessRevoked, cancellationToken));
        Assert.Equal(0L, await CountAuditAsync(connectionString, AuditActions.AccessRolesChanged, cancellationToken));
    }

    [Fact]
    public async Task DeleteOrStripACoreRole_ThatGivesTheOnlyAdminCoreAdmin_Returns409()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await CreateDatabaseAsync(cancellationToken);
        var admin = await SaveUserAsync(connectionString, "only-admin@example.test", cancellationToken);

        await using var factory = new ApiFactory(connectionString);
        using var client = factory.CreateAdminClient();

        var created = await client.PostAsJsonAsync(
            "/api/apps/core/roles",
            new CreateRoleRequest("operators", null, false, [CoreApplication.AdminPermissionKey]),
            cancellationToken);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var role = await created.Content.ReadFromJsonAsync<RoleResponse>(cancellationToken);
        await SetCoreRolesAsync(connectionString, admin.Id, [role!.Id], cancellationToken);

        var strip = await client.PutAsJsonAsync(
            $"/api/apps/core/roles/{role.Id}/permissions",
            new ReplaceRolePermissionsRequest([]),
            cancellationToken);
        var delete = await client.DeleteAsync($"/api/apps/core/roles/{role.Id}", cancellationToken);

        await AssertProblemAsync(strip, HttpStatusCode.Conflict, AuthorizationErrors.LastAdmin);
        await AssertProblemAsync(delete, HttpStatusCode.Conflict, AuthorizationErrors.LastAdmin);
        Assert.True(await IsActiveAdminAsync(connectionString, admin.Id, cancellationToken));
        Assert.Equal(0L, await CountAuditAsync(connectionString, AuditActions.RoleDeleted, cancellationToken));
    }

    [Fact]
    public async Task WithTwoAdmins_LockingOneSucceedsAndTheOtherBecomesProtected()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await CreateDatabaseAsync(cancellationToken);
        var first = await SaveUserAsync(connectionString, "first-admin@example.test", cancellationToken);
        var second = await SaveUserAsync(connectionString, "second-admin@example.test", cancellationToken);
        var plain = await SaveUserAsync(connectionString, "plain-user@example.test", cancellationToken);
        await SetCoreRolesAsync(connectionString, first.Id, [CoreApplication.AdminRoleId], cancellationToken);
        await SetCoreRolesAsync(connectionString, second.Id, [CoreApplication.AdminRoleId], cancellationToken);

        await using var factory = new ApiFactory(connectionString);
        using var client = factory.CreateAdminClient();

        var lockPlain = await client.PostAsJsonAsync($"/api/users/{plain.Id}/lock", new LockUserRequest(null), cancellationToken);
        var lockFirst = await client.PostAsJsonAsync($"/api/users/{first.Id}/lock", new LockUserRequest(null), cancellationToken);
        var lockSecond = await client.PostAsJsonAsync($"/api/users/{second.Id}/lock", new LockUserRequest(null), cancellationToken);

        Assert.Equal(HttpStatusCode.OK, lockPlain.StatusCode);
        Assert.Equal(HttpStatusCode.OK, lockFirst.StatusCode);
        await AssertProblemAsync(lockSecond, HttpStatusCode.Conflict, AuthorizationErrors.LastAdmin);
        Assert.True(await IsActiveAdminAsync(connectionString, second.Id, cancellationToken));
    }

    [Fact]
    public async Task WithTwoAdmins_ConcurrentLocksOfBoth_LeaveOneAdmin()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await CreateDatabaseAsync(cancellationToken);
        var first = await SaveUserAsync(connectionString, "first-admin@example.test", cancellationToken);
        var second = await SaveUserAsync(connectionString, "second-admin@example.test", cancellationToken);
        await SetCoreRolesAsync(connectionString, first.Id, [CoreApplication.AdminRoleId], cancellationToken);
        await SetCoreRolesAsync(connectionString, second.Id, [CoreApplication.AdminRoleId], cancellationToken);

        await using var factory = new ApiFactory(connectionString);
        using var firstClient = factory.CreateAdminClient();
        using var secondClient = factory.CreateAdminClient();

        var responses = await Task.WhenAll(
            firstClient.PostAsJsonAsync($"/api/users/{first.Id}/lock", new LockUserRequest(null), cancellationToken),
            secondClient.PostAsJsonAsync($"/api/users/{second.Id}/lock", new LockUserRequest(null), cancellationToken));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(
            1L,
            await ScalarAsync(connectionString, "SELECT count(*) FROM users WHERE status = 'Active'", cancellationToken));
    }

    [Fact]
    public async Task WithoutAnyAdmin_TheGuardDoesNotBlockWrites()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await CreateDatabaseAsync(cancellationToken);
        var user = await SaveUserAsync(connectionString, "plain-user@example.test", cancellationToken);

        await using var factory = new ApiFactory(connectionString);
        using var client = factory.CreateAdminClient();

        var response = await client.PostAsJsonAsync($"/api/users/{user.Id}/lock", new LockUserRequest(null), cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<string> CreateDatabaseAsync(CancellationToken cancellationToken)
    {
        var name = $"last_admin_{Guid.NewGuid():N}";

        await using (var connection = new NpgsqlConnection(postgres.ConnectionString))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand($"CREATE DATABASE {name}", connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var connectionString = new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { Database = name }.ConnectionString;
        DatabaseMigrator.Migrate(connectionString, NullLogger.Instance);
        return connectionString;
    }

    private static async Task<User> SaveUserAsync(string connectionString, string email, CancellationToken cancellationToken)
    {
        var hasher = new PasswordHasher<User>(Options.Create(new PasswordHasherOptions { IterationCount = 1_000 }));
        var user = User.Create(email, "Test User", "correct horse battery staple", hasher).Value;
        user.MarkEmailVerified(DateTime.UtcNow);

        using var sessionFactory = NHibernateExtensions.BuildSessionFactory(connectionString, TimeProvider.System);
        using var session = sessionFactory.OpenSession();
        using var transaction = session.BeginTransaction();
        await session.SaveAsync(user, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return user;
    }

    private static async Task SetCoreRolesAsync(
        string connectionString,
        Guid userId,
        IReadOnlyList<Guid> roleIds,
        CancellationToken cancellationToken)
    {
        using var sessionFactory = NHibernateExtensions.BuildSessionFactory(connectionString, TimeProvider.System);
        using var session = sessionFactory.OpenSession();
        using var transaction = session.BeginTransaction();
        var roles = new List<Role>();

        foreach (var roleId in roleIds)
        {
            roles.Add(await session.GetAsync<Role>(roleId, cancellationToken));
        }

        await session.SaveAsync(UserApplicationAccess.Grant(userId, CoreApplication.Id, roles).Value, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<bool> IsActiveAdminAsync(string connectionString, Guid userId, CancellationToken cancellationToken)
    {
        const string Sql = """
            SELECT count(*)
            FROM users u
            JOIN user_application_accesses a ON a.user_id = u.id AND a.application_id = @coreId
            JOIN user_application_access_roles ar ON ar.user_application_access_id = a.id
            JOIN role_permissions rp ON rp.role_id = ar.role_id AND rp.permission_id = @permissionId
            WHERE u.id = @id AND u.status = 'Active' AND u.email_verified_at_utc IS NOT NULL
            """;

        return await ScalarAsync(
            connectionString,
            Sql,
            cancellationToken,
            ("id", userId),
            ("coreId", CoreApplication.Id),
            ("permissionId", CoreApplication.AdminPermissionId)) > 0;
    }

    private static async Task<string> StatusAsync(string connectionString, Guid userId, CancellationToken cancellationToken) =>
        await TextAsync(connectionString, "SELECT status FROM users WHERE id = @id", userId, cancellationToken);

    private static async Task<string> TextAsync(string connectionString, string sql, Guid id, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        return (string)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static Task<long> CountAuditAsync(string connectionString, string action, CancellationToken cancellationToken) =>
        ScalarAsync(connectionString, "SELECT count(*) FROM audit_logs WHERE action = @action", cancellationToken, ("action", action));

    private static async Task<long> ScalarAsync(
        string connectionString,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, Error error)
    {
        Assert.Equal(status, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(TestContext.Current.CancellationToken);
        Assert.NotNull(problem);
        Assert.Equal(error.Code, problem.Extensions["errorCode"]?.ToString());
    }
}
