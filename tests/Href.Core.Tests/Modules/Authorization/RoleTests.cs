using Href.Core.Api.Modules.Authorization;

namespace Href.Core.Tests.Modules.Authorization;

public sealed class RoleTests
{
    private static readonly Guid ApplicationId = Guid.CreateVersion7();
    private static readonly Guid OtherApplicationId = Guid.CreateVersion7();

    [Fact]
    public void Create_WithValidInputs_CreatesNonDefaultRoleWithoutPermissions()
    {
        var result = Role.Create(ApplicationId, "  Order Manager  ", "  Manages orders  ");

        Assert.True(result.IsSuccess);
        var role = result.Value;
        Assert.Equal(ApplicationId, role.ApplicationId);
        Assert.Equal("Order Manager", role.Name);
        Assert.Equal("Manages orders", role.Description);
        Assert.False(role.IsDefault);
        Assert.Empty(role.Permissions);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Order\nManager")]
    [InlineData("Order\tManager")]
    public void Create_WithInvalidName_ReturnsInvalidRoleName(string? name)
    {
        var result = Role.Create(ApplicationId, name, null);

        Assert.True(result.IsFailure);
        Assert.Equal(AuthorizationErrors.InvalidRoleName, result.Error);
    }

    [Fact]
    public void Create_WithNameLongerThanMaximum_ReturnsInvalidRoleName()
    {
        Assert.True(Role.Create(ApplicationId, new string('r', Role.NameMaxLength), null).IsSuccess);
        Assert.Equal(
            AuthorizationErrors.InvalidRoleName,
            Role.Create(ApplicationId, new string('r', Role.NameMaxLength + 1), null).Error);
    }

    [Fact]
    public void Create_WithInvalidDescription_ReturnsInvalidDescription()
    {
        Assert.Equal(
            AuthorizationErrors.InvalidDescription,
            Role.Create(ApplicationId, "Admin", new string('d', Role.DescriptionMaxLength + 1)).Error);
    }

    [Fact]
    public void Create_WithEmptyApplicationId_Throws()
    {
        Assert.Throws<ArgumentException>(() => Role.Create(Guid.Empty, "Admin", null));
    }

    [Fact]
    public void Update_WithInvalidName_KeepsPreviousValues()
    {
        var role = Role.Create(ApplicationId, "Admin", "Old").Value;

        Assert.Equal(AuthorizationErrors.InvalidRoleName, role.Update(" ", "New").Error);
        Assert.Equal("Admin", role.Name);
        Assert.Equal("Old", role.Description);

        Assert.True(role.Update("Administrator", null).IsSuccess);
        Assert.Equal("Administrator", role.Name);
        Assert.Null(role.Description);
    }

    [Fact]
    public void AddPermission_FromSameApplication_AddsItOnce()
    {
        var role = Role.Create(ApplicationId, "Admin", null).Value;
        var permission = CreatePermission(ApplicationId, "orders.read");

        Assert.True(role.AddPermission(permission).IsSuccess);
        Assert.True(role.AddPermission(permission).IsSuccess);

        Assert.Single(role.Permissions);
        Assert.Contains(permission, role.Permissions);
    }

    [Fact]
    public void AddPermission_FromOtherApplication_ReturnsCrossApplicationPermissionAndIsNotAdded()
    {
        var role = Role.Create(ApplicationId, "Admin", null).Value;
        var foreign = CreatePermission(OtherApplicationId, "orders.read");

        var result = role.AddPermission(foreign);

        Assert.Equal(AuthorizationErrors.CrossApplicationPermission, result.Error);
        Assert.Empty(role.Permissions);
    }

    [Fact]
    public void RemovePermission_RemovesOnlyThatPermission()
    {
        var role = Role.Create(ApplicationId, "Admin", null).Value;
        var read = CreatePermission(ApplicationId, "orders.read");
        var write = CreatePermission(ApplicationId, "orders.write");
        role.AddPermission(read);
        role.AddPermission(write);

        Assert.True(role.RemovePermission(read));
        Assert.False(role.RemovePermission(read));

        Assert.Equal([write], role.Permissions);
    }

    [Fact]
    public void ReplacePermissions_WithSameApplicationPermissions_ReplacesTheWholeSet()
    {
        var role = Role.Create(ApplicationId, "Admin", null).Value;
        var read = CreatePermission(ApplicationId, "orders.read");
        var write = CreatePermission(ApplicationId, "orders.write");
        var delete = CreatePermission(ApplicationId, "orders.delete");
        role.AddPermission(read);

        var result = role.ReplacePermissions([write, delete, write]);

        Assert.True(result.IsSuccess);
        Assert.Equal(["orders.delete", "orders.write"], role.GetPermissionKeys());
    }

    [Fact]
    public void ReplacePermissions_WithAnyForeignPermission_ChangesNothing()
    {
        var role = Role.Create(ApplicationId, "Admin", null).Value;
        var read = CreatePermission(ApplicationId, "orders.read");
        var write = CreatePermission(ApplicationId, "orders.write");
        var foreign = CreatePermission(OtherApplicationId, "orders.delete");
        role.AddPermission(read);

        var result = role.ReplacePermissions([write, foreign]);

        Assert.Equal(AuthorizationErrors.CrossApplicationPermission, result.Error);
        Assert.Equal(["orders.read"], role.GetPermissionKeys());
    }

    [Fact]
    public void MakeDefault_WithoutCurrentDefault_MarksRoleAsDefault()
    {
        var role = Role.Create(ApplicationId, "Member", null).Value;

        Assert.True(role.MakeDefault(null).IsSuccess);
        Assert.True(role.IsDefault);
    }

    [Fact]
    public void MakeDefault_WithCurrentDefaultOfSameApplication_MovesTheDefaultFlag()
    {
        var current = Role.Create(ApplicationId, "Member", null).Value;
        current.MakeDefault(null);
        var next = Role.Create(ApplicationId, "Customer", null).Value;

        var result = next.MakeDefault(current);

        Assert.True(result.IsSuccess);
        Assert.True(next.IsDefault);
        Assert.False(current.IsDefault);
    }

    [Fact]
    public void MakeDefault_WhenAlreadyDefault_KeepsIt()
    {
        var role = Role.Create(ApplicationId, "Member", null).Value;
        role.MakeDefault(null);

        Assert.True(role.MakeDefault(role).IsSuccess);
        Assert.True(role.IsDefault);
    }

    [Fact]
    public void MakeDefault_WithCurrentDefaultOfOtherApplication_ReturnsCrossApplicationRoleAndChangesNothing()
    {
        var foreignDefault = Role.Create(OtherApplicationId, "Member", null).Value;
        foreignDefault.MakeDefault(null);
        var role = Role.Create(ApplicationId, "Customer", null).Value;

        var result = role.MakeDefault(foreignDefault);

        Assert.Equal(AuthorizationErrors.CrossApplicationRole, result.Error);
        Assert.False(role.IsDefault);
        Assert.True(foreignDefault.IsDefault);
    }

    [Fact]
    public void ClearDefault_RemovesTheDefaultFlag()
    {
        var role = Role.Create(ApplicationId, "Member", null).Value;
        role.MakeDefault(null);

        role.ClearDefault();

        Assert.False(role.IsDefault);
    }

    private static Permission CreatePermission(Guid applicationId, string key) =>
        Permission.Create(applicationId, key, null).Value;
}
