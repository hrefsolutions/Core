using Href.Core.Api.Modules.Authorization;

namespace Href.Core.Tests.Modules.Authorization;

public sealed class UserApplicationAccessTests
{
    private static readonly Guid UserId = Guid.CreateVersion7();
    private static readonly Guid ApplicationId = Guid.CreateVersion7();
    private static readonly Guid OtherApplicationId = Guid.CreateVersion7();

    [Fact]
    public void Grant_WithRolesOfSameApplication_CreatesAccessWithThoseRoles()
    {
        var admin = CreateRole(ApplicationId, "Admin");
        var viewer = CreateRole(ApplicationId, "Viewer");

        var result = UserApplicationAccess.Grant(UserId, ApplicationId, [viewer, admin, viewer]);

        Assert.True(result.IsSuccess);
        var access = result.Value;
        Assert.Equal(UserId, access.UserId);
        Assert.Equal(ApplicationId, access.ApplicationId);
        Assert.Equal(["Admin", "Viewer"], access.GetRoleNames());
    }

    [Fact]
    public void Grant_WithoutRoles_CreatesAccessWithNoRoles()
    {
        var access = UserApplicationAccess.Grant(UserId, ApplicationId, []).Value;

        Assert.Empty(access.Roles);
        Assert.Empty(access.GetRoleNames());
        Assert.Empty(access.GetPermissionKeys());
    }

    [Fact]
    public void Grant_WithRoleOfOtherApplication_ReturnsCrossApplicationRole()
    {
        var result = UserApplicationAccess.Grant(
            UserId,
            ApplicationId,
            [CreateRole(ApplicationId, "Admin"), CreateRole(OtherApplicationId, "Intruder")]);

        Assert.True(result.IsFailure);
        Assert.Equal(AuthorizationErrors.CrossApplicationRole, result.Error);
    }

    [Fact]
    public void Grant_WithEmptyIdentifiers_Throws()
    {
        Assert.Throws<ArgumentException>(() => UserApplicationAccess.Grant(Guid.Empty, ApplicationId, []));
        Assert.Throws<ArgumentException>(() => UserApplicationAccess.Grant(UserId, Guid.Empty, []));
    }

    [Fact]
    public void AssignRole_FromOtherApplication_ReturnsCrossApplicationRoleAndIsNotAdded()
    {
        var access = UserApplicationAccess.Grant(UserId, ApplicationId, []).Value;

        var result = access.AssignRole(CreateRole(OtherApplicationId, "Intruder"));

        Assert.Equal(AuthorizationErrors.CrossApplicationRole, result.Error);
        Assert.Empty(access.Roles);
    }

    [Fact]
    public void AssignRole_AndRemoveRole_ChangeTheRoleSet()
    {
        var admin = CreateRole(ApplicationId, "Admin");
        var viewer = CreateRole(ApplicationId, "Viewer");
        var access = UserApplicationAccess.Grant(UserId, ApplicationId, [admin]).Value;

        Assert.True(access.AssignRole(viewer).IsSuccess);
        Assert.True(access.AssignRole(viewer).IsSuccess);
        Assert.Equal(["Admin", "Viewer"], access.GetRoleNames());

        Assert.True(access.RemoveRole(admin));
        Assert.False(access.RemoveRole(admin));
        Assert.Equal(["Viewer"], access.GetRoleNames());
    }

    [Fact]
    public void ReplaceRoles_WithAnyForeignRole_ChangesNothing()
    {
        var admin = CreateRole(ApplicationId, "Admin");
        var access = UserApplicationAccess.Grant(UserId, ApplicationId, [admin]).Value;

        var result = access.ReplaceRoles([CreateRole(ApplicationId, "Viewer"), CreateRole(OtherApplicationId, "Intruder")]);

        Assert.Equal(AuthorizationErrors.CrossApplicationRole, result.Error);
        Assert.Equal(["Admin"], access.GetRoleNames());
    }

    [Fact]
    public void ReplaceRoles_WithSameApplicationRoles_ReplacesTheWholeSet()
    {
        var access = UserApplicationAccess.Grant(UserId, ApplicationId, [CreateRole(ApplicationId, "Admin")]).Value;

        Assert.True(access.ReplaceRoles([CreateRole(ApplicationId, "Viewer")]).IsSuccess);
        Assert.Equal(["Viewer"], access.GetRoleNames());

        Assert.True(access.ReplaceRoles([]).IsSuccess);
        Assert.Empty(access.Roles);
    }

    [Fact]
    public void GetPermissionKeys_ReturnsTheSortedDistinctUnionOfAllRolePermissions()
    {
        var read = Permission.Create(ApplicationId, "orders.read", null).Value;
        var write = Permission.Create(ApplicationId, "orders.write", null).Value;
        var export = Permission.Create(ApplicationId, "billing.export", null).Value;
        var admin = CreateRole(ApplicationId, "Admin");
        admin.AddPermission(read);
        admin.AddPermission(write);
        var accountant = CreateRole(ApplicationId, "Accountant");
        accountant.AddPermission(read);
        accountant.AddPermission(export);
        var empty = CreateRole(ApplicationId, "Empty");
        var access = UserApplicationAccess.Grant(UserId, ApplicationId, [admin, accountant, empty]).Value;

        Assert.Equal(["billing.export", "orders.read", "orders.write"], access.GetPermissionKeys());
    }

    private static Role CreateRole(Guid applicationId, string name) => Role.Create(applicationId, name, null).Value;
}
