using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Href.Core.Api.Modules.Applications;
using Href.Core.Api.Modules.Audit;
using Href.Core.Api.Modules.Auth;
using Href.Core.Api.Modules.Authorization;
using Href.Core.Api.Modules.Users;
using Microsoft.AspNetCore.Mvc;
using NHibernate.Linq;

namespace Href.Core.Tests.Integration;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class UserDeletionIntegrationTests(PostgresFixture postgres)
{
    private const string Password = "correct horse battery";

    [Fact]
    public async Task Delete_LockedUserWithoutAccess_Returns204DeletesItsTokensAndRecordsUserDeleted()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateAdminClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        var user = await CreateUserAsync(client, UniqueEmail("delete"));
        await SeedTokensAsync(user.Id, CoreApplication.Id);
        await LockAsync(client, user.Id);

        var response = await client.DeleteAsync($"/api/users/{user.Id}", cancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using var session = postgres.SessionFactory.OpenSession();
        Assert.Null(await session.GetAsync<User>(user.Id, cancellationToken));
        Assert.False(await session.Query<RefreshToken>().AnyAsync(token => token.UserId == user.Id, cancellationToken));
        Assert.False(await session.Query<UserToken>().AnyAsync(token => token.UserId == user.Id, cancellationToken));

        var entityId = user.Id.ToString();
        var rows = await session.Query<AuditLog>()
            .Where(row => row.EntityType == UserAuditing.EntityType && row.EntityId == entityId)
            .ToListAsync(cancellationToken);
        Assert.Contains(rows, row => row.Action == AuditActions.UserCreated);
        var deleted = Assert.Single(rows, row => row.Action == AuditActions.UserDeleted);
        Assert.Null(deleted.NewValues);
        Assert.NotNull(deleted.OldValues);
        using var before = JsonDocument.Parse(deleted.OldValues);
        Assert.Equal(user.Email, before.RootElement.GetProperty("email").GetString());
        Assert.Equal("Locked", before.RootElement.GetProperty("status").GetString());
        Assert.DoesNotContain("passwordHash", deleted.OldValues, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("securityStamp", deleted.OldValues, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Delete_ActiveUser_Returns409DeleteRequiresLockedAndKeepsTheUser()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateAdminClient();
        var user = await CreateUserAsync(client, UniqueEmail("active"));

        var response = await client.DeleteAsync($"/api/users/{user.Id}", TestContext.Current.CancellationToken);

        await AssertProblemAsync(response, HttpStatusCode.Conflict, UserErrors.DeleteRequiresLocked);
        await AssertUserKeptAsync(user.Id);
    }

    [Fact]
    public async Task Delete_LockedUserWithAnAccessWithoutRoles_Returns409UntilTheAccessIsRevoked()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateAdminClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        var appKey = await CreateAppAsync(client);
        var user = await CreateUserAsync(client, UniqueEmail("access"));
        var grant = await client.PutAsJsonAsync(
            $"/api/apps/{appKey}/users/{user.Id}/roles",
            new ReplaceUserRolesRequest([]),
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, grant.StatusCode);
        await LockAsync(client, user.Id);

        var blocked = await client.DeleteAsync($"/api/users/{user.Id}", cancellationToken);

        await AssertProblemAsync(blocked, HttpStatusCode.Conflict, UserErrors.HasApplicationAccesses);
        await AssertUserKeptAsync(user.Id);

        var revoke = await client.DeleteAsync($"/api/apps/{appKey}/users/{user.Id}", cancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

        var deleted = await client.DeleteAsync($"/api/users/{user.Id}", cancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
    }

    [Fact]
    public async Task Delete_UnknownUser_Returns404()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateAdminClient();

        var response = await client.DeleteAsync($"/api/users/{Guid.NewGuid()}", TestContext.Current.CancellationToken);

        await AssertProblemAsync(response, HttpStatusCode.NotFound, UserErrors.NotFound);
    }

    [Fact]
    public async Task GrantAccess_ToADeletedUser_Returns404()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateAdminClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        var appKey = await CreateAppAsync(client);
        var user = await CreateUserAsync(client, UniqueEmail("gone"));
        await LockAsync(client, user.Id);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/users/{user.Id}", cancellationToken)).StatusCode);

        var grant = await client.PutAsJsonAsync(
            $"/api/apps/{appKey}/users/{user.Id}/roles",
            new ReplaceUserRolesRequest([]),
            cancellationToken);

        await AssertProblemAsync(grant, HttpStatusCode.NotFound, UserErrors.NotFound);
    }

    private static string UniqueEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}@example.com";

    private static async Task<UserResponse> CreateUserAsync(HttpClient client, string email)
    {
        var response = await client.PostAsJsonAsync(
            "/api/users",
            new CreateUserRequest(email, "Delete Me", Password),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<UserResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(created);
        return created;
    }

    private static async Task<string> CreateAppAsync(HttpClient client)
    {
        var appKey = $"del-{Guid.NewGuid():N}"[..20];
        var response = await client.PostAsJsonAsync(
            "/api/apps",
            new CreateApplicationRequest(appKey, "Deletion Client", null, false),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return appKey;
    }

    private static async Task LockAsync(HttpClient client, Guid userId)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/users/{userId}/lock",
            new LockUserRequest("leaving"),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task SeedTokensAsync(Guid userId, Guid applicationId)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var now = DateTime.UtcNow;
        using var session = postgres.SessionFactory.OpenSession();
        using var transaction = session.BeginTransaction();
        await session.SaveAsync(RefreshToken.Issue(userId, applicationId, now, TimeSpan.FromDays(30), null, null).Token, cancellationToken);
        await session.SaveAsync(UserToken.Issue(userId, applicationId, UserTokenPurpose.PasswordReset, now, TimeSpan.FromHours(1)).Token, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task AssertUserKeptAsync(Guid userId)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var session = postgres.SessionFactory.OpenSession();
        Assert.NotNull(await session.GetAsync<User>(userId, cancellationToken));
        var entityId = userId.ToString();
        Assert.False(await session.Query<AuditLog>()
            .AnyAsync(row => row.EntityId == entityId && row.Action == AuditActions.UserDeleted, cancellationToken));
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode expectedStatus, Href.Core.Api.Common.Results.Error expectedError)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(TestContext.Current.CancellationToken);
        Assert.NotNull(problem);
        Assert.Equal(expectedError.Code, problem.Extensions["errorCode"]?.ToString());
    }
}
