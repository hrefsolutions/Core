using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Href.Core.Api.Common.Persistence;
using Href.Core.Api.Modules.Applications;
using Href.Core.Api.Modules.Audit;
using Href.Core.Api.Modules.Auth;
using Href.Core.Api.Modules.Users;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Href.Core.Tests.Integration;

/// <summary>
/// Each test runs on its own database created in the shared container, because the bootstrap looks for any active
/// admin in the whole database.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AdminBootstrapIntegrationTests(PostgresFixture postgres)
{
    private const string AdminEmail = "root@example.test";
    private const string AdminPassword = "correct horse battery staple";
    private static readonly string CoreSecret = new('s', 40);

    [Fact]
    public async Task Bootstrap_CreatesTheAdminOnceAndTheAdminCanLogInThroughTheCoreApplication()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await CreateDatabaseAsync(cancellationToken);

        await using (var factory = new ApiFactory(connectionString, overrides: BootstrapSettings()))
        {
            using var core = CoreClient(factory, CoreSecret);
            var login = await core.PostAsJsonAsync("/api/auth/login", new LoginRequest(AdminEmail, AdminPassword), cancellationToken);
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            var tokens = await login.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken);

            using var admin = factory.CreateClient();
            admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);
            var users = await admin.GetAsync("/api/users", cancellationToken);
            Assert.Equal(HttpStatusCode.OK, users.StatusCode);
        }

        await using (var again = new ApiFactory(connectionString, overrides: BootstrapSettings()))
        {
            using var client = again.CreateClient();
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health", cancellationToken)).StatusCode);
        }

        Assert.Equal(1L, await ScalarAsync(connectionString, "SELECT count(*) FROM users WHERE email = @email", cancellationToken, ("email", AdminEmail)));
        Assert.Equal(1L, await ScalarAsync(
            connectionString,
            """
            SELECT count(*) FROM user_application_accesses a
            JOIN user_application_access_roles r ON r.user_application_access_id = a.id
            JOIN users u ON u.id = a.user_id
            WHERE u.email = @email AND a.application_id = @core AND r.role_id = @role
            """,
            cancellationToken,
            ("email", AdminEmail),
            ("core", CoreApplication.Id),
            ("role", CoreApplication.AdminRoleId)));
        Assert.Equal(1L, await CountAuditAsync(connectionString, AuditActions.UserCreated, cancellationToken));
        Assert.Equal(1L, await CountAuditAsync(connectionString, AuditActions.AccessGranted, cancellationToken));
        Assert.Equal(1L, await CountAuditAsync(connectionString, AuditActions.ApplicationSecretRotated, cancellationToken));
        Assert.Equal(0L, await ScalarAsync(
            connectionString,
            "SELECT count(*) FROM audit_logs WHERE action = ANY(@actions) AND actor_type <> 'System'",
            cancellationToken,
            ("actions", new[] { AuditActions.UserCreated, AuditActions.AccessGranted, AuditActions.ApplicationSecretRotated })));
        Assert.Equal(0L, await ScalarAsync(
            connectionString,
            "SELECT count(*) FROM audit_logs WHERE new_values::text LIKE '%' || @secret || '%'",
            cancellationToken,
            ("secret", CoreSecret)));
    }

    [Fact]
    public async Task Bootstrap_WithAnExistingVerifiedUser_GrantsAdminAccessAndKeepsThePassword()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await CreateDatabaseAsync(cancellationToken);
        const string ExistingPassword = "existing horse battery";
        await SaveUserAsync(connectionString, AdminEmail, ExistingPassword, verified: true, cancellationToken);

        await using var factory = new ApiFactory(connectionString, overrides: BootstrapSettings());
        using var core = CoreClient(factory, CoreSecret);

        var withExistingPassword = await core.PostAsJsonAsync("/api/auth/login", new LoginRequest(AdminEmail, ExistingPassword), cancellationToken);
        var withBootstrapPassword = await core.PostAsJsonAsync("/api/auth/login", new LoginRequest(AdminEmail, AdminPassword), cancellationToken);

        Assert.Equal(HttpStatusCode.OK, withExistingPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, withBootstrapPassword.StatusCode);
        Assert.Equal(1L, await ScalarAsync(connectionString, "SELECT count(*) FROM users", cancellationToken));
        Assert.Equal(0L, await CountAuditAsync(connectionString, AuditActions.UserUpdated, cancellationToken));
        Assert.Equal(0L, await CountAuditAsync(connectionString, AuditActions.UserCreated, cancellationToken));
        Assert.Equal(1L, await CountAuditAsync(connectionString, AuditActions.AccessGranted, cancellationToken));
    }

    [Fact]
    public async Task Bootstrap_WithAnExistingUnverifiedUser_ReplacesItsPasswordBeforeGrantingAdmin()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await CreateDatabaseAsync(cancellationToken);
        const string SquatterPassword = "squatter horse battery";
        await SaveUserAsync(connectionString, AdminEmail, SquatterPassword, verified: false, cancellationToken);

        await using var factory = new ApiFactory(connectionString, overrides: BootstrapSettings());
        using var core = CoreClient(factory, CoreSecret);

        var withSquatterPassword = await core.PostAsJsonAsync("/api/auth/login", new LoginRequest(AdminEmail, SquatterPassword), cancellationToken);
        var withBootstrapPassword = await core.PostAsJsonAsync("/api/auth/login", new LoginRequest(AdminEmail, AdminPassword), cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, withSquatterPassword.StatusCode);
        Assert.Equal(HttpStatusCode.OK, withBootstrapPassword.StatusCode);
        Assert.Equal(1L, await CountAuditAsync(connectionString, AuditActions.UserUpdated, cancellationToken));
    }

    [Fact]
    public async Task Bootstrap_WhenTheOnlyAdminIsUnverified_CreatesANewAdmin()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await CreateDatabaseAsync(cancellationToken);
        var stuckAdmin = await SaveUserAsync(connectionString, "stuck-admin@example.test", "stuck horse battery", verified: false, cancellationToken);

        using (var sessionFactory = NHibernateExtensions.BuildSessionFactory(connectionString, TimeProvider.System))
        using (var session = sessionFactory.OpenSession())
        using (var transaction = session.BeginTransaction())
        {
            var adminRole = await session.GetAsync<Href.Core.Api.Modules.Authorization.Role>(CoreApplication.AdminRoleId, cancellationToken);
            await session.SaveAsync(
                Href.Core.Api.Modules.Authorization.UserApplicationAccess.Grant(stuckAdmin.Id, CoreApplication.Id, [adminRole]).Value,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        await using var factory = new ApiFactory(connectionString, overrides: BootstrapSettings());
        using var core = CoreClient(factory, CoreSecret);

        var login = await core.PostAsJsonAsync("/api/auth/login", new LoginRequest(AdminEmail, AdminPassword), cancellationToken);

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Equal(1L, await CountAuditAsync(connectionString, AuditActions.UserCreated, cancellationToken));
    }

    [Fact]
    public async Task Bootstrap_WithoutVariables_ChangesNothingAndTheCoreSecretStaysUnknown()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await CreateDatabaseAsync(cancellationToken);

        await using var factory = new ApiFactory(connectionString);
        using var core = CoreClient(factory, CoreSecret);

        var login = await core.PostAsJsonAsync("/api/auth/login", new LoginRequest(AdminEmail, AdminPassword), cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        Assert.Equal(0L, await ScalarAsync(connectionString, "SELECT count(*) FROM users", cancellationToken));
        Assert.Equal(0L, await ScalarAsync(connectionString, "SELECT count(*) FROM audit_logs", cancellationToken));
    }

    [Fact]
    public void Api_WithOnlyTheAdminEmail_DoesNotStart()
    {
        using var factory = new ApiFactory(
            postgres.ConnectionString,
            overrides: new Dictionary<string, string?> { ["Bootstrap:AdminEmail"] = AdminEmail });

        var exception = Record.Exception(() => factory.CreateClient());

        Assert.NotNull(exception);
    }

    private static Dictionary<string, string?> BootstrapSettings() => new()
    {
        ["Bootstrap:AdminEmail"] = AdminEmail,
        ["Bootstrap:AdminPassword"] = AdminPassword,
        ["Bootstrap:AdminFullName"] = "Root Admin",
        ["Bootstrap:CoreClientSecret"] = CoreSecret,
    };

    private static HttpClient CoreClient(ApiFactory factory, string secret)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(AppKeyAuthentication.AppKeyHeaderName, CoreApplication.AppKey);
        client.DefaultRequestHeaders.Add(AppKeyAuthentication.AppSecretHeaderName, secret);
        return client;
    }

    private async Task<string> CreateDatabaseAsync(CancellationToken cancellationToken)
    {
        var name = $"bootstrap_{Guid.NewGuid():N}";

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

    private static async Task<User> SaveUserAsync(
        string connectionString,
        string email,
        string password,
        bool verified,
        CancellationToken cancellationToken)
    {
        var hasher = new PasswordHasher<User>(Options.Create(new PasswordHasherOptions { IterationCount = 1_000 }));
        var user = User.Create(email, "Existing User", password, hasher).Value;

        if (verified)
        {
            user.MarkEmailVerified(DateTime.UtcNow);
        }

        using var sessionFactory = NHibernateExtensions.BuildSessionFactory(connectionString, TimeProvider.System);
        using var session = sessionFactory.OpenSession();
        using var transaction = session.BeginTransaction();
        await session.SaveAsync(user, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return user;
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
}
