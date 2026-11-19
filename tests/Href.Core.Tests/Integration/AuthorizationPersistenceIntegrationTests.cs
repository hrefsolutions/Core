using Href.Core.Api.Modules.Applications;
using Href.Core.Api.Modules.Authorization;
using Href.Core.Api.Modules.Users;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using NHibernate;
using NHibernate.Exceptions;
using Npgsql;

namespace Href.Core.Tests.Integration;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AuthorizationPersistenceIntegrationTests(PostgresFixture postgres)
{
    private static readonly PasswordHasher<User> Hasher = new(
        Options.Create(new PasswordHasherOptions { IterationCount = 1_000 }));

    [Fact]
    public async Task RolesPermissionsAndAccess_RoundTripTheManyToManyRelations()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var application = await SaveApplicationAsync(cancellationToken);
        var user = await SaveUserAsync(cancellationToken);
        var read = Permission.Create(application.Id, "orders.read", "Read orders").Value;
        var write = Permission.Create(application.Id, "orders.write", null).Value;
        var export = Permission.Create(application.Id, "billing.export", null).Value;
        var admin = Role.Create(application.Id, "Admin", "Full access").Value;
        admin.AddPermission(read);
        admin.AddPermission(write);
        var accountant = Role.Create(application.Id, "Accountant", null).Value;
        accountant.AddPermission(read);
        accountant.AddPermission(export);
        accountant.MakeDefault(null);
        var access = UserApplicationAccess.Grant(user.Id, application.Id, [admin, accountant]).Value;

        await SaveAllAsync(cancellationToken, read, write, export, admin, accountant, access);

        using var session = postgres.SessionFactory.OpenSession();
        var loadedAccess = await session.GetAsync<UserApplicationAccess>(access.Id, cancellationToken);
        var loadedAdmin = await session.GetAsync<Role>(admin.Id, cancellationToken);
        var loadedAccountant = await session.GetAsync<Role>(accountant.Id, cancellationToken);
        var loadedRead = await session.GetAsync<Permission>(read.Id, cancellationToken);

        Assert.Equal(user.Id, loadedAccess.UserId);
        Assert.Equal(application.Id, loadedAccess.ApplicationId);
        Assert.Equal(["Accountant", "Admin"], loadedAccess.GetRoleNames());
        Assert.Equal(["billing.export", "orders.read", "orders.write"], loadedAccess.GetPermissionKeys());
        Assert.Equal(["orders.read", "orders.write"], loadedAdmin.GetPermissionKeys());
        Assert.Equal("Full access", loadedAdmin.Description);
        Assert.False(loadedAdmin.IsDefault);
        Assert.True(loadedAccountant.IsDefault);
        Assert.Equal("Read orders", loadedRead.Description);
        Assert.Equal(application.Id, loadedRead.ApplicationId);
        Assert.NotEqual(default, loadedAccess.CreatedAtUtc);
        Assert.NotEqual(default, loadedAdmin.CreatedAtUtc);
        Assert.NotEqual(default, loadedRead.CreatedAtUtc);
        Assert.Equal(DateTimeKind.Utc, loadedAccess.CreatedAtUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, loadedAdmin.UpdatedAtUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, loadedRead.UpdatedAtUtc.Kind);
    }

    [Fact]
    public async Task ChangingRoleAndAccessSets_PersistsAddsAndRemovals()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var application = await SaveApplicationAsync(cancellationToken);
        var user = await SaveUserAsync(cancellationToken);
        var read = Permission.Create(application.Id, "orders.read", null).Value;
        var write = Permission.Create(application.Id, "orders.write", null).Value;
        var admin = Role.Create(application.Id, "Admin", null).Value;
        var viewer = Role.Create(application.Id, "Viewer", null).Value;
        admin.AddPermission(read);
        var access = UserApplicationAccess.Grant(user.Id, application.Id, [admin]).Value;
        await SaveAllAsync(cancellationToken, read, write, admin, viewer, access);
        DateTime adminUpdatedAtUtc;
        DateTime accessUpdatedAtUtc;

        using (var session = postgres.SessionFactory.OpenSession())
        using (var transaction = session.BeginTransaction())
        {
            var loadedAdmin = await session.GetAsync<Role>(admin.Id, cancellationToken);
            var loadedWrite = await session.GetAsync<Permission>(write.Id, cancellationToken);
            var loadedViewer = await session.GetAsync<Role>(viewer.Id, cancellationToken);
            var loadedAccess = await session.GetAsync<UserApplicationAccess>(access.Id, cancellationToken);
            adminUpdatedAtUtc = loadedAdmin.UpdatedAtUtc;
            accessUpdatedAtUtc = loadedAccess.UpdatedAtUtc;
            Assert.True(loadedAdmin.ReplacePermissions([loadedWrite]).IsSuccess);
            Assert.True(loadedAccess.ReplaceRoles([loadedViewer]).IsSuccess);
            await transaction.CommitAsync(cancellationToken);
        }

        using var verifySession = postgres.SessionFactory.OpenSession();
        var reloadedAdmin = await verifySession.GetAsync<Role>(admin.Id, cancellationToken);
        var reloadedAccess = await verifySession.GetAsync<UserApplicationAccess>(access.Id, cancellationToken);

        Assert.Equal(["orders.write"], reloadedAdmin.GetPermissionKeys());
        Assert.Equal(["Viewer"], reloadedAccess.GetRoleNames());
        Assert.Equal(adminUpdatedAtUtc, reloadedAdmin.UpdatedAtUtc);
        Assert.Equal(accessUpdatedAtUtc, reloadedAccess.UpdatedAtUtc);
    }

    [Fact]
    public async Task DeletingRole_RemovesItFromAccessesAndKeepsPermissions()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var application = await SaveApplicationAsync(cancellationToken);
        var user = await SaveUserAsync(cancellationToken);
        var read = Permission.Create(application.Id, "orders.read", null).Value;
        var admin = Role.Create(application.Id, "Admin", null).Value;
        var viewer = Role.Create(application.Id, "Viewer", null).Value;
        admin.AddPermission(read);
        viewer.AddPermission(read);
        var access = UserApplicationAccess.Grant(user.Id, application.Id, [admin, viewer]).Value;
        await SaveAllAsync(cancellationToken, read, admin, viewer, access);

        using (var session = postgres.SessionFactory.OpenSession())
        using (var transaction = session.BeginTransaction())
        {
            await session.DeleteAsync(await session.GetAsync<Role>(admin.Id, cancellationToken), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        using var verifySession = postgres.SessionFactory.OpenSession();
        var reloadedAccess = await verifySession.GetAsync<UserApplicationAccess>(access.Id, cancellationToken);

        Assert.Null(await verifySession.GetAsync<Role>(admin.Id, cancellationToken));
        Assert.NotNull(await verifySession.GetAsync<Permission>(read.Id, cancellationToken));
        Assert.Equal(["Viewer"], reloadedAccess.GetRoleNames());
        Assert.Equal(0L, await CountRowsAsync(verifySession, "role_permissions", "role_id", admin.Id, cancellationToken));
        Assert.Equal(0L, await CountRowsAsync(verifySession, "user_application_access_roles", "role_id", admin.Id, cancellationToken));
    }

    [Fact]
    public async Task DeletingPermission_RemovesItFromRoles()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var application = await SaveApplicationAsync(cancellationToken);
        var read = Permission.Create(application.Id, "orders.read", null).Value;
        var write = Permission.Create(application.Id, "orders.write", null).Value;
        var admin = Role.Create(application.Id, "Admin", null).Value;
        admin.AddPermission(read);
        admin.AddPermission(write);
        await SaveAllAsync(cancellationToken, read, write, admin);

        using (var session = postgres.SessionFactory.OpenSession())
        using (var transaction = session.BeginTransaction())
        {
            await session.DeleteAsync(await session.GetAsync<Permission>(read.Id, cancellationToken), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        using var verifySession = postgres.SessionFactory.OpenSession();
        var reloadedAdmin = await verifySession.GetAsync<Role>(admin.Id, cancellationToken);

        Assert.Equal(["orders.write"], reloadedAdmin.GetPermissionKeys());
    }

    [Fact]
    public async Task RevokingAccess_DeletesTheRowAndItsRoleLinksButKeepsRoles()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var application = await SaveApplicationAsync(cancellationToken);
        var user = await SaveUserAsync(cancellationToken);
        var admin = Role.Create(application.Id, "Admin", null).Value;
        var access = UserApplicationAccess.Grant(user.Id, application.Id, [admin]).Value;
        await SaveAllAsync(cancellationToken, admin, access);

        using (var session = postgres.SessionFactory.OpenSession())
        using (var transaction = session.BeginTransaction())
        {
            await session.DeleteAsync(await session.GetAsync<UserApplicationAccess>(access.Id, cancellationToken), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        using var verifySession = postgres.SessionFactory.OpenSession();

        Assert.Null(await verifySession.GetAsync<UserApplicationAccess>(access.Id, cancellationToken));
        Assert.NotNull(await verifySession.GetAsync<Role>(admin.Id, cancellationToken));
        Assert.Equal(0L, await CountRowsAsync(verifySession, "user_application_access_roles", "user_application_access_id", access.Id, cancellationToken));
    }

    [Fact]
    public async Task DuplicatePermissionKeyInSameApplication_IsRejectedButAllowedInAnotherApplication()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var application = await SaveApplicationAsync(cancellationToken);
        var otherApplication = await SaveApplicationAsync(cancellationToken);
        await SaveAllAsync(cancellationToken, Permission.Create(application.Id, "orders.read", null).Value);
        await SaveAllAsync(cancellationToken, Permission.Create(otherApplication.Id, "orders.read", null).Value);

        await AssertUniqueViolationAsync(
            Permission.Create(application.Id, "orders.read", null).Value,
            Permission.PermissionKeyUniqueIndexName,
            cancellationToken);
    }

    [Fact]
    public async Task DuplicateRoleNameInSameApplicationIgnoringCase_IsRejectedButAllowedInAnotherApplication()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var application = await SaveApplicationAsync(cancellationToken);
        var otherApplication = await SaveApplicationAsync(cancellationToken);
        await SaveAllAsync(cancellationToken, Role.Create(application.Id, "Admin", null).Value);
        await SaveAllAsync(cancellationToken, Role.Create(otherApplication.Id, "Admin", null).Value);

        await AssertUniqueViolationAsync(
            Role.Create(application.Id, "ADMIN", null).Value,
            Role.NameUniqueIndexName,
            cancellationToken);
    }

    [Fact]
    public async Task SecondDefaultRoleInSameApplication_IsRejectedButAllowedInAnotherApplication()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var application = await SaveApplicationAsync(cancellationToken);
        var otherApplication = await SaveApplicationAsync(cancellationToken);
        await SaveAllAsync(cancellationToken, CreateDefaultRole(application.Id, "Member"));
        await SaveAllAsync(cancellationToken, CreateDefaultRole(otherApplication.Id, "Member"));
        await SaveAllAsync(cancellationToken, Role.Create(application.Id, "Guest", null).Value);

        await AssertUniqueViolationAsync(
            CreateDefaultRole(application.Id, "Customer"),
            Role.DefaultUniqueIndexName,
            cancellationToken);
    }

    [Fact]
    public async Task SecondAccessForSameUserAndApplication_IsRejected()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var application = await SaveApplicationAsync(cancellationToken);
        var otherApplication = await SaveApplicationAsync(cancellationToken);
        var user = await SaveUserAsync(cancellationToken);
        await SaveAllAsync(cancellationToken, UserApplicationAccess.Grant(user.Id, application.Id, []).Value);
        await SaveAllAsync(cancellationToken, UserApplicationAccess.Grant(user.Id, otherApplication.Id, []).Value);

        await AssertUniqueViolationAsync(
            UserApplicationAccess.Grant(user.Id, application.Id, []).Value,
            UserApplicationAccess.UserApplicationUniqueIndexName,
            cancellationToken);
    }

    [Fact]
    public async Task AccessForUnknownUser_IsRejectedByForeignKey()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var application = await SaveApplicationAsync(cancellationToken);
        var orphan = UserApplicationAccess.Grant(Guid.CreateVersion7(), application.Id, []).Value;

        using var session = postgres.SessionFactory.OpenSession();
        using var transaction = session.BeginTransaction();
        await session.SaveAsync(orphan, cancellationToken);

        var exception = await Assert.ThrowsAsync<GenericADOException>(() => transaction.CommitAsync(cancellationToken));

        var postgresException = PostgresExceptions.Find(exception);
        Assert.NotNull(postgresException);
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, postgresException.SqlState);
        Assert.Equal("fk_user_application_accesses_user_id", postgresException.ConstraintName);
    }

    [Fact]
    public async Task PermissionForUnknownApplication_IsRejectedByForeignKey()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var orphan = Permission.Create(Guid.CreateVersion7(), "orders.read", null).Value;

        using var session = postgres.SessionFactory.OpenSession();
        using var transaction = session.BeginTransaction();
        await session.SaveAsync(orphan, cancellationToken);

        var exception = await Assert.ThrowsAsync<GenericADOException>(() => transaction.CommitAsync(cancellationToken));

        var postgresException = PostgresExceptions.Find(exception);
        Assert.NotNull(postgresException);
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, postgresException.SqlState);
        Assert.Equal("fk_permissions_application_id", postgresException.ConstraintName);
    }

    private static Role CreateDefaultRole(Guid applicationId, string name)
    {
        var role = Role.Create(applicationId, name, null).Value;
        role.MakeDefault(null);
        return role;
    }

    private static async Task<long> CountRowsAsync(
        ISession session,
        string table,
        string column,
        Guid id,
        CancellationToken cancellationToken) =>
        Convert.ToInt64(await session
            .CreateSQLQuery($"SELECT COUNT(*) FROM {table} WHERE {column} = :id")
            .SetParameter("id", id)
            .UniqueResultAsync(cancellationToken));

    private async Task AssertUniqueViolationAsync(object entity, string indexName, CancellationToken cancellationToken)
    {
        using var session = postgres.SessionFactory.OpenSession();
        using var transaction = session.BeginTransaction();
        await session.SaveAsync(entity, cancellationToken);

        var exception = await Assert.ThrowsAsync<GenericADOException>(() => transaction.CommitAsync(cancellationToken));

        var postgresException = PostgresExceptions.Find(exception);
        Assert.NotNull(postgresException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgresException.SqlState);
        Assert.Equal(indexName, postgresException.ConstraintName);
    }

    private async Task SaveAllAsync(CancellationToken cancellationToken, params object[] entities)
    {
        using var session = postgres.SessionFactory.OpenSession();
        using var transaction = session.BeginTransaction();

        foreach (var entity in entities)
        {
            await session.SaveAsync(entity, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<Application> SaveApplicationAsync(CancellationToken cancellationToken)
    {
        var application = Application.Create($"rbac-{Guid.NewGuid():N}"[..20], "RBAC App", null, false, TimeProvider.System).Value.Application;
        await SaveAllAsync(cancellationToken, application);
        return application;
    }

    private async Task<User> SaveUserAsync(CancellationToken cancellationToken)
    {
        var user = User.Create($"rbac-{Guid.NewGuid():N}@example.com", "RBAC User", "correct horse battery", Hasher).Value;
        await SaveAllAsync(cancellationToken, user);
        return user;
    }
}
