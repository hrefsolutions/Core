using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Href.Core.Api.Common.Results;
using Href.Core.Api.Modules.Applications;
using Href.Core.Api.Modules.Audit;
using Href.Core.Api.Modules.Auth;
using Href.Core.Api.Modules.Authorization;
using Href.Core.Api.Modules.Users;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using NHibernate;
using NHibernate.Linq;
using Npgsql;

namespace Href.Core.Tests.Integration;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SessionsEndpointsIntegrationTests(PostgresFixture postgres)
{
    private const string Password = "correct horse battery";

    private static readonly PasswordHasher<User> FastHasher = new(
        Options.Create(new PasswordHasherOptions { IterationCount = 1_000 }));

    [Fact]
    public async Task Logout_RevokesTheFamilySoRefreshFailsAndAuditsIt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var seed = await SeedAsync(cancellationToken);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = CreateAppClient(factory, seed.Apps[0]);
        var login = await LoginAsync(client, seed.User, cancellationToken);
        var refreshed = await RefreshAsync(client, login.RefreshToken, cancellationToken);

        var logout = await client.PostAsJsonAsync("/api/auth/logout", new RefreshRequest(refreshed.RefreshToken), cancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        var afterwards = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(refreshed.RefreshToken), cancellationToken);
        await AssertProblemAsync(afterwards, HttpStatusCode.Unauthorized, AuthErrors.RefreshTokenRevoked, cancellationToken);
        var tokens = await ReadTokensAsync(seed.User.Id, cancellationToken);
        Assert.Equal(
            [RefreshTokenRevocationReason.Rotated, RefreshTokenRevocationReason.Logout],
            tokens.Select(token => token.RevokedReason!.Value));
        var audit = Assert.Single(await ReadAuditAsync(tokens[0].FamilyId.ToString(), cancellationToken));
        Assert.Equal(AuditActions.Logout, audit.Action);
        Assert.Equal(nameof(AuditActorType.User), audit.ActorType);
        Assert.Equal(seed.User.Id.ToString(), audit.ActorId);
        Assert.Equal(seed.Apps[0].AppKey, audit.AppKey);
        Assert.DoesNotContain(refreshed.RefreshToken, audit.NewValues, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Logout_WithARotatedToken_IsTreatedAsReuse()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var seed = await SeedAsync(cancellationToken);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = CreateAppClient(factory, seed.Apps[0]);
        var login = await LoginAsync(client, seed.User, cancellationToken);
        var refreshed = await RefreshAsync(client, login.RefreshToken, cancellationToken);

        var logout = await client.PostAsJsonAsync("/api/auth/logout", new RefreshRequest(login.RefreshToken), cancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        await AssertProblemAsync(
            await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(refreshed.RefreshToken), cancellationToken),
            HttpStatusCode.Unauthorized,
            AuthErrors.RefreshTokenRevoked,
            cancellationToken);
        var tokens = await ReadTokensAsync(seed.User.Id, cancellationToken);
        Assert.Equal(RefreshTokenRevocationReason.ReuseDetected, tokens[1].RevokedReason);
        var audit = Assert.Single(await ReadAuditAsync(tokens[0].FamilyId.ToString(), cancellationToken));
        Assert.Equal(AuditActions.RefreshTokenReuseDetected, audit.Action);
    }

    [Fact]
    public async Task Logout_OfAnExpiredFamily_ChangesNothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var seed = await SeedAsync(cancellationToken);
        var expired = RefreshToken.Issue(seed.User.Id, seed.Apps[0].Id, DateTime.UtcNow.AddDays(-31), TimeSpan.FromDays(30), null, null);
        await SaveAllAsync(cancellationToken, expired.Token);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = CreateAppClient(factory, seed.Apps[0]);

        var logout = await client.PostAsJsonAsync("/api/auth/logout", new RefreshRequest(expired.PlainToken), cancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Null(Assert.Single(await ReadTokensAsync(seed.User.Id, cancellationToken)).RevokedAtUtc);
        Assert.Empty(await ReadAuditAsync(expired.Token.FamilyId.ToString(), cancellationToken));
    }

    [Fact]
    public async Task Logout_Twice_Returns204BothTimesAndAuditsOnce()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var seed = await SeedAsync(cancellationToken);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = CreateAppClient(factory, seed.Apps[0]);
        var login = await LoginAsync(client, seed.User, cancellationToken);

        var first = await client.PostAsJsonAsync("/api/auth/logout", new RefreshRequest(login.RefreshToken), cancellationToken);
        var second = await client.PostAsJsonAsync("/api/auth/logout", new RefreshRequest(login.RefreshToken), cancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        var token = Assert.Single(await ReadTokensAsync(seed.User.Id, cancellationToken));
        Assert.Single(await ReadAuditAsync(token.FamilyId.ToString(), cancellationToken));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-real-refresh-token")]
    public async Task Logout_WithAMissingOrUnknownToken_Returns204(string? refreshToken)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var seed = await SeedAsync(cancellationToken);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = CreateAppClient(factory, seed.Apps[0]);

        var response = await client.PostAsJsonAsync("/api/auth/logout", new RefreshRequest(refreshToken), cancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Logout_WithATokenOfAnotherApplication_Returns204AndLeavesTheSessionActive()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var seed = await SeedAsync(cancellationToken);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var ownClient = CreateAppClient(factory, seed.Apps[0]);
        using var otherClient = CreateAppClient(factory, seed.Apps[1]);
        var login = await LoginAsync(ownClient, seed.User, cancellationToken);

        var logout = await otherClient.PostAsJsonAsync("/api/auth/logout", new RefreshRequest(login.RefreshToken), cancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        await RefreshAsync(ownClient, login.RefreshToken, cancellationToken);
    }

    [Fact]
    public async Task Logout_EndsOnlyItsOwnSession()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var seed = await SeedAsync(cancellationToken);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = CreateAppClient(factory, seed.Apps[0]);
        var first = await LoginAsync(client, seed.User, cancellationToken);
        var second = await LoginAsync(client, seed.User, cancellationToken);

        await client.PostAsJsonAsync("/api/auth/logout", new RefreshRequest(first.RefreshToken), cancellationToken);

        await RefreshAsync(client, second.RefreshToken, cancellationToken);
    }

    [Fact]
    public async Task Logout_WithoutApplicationCredentials_Returns401()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/logout", new RefreshRequest("token"), cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetSessions_ListsOnlyActiveFamiliesWithTheirStartAndLastRotation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var seed = await SeedAsync(cancellationToken);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var firstApp = CreateAppClient(factory, seed.Apps[0]);
        using var secondApp = CreateAppClient(factory, seed.Apps[1]);
        using var admin = factory.CreateClient();
        var loggedOut = await LoginAsync(firstApp, seed.User, cancellationToken);
        await firstApp.PostAsJsonAsync("/api/auth/logout", new RefreshRequest(loggedOut.RefreshToken), cancellationToken);
        var expired = RefreshToken.Issue(seed.User.Id, seed.Apps[0].Id, DateTime.UtcNow.AddDays(-31), TimeSpan.FromDays(30), null, null);
        await SaveAllAsync(cancellationToken, expired.Token);
        var rotated = await LoginAsync(firstApp, seed.User, cancellationToken);
        await LoginAsync(secondApp, seed.User, cancellationToken);
        var latest = await RefreshAsync(firstApp, rotated.RefreshToken, cancellationToken);

        var response = await admin.GetAsync($"/api/users/{seed.User.Id}/sessions", cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sessions = await response.Content.ReadFromJsonAsync<List<UserSessionResponse>>(cancellationToken);
        Assert.NotNull(sessions);
        Assert.Equal([seed.Apps[0].AppKey, seed.Apps[1].AppKey], sessions.Select(session => session.AppKey));
        var tokens = await ReadTokensAsync(seed.User.Id, cancellationToken);
        var rotatedFamily = tokens.Single(token => token.TokenHash == RefreshToken.HashToken(latest.RefreshToken));
        var familyTokens = tokens.Where(token => token.FamilyId == rotatedFamily.FamilyId).ToList();
        var first = sessions[0];
        Assert.Equal(rotatedFamily.FamilyId, first.FamilyId);
        Assert.Equal(seed.Apps[0].Id, first.ApplicationId);
        Assert.Equal(familyTokens.Min(token => token.CreatedAtUtc), first.StartedAtUtc);
        Assert.Equal(rotatedFamily.CreatedAtUtc, first.LastRotatedAtUtc);
        Assert.True(first.LastRotatedAtUtc > first.StartedAtUtc);
        Assert.Equal(rotatedFamily.ExpiresAtUtc, first.ExpiresAtUtc);
        Assert.Equal(rotatedFamily.CreatedByIp, first.IpAddress);
        Assert.Equal(rotatedFamily.UserAgent, first.UserAgent);
    }

    [Fact]
    public async Task GetSessions_ForAnUnknownUser_Returns404()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var admin = factory.CreateClient();

        var response = await admin.GetAsync($"/api/users/{Guid.CreateVersion7()}/sessions", cancellationToken);

        await AssertProblemAsync(response, HttpStatusCode.NotFound, UserErrors.NotFound, cancellationToken);
    }

    [Fact]
    public async Task DeleteSessions_RevokesEverySessionAndAuditsTheCount()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var seed = await SeedAsync(cancellationToken);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var firstApp = CreateAppClient(factory, seed.Apps[0]);
        using var secondApp = CreateAppClient(factory, seed.Apps[1]);
        using var admin = factory.CreateClient();
        var first = await LoginAsync(firstApp, seed.User, cancellationToken);
        var second = await LoginAsync(secondApp, seed.User, cancellationToken);

        var response = await admin.DeleteAsync($"/api/users/{seed.User.Id}/sessions", cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<RevokeSessionsResponse>(cancellationToken);
        Assert.Equal(2, body!.RevokedSessions);
        await AssertProblemAsync(
            await firstApp.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(first.RefreshToken), cancellationToken),
            HttpStatusCode.Unauthorized,
            AuthErrors.RefreshTokenRevoked,
            cancellationToken);
        await AssertProblemAsync(
            await secondApp.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(second.RefreshToken), cancellationToken),
            HttpStatusCode.Unauthorized,
            AuthErrors.RefreshTokenRevoked,
            cancellationToken);
        Assert.All(await ReadTokensAsync(seed.User.Id, cancellationToken), token => Assert.Equal(RefreshTokenRevocationReason.AdminRevoked, token.RevokedReason));
        var audit = Assert.Single(await ReadAuditAsync(seed.User.Id.ToString(), cancellationToken), row => row.Action == AuditActions.SessionsRevoked);
        using var values = JsonDocument.Parse(audit.NewValues);
        Assert.Equal(2, values.RootElement.GetProperty("revokedSessions").GetInt32());
        Assert.Equal(nameof(RefreshTokenRevocationReason.AdminRevoked), values.RootElement.GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, values.RootElement.GetProperty("appKey").ValueKind);
    }

    [Fact]
    public async Task DeleteSessions_ForOneApplication_LeavesTheOtherSessionsActive()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var seed = await SeedAsync(cancellationToken);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var firstApp = CreateAppClient(factory, seed.Apps[0]);
        using var secondApp = CreateAppClient(factory, seed.Apps[1]);
        using var admin = factory.CreateClient();
        var first = await LoginAsync(firstApp, seed.User, cancellationToken);
        var second = await LoginAsync(secondApp, seed.User, cancellationToken);

        var response = await admin.DeleteAsync($"/api/users/{seed.User.Id}/sessions?appKey={seed.Apps[0].AppKey}", cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, (await response.Content.ReadFromJsonAsync<RevokeSessionsResponse>(cancellationToken))!.RevokedSessions);
        await AssertProblemAsync(
            await firstApp.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(first.RefreshToken), cancellationToken),
            HttpStatusCode.Unauthorized,
            AuthErrors.RefreshTokenRevoked,
            cancellationToken);
        await RefreshAsync(secondApp, second.RefreshToken, cancellationToken);
        var audit = Assert.Single(await ReadAuditAsync(seed.User.Id.ToString(), cancellationToken), row => row.Action == AuditActions.SessionsRevoked);
        Assert.Contains(seed.Apps[0].AppKey, audit.NewValues, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("%20")]
    public async Task DeleteSessions_WithABlankAppKey_Returns404AndRevokesNothing(string appKey)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var seed = await SeedAsync(cancellationToken);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = CreateAppClient(factory, seed.Apps[0]);
        using var admin = factory.CreateClient();
        var login = await LoginAsync(client, seed.User, cancellationToken);

        var response = await admin.DeleteAsync($"/api/users/{seed.User.Id}/sessions?appKey={appKey}", cancellationToken);

        await AssertProblemAsync(response, HttpStatusCode.NotFound, ApplicationErrors.NotFound, cancellationToken);
        await RefreshAsync(client, login.RefreshToken, cancellationToken);
    }

    [Fact]
    public async Task DeleteSessions_WithNothingToRevoke_Returns200WithZeroAndRecordsNothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var seed = await SeedAsync(cancellationToken);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var admin = factory.CreateClient();

        var response = await admin.DeleteAsync($"/api/users/{seed.User.Id}/sessions", cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, (await response.Content.ReadFromJsonAsync<RevokeSessionsResponse>(cancellationToken))!.RevokedSessions);
        Assert.DoesNotContain(await ReadAuditAsync(seed.User.Id.ToString(), cancellationToken), row => row.Action == AuditActions.SessionsRevoked);
    }

    [Fact]
    public async Task DeleteSessions_ForAnUnknownUserOrApplication_Returns404()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var seed = await SeedAsync(cancellationToken);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var admin = factory.CreateClient();

        var unknownUser = await admin.DeleteAsync($"/api/users/{Guid.CreateVersion7()}/sessions", cancellationToken);
        var unknownApp = await admin.DeleteAsync($"/api/users/{seed.User.Id}/sessions?appKey=no-such-app", cancellationToken);

        await AssertProblemAsync(unknownUser, HttpStatusCode.NotFound, UserErrors.NotFound, cancellationToken);
        await AssertProblemAsync(unknownApp, HttpStatusCode.NotFound, ApplicationErrors.NotFound, cancellationToken);
    }

    [Fact]
    public async Task LockUser_RevokesAllSessionsInTheSameTransaction()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var seed = await SeedAsync(cancellationToken);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = CreateAppClient(factory, seed.Apps[0]);
        using var admin = factory.CreateClient();
        var login = await LoginAsync(client, seed.User, cancellationToken);

        var lockResponse = await admin.PostAsJsonAsync($"/api/users/{seed.User.Id}/lock", new LockUserRequest("Compromised"), cancellationToken);

        Assert.Equal(HttpStatusCode.OK, lockResponse.StatusCode);
        await AssertProblemAsync(
            await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(login.RefreshToken), cancellationToken),
            HttpStatusCode.Unauthorized,
            AuthErrors.RefreshTokenRevoked,
            cancellationToken);
        var token = Assert.Single(await ReadTokensAsync(seed.User.Id, cancellationToken));
        Assert.Equal(RefreshTokenRevocationReason.UserLocked, token.RevokedReason);
        var actions = (await ReadAuditAsync(seed.User.Id.ToString(), cancellationToken)).Select(row => row.Action).ToList();
        Assert.Contains(AuditActions.UserLocked, actions);
        Assert.Contains(AuditActions.SessionsRevoked, actions);
    }

    [Fact]
    public async Task ConcurrentRefreshAndAdminRevoke_LeaveNoActiveSession()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var seed = await SeedAsync(cancellationToken);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = CreateAppClient(factory, seed.Apps[0]);
        using var admin = factory.CreateClient();
        var login = await LoginAsync(client, seed.User, cancellationToken);

        var responses = await RunWhileUserLockIsHeldAsync(
            seed.User.Id,
            () => client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(login.RefreshToken), cancellationToken),
            () => admin.DeleteAsync($"/api/users/{seed.User.Id}/sessions", cancellationToken),
            cancellationToken);

        Assert.Contains(responses[0].StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Unauthorized });
        Assert.Equal(HttpStatusCode.OK, responses[1].StatusCode);
        Assert.All(await ReadTokensAsync(seed.User.Id, cancellationToken), token => Assert.NotNull(token.RevokedAtUtc));
    }

    /// <summary>
    /// The login queues on the advisory lock first, so it is granted first when the blocker releases it. With the lock
    /// order reversed in the user lock (row before advisory lock), the login would then wait on the row the user lock holds
    /// while the user lock waits on the advisory lock: a deadlock, reported by PostgreSQL as a 500 on one request.
    /// </summary>
    [Fact]
    public async Task ConcurrentLoginAndUserLock_DoNotDeadlockAndLeaveNoActiveSession()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var seed = await SeedAsync(cancellationToken);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = CreateAppClient(factory, seed.Apps[0]);
        using var admin = factory.CreateClient();

        var responses = await RunWhileUserLockIsHeldAsync(
            seed.User.Id,
            () => client.PostAsJsonAsync("/api/auth/login", new LoginRequest(seed.User.Email, Password), cancellationToken),
            () => admin.PostAsJsonAsync($"/api/users/{seed.User.Id}/lock", new LockUserRequest("Compromised"), cancellationToken),
            cancellationToken);

        Assert.Contains(responses[0].StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Forbidden });
        Assert.Equal(HttpStatusCode.OK, responses[1].StatusCode);
        Assert.All(await ReadTokensAsync(seed.User.Id, cancellationToken), token => Assert.NotNull(token.RevokedAtUtc));
    }

    private async Task<HttpResponseMessage[]> RunWhileUserLockIsHeldAsync(
        Guid userId,
        Func<Task<HttpResponseMessage>> first,
        Func<Task<HttpResponseMessage>> second,
        CancellationToken cancellationToken)
    {
        await using var blocker = new NpgsqlConnection(postgres.ConnectionString);
        await blocker.OpenAsync(cancellationToken);
        await using var transaction = await blocker.BeginTransactionAsync(cancellationToken);
        await using (var hold = new NpgsqlCommand(
            "SELECT 1 FROM pg_advisory_xact_lock(hashtextextended('refresh_tokens:' || @userId, 0))",
            blocker,
            transaction))
        {
            hold.Parameters.AddWithValue("userId", userId.ToString());
            await hold.ExecuteScalarAsync(cancellationToken);
        }

        var firstTask = first();
        await WaitForBlockedAdvisoryLocksAsync(1, cancellationToken);
        var secondTask = second();
        await WaitForBlockedAdvisoryLocksAsync(2, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await Task.WhenAll(firstTask, secondTask);
    }

    private async Task WaitForBlockedAdvisoryLocksAsync(int expected, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var deadline = DateTime.UtcNow.AddSeconds(15);

        while (true)
        {
            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND NOT granted",
                connection);
            var blocked = (long)(await command.ExecuteScalarAsync(cancellationToken))!;

            if (blocked >= expected)
            {
                return;
            }

            Assert.True(DateTime.UtcNow < deadline, $"Only {blocked} of {expected} requests reached the advisory lock.");
            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        }
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, Error error, CancellationToken cancellationToken)
    {
        Assert.Equal(status, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken);
        Assert.NotNull(problem);
        Assert.Equal(error.Code, problem.Extensions["errorCode"]?.ToString());
    }

    private static async Task<TokenResponse> LoginAsync(HttpClient client, User user, CancellationToken cancellationToken)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email, Password), cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken))!;
    }

    private static async Task<TokenResponse> RefreshAsync(HttpClient client, string refreshToken, CancellationToken cancellationToken)
    {
        var response = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(refreshToken), cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken))!;
    }

    private static HttpClient CreateAppClient(ApiFactory factory, SeededApplication app)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(AppKeyAuthentication.AppKeyHeaderName, app.AppKey);
        client.DefaultRequestHeaders.Add(AppKeyAuthentication.AppSecretHeaderName, app.Secret);
        return client;
    }

    private async Task<Seed> SeedAsync(CancellationToken cancellationToken)
    {
        var user = User.Create($"sessions-{Guid.NewGuid():N}@example.com", "Sessions User", Password, FastHasher).Value;
        user.MarkEmailVerified(DateTime.UtcNow);
        var entities = new List<object> { user };
        var apps = new List<SeededApplication>();

        for (var index = 0; index < 2; index++)
        {
            var (application, secret) = Application.Create($"ses-{Guid.NewGuid():N}"[..20], $"Sessions App {index}", null, false, TimeProvider.System).Value;
            var role = Role.Create(application.Id, "Member", null).Value;
            entities.Add(application);
            entities.Add(role);
            entities.Add(UserApplicationAccess.Grant(user.Id, application.Id, [role]).Value);
            apps.Add(new SeededApplication(application.Id, application.AppKey, secret));
        }

        await SaveAllAsync(cancellationToken, [.. entities]);
        return new Seed(user, apps);
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

    private async Task<List<RefreshToken>> ReadTokensAsync(Guid userId, CancellationToken cancellationToken)
    {
        using var session = postgres.SessionFactory.OpenSession();
        return await session.Query<RefreshToken>()
            .Where(token => token.UserId == userId)
            .OrderBy(token => token.CreatedAtUtc)
            .ToListAsync(cancellationToken);
    }

    private async Task<List<AuditRow>> ReadAuditAsync(string entityId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT action, actor_type, actor_id, app_key, COALESCE(new_values::text, '') FROM audit_logs WHERE entity_id = @entityId ORDER BY occurred_at_utc",
            connection);
        command.Parameters.AddWithValue("entityId", entityId);
        var rows = new List<AuditRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new AuditRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4)));
        }

        return rows;
    }

    private sealed record SeededApplication(Guid Id, string AppKey, string Secret);

    private sealed record Seed(User User, IReadOnlyList<SeededApplication> Apps);

    private sealed record AuditRow(string Action, string ActorType, string? ActorId, string? AppKey, string NewValues);
}
