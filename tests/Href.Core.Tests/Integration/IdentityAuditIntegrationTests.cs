using System.Text.Json;
using Href.Core.Api.Modules.Applications;
using Href.Core.Api.Modules.Audit;
using Href.Core.Api.Modules.Authorization;
using Href.Core.Api.Modules.Users;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using NHibernate.Linq;

namespace Href.Core.Tests.Integration;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class IdentityAuditIntegrationTests(PostgresFixture postgres)
{
    private static readonly DateTime Now = new(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc);

    private static readonly PasswordHasher<User> Hasher = new(
        Options.Create(new PasswordHasherOptions { IterationCount = 1_000 }));

    [Fact]
    public async Task UserChangeAndItsAuditRow_CommitTogether()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var user = User.Create($"audit-{Guid.NewGuid():N}@example.com", "Ana", "correct horse battery", Hasher).Value;
        await SaveAsync(user, cancellationToken);

        using (var session = postgres.SessionFactory.OpenSession())
        using (var transaction = session.BeginTransaction())
        {
            var auditService = new AuditService(session, new HttpContextAccessor(), TimeProvider.System);
            var loaded = await session.GetAsync<User>(user.Id, cancellationToken);
            var before = UserAuditSnapshot.From(loaded);
            Assert.True(loaded.Lock("Suspicious activity", Now).IsSuccess);
            await auditService.RecordUserChangeAsync(AuditActions.UserLocked, before, UserAuditSnapshot.From(loaded), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        using var verifySession = postgres.SessionFactory.OpenSession();
        var persisted = await verifySession.GetAsync<User>(user.Id, cancellationToken);
        var auditRow = Assert.Single(await AuditRowsAsync(verifySession, "User", user.Id, cancellationToken));

        Assert.Equal(UserStatus.Locked, persisted.Status);
        Assert.Equal(AuditActions.UserLocked, auditRow.Action);
        Assert.Equal(AuditActorType.System, auditRow.ActorType);
        Assert.Null(auditRow.AppKey);
        Assert.Equal("Active", ReadString(auditRow.OldValues, "status"));
        Assert.Equal("Locked", ReadString(auditRow.NewValues, "status"));
        Assert.Equal("Suspicious activity", ReadString(auditRow.NewValues, "lockReason"));
        Assert.DoesNotContain("passwordHash", auditRow.NewValues, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("securityStamp", auditRow.NewValues, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(persisted.PasswordHash, auditRow.NewValues, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UserChangeAndItsAuditRow_RollBackTogether()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var user = User.Create($"audit-{Guid.NewGuid():N}@example.com", "Ana", "correct horse battery", Hasher).Value;
        await SaveAsync(user, cancellationToken);

        using (var session = postgres.SessionFactory.OpenSession())
        using (var transaction = session.BeginTransaction())
        {
            var auditService = new AuditService(session, new HttpContextAccessor(), TimeProvider.System);
            var loaded = await session.GetAsync<User>(user.Id, cancellationToken);
            var before = UserAuditSnapshot.From(loaded);
            Assert.True(loaded.UpdateProfile("Ana María").IsSuccess);
            await auditService.RecordUserChangeAsync(AuditActions.UserUpdated, before, UserAuditSnapshot.From(loaded), cancellationToken);
            await session.FlushAsync(cancellationToken);
            await transaction.RollbackAsync(cancellationToken);
        }

        using var verifySession = postgres.SessionFactory.OpenSession();
        var persisted = await verifySession.GetAsync<User>(user.Id, cancellationToken);

        Assert.Equal("Ana", persisted.FullName);
        Assert.Empty(await AuditRowsAsync(verifySession, "User", user.Id, cancellationToken));
    }

    [Fact]
    public async Task RoleChangeAndItsAuditRow_CommitTogetherUnderTheAffectedApplication()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (application, _) = Application.Create($"audit-{Guid.NewGuid():N}"[..20], "Audit App", null, false, TimeProvider.System).Value;
        await SaveAsync(application, cancellationToken);
        var read = Permission.Create(application.Id, "orders.read", null).Value;
        var role = Role.Create(application.Id, "Admin", null).Value;

        using (var session = postgres.SessionFactory.OpenSession())
        using (var transaction = session.BeginTransaction())
        {
            var auditService = new AuditService(session, new HttpContextAccessor(), TimeProvider.System);
            await session.SaveAsync(read, cancellationToken);
            await session.SaveAsync(role, cancellationToken);
            var before = RoleAuditSnapshot.From(role);
            role.AddPermission(read);
            await auditService.RecordRoleChangeAsync(
                AuditActions.RolePermissionsChanged,
                application.AppKey,
                before,
                RoleAuditSnapshot.From(role),
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        using var verifySession = postgres.SessionFactory.OpenSession();
        var persisted = await verifySession.GetAsync<Role>(role.Id, cancellationToken);
        var auditRow = Assert.Single(await AuditRowsAsync(verifySession, "Role", role.Id, cancellationToken));

        Assert.Equal(["orders.read"], persisted.GetPermissionKeys());
        Assert.Equal(AuditActions.RolePermissionsChanged, auditRow.Action);
        Assert.Equal(application.AppKey, auditRow.AppKey);
        Assert.Empty(ReadStrings(auditRow.OldValues, "permissionKeys"));
        Assert.Equal(["orders.read"], ReadStrings(auditRow.NewValues, "permissionKeys"));
    }

    private static string? ReadString(string? json, string property)
    {
        Assert.NotNull(json);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty(property).GetString();
    }

    private static List<string?> ReadStrings(string? json, string property)
    {
        Assert.NotNull(json);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty(property).EnumerateArray().Select(item => item.GetString()).ToList();
    }

    private static async Task<List<AuditLog>> AuditRowsAsync(
        NHibernate.ISession session,
        string entityType,
        Guid entityId,
        CancellationToken cancellationToken)
    {
        var id = entityId.ToString();
        return await session.Query<AuditLog>()
            .Where(row => row.EntityType == entityType && row.EntityId == id)
            .ToListAsync(cancellationToken);
    }

    private async Task SaveAsync(object entity, CancellationToken cancellationToken)
    {
        using var session = postgres.SessionFactory.OpenSession();
        using var transaction = session.BeginTransaction();
        await session.SaveAsync(entity, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
