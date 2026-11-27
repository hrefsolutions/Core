using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Href.Core.Api.Modules.Applications;
using Href.Core.Api.Modules.Audit;
using Href.Core.Api.Modules.Auth;
using Href.Core.Api.Modules.Authorization;
using Href.Core.Api.Modules.Notifications;
using Href.Core.Api.Modules.Users;
using Microsoft.AspNetCore.Mvc;
using NHibernate.Linq;

namespace Href.Core.Tests.Integration;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ApplicationDeletionIntegrationTests(PostgresFixture postgres)
{
    private const string Password = "correct horse battery";

    [Fact]
    public async Task Delete_SuspendedApplication_DeletesItsCatalogAndTokensCancelsOpenNotificationsAndAuditsEachStep()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var admin = factory.CreateAdminClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        var app = await CreateAppAsync(admin, UniqueKey());
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync($"/api/apps/{app.AppKey}/permissions", new CreatePermissionRequest("orders.read", null), cancellationToken)).StatusCode);
        var roleResponse = await admin.PostAsJsonAsync(
            $"/api/apps/{app.AppKey}/roles",
            new CreateRoleRequest("reader", null, true, ["orders.read"]),
            cancellationToken);
        Assert.Equal(HttpStatusCode.Created, roleResponse.StatusCode);
        var role = await roleResponse.Content.ReadFromJsonAsync<RoleResponse>(cancellationToken);
        Assert.NotNull(role);

        var pending = await QueueAsync(factory, app);
        var processing = await QueueAsync(factory, app);
        var sent = await QueueAsync(factory, app);
        await AdvanceAsync(processing, toSent: false);
        await AdvanceAsync(sent, toSent: true);
        var versions = await LoadVersionsAsync(pending, processing, sent);

        var userId = await CreateUserAsync(admin);
        await SeedTokensAsync(userId, app.Id);
        await SuspendAsync(admin, app.AppKey);

        var response = await admin.DeleteAsync($"/api/apps/{app.AppKey}", cancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using var session = postgres.SessionFactory.OpenSession();
        Assert.Null(await session.GetAsync<Application>(app.Id, cancellationToken));
        Assert.False(await session.Query<Role>().AnyAsync(candidate => candidate.ApplicationId == app.Id, cancellationToken));
        Assert.False(await session.Query<Permission>().AnyAsync(candidate => candidate.ApplicationId == app.Id, cancellationToken));
        Assert.False(await session.Query<RefreshToken>().AnyAsync(token => token.ApplicationId == app.Id, cancellationToken));
        Assert.False(await session.Query<UserToken>().AnyAsync(token => token.ApplicationId == app.Id, cancellationToken));
        Assert.NotNull(await session.GetAsync<User>(userId, cancellationToken));

        var pendingAfter = Assert.IsAssignableFrom<Notification>(await session.GetAsync<Notification>(pending, cancellationToken));
        var processingAfter = Assert.IsAssignableFrom<Notification>(await session.GetAsync<Notification>(processing, cancellationToken));
        var sentAfter = Assert.IsAssignableFrom<Notification>(await session.GetAsync<Notification>(sent, cancellationToken));
        Assert.Equal(NotificationStatus.Cancelled, pendingAfter.Status);
        Assert.Equal(versions[pending] + 1, pendingAfter.Version);
        Assert.Equal(NotificationStatus.Cancelled, processingAfter.Status);
        Assert.Null(processingAfter.ProcessingStartedAtUtc);
        Assert.Equal(versions[processing] + 1, processingAfter.Version);
        Assert.Equal(NotificationStatus.Sent, sentAfter.Status);
        Assert.Equal(versions[sent], sentAfter.Version);

        var rows = await session.Query<AuditLog>()
            .Where(row => row.ApplicationId == app.Id)
            .ToListAsync(cancellationToken);
        Assert.All(rows, row => Assert.Equal(app.AppKey, row.AppKey));

        var cancelled = Assert.Single(rows, row => row.Action == AuditActions.NotificationsCancelled);
        Assert.Equal(ApplicationAuditing.EntityType, cancelled.EntityType);
        Assert.Equal(app.Id.ToString(), cancelled.EntityId);
        using (var values = JsonDocument.Parse(cancelled.NewValues!))
        {
            Assert.Equal(NotificationsCancelledAuditSnapshot.ApplicationDeletedReason, values.RootElement.GetProperty("reason").GetString());
            Assert.Equal(2, values.RootElement.GetProperty("cancelledCount").GetInt32());
            var ids = values.RootElement.GetProperty("notificationIds").EnumerateArray().Select(item => item.GetGuid()).ToList();
            Assert.Equal(new[] { pending, processing }.Order(), ids);
        }

        var roleDeleted = Assert.Single(rows, row => row.Action == AuditActions.RoleDeleted);
        Assert.Equal(role.Id.ToString(), roleDeleted.EntityId);
        Assert.Null(roleDeleted.NewValues);
        var permissionDeleted = Assert.Single(rows, row => row.Action == AuditActions.PermissionDeleted);
        Assert.Null(permissionDeleted.NewValues);
        var applicationDeleted = Assert.Single(rows, row => row.Action == AuditActions.ApplicationDeleted);
        Assert.Equal(app.Id.ToString(), applicationDeleted.EntityId);
        Assert.Null(applicationDeleted.NewValues);
        Assert.NotNull(applicationDeleted.OldValues);
        Assert.DoesNotContain("secret", applicationDeleted.OldValues.Replace("clientSecretRotatedAtUtc", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(AuditActorType.User, applicationDeleted.ActorType);
    }

    [Fact]
    public async Task Delete_ActiveApplication_Returns409DeleteRequiresSuspended()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var admin = factory.CreateAdminClient();
        var app = await CreateAppAsync(admin, UniqueKey());

        var response = await admin.DeleteAsync($"/api/apps/{app.AppKey}", TestContext.Current.CancellationToken);

        await AssertProblemAsync(response, HttpStatusCode.Conflict, ApplicationErrors.DeleteRequiresSuspended);
        await AssertApplicationKeptAsync(app.Id);
    }

    [Fact]
    public async Task Delete_ApplicationWithAUserAccess_Returns409UntilTheAccessIsRevoked()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var admin = factory.CreateAdminClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        var app = await CreateAppAsync(admin, UniqueKey());
        var userId = await CreateUserAsync(admin);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/apps/{app.AppKey}/users/{userId}/roles", new ReplaceUserRolesRequest([]), cancellationToken)).StatusCode);
        await SuspendAsync(admin, app.AppKey);

        var blocked = await admin.DeleteAsync($"/api/apps/{app.AppKey}", cancellationToken);

        await AssertProblemAsync(blocked, HttpStatusCode.Conflict, ApplicationErrors.HasUserAccesses);
        await AssertApplicationKeptAsync(app.Id);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/apps/{app.AppKey}/users/{userId}", cancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/apps/{app.AppKey}", cancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Delete_CoreApplication_Returns409Reserved()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var admin = factory.CreateAdminClient();

        var response = await admin.DeleteAsync($"/api/apps/{CoreApplication.AppKey}", TestContext.Current.CancellationToken);

        await AssertProblemAsync(response, HttpStatusCode.Conflict, ApplicationErrors.Reserved);
        await AssertApplicationKeptAsync(CoreApplication.Id);
    }

    [Fact]
    public async Task Delete_UnknownApplication_Returns404()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var admin = factory.CreateAdminClient();

        var response = await admin.DeleteAsync($"/api/apps/{UniqueKey()}", TestContext.Current.CancellationToken);

        await AssertProblemAsync(response, HttpStatusCode.NotFound, ApplicationErrors.NotFound);
    }

    [Fact]
    public async Task NewApplicationWithTheKeyOfADeletedOne_DoesNotSeeItsNotificationsOrAuditHistory()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var admin = factory.CreateAdminClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        var appKey = UniqueKey();
        var oldApp = await CreateAppAsync(admin, appKey);
        var oldNotification = await QueueAsync(factory, oldApp);
        await SuspendAsync(admin, appKey);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/apps/{appKey}", cancellationToken)).StatusCode);

        var newApp = await CreateAppAsync(admin, appKey);

        Assert.NotEqual(oldApp.Id, newApp.Id);
        using var newClient = CreateAppClient(factory, newApp);
        var list = await newClient.GetFromJsonAsync<NotificationPageResponse>("/api/notifications", cancellationToken);
        Assert.NotNull(list);
        Assert.Equal(0, list.TotalCount);
        Assert.Equal(HttpStatusCode.NotFound, (await newClient.GetAsync($"/api/notifications/{oldNotification}", cancellationToken)).StatusCode);

        var newHistory = await GetAuditAsync(admin, $"/api/audit-logs?applicationId={newApp.Id}");
        var created = Assert.Single(newHistory.Items);
        Assert.Equal(AuditActions.ApplicationCreated, created.Action);
        Assert.Equal(newApp.Id, created.ApplicationId);

        var oldHistory = await GetAuditAsync(admin, $"/api/audit-logs?applicationId={oldApp.Id}");
        Assert.Contains(oldHistory.Items, row => row.Action == AuditActions.NotificationQueued);
        Assert.Contains(oldHistory.Items, row => row.Action == AuditActions.NotificationsCancelled);
        Assert.Contains(oldHistory.Items, row => row.Action == AuditActions.ApplicationDeleted);
        Assert.All(oldHistory.Items, row => Assert.Equal(oldApp.Id, row.ApplicationId));

        var byKey = await GetAuditAsync(admin, $"/api/audit-logs?appKey={appKey}&pageSize=200");
        Assert.Equal(oldHistory.TotalCount + newHistory.TotalCount, byKey.TotalCount);
    }

    [Fact]
    public async Task AuditEntries_CarryTheApplicationIdOfTheCallerOrOfTheAffectedApplication()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var admin = factory.CreateAdminClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        var app = await CreateAppAsync(admin, UniqueKey());
        var userId = await CreateUserAsync(admin);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/apps/{app.AppKey}/users/{userId}/roles", new ReplaceUserRolesRequest([]), cancellationToken)).StatusCode);

        using var session = postgres.SessionFactory.OpenSession();
        var entityId = userId.ToString();
        var userCreated = await session.Query<AuditLog>()
            .SingleAsync(row => row.EntityId == entityId && row.Action == AuditActions.UserCreated, cancellationToken);
        Assert.Equal(CoreApplication.AppKey, userCreated.AppKey);
        Assert.Equal(CoreApplication.Id, userCreated.ApplicationId);

        var granted = await session.Query<AuditLog>()
            .SingleAsync(row => row.Action == AuditActions.AccessGranted && row.AppKey == app.AppKey, cancellationToken);
        Assert.Equal(app.Id, granted.ApplicationId);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("{01937a00-0000-7000-8000-000000000001}")]
    public async Task AuditLogs_WithAnInvalidApplicationId_Returns400(string applicationId)
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var admin = factory.CreateAdminClient();

        var list = await admin.GetAsync($"/api/audit-logs?applicationId={Uri.EscapeDataString(applicationId)}", TestContext.Current.CancellationToken);
        var export = await admin.GetAsync($"/api/audit-logs/export?format=csv&applicationId={Uri.EscapeDataString(applicationId)}", TestContext.Current.CancellationToken);

        await AssertProblemAsync(list, HttpStatusCode.BadRequest, AuditLogErrors.InvalidApplicationId);
        await AssertProblemAsync(export, HttpStatusCode.BadRequest, AuditLogErrors.InvalidApplicationId);
    }

    private static string UniqueKey() => $"del-{Guid.NewGuid():N}"[..20];

    private static async Task<ApplicationCreatedResponse> CreateAppAsync(HttpClient admin, string appKey)
    {
        var response = await admin.PostAsJsonAsync(
            "/api/apps",
            new CreateApplicationRequest(appKey, "Deletion Client", null, false),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<ApplicationCreatedResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(created);
        return created;
    }

    private static HttpClient CreateAppClient(ApiFactory factory, ApplicationCreatedResponse app)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(AppKeyAuthentication.AppKeyHeaderName, app.AppKey);
        client.DefaultRequestHeaders.Add(AppKeyAuthentication.AppSecretHeaderName, app.ClientSecret);
        return client;
    }

    private static async Task<Guid> QueueAsync(ApiFactory factory, ApplicationCreatedResponse app)
    {
        using var client = CreateAppClient(factory, app);
        using var content = new StringContent(
            """{"to":{"email":"ana@example.com"},"subject":"Hola","html":"<p>x</p>"}""",
            Encoding.UTF8,
            "application/json");
        var response = await client.PostAsync("/api/notifications/email", content, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var queued = await response.Content.ReadFromJsonAsync<QueuedNotificationResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(queued);
        return queued.Id;
    }

    private async Task AdvanceAsync(Guid notificationId, bool toSent)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var session = postgres.SessionFactory.OpenSession();
        using var transaction = session.BeginTransaction();
        var notification = Assert.IsAssignableFrom<Notification>(await session.GetAsync<Notification>(notificationId, cancellationToken));
        Assert.True(notification.MarkProcessing(DateTime.UtcNow).IsSuccess);

        if (toSent)
        {
            Assert.True(notification.MarkSent(DateTime.UtcNow).IsSuccess);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<Dictionary<Guid, int>> LoadVersionsAsync(params Guid[] ids)
    {
        using var session = postgres.SessionFactory.OpenSession();
        var versions = new Dictionary<Guid, int>();

        foreach (var id in ids)
        {
            versions[id] = Assert.IsAssignableFrom<Notification>(await session.GetAsync<Notification>(id, TestContext.Current.CancellationToken)).Version;
        }

        return versions;
    }

    private static async Task<Guid> CreateUserAsync(HttpClient admin)
    {
        var response = await admin.PostAsJsonAsync(
            "/api/users",
            new CreateUserRequest($"app-del-{Guid.NewGuid():N}@example.com", "App User", Password),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<UserResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(created);
        return created.Id;
    }

    private async Task SeedTokensAsync(Guid userId, Guid applicationId)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var now = DateTime.UtcNow;
        using var session = postgres.SessionFactory.OpenSession();
        using var transaction = session.BeginTransaction();
        await session.SaveAsync(RefreshToken.Issue(userId, applicationId, now, TimeSpan.FromDays(30), null, null).Token, cancellationToken);
        await session.SaveAsync(UserToken.Issue(userId, applicationId, UserTokenPurpose.EmailVerification, now, TimeSpan.FromHours(1)).Token, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task SuspendAsync(HttpClient admin, string appKey)
    {
        var response = await admin.PatchAsJsonAsync(
            $"/api/apps/{appKey}/status",
            new UpdateApplicationStatusRequest("Suspended"),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<AuditLogPageResponse> GetAuditAsync(HttpClient admin, string url)
    {
        var page = await admin.GetFromJsonAsync<AuditLogPageResponse>(url, TestContext.Current.CancellationToken);
        Assert.NotNull(page);
        return page;
    }

    private async Task AssertApplicationKeptAsync(Guid applicationId)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var session = postgres.SessionFactory.OpenSession();
        Assert.NotNull(await session.GetAsync<Application>(applicationId, cancellationToken));
        var entityId = applicationId.ToString();
        Assert.False(await session.Query<AuditLog>()
            .AnyAsync(row => row.EntityId == entityId && row.Action == AuditActions.ApplicationDeleted, cancellationToken));
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode expectedStatus, Href.Core.Api.Common.Results.Error expectedError)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(TestContext.Current.CancellationToken);
        Assert.NotNull(problem);
        Assert.Equal(expectedError.Code, problem.Extensions["errorCode"]?.ToString());
    }
}
