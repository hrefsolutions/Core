using Href.Core.Api.Modules.Authorization;

namespace Href.Core.Tests.Modules.Authorization;

public sealed class PermissionTests
{
    private static readonly Guid ApplicationId = Guid.CreateVersion7();

    [Theory]
    [InlineData("orders.read")]
    [InlineData("orders")]
    [InlineData("orders:write")]
    [InlineData("billing-reports_export.v2")]
    [InlineData("  orders.read  ")]
    public void Create_WithValidKey_CreatesTrimmedPermission(string key)
    {
        var result = Permission.Create(ApplicationId, key, "  Read orders  ");

        Assert.True(result.IsSuccess);
        Assert.Equal(key.Trim(), result.Value.Key);
        Assert.Equal(ApplicationId, result.Value.ApplicationId);
        Assert.Equal("Read orders", result.Value.Description);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Orders.Read")]
    [InlineData("orders..read")]
    [InlineData(".orders")]
    [InlineData("orders.")]
    [InlineData("orders read")]
    [InlineData("orders/read")]
    [InlineData("órdenes.read")]
    public void Create_WithInvalidKey_ReturnsInvalidPermissionKey(string? key)
    {
        var result = Permission.Create(ApplicationId, key, null);

        Assert.True(result.IsFailure);
        Assert.Equal(AuthorizationErrors.InvalidPermissionKey, result.Error);
    }

    [Fact]
    public void Create_WithKeyLongerThanMaximum_ReturnsInvalidPermissionKey()
    {
        Assert.True(Permission.Create(ApplicationId, new string('a', Permission.KeyMaxLength), null).IsSuccess);
        Assert.Equal(
            AuthorizationErrors.InvalidPermissionKey,
            Permission.Create(ApplicationId, new string('a', Permission.KeyMaxLength + 1), null).Error);
    }

    [Fact]
    public void Create_WithBlankDescription_StoresNull()
    {
        Assert.Null(Permission.Create(ApplicationId, "orders.read", "   ").Value.Description);
    }

    [Fact]
    public void Create_WithInvalidDescription_ReturnsInvalidDescription()
    {
        Assert.Equal(
            AuthorizationErrors.InvalidDescription,
            Permission.Create(ApplicationId, "orders.read", new string('d', Permission.DescriptionMaxLength + 1)).Error);
        Assert.Equal(
            AuthorizationErrors.InvalidDescription,
            Permission.Create(ApplicationId, "orders.read", "bad\0text").Error);
    }

    [Fact]
    public void Create_WithEmptyApplicationId_Throws()
    {
        Assert.Throws<ArgumentException>(() => Permission.Create(Guid.Empty, "orders.read", null));
    }

    [Fact]
    public void UpdateDescription_ChangesOnlyDescription()
    {
        var permission = Permission.Create(ApplicationId, "orders.read", "Old").Value;

        Assert.True(permission.UpdateDescription(" New ").IsSuccess);
        Assert.Equal("New", permission.Description);
        Assert.Equal("orders.read", permission.Key);

        Assert.Equal(AuthorizationErrors.InvalidDescription, permission.UpdateDescription("x\0").Error);
        Assert.Equal("New", permission.Description);
    }
}
