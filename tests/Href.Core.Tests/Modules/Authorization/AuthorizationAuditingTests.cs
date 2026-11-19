using System.Text.Json;
using Href.Core.Api.Modules.Audit;
using Href.Core.Api.Modules.Authorization;
using Href.Core.Tests.Modules.Audit;

namespace Href.Core.Tests.Modules.Authorization;

public sealed class AuthorizationAuditingTests
{
    private const string AppKey = "billing-portal";

    private static readonly Guid ApplicationId = Guid.CreateVersion7();
    private static readonly Guid UserId = Guid.CreateVersion7();

    [Fact]
    public void PermissionSnapshot_SerializesItsFieldsInCamelCase()
    {
        var permission = Permission.Create(ApplicationId, "orders.read", "Read orders").Value;

        using var document = JsonDocument.Parse(AuditService.SerializeSnapshot(PermissionAuditSnapshot.From(permission))!);
        var root = document.RootElement;

        Assert.Equal(["applicationId", "description", "id", "key"], PropertyNames(root));
        Assert.Equal(permission.Id, root.GetProperty("id").GetGuid());
        Assert.Equal(ApplicationId, root.GetProperty("applicationId").GetGuid());
        Assert.Equal("orders.read", root.GetProperty("key").GetString());
        Assert.Equal("Read orders", root.GetProperty("description").GetString());
    }

    [Fact]
    public void RoleSnapshot_SerializesSortedPermissionKeys()
    {
        var role = Role.Create(ApplicationId, "Admin", null).Value;
        role.AddPermission(Permission.Create(ApplicationId, "orders.write", null).Value);
        role.AddPermission(Permission.Create(ApplicationId, "billing.export", null).Value);
        role.MakeDefault(null);

        using var document = JsonDocument.Parse(AuditService.SerializeSnapshot(RoleAuditSnapshot.From(role))!);
        var root = document.RootElement;

        Assert.Equal(["applicationId", "description", "id", "isDefault", "name", "permissionKeys"], PropertyNames(root));
        Assert.Equal("Admin", root.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("description").ValueKind);
        Assert.True(root.GetProperty("isDefault").GetBoolean());
        Assert.Equal(
            ["billing.export", "orders.write"],
            root.GetProperty("permissionKeys").EnumerateArray().Select(key => key.GetString()));
    }

    [Fact]
    public void AccessSnapshot_SerializesRolesSortedByNameWithTheirIds()
    {
        var viewer = Role.Create(ApplicationId, "Viewer", null).Value;
        var admin = Role.Create(ApplicationId, "Admin", null).Value;
        var access = UserApplicationAccess.Grant(UserId, ApplicationId, [viewer, admin]).Value;

        using var document = JsonDocument.Parse(AuditService.SerializeSnapshot(AccessAuditSnapshot.From(access))!);
        var root = document.RootElement;

        Assert.Equal(["applicationId", "id", "roles", "userId"], PropertyNames(root));
        Assert.Equal(UserId, root.GetProperty("userId").GetGuid());
        var roles = root.GetProperty("roles").EnumerateArray().ToList();
        Assert.Equal(["Admin", "Viewer"], roles.Select(role => role.GetProperty("name").GetString()));
        Assert.Equal([admin.Id, viewer.Id], roles.Select(role => role.GetProperty("id").GetGuid()));
        Assert.Equal(["id", "name"], PropertyNames(roles[0]));
    }

    [Fact]
    public async Task RecordRoleChangeAsync_RecordsTheRoleUnderTheAffectedApplication()
    {
        var auditService = new CapturingAuditService();
        var role = Role.Create(ApplicationId, "Admin", null).Value;
        var before = RoleAuditSnapshot.From(role);
        role.AddPermission(Permission.Create(ApplicationId, "orders.read", null).Value);
        var after = RoleAuditSnapshot.From(role);

        await auditService.RecordRoleChangeAsync(AuditActions.RolePermissionsChanged, AppKey, before, after, TestContext.Current.CancellationToken);

        var entry = Assert.Single(auditService.Entries);
        Assert.Equal("ROLE_PERMISSIONS_CHANGED", entry.Action);
        Assert.Equal("Role", entry.EntityType);
        Assert.Equal(role.Id.ToString(), entry.EntityId);
        Assert.Equal(AppKey, entry.AppKeyOverride);
        Assert.Same(before, entry.OldValues);
        Assert.Same(after, entry.NewValues);
    }

    [Fact]
    public async Task RecordRoleChangeAsync_ForDeletion_HasNoNewValues()
    {
        var auditService = new CapturingAuditService();
        var before = RoleAuditSnapshot.From(Role.Create(ApplicationId, "Admin", null).Value);

        await auditService.RecordRoleChangeAsync(AuditActions.RoleDeleted, AppKey, before, null, TestContext.Current.CancellationToken);

        var entry = Assert.Single(auditService.Entries);
        Assert.Equal("ROLE_DELETED", entry.Action);
        Assert.Equal(before.Id.ToString(), entry.EntityId);
        Assert.Null(entry.NewValues);
    }

    [Fact]
    public async Task RecordPermissionChangeAsync_RecordsThePermissionUnderTheAffectedApplication()
    {
        var auditService = new CapturingAuditService();
        var after = PermissionAuditSnapshot.From(Permission.Create(ApplicationId, "orders.read", null).Value);

        await auditService.RecordPermissionChangeAsync(AuditActions.PermissionCreated, AppKey, null, after, TestContext.Current.CancellationToken);

        var entry = Assert.Single(auditService.Entries);
        Assert.Equal("PERMISSION_CREATED", entry.Action);
        Assert.Equal("Permission", entry.EntityType);
        Assert.Equal(after.Id.ToString(), entry.EntityId);
        Assert.Equal(AppKey, entry.AppKeyOverride);
        Assert.Null(entry.OldValues);
    }

    [Fact]
    public async Task RecordAccessChangeAsync_RecordsTheAccessUnderTheAffectedApplication()
    {
        var auditService = new CapturingAuditService();
        var before = AccessAuditSnapshot.From(UserApplicationAccess.Grant(UserId, ApplicationId, []).Value);

        await auditService.RecordAccessChangeAsync(AuditActions.AccessRevoked, AppKey, before, null, TestContext.Current.CancellationToken);

        var entry = Assert.Single(auditService.Entries);
        Assert.Equal("ACCESS_REVOKED", entry.Action);
        Assert.Equal("UserApplicationAccess", entry.EntityType);
        Assert.Equal(before.Id.ToString(), entry.EntityId);
        Assert.Equal(AppKey, entry.AppKeyOverride);
    }

    [Fact]
    public async Task Helpers_WithMissingAppKeyMissingSnapshotsOrMismatchedIds_Throw()
    {
        var auditService = new CapturingAuditService();
        var cancellationToken = TestContext.Current.CancellationToken;
        var first = RoleAuditSnapshot.From(Role.Create(ApplicationId, "Admin", null).Value);
        var second = RoleAuditSnapshot.From(Role.Create(ApplicationId, "Viewer", null).Value);

        await Assert.ThrowsAsync<ArgumentException>(() => auditService.RecordRoleChangeAsync(AuditActions.RoleUpdated, " ", first, first, cancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => auditService.RecordRoleChangeAsync(AuditActions.RoleUpdated, AppKey, null, null, cancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => auditService.RecordRoleChangeAsync(AuditActions.RoleUpdated, AppKey, first, second, cancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => auditService.RecordPermissionChangeAsync(AuditActions.PermissionDeleted, AppKey, null, null, cancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => auditService.RecordAccessChangeAsync(AuditActions.AccessGranted, AppKey, null, null, cancellationToken));
        Assert.Empty(auditService.Entries);
    }

    private static IEnumerable<string> PropertyNames(JsonElement element) =>
        element.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal);
}
