using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Href.Core.Api.Modules.Applications;
using Href.Core.Api.Modules.Audit;
using Href.Core.Api.Modules.Authorization;
using Href.Core.Api.Modules.Users;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using NHibernate.Linq;

namespace Href.Core.Tests.Integration;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class UsersEndpointIntegrationTests(PostgresFixture postgres)
{
    private const string Password = "correct horse battery";

    [Fact]
    public async Task Create_Returns201WithVerifiedActiveUserAndRecordsSafeAuditRow()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        var email = UniqueEmail("create");

        var response = await client.PostAsJsonAsync(
            "/api/users",
            new CreateUserRequest($"  {email.ToUpperInvariant()} ", "  Ana Pérez ", Password),
            cancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        AssertNoSecrets(json);
        var created = await response.Content.ReadFromJsonAsync<UserResponse>(cancellationToken);
        Assert.NotNull(created);
        Assert.EndsWith($"/api/users/{created.Id}", response.Headers.Location?.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(email, created.Email);
        Assert.Equal("Ana Pérez", created.FullName);
        Assert.Equal("Active", created.Status);
        Assert.NotNull(created.EmailVerifiedAtUtc);
        Assert.Null(created.LockedAtUtc);

        var stored = await LoadUserAsync(created.Id);
        Assert.True(stored.VerifyPassword(Password, new PasswordHasher<User>()));

        var auditRow = Assert.Single(await AuditRowsAsync(created.Id));
        Assert.Equal(AuditActions.UserCreated, auditRow.Action);
        Assert.Equal(UserAuditing.EntityType, auditRow.EntityType);
        Assert.Equal(AuditActorType.Anonymous, auditRow.ActorType);
        Assert.Null(auditRow.OldValues);
        Assert.Equal(email, Parse(auditRow.NewValues).GetProperty("email").GetString());
        AssertNoSecrets(auditRow.NewValues!);
        Assert.DoesNotContain(stored.PasswordHash, auditRow.NewValues, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_WithEmailTakenInOtherCase_Returns409()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();
        var email = UniqueEmail("dup");
        await CreateUserAsync(client, email, "First");

        var response = await client.PostAsJsonAsync(
            "/api/users",
            new CreateUserRequest(email.ToUpperInvariant(), "Second", Password),
            TestContext.Current.CancellationToken);

        await AssertProblemAsync(response, HttpStatusCode.Conflict, UserErrors.EmailTaken);
    }

    [Fact]
    public async Task Create_WhenSameEmailIsPostedConcurrently_ReturnsOneCreatedAndOnlyConflicts()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();
        var email = UniqueEmail("race");

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.PostAsJsonAsync(
            "/api/users",
            new CreateUserRequest(email, "Race", Password),
            TestContext.Current.CancellationToken)));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
        Assert.All(
            responses.Where(response => response.StatusCode != HttpStatusCode.Created),
            response => Assert.Equal(HttpStatusCode.Conflict, response.StatusCode));
    }

    [Theory]
    [InlineData("not-an-email", "Ana", Password, "Users.InvalidEmail")]
    [InlineData(null, "Ana", Password, "Users.InvalidEmail")]
    [InlineData("valid@example.com", " ", Password, "Users.InvalidFullName")]
    [InlineData("valid@example.com", "Ana", "short", "Users.WeakPassword")]
    [InlineData("valid@example.com", "Ana", null, "Users.WeakPassword")]
    public async Task Create_WithInvalidInput_Returns400AndCreatesNothing(string? email, string? fullName, string? password, string errorCode)
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();
        var uniqueEmail = email == "valid@example.com" ? UniqueEmail("invalid") : email;

        var response = await client.PostAsJsonAsync(
            "/api/users",
            new CreateUserRequest(uniqueEmail, fullName, password),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(TestContext.Current.CancellationToken);
        Assert.NotNull(problem);
        Assert.Equal(errorCode, problem.Extensions["errorCode"]?.ToString());

        if (uniqueEmail is not null && uniqueEmail.Contains('@'))
        {
            Assert.Equal(0, await CountUsersByEmailAsync(uniqueEmail));
        }
    }

    [Fact]
    public async Task List_FiltersBySearchAndStatusAndPages()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        var tag = $"lst{Guid.NewGuid():N}"[..14];
        var alpha = await CreateUserAsync(client, $"{tag}-a@example.com", "Alpha Person");
        var beta = await CreateUserAsync(client, $"{tag}-b@example.com", $"Beta {tag.ToUpperInvariant()}");
        var gamma = await CreateUserAsync(client, $"{tag}-c@example.com", "Gamma Person");
        await client.PostAsJsonAsync($"/api/users/{gamma.Id}/lock", new LockUserRequest("test"), cancellationToken);

        var all = await GetPageAsync(client, $"/api/users?search={tag.ToUpperInvariant()}");
        Assert.Equal(3, all.TotalCount);
        Assert.Equal([alpha.Id, beta.Id, gamma.Id], all.Items.Select(item => item.Id));
        Assert.All(all.Items, item => Assert.DoesNotContain("password", JsonSerializer.Serialize(item), StringComparison.OrdinalIgnoreCase));

        var locked = await GetPageAsync(client, $"/api/users?search={tag}&status=Locked");
        Assert.Equal([gamma.Id], locked.Items.Select(item => item.Id));

        var active = await GetPageAsync(client, $"/api/users?search={tag}&status=Active");
        Assert.Equal([alpha.Id, beta.Id], active.Items.Select(item => item.Id));

        var secondPage = await GetPageAsync(client, $"/api/users?search={tag}&page=2&pageSize=2");
        Assert.Equal(3, secondPage.TotalCount);
        Assert.Equal(2, secondPage.Page);
        Assert.Equal(2, secondPage.PageSize);
        Assert.Equal([gamma.Id], secondPage.Items.Select(item => item.Id));

        var byName = await GetPageAsync(client, $"/api/users?search=beta%20{tag}");
        Assert.Equal([beta.Id], byName.Items.Select(item => item.Id));

        var beyond = await GetPageAsync(client, $"/api/users?search={tag}&page=5&pageSize=2");
        Assert.Empty(beyond.Items);
        Assert.Equal(3, beyond.TotalCount);
    }

    [Fact]
    public async Task List_TreatsLikeWildcardsInSearchLiterally()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();
        var tag = $"wc{Guid.NewGuid():N}"[..14];
        var underscore = await CreateUserAsync(client, $"{tag}_x@example.com", "Underscore");
        await CreateUserAsync(client, $"{tag}ax@example.com", "Letter");

        var page = await GetPageAsync(client, $"/api/users?search={tag}_x");
        Assert.Equal([underscore.Id], page.Items.Select(item => item.Id));

        var percent = await GetPageAsync(client, $"/api/users?search={tag}%25");
        Assert.Empty(percent.Items);
    }

    [Theory]
    [InlineData("?page=0", "Users.InvalidPage")]
    [InlineData("?pageSize=0", "Users.InvalidPageSize")]
    [InlineData("?pageSize=201", "Users.InvalidPageSize")]
    [InlineData("?status=locked", "Users.InvalidStatus")]
    [InlineData("?status=1", "Users.InvalidStatus")]
    [InlineData("?status=Active,Locked", "Users.InvalidStatus")]
    public async Task List_WithInvalidQuery_Returns400(string query, string errorCode)
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/users{query}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(TestContext.Current.CancellationToken);
        Assert.NotNull(problem);
        Assert.Equal(errorCode, problem.Extensions["errorCode"]?.ToString());
    }

    [Fact]
    public async Task GetById_IncludesApplicationsWithSortedRoles()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        var user = await CreateUserAsync(client, UniqueEmail("detail"), "Detail");
        var zetaApp = Application.Create($"zz-{Guid.NewGuid():N}"[..20], "Zeta App", null, false, TimeProvider.System).Value.Application;
        var alphaApp = Application.Create($"aa-{Guid.NewGuid():N}"[..20], "Alpha App", null, false, TimeProvider.System).Value.Application;
        var viewer = Role.Create(alphaApp.Id, "Viewer", null).Value;
        var admin = Role.Create(alphaApp.Id, "Admin", null).Value;
        var zetaAccess = UserApplicationAccess.Grant(user.Id, zetaApp.Id, []).Value;
        var alphaAccess = UserApplicationAccess.Grant(user.Id, alphaApp.Id, [viewer, admin]).Value;
        await SaveAllAsync(zetaApp, alphaApp, viewer, admin, zetaAccess, alphaAccess);

        var response = await client.GetAsync($"/api/users/{user.Id}", cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertNoSecrets(await response.Content.ReadAsStringAsync(cancellationToken));
        var detail = await response.Content.ReadFromJsonAsync<UserDetailResponse>(cancellationToken);
        Assert.NotNull(detail);
        Assert.Equal(user.Id, detail.Id);
        Assert.Equal([alphaApp.AppKey, zetaApp.AppKey], detail.Applications.Select(app => app.AppKey));
        Assert.Equal(alphaApp.Id, detail.Applications[0].ApplicationId);
        Assert.Equal("Alpha App", detail.Applications[0].Name);
        Assert.Equal(["Admin", "Viewer"], detail.Applications[0].Roles);
        Assert.Empty(detail.Applications[1].Roles);
    }

    [Fact]
    public async Task UnknownUser_Returns404OnEveryRoute()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        var id = Guid.CreateVersion7();

        await AssertProblemAsync(await client.GetAsync($"/api/users/{id}", cancellationToken), HttpStatusCode.NotFound, UserErrors.NotFound);
        await AssertProblemAsync(
            await client.PutAsJsonAsync($"/api/users/{id}", new UpdateUserRequest(UniqueEmail("ghost"), "Ghost"), cancellationToken),
            HttpStatusCode.NotFound,
            UserErrors.NotFound);
        await AssertProblemAsync(await client.PostAsync($"/api/users/{id}/lock", EmptyJsonBody(), cancellationToken), HttpStatusCode.NotFound, UserErrors.NotFound);
        await AssertProblemAsync(await client.PostAsync($"/api/users/{id}/unlock", null, cancellationToken), HttpStatusCode.NotFound, UserErrors.NotFound);
    }

    [Fact]
    public async Task Update_NameOnly_KeepsVerificationAndRecordsUserUpdated()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();
        var email = UniqueEmail("upd");
        var user = await CreateUserAsync(client, email, "Old Name");

        var response = await client.PutAsJsonAsync(
            $"/api/users/{user.Id}",
            new UpdateUserRequest(email.ToUpperInvariant(), "New Name"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<UserResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(updated);
        Assert.Equal("New Name", updated.FullName);
        Assert.NotNull(updated.EmailVerifiedAtUtc);

        var rows = await AuditRowsAsync(user.Id);
        Assert.Equal([AuditActions.UserCreated, AuditActions.UserUpdated], rows.Select(row => row.Action));
        Assert.Equal("Old Name", Parse(rows[1].OldValues).GetProperty("fullName").GetString());
        Assert.Equal("New Name", Parse(rows[1].NewValues).GetProperty("fullName").GetString());
    }

    [Fact]
    public async Task Update_WithNewEmail_ClearsVerificationRegeneratesStampAndRecordsEmailChanged()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();
        var user = await CreateUserAsync(client, UniqueEmail("chg"), "Ana");
        var stampBefore = (await LoadUserAsync(user.Id)).SecurityStamp;
        var newEmail = UniqueEmail("new");

        var response = await client.PutAsJsonAsync(
            $"/api/users/{user.Id}",
            new UpdateUserRequest(newEmail, "Ana María"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<UserResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(updated);
        Assert.Equal(newEmail, updated.Email);
        Assert.Equal("Ana María", updated.FullName);
        Assert.Null(updated.EmailVerifiedAtUtc);
        Assert.NotEqual(stampBefore, (await LoadUserAsync(user.Id)).SecurityStamp);

        var rows = await AuditRowsAsync(user.Id);
        Assert.Equal([AuditActions.UserCreated, AuditActions.UserEmailChanged], rows.Select(row => row.Action));
        Assert.Equal(user.Email, Parse(rows[1].OldValues).GetProperty("email").GetString());
        Assert.Equal(newEmail, Parse(rows[1].NewValues).GetProperty("email").GetString());
        Assert.Equal("Ana María", Parse(rows[1].NewValues).GetProperty("fullName").GetString());
    }

    [Fact]
    public async Task Update_WithoutChanges_Returns200AndRecordsNothing()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();
        var user = await CreateUserAsync(client, UniqueEmail("same"), "Same");

        var response = await client.PutAsJsonAsync(
            $"/api/users/{user.Id}",
            new UpdateUserRequest(user.Email, " Same "),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([AuditActions.UserCreated], (await AuditRowsAsync(user.Id)).Select(row => row.Action));
    }

    [Fact]
    public async Task Update_WithEmailOfAnotherUser_Returns409AndKeepsBothUsers()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();
        var first = await CreateUserAsync(client, UniqueEmail("first"), "First");
        var second = await CreateUserAsync(client, UniqueEmail("second"), "Second");

        var response = await client.PutAsJsonAsync(
            $"/api/users/{second.Id}",
            new UpdateUserRequest(first.Email.ToUpperInvariant(), "Second Renamed"),
            TestContext.Current.CancellationToken);

        await AssertProblemAsync(response, HttpStatusCode.Conflict, UserErrors.EmailTaken);
        var stored = await LoadUserAsync(second.Id);
        Assert.Equal(second.Email, stored.Email);
        Assert.Equal("Second", stored.FullName);
        Assert.Equal([AuditActions.UserCreated], (await AuditRowsAsync(second.Id)).Select(row => row.Action));
    }

    [Theory]
    [InlineData("broken", "Valid Name", "Users.InvalidEmail")]
    [InlineData(null, "Valid Name", "Users.InvalidEmail")]
    [InlineData("keep", "", "Users.InvalidFullName")]
    [InlineData("keep", null, "Users.InvalidFullName")]
    public async Task Update_WithInvalidInput_Returns400AndChangesNothing(string? email, string? fullName, string errorCode)
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();
        var user = await CreateUserAsync(client, UniqueEmail("inv"), "Original");

        var response = await client.PutAsJsonAsync(
            $"/api/users/{user.Id}",
            new UpdateUserRequest(email == "keep" ? UniqueEmail("other") : email, fullName),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(TestContext.Current.CancellationToken);
        Assert.NotNull(problem);
        Assert.Equal(errorCode, problem.Extensions["errorCode"]?.ToString());
        var stored = await LoadUserAsync(user.Id);
        Assert.Equal(user.Email, stored.Email);
        Assert.Equal("Original", stored.FullName);
        Assert.NotNull(stored.EmailVerifiedAtUtc);
    }

    [Fact]
    public async Task LockAndUnlock_ChangeStatusRegenerateStampAndRecordAuditRows()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();
        var cancellationToken = TestContext.Current.CancellationToken;
        var user = await CreateUserAsync(client, UniqueEmail("lock"), "Lockable");
        var stampBefore = (await LoadUserAsync(user.Id)).SecurityStamp;

        var lockResponse = await client.PostAsJsonAsync($"/api/users/{user.Id}/lock", new LockUserRequest("Compromised"), cancellationToken);

        Assert.Equal(HttpStatusCode.OK, lockResponse.StatusCode);
        var locked = await lockResponse.Content.ReadFromJsonAsync<UserResponse>(cancellationToken);
        Assert.NotNull(locked);
        Assert.Equal("Locked", locked.Status);
        Assert.Equal("Compromised", locked.LockReason);
        Assert.NotNull(locked.LockedAtUtc);
        var stampLocked = (await LoadUserAsync(user.Id)).SecurityStamp;
        Assert.NotEqual(stampBefore, stampLocked);

        await AssertProblemAsync(
            await client.PostAsJsonAsync($"/api/users/{user.Id}/lock", new LockUserRequest("Again"), cancellationToken),
            HttpStatusCode.Conflict,
            UserErrors.AlreadyLocked);

        var unlockResponse = await client.PostAsync($"/api/users/{user.Id}/unlock", null, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, unlockResponse.StatusCode);
        var unlocked = await unlockResponse.Content.ReadFromJsonAsync<UserResponse>(cancellationToken);
        Assert.NotNull(unlocked);
        Assert.Equal("Active", unlocked.Status);
        Assert.Null(unlocked.LockReason);
        Assert.Null(unlocked.LockedAtUtc);
        Assert.NotEqual(stampLocked, (await LoadUserAsync(user.Id)).SecurityStamp);

        await AssertProblemAsync(
            await client.PostAsync($"/api/users/{user.Id}/unlock", null, cancellationToken),
            HttpStatusCode.Conflict,
            UserErrors.NotLocked);

        var rows = await AuditRowsAsync(user.Id);
        Assert.Equal([AuditActions.UserCreated, AuditActions.UserLocked, AuditActions.UserUnlocked], rows.Select(row => row.Action));
        Assert.Equal("Active", Parse(rows[1].OldValues).GetProperty("status").GetString());
        Assert.Equal("Locked", Parse(rows[1].NewValues).GetProperty("status").GetString());
        Assert.Equal("Compromised", Parse(rows[1].NewValues).GetProperty("lockReason").GetString());
        Assert.Equal("Active", Parse(rows[2].NewValues).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Lock_WithEmptyBody_LocksWithoutReason()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();
        var user = await CreateUserAsync(client, UniqueEmail("nobody"), "No Body");

        var response = await client.PostAsync($"/api/users/{user.Id}/lock", EmptyJsonBody(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var locked = await response.Content.ReadFromJsonAsync<UserResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(locked);
        Assert.Equal("Locked", locked.Status);
        Assert.Null(locked.LockReason);
    }

    [Fact]
    public async Task Lock_WithTooLongReason_Returns400AndKeepsUserActive()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();
        var user = await CreateUserAsync(client, UniqueEmail("long"), "Long Reason");

        var response = await client.PostAsJsonAsync(
            $"/api/users/{user.Id}/lock",
            new LockUserRequest(new string('r', User.LockReasonMaxLength + 1)),
            TestContext.Current.CancellationToken);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, UserErrors.InvalidLockReason);
        Assert.Equal(UserStatus.Active, (await LoadUserAsync(user.Id)).Status);
    }

    [Fact]
    public async Task UsersRoutes_DoNotExposeDelete()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();
        var user = await CreateUserAsync(client, UniqueEmail("nodelete"), "No Delete");

        var response = await client.DeleteAsync($"/api/users/{user.Id}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.NotNull(await LoadUserAsync(user.Id));
    }

    private static StringContent EmptyJsonBody() => new(string.Empty, System.Text.Encoding.UTF8, "application/json");

    private static string UniqueEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}@example.com";

    private static void AssertNoSecrets(string json)
    {
        Assert.DoesNotContain("passwordHash", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("securityStamp", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"password\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Password, json, StringComparison.Ordinal);
    }

    private static JsonElement Parse(string? json)
    {
        Assert.NotNull(json);
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode expectedStatus, Href.Core.Api.Common.Results.Error expectedError)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(TestContext.Current.CancellationToken);
        Assert.NotNull(problem);
        Assert.Equal(expectedError.Code, problem.Extensions["errorCode"]?.ToString());
    }

    private static async Task<UserResponse> CreateUserAsync(HttpClient client, string email, string fullName)
    {
        var response = await client.PostAsJsonAsync(
            "/api/users",
            new CreateUserRequest(email, fullName, Password),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<UserResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(created);
        return created;
    }

    private static async Task<UserPageResponse> GetPageAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<UserPageResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(page);
        return page;
    }

    private async Task<User> LoadUserAsync(Guid id)
    {
        using var session = postgres.SessionFactory.OpenSession();
        var user = await session.GetAsync<User>(id, TestContext.Current.CancellationToken);
        Assert.NotNull(user);
        return user;
    }

    private async Task<int> CountUsersByEmailAsync(string email)
    {
        using var session = postgres.SessionFactory.OpenSession();
        var normalized = email.Trim().ToLowerInvariant();
        return await session.Query<User>().CountAsync(user => user.Email == normalized, TestContext.Current.CancellationToken);
    }

    private async Task<List<AuditLog>> AuditRowsAsync(Guid userId)
    {
        using var session = postgres.SessionFactory.OpenSession();
        var entityId = userId.ToString();
        var rows = await session.Query<AuditLog>()
            .Where(log => log.EntityType == UserAuditing.EntityType && log.EntityId == entityId)
            .ToListAsync(TestContext.Current.CancellationToken);

        return rows.OrderBy(log => log.OccurredAtUtc).ThenBy(log => log.Id).ToList();
    }

    private async Task SaveAllAsync(params object[] entities)
    {
        using var session = postgres.SessionFactory.OpenSession();
        using var transaction = session.BeginTransaction();

        foreach (var entity in entities)
        {
            await session.SaveAsync(entity, TestContext.Current.CancellationToken);
        }

        await transaction.CommitAsync(TestContext.Current.CancellationToken);
    }
}
