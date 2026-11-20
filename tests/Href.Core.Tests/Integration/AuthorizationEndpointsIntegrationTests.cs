using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Href.Core.Api.Modules.Applications;
using Href.Core.Api.Modules.Audit;
using Href.Core.Api.Modules.Authorization;
using Href.Core.Api.Modules.Users;
using Microsoft.AspNetCore.Mvc;
using NHibernate.Linq;

namespace Href.Core.Tests.Integration;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AuthorizationEndpointsIntegrationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Permissions_CreateGetListUpdate_WorkPerApplicationAndAreAudited()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        var appKey = await CreateAppAsync(client);
        var otherAppKey = await CreateAppAsync(client);

        var created = await CreatePermissionAsync(client, appKey, "orders.write", "Write orders");
        await CreatePermissionAsync(client, appKey, "billing.export", null);
        await CreatePermissionAsync(client, otherAppKey, "orders.write", null);

        var get = await client.GetFromJsonAsync<PermissionResponse>($"/api/apps/{appKey}/permissions/orders.write", cancellationToken);
        Assert.NotNull(get);
        Assert.Equal(created.Id, get.Id);
        Assert.Equal("Write orders", get.Description);

        var list = await client.GetFromJsonAsync<List<PermissionResponse>>($"/api/apps/{appKey}/permissions", cancellationToken);
        Assert.NotNull(list);
        Assert.Equal(["billing.export", "orders.write"], list.Select(permission => permission.Key));

        var update = await client.PutAsJsonAsync(
            $"/api/apps/{appKey}/permissions/orders.write",
            new UpdatePermissionRequest("Create and edit orders"),
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var updated = await update.Content.ReadFromJsonAsync<PermissionResponse>(cancellationToken);
        Assert.NotNull(updated);
        Assert.Equal("Create and edit orders", updated.Description);

        var unchanged = await client.PutAsJsonAsync(
            $"/api/apps/{appKey}/permissions/orders.write",
            new UpdatePermissionRequest("  Create and edit orders "),
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, unchanged.StatusCode);

        var rows = await AuditRowsAsync(AuthorizationAuditing.PermissionEntityType, created.Id);
        Assert.Equal([AuditActions.PermissionCreated, AuditActions.PermissionUpdated], rows.Select(row => row.Action));
        Assert.All(rows, row => Assert.Equal(appKey, row.AppKey));
        Assert.Equal("Write orders", Parse(rows[1].OldValues).GetProperty("description").GetString());
        Assert.Equal("Create and edit orders", Parse(rows[1].NewValues).GetProperty("description").GetString());
    }

    [Fact]
    public async Task Permissions_RejectDuplicatesInvalidKeysAndUnknownTargets()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        var appKey = await CreateAppAsync(client);
        await CreatePermissionAsync(client, appKey, "orders.read", null);

        await AssertProblemAsync(
            await client.PostAsJsonAsync($"/api/apps/{appKey}/permissions", new CreatePermissionRequest("orders.read", null), cancellationToken),
            HttpStatusCode.Conflict,
            AuthorizationErrors.PermissionKeyTaken.Code);
        await AssertProblemAsync(
            await client.PostAsJsonAsync($"/api/apps/{appKey}/permissions", new CreatePermissionRequest("Orders.Read", null), cancellationToken),
            HttpStatusCode.BadRequest,
            AuthorizationErrors.InvalidPermissionKey.Code);
        await AssertProblemAsync(
            await client.PostAsJsonAsync("/api/apps/no-such-app/permissions", new CreatePermissionRequest("orders.read", null), cancellationToken),
            HttpStatusCode.NotFound,
            ApplicationErrors.NotFound.Code);
        await AssertProblemAsync(
            await client.GetAsync("/api/apps/no-such-app/permissions", cancellationToken),
            HttpStatusCode.NotFound,
            ApplicationErrors.NotFound.Code);
        await AssertProblemAsync(
            await client.GetAsync($"/api/apps/{appKey}/permissions/orders.unknown", cancellationToken),
            HttpStatusCode.NotFound,
            AuthorizationErrors.PermissionNotFound.Code);
        await AssertProblemAsync(
            await client.DeleteAsync($"/api/apps/{appKey}/permissions/orders.unknown", cancellationToken),
            HttpStatusCode.NotFound,
            AuthorizationErrors.PermissionNotFound.Code);
        await AssertProblemAsync(
            await client.PutAsJsonAsync($"/api/apps/{appKey}/permissions/orders.read", new UpdatePermissionRequest(new string('d', 501)), cancellationToken),
            HttpStatusCode.BadRequest,
            AuthorizationErrors.InvalidDescription.Code);
    }

    [Fact]
    public async Task DeletingPermission_RemovesItFromRolesAndAuditsEveryAffectedRole()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        var appKey = await CreateAppAsync(client);
        var read = await CreatePermissionAsync(client, appKey, "orders.read", null);
        await CreatePermissionAsync(client, appKey, "orders.write", null);
        var admin = await CreateRoleAsync(client, appKey, "Admin", false, "orders.read", "orders.write");
        var viewer = await CreateRoleAsync(client, appKey, "Viewer", false, "orders.read");
        var unrelated = await CreateRoleAsync(client, appKey, "Writer", false, "orders.write");

        var response = await client.DeleteAsync($"/api/apps/{appKey}/permissions/orders.read", cancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(["orders.write"], (await GetRoleAsync(client, appKey, admin.Id)).PermissionKeys);
        Assert.Empty((await GetRoleAsync(client, appKey, viewer.Id)).PermissionKeys);

        var permissionRows = await AuditRowsAsync(AuthorizationAuditing.PermissionEntityType, read.Id);
        Assert.Equal([AuditActions.PermissionCreated, AuditActions.PermissionDeleted], permissionRows.Select(row => row.Action));
        Assert.Null(permissionRows[1].NewValues);
        Assert.Equal(appKey, permissionRows[1].AppKey);

        var adminRows = await AuditRowsAsync(AuthorizationAuditing.RoleEntityType, admin.Id);
        Assert.Equal(AuditActions.RolePermissionsChanged, adminRows[^1].Action);
        Assert.Equal(["orders.read", "orders.write"], ReadStrings(adminRows[^1].OldValues, "permissionKeys"));
        Assert.Equal(["orders.write"], ReadStrings(adminRows[^1].NewValues, "permissionKeys"));
        Assert.Equal(AuditActions.RolePermissionsChanged, (await AuditRowsAsync(AuthorizationAuditing.RoleEntityType, viewer.Id))[^1].Action);
        Assert.Equal([AuditActions.RoleCreated], (await AuditRowsAsync(AuthorizationAuditing.RoleEntityType, unrelated.Id)).Select(row => row.Action));
    }

    [Fact]
    public async Task Roles_CreateWithPermissionsAndDefault_MovesTheDefaultAndAuditsBothRoles()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        var appKey = await CreateAppAsync(client);
        await CreatePermissionAsync(client, appKey, "orders.read", null);
        await CreatePermissionAsync(client, appKey, "billing.export", null);

        var member = await CreateRoleAsync(client, appKey, "Member", true, "orders.read");
        var customer = await CreateRoleAsync(client, appKey, "Customer", true, "orders.read", "billing.export", "orders.read");

        Assert.True(customer.IsDefault);
        Assert.Equal(["billing.export", "orders.read"], customer.PermissionKeys);
        Assert.False((await GetRoleAsync(client, appKey, member.Id)).IsDefault);

        var roles = await client.GetFromJsonAsync<List<RoleResponse>>($"/api/apps/{appKey}/roles", cancellationToken);
        Assert.NotNull(roles);
        Assert.Equal(["Customer", "Member"], roles.Select(role => role.Name));
        Assert.Single(roles, role => role.IsDefault);

        var memberRows = await AuditRowsAsync(AuthorizationAuditing.RoleEntityType, member.Id);
        Assert.Equal([AuditActions.RoleCreated, AuditActions.RoleUpdated], memberRows.Select(row => row.Action));
        Assert.True(Parse(memberRows[1].OldValues).GetProperty("isDefault").GetBoolean());
        Assert.False(Parse(memberRows[1].NewValues).GetProperty("isDefault").GetBoolean());
        Assert.All(memberRows, row => Assert.Equal(appKey, row.AppKey));
    }

    [Fact]
    public async Task Roles_RejectUnknownOrForeignPermissionsDuplicatesAndInvalidLists()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        var appKey = await CreateAppAsync(client);
        var otherAppKey = await CreateAppAsync(client);
        await CreatePermissionAsync(client, appKey, "orders.read", null);
        await CreatePermissionAsync(client, otherAppKey, "billing.export", null);
        await CreateRoleAsync(client, appKey, "Admin", false);

        var unknown = await client.PostAsJsonAsync(
            $"/api/apps/{appKey}/roles",
            new CreateRoleRequest("Viewer", null, false, ["orders.read", "billing.export", "nope"]),
            cancellationToken);
        var problem = await AssertProblemAsync(unknown, HttpStatusCode.BadRequest, AuthorizationErrors.UnknownPermissionsCode);
        Assert.Contains("billing.export, nope", problem.Detail, StringComparison.Ordinal);

        await AssertProblemAsync(
            await client.PostAsJsonAsync($"/api/apps/{appKey}/roles", new CreateRoleRequest("ADMIN", null, false, null), cancellationToken),
            HttpStatusCode.Conflict,
            AuthorizationErrors.RoleNameTaken.Code);
        await AssertProblemAsync(
            await client.PostAsJsonAsync($"/api/apps/{appKey}/roles", new CreateRoleRequest("Blank Key", null, false, [" "]), cancellationToken),
            HttpStatusCode.BadRequest,
            AuthorizationErrors.InvalidPermissionKeys.Code);
        await AssertProblemAsync(
            await client.PostAsJsonAsync($"/api/apps/{appKey}/roles", new CreateRoleRequest("Null Key", null, false, [null]), cancellationToken),
            HttpStatusCode.BadRequest,
            AuthorizationErrors.InvalidPermissionKeys.Code);
        await AssertProblemAsync(
            await client.PostAsJsonAsync(
                $"/api/apps/{appKey}/roles",
                new CreateRoleRequest("Too Many", null, false, Enumerable.Range(0, 201).Select(index => (string?)$"p{index}").ToList()),
                cancellationToken),
            HttpStatusCode.BadRequest,
            AuthorizationErrors.InvalidPermissionKeys.Code);
        await AssertProblemAsync(
            await client.PostAsJsonAsync($"/api/apps/{appKey}/roles", new CreateRoleRequest(" ", null, false, null), cancellationToken),
            HttpStatusCode.BadRequest,
            AuthorizationErrors.InvalidRoleName.Code);

        var roles = await client.GetFromJsonAsync<List<RoleResponse>>($"/api/apps/{appKey}/roles", cancellationToken);
        Assert.NotNull(roles);
        Assert.Equal(["Admin"], roles.Select(role => role.Name));
    }

    [Fact]
    public async Task Roles_UpdateReplacePermissionsAndClearDefault_AreAuditedOnlyWhenSomethingChanges()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        var appKey = await CreateAppAsync(client);
        await CreatePermissionAsync(client, appKey, "orders.read", null);
        await CreatePermissionAsync(client, appKey, "orders.write", null);
        var role = await CreateRoleAsync(client, appKey, "Member", true, "orders.read");

        var update = await client.PutAsJsonAsync(
            $"/api/apps/{appKey}/roles/{role.Id}",
            new UpdateRoleRequest("Members", "Every member", false),
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var updated = await update.Content.ReadFromJsonAsync<RoleResponse>(cancellationToken);
        Assert.NotNull(updated);
        Assert.Equal("Members", updated.Name);
        Assert.Equal("Every member", updated.Description);
        Assert.False(updated.IsDefault);

        var sameUpdate = await client.PutAsJsonAsync(
            $"/api/apps/{appKey}/roles/{role.Id}",
            new UpdateRoleRequest(" Members ", "Every member", false),
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, sameUpdate.StatusCode);

        var replace = await client.PutAsJsonAsync(
            $"/api/apps/{appKey}/roles/{role.Id}/permissions",
            new ReplaceRolePermissionsRequest(["orders.write"]),
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, replace.StatusCode);
        var replaced = await replace.Content.ReadFromJsonAsync<RoleResponse>(cancellationToken);
        Assert.NotNull(replaced);
        Assert.Equal(["orders.write"], replaced.PermissionKeys);

        var sameReplace = await client.PutAsJsonAsync(
            $"/api/apps/{appKey}/roles/{role.Id}/permissions",
            new ReplaceRolePermissionsRequest(["orders.write"]),
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, sameReplace.StatusCode);

        await AssertProblemAsync(
            await client.PutAsJsonAsync($"/api/apps/{appKey}/roles/{role.Id}/permissions", new ReplaceRolePermissionsRequest(null), cancellationToken),
            HttpStatusCode.BadRequest,
            AuthorizationErrors.InvalidPermissionKeys.Code);

        var rows = await AuditRowsAsync(AuthorizationAuditing.RoleEntityType, role.Id);
        Assert.Equal(
            [AuditActions.RoleCreated, AuditActions.RoleUpdated, AuditActions.RolePermissionsChanged],
            rows.Select(row => row.Action));
        Assert.True(Parse(rows[1].OldValues).GetProperty("isDefault").GetBoolean());
        Assert.False(Parse(rows[1].NewValues).GetProperty("isDefault").GetBoolean());
    }

    [Fact]
    public async Task Roles_UpdateToDefault_MovesTheFlagFromThePreviousDefault()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        var appKey = await CreateAppAsync(client);
        var member = await CreateRoleAsync(client, appKey, "Member", true);
        var guest = await CreateRoleAsync(client, appKey, "Guest", false);

        var response = await client.PutAsJsonAsync(
            $"/api/apps/{appKey}/roles/{guest.Id}",
            new UpdateRoleRequest("Guest", null, true),
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await GetRoleAsync(client, appKey, guest.Id)).IsDefault);
        Assert.False((await GetRoleAsync(client, appKey, member.Id)).IsDefault);
        Assert.Equal(AuditActions.RoleUpdated, (await AuditRowsAsync(AuthorizationAuditing.RoleEntityType, member.Id))[^1].Action);
    }

    [Fact]
    public async Task Roles_RenameToExistingNameIgnoringCase_Returns409AndKeepsTheName()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();
        var appKey = await CreateAppAsync(client);
        await CreateRoleAsync(client, appKey, "Admin", false);
        var viewer = await CreateRoleAsync(client, appKey, "Viewer", false);

        await AssertProblemAsync(
            await client.PutAsJsonAsync($"/api/apps/{appKey}/roles/{viewer.Id}", new UpdateRoleRequest("admin", null, false), TestContext.Current.CancellationToken),
            HttpStatusCode.Conflict,
            AuthorizationErrors.RoleNameTaken.Code);
        Assert.Equal("Viewer", (await GetRoleAsync(client, appKey, viewer.Id)).Name);
    }

    [Fact]
    public async Task Roles_OfAnotherApplication_AreNotFound()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        var appKey = await CreateAppAsync(client);
        var otherAppKey = await CreateAppAsync(client);
        var foreign = await CreateRoleAsync(client, otherAppKey, "Admin", false);

        await AssertProblemAsync(await client.GetAsync($"/api/apps/{appKey}/roles/{foreign.Id}", cancellationToken), HttpStatusCode.NotFound, AuthorizationErrors.RoleNotFound.Code);
        await AssertProblemAsync(
            await client.PutAsJsonAsync($"/api/apps/{appKey}/roles/{foreign.Id}", new UpdateRoleRequest("Hijacked", null, false), cancellationToken),
            HttpStatusCode.NotFound,
            AuthorizationErrors.RoleNotFound.Code);
        await AssertProblemAsync(await client.DeleteAsync($"/api/apps/{appKey}/roles/{foreign.Id}", cancellationToken), HttpStatusCode.NotFound, AuthorizationErrors.RoleNotFound.Code);
        Assert.Equal("Admin", (await GetRoleAsync(client, otherAppKey, foreign.Id)).Name);
    }

    [Fact]
    public async Task Users_GrantChangeAndRevokeAccess_AreAuditedUnderTheApplication()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        var appKey = await CreateAppAsync(client);
        var admin = await CreateRoleAsync(client, appKey, "Admin", false);
        var viewer = await CreateRoleAsync(client, appKey, "Viewer", false);
        var user = await CreateUserAsync(client);

        var grant = await client.PutAsJsonAsync($"/api/apps/{appKey}/users/{user.Id}/roles", new ReplaceUserRolesRequest([viewer.Id, admin.Id, viewer.Id]), cancellationToken);
        Assert.Equal(HttpStatusCode.OK, grant.StatusCode);
        var granted = await grant.Content.ReadFromJsonAsync<ApplicationUserResponse>(cancellationToken);
        Assert.NotNull(granted);
        Assert.Equal(user.Id, granted.UserId);
        Assert.Equal(user.Email, granted.Email);
        Assert.Equal(["Admin", "Viewer"], granted.Roles.Select(role => role.Name));

        var change = await client.PutAsJsonAsync($"/api/apps/{appKey}/users/{user.Id}/roles", new ReplaceUserRolesRequest([viewer.Id]), cancellationToken);
        Assert.Equal(HttpStatusCode.OK, change.StatusCode);
        var same = await client.PutAsJsonAsync($"/api/apps/{appKey}/users/{user.Id}/roles", new ReplaceUserRolesRequest([viewer.Id]), cancellationToken);
        Assert.Equal(HttpStatusCode.OK, same.StatusCode);

        var detail = await client.GetFromJsonAsync<UserDetailResponse>($"/api/users/{user.Id}", cancellationToken);
        Assert.NotNull(detail);
        Assert.Equal(["Viewer"], Assert.Single(detail.Applications).Roles);

        var revoke = await client.DeleteAsync($"/api/apps/{appKey}/users/{user.Id}", cancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        await AssertProblemAsync(await client.DeleteAsync($"/api/apps/{appKey}/users/{user.Id}", cancellationToken), HttpStatusCode.NotFound, AuthorizationErrors.AccessNotFound.Code);

        var rows = await AccessAuditRowsAsync(appKey);
        Assert.Equal([AuditActions.AccessGranted, AuditActions.AccessRolesChanged, AuditActions.AccessRevoked], rows.Select(row => row.Action));
        Assert.All(rows, row => Assert.Equal(appKey, row.AppKey));
        Assert.Equal(user.Id, Parse(rows[0].NewValues).GetProperty("userId").GetGuid());
        Assert.Null(rows[2].NewValues);
    }

    [Fact]
    public async Task Users_LockedUserCanStillBeGivenRoles()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        var appKey = await CreateAppAsync(client);
        var role = await CreateRoleAsync(client, appKey, "Member", false);
        var user = await CreateUserAsync(client);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/users/{user.Id}/lock", new LockUserRequest("test"), cancellationToken)).StatusCode);

        var response = await client.PutAsJsonAsync($"/api/apps/{appKey}/users/{user.Id}/roles", new ReplaceUserRolesRequest([role.Id]), cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var granted = await response.Content.ReadFromJsonAsync<ApplicationUserResponse>(cancellationToken);
        Assert.NotNull(granted);
        Assert.Equal("Locked", granted.Status);
        Assert.Equal(["Member"], granted.Roles.Select(item => item.Name));
    }

    [Fact]
    public async Task Users_WithForeignOrUnknownRolesOrUnknownUser_AreRejected()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        var appKey = await CreateAppAsync(client);
        var otherAppKey = await CreateAppAsync(client);
        var foreign = await CreateRoleAsync(client, otherAppKey, "Admin", false);
        var user = await CreateUserAsync(client);
        var unknownRoleId = Guid.CreateVersion7();

        var problem = await AssertProblemAsync(
            await client.PutAsJsonAsync($"/api/apps/{appKey}/users/{user.Id}/roles", new ReplaceUserRolesRequest([foreign.Id, unknownRoleId]), cancellationToken),
            HttpStatusCode.BadRequest,
            AuthorizationErrors.UnknownRolesCode);
        Assert.Contains(foreign.Id.ToString(), problem.Detail, StringComparison.Ordinal);
        Assert.Contains(unknownRoleId.ToString(), problem.Detail, StringComparison.Ordinal);

        await AssertProblemAsync(
            await client.PutAsJsonAsync($"/api/apps/{appKey}/users/{Guid.CreateVersion7()}/roles", new ReplaceUserRolesRequest([]), cancellationToken),
            HttpStatusCode.NotFound,
            UserErrors.NotFound.Code);
        await AssertProblemAsync(
            await client.PutAsJsonAsync($"/api/apps/{appKey}/users/{user.Id}/roles", new ReplaceUserRolesRequest(null), cancellationToken),
            HttpStatusCode.BadRequest,
            AuthorizationErrors.InvalidRoleIds.Code);
        Assert.Empty(await AccessAuditRowsAsync(appKey));
    }

    [Fact]
    public async Task Users_ListReturnsUsersWithAccessPagedByEmail()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        var appKey = await CreateAppAsync(client);
        var role = await CreateRoleAsync(client, appKey, "Member", false);
        var tag = Guid.NewGuid().ToString("N")[..10];
        var bob = await CreateUserAsync(client, $"b-{tag}@example.com");
        var ana = await CreateUserAsync(client, $"a-{tag}@example.com");
        var carl = await CreateUserAsync(client, $"c-{tag}@example.com");
        await CreateUserAsync(client, $"d-{tag}@example.com");

        foreach (var user in new[] { bob, ana, carl })
        {
            var response = await client.PutAsJsonAsync($"/api/apps/{appKey}/users/{user.Id}/roles", new ReplaceUserRolesRequest([role.Id]), cancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var first = await client.GetFromJsonAsync<ApplicationUserPageResponse>($"/api/apps/{appKey}/users?pageSize=2", cancellationToken);
        var second = await client.GetFromJsonAsync<ApplicationUserPageResponse>($"/api/apps/{appKey}/users?page=2&pageSize=2", cancellationToken);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(3, first.TotalCount);
        Assert.Equal([ana.Id, bob.Id], first.Items.Select(item => item.UserId));
        Assert.Equal([carl.Id], second.Items.Select(item => item.UserId));
        Assert.Equal(["Member"], first.Items[0].Roles.Select(item => item.Name));
        Assert.Equal("Active", first.Items[0].Status);

        await AssertProblemAsync(await client.GetAsync($"/api/apps/{appKey}/users?page=0", cancellationToken), HttpStatusCode.BadRequest, AuthorizationErrors.InvalidPage.Code);
        await AssertProblemAsync(await client.GetAsync($"/api/apps/{appKey}/users?pageSize=201", cancellationToken), HttpStatusCode.BadRequest, AuthorizationErrors.InvalidPageSize.Code);
    }

    [Fact]
    public async Task DeletingRole_RemovesItFromAccessesAndAuditsEveryAffectedAccess()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        var appKey = await CreateAppAsync(client);
        var admin = await CreateRoleAsync(client, appKey, "Admin", false);
        var viewer = await CreateRoleAsync(client, appKey, "Viewer", false);
        var user = await CreateUserAsync(client);
        await client.PutAsJsonAsync($"/api/apps/{appKey}/users/{user.Id}/roles", new ReplaceUserRolesRequest([admin.Id, viewer.Id]), cancellationToken);

        var response = await client.DeleteAsync($"/api/apps/{appKey}/roles/{admin.Id}", cancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await AssertProblemAsync(await client.GetAsync($"/api/apps/{appKey}/roles/{admin.Id}", cancellationToken), HttpStatusCode.NotFound, AuthorizationErrors.RoleNotFound.Code);
        var page = await client.GetFromJsonAsync<ApplicationUserPageResponse>($"/api/apps/{appKey}/users", cancellationToken);
        Assert.NotNull(page);
        Assert.Equal(["Viewer"], Assert.Single(page.Items).Roles.Select(role => role.Name));

        var accessRows = await AccessAuditRowsAsync(appKey);
        Assert.Equal([AuditActions.AccessGranted, AuditActions.AccessRolesChanged], accessRows.Select(row => row.Action));
        var roleRows = await AuditRowsAsync(AuthorizationAuditing.RoleEntityType, admin.Id);
        Assert.Equal([AuditActions.RoleCreated, AuditActions.RoleDeleted], roleRows.Select(row => row.Action));
        Assert.Null(roleRows[1].NewValues);
    }

    [Fact]
    public async Task ConcurrentDefaultRoleCreations_LeaveExactlyOneDefault()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        var appKey = await CreateAppAsync(client);

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(index => client.PostAsJsonAsync(
            $"/api/apps/{appKey}/roles",
            new CreateRoleRequest($"Role {index}", null, true, null),
            cancellationToken)));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Created, response.StatusCode));
        var roles = await client.GetFromJsonAsync<List<RoleResponse>>($"/api/apps/{appKey}/roles", cancellationToken);
        Assert.NotNull(roles);
        Assert.Equal(6, roles.Count);
        Assert.Single(roles, role => role.IsDefault);
    }

    [Fact]
    public async Task ConcurrentGrantsForSameUser_CreateOneAccess()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        var appKey = await CreateAppAsync(client);
        var role = await CreateRoleAsync(client, appKey, "Member", false);
        var user = await CreateUserAsync(client);

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => client.PutAsJsonAsync(
            $"/api/apps/{appKey}/users/{user.Id}/roles",
            new ReplaceUserRolesRequest([role.Id]),
            cancellationToken)));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        var rows = await AccessAuditRowsAsync(appKey);
        Assert.Equal([AuditActions.AccessGranted], rows.Select(row => row.Action));
    }

    private static JsonElement Parse(string? json)
    {
        Assert.NotNull(json);
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static List<string?> ReadStrings(string? json, string property) =>
        Parse(json).GetProperty(property).EnumerateArray().Select(item => item.GetString()).ToList();

    private static async Task<ProblemDetails> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode expectedStatus, string expectedCode)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(TestContext.Current.CancellationToken);
        Assert.NotNull(problem);
        Assert.Equal(expectedCode, problem.Extensions["errorCode"]?.ToString());
        return problem;
    }

    private static async Task<string> CreateAppAsync(HttpClient client)
    {
        var appKey = $"rbac-{Guid.NewGuid():N}"[..20];
        var response = await client.PostAsJsonAsync(
            "/api/apps",
            new CreateApplicationRequest(appKey, "RBAC App", null, false),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return appKey;
    }

    private static async Task<UserResponse> CreateUserAsync(HttpClient client, string? email = null)
    {
        var response = await client.PostAsJsonAsync(
            "/api/users",
            new CreateUserRequest(email ?? $"rbac-{Guid.NewGuid():N}@example.com", "RBAC User", "correct horse battery"),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var user = await response.Content.ReadFromJsonAsync<UserResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(user);
        return user;
    }

    private static async Task<PermissionResponse> CreatePermissionAsync(HttpClient client, string appKey, string key, string? description)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/apps/{appKey}/permissions",
            new CreatePermissionRequest(key, description),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.EndsWith($"/api/apps/{appKey}/permissions/{key}", response.Headers.Location?.ToString(), StringComparison.Ordinal);
        var permission = await response.Content.ReadFromJsonAsync<PermissionResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(permission);
        return permission;
    }

    private static async Task<RoleResponse> CreateRoleAsync(HttpClient client, string appKey, string name, bool isDefault, params string[] permissionKeys)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/apps/{appKey}/roles",
            new CreateRoleRequest(name, null, isDefault, permissionKeys),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var role = await response.Content.ReadFromJsonAsync<RoleResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(role);
        Assert.EndsWith($"/api/apps/{appKey}/roles/{role.Id}", response.Headers.Location?.ToString(), StringComparison.OrdinalIgnoreCase);
        return role;
    }

    private static async Task<RoleResponse> GetRoleAsync(HttpClient client, string appKey, Guid roleId)
    {
        var role = await client.GetFromJsonAsync<RoleResponse>($"/api/apps/{appKey}/roles/{roleId}", TestContext.Current.CancellationToken);
        Assert.NotNull(role);
        return role;
    }

    private async Task<List<AuditLog>> AuditRowsAsync(string entityType, Guid entityId)
    {
        using var session = postgres.SessionFactory.OpenSession();
        var id = entityId.ToString();
        var rows = await session.Query<AuditLog>()
            .Where(log => log.EntityType == entityType && log.EntityId == id)
            .ToListAsync(TestContext.Current.CancellationToken);

        return rows.OrderBy(log => log.OccurredAtUtc).ThenBy(log => log.Id).ToList();
    }

    private async Task<List<AuditLog>> AccessAuditRowsAsync(string appKey)
    {
        using var session = postgres.SessionFactory.OpenSession();
        var rows = await session.Query<AuditLog>()
            .Where(log => log.EntityType == AuthorizationAuditing.AccessEntityType && log.AppKey == appKey)
            .ToListAsync(TestContext.Current.CancellationToken);

        return rows.OrderBy(log => log.OccurredAtUtc).ThenBy(log => log.Id).ToList();
    }
}
