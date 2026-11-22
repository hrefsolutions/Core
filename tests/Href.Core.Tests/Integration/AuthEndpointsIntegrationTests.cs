using System.Net;
using System.Net.Http.Json;
using Href.Core.Api.Common.Results;
using Href.Core.Api.Modules.Applications;
using Href.Core.Api.Modules.Audit;
using Href.Core.Api.Modules.Auth;
using Href.Core.Api.Modules.Authorization;
using Href.Core.Api.Modules.Users;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NHibernate;
using NHibernate.Linq;
using Npgsql;

namespace Href.Core.Tests.Integration;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AuthEndpointsIntegrationTests(PostgresFixture postgres)
{
    private const string Password = "correct horse battery";
    private const string Issuer = "http://localhost";

    private static readonly PasswordHasher<User> FastHasher = new(
        Options.Create(new PasswordHasherOptions { IterationCount = 1_000 }));

    [Fact]
    public async Task Login_WithValidCredentials_ReturnsTokensForTheCallingApplication()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var seed = await SeedAsync(cancellationToken);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = CreateAppClient(factory, seed.App);

        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest($"  {seed.User.Email.ToUpperInvariant()} ", Password), cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var tokens = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken);
        Assert.NotNull(tokens);
        Assert.Equal("Bearer", tokens.TokenType);
        Assert.Equal(900, tokens.ExpiresIn);
        Assert.Equal(43, tokens.RefreshToken.Length);
        Assert.InRange(tokens.RefreshTokenExpiresAtUtc, DateTime.UtcNow.AddDays(30).AddMinutes(-5), DateTime.UtcNow.AddDays(30).AddMinutes(5));

        var token = await ValidateWithPublishedKeysAsync(client, tokens.AccessToken, seed.App.AppKey);
        Assert.Equal(seed.User.Id.ToString(), token.Subject);
        Assert.Equal(seed.User.Email, token.GetClaim("email").Value);
        Assert.Equal("Auth User", token.GetClaim("name").Value);
        Assert.Equal(["Admin", "Viewer"], ClaimValues(token, "roles"));
        Assert.Equal(["orders.read", "orders.write"], ClaimValues(token, "permissions"));

        using var session = postgres.SessionFactory.OpenSession();
        var refreshTokenHash = RefreshToken.HashToken(tokens.RefreshToken);
        var refreshToken = await session.Query<RefreshToken>()
            .SingleAsync(candidate => candidate.TokenHash == refreshTokenHash, cancellationToken);
        Assert.Equal(seed.User.Id, refreshToken.UserId);
        Assert.Equal(seed.App.Id, refreshToken.ApplicationId);
        Assert.NotNull((await session.GetAsync<User>(seed.User.Id, cancellationToken)).LastLoginAtUtc);

        var audit = Assert.Single(await ReadAuditAsync(seed.User.Id.ToString(), cancellationToken), row => row.Action == AuditActions.LoginSucceeded);
        Assert.Equal(nameof(AuditActorType.User), audit.ActorType);
        Assert.Equal(seed.User.Id.ToString(), audit.ActorId);
        Assert.Equal(seed.App.AppKey, audit.AppKey);
        Assert.Contains(refreshToken.FamilyId.ToString(), audit.NewValues, StringComparison.Ordinal);
        Assert.DoesNotContain(tokens.RefreshToken, audit.NewValues, StringComparison.Ordinal);
        Assert.DoesNotContain(refreshToken.TokenHash, audit.NewValues, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_UpgradesAnOutdatedPasswordHash()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var seed = await SeedAsync(cancellationToken);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = CreateAppClient(factory, seed.App);

        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(seed.User.Email, Password), cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var session = postgres.SessionFactory.OpenSession();
        var user = await session.GetAsync<User>(seed.User.Id, cancellationToken);
        Assert.NotEqual(seed.User.PasswordHash, user.PasswordHash);
        Assert.True(user.VerifyPassword(Password, new PasswordHasher<User>()));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("wrong-password")]
    [InlineData("malformed-email")]
    [InlineData("empty-password")]
    [InlineData("null-fields")]
    public async Task Login_WithInvalidCredentials_ReturnsTheSame401(string scenario)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var seed = await SeedAsync(cancellationToken);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = CreateAppClient(factory, seed.App);
        var request = scenario switch
        {
            "unknown" => new LoginRequest($"nobody-{Guid.NewGuid():N}@example.com", Password),
            "wrong-password" => new LoginRequest(seed.User.Email, "wrong horse battery"),
            "malformed-email" => new LoginRequest("not an email", Password),
            "empty-password" => new LoginRequest(seed.User.Email, string.Empty),
            _ => new LoginRequest(null, null),
        };

        var response = await client.PostAsJsonAsync("/api/auth/login", request, cancellationToken);

        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, AuthErrors.InvalidCredentials, cancellationToken);
        Assert.Empty(await ReadRefreshTokensAsync(seed.User.Id, cancellationToken));
    }

    [Fact]
    public async Task Login_LockedUser_Returns403OnlyWithTheCorrectPassword()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var seed = await SeedAsync(cancellationToken, configureUser: user => user.Lock("Fraud", DateTime.UtcNow));
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = CreateAppClient(factory, seed.App);

        var wrongPassword = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(seed.User.Email, "wrong horse battery"), cancellationToken);
        var rightPassword = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(seed.User.Email, Password), cancellationToken);

        await AssertProblemAsync(wrongPassword, HttpStatusCode.Unauthorized, AuthErrors.InvalidCredentials, cancellationToken);
        await AssertProblemAsync(rightPassword, HttpStatusCode.Forbidden, AuthErrors.UserLocked, cancellationToken);
    }

    [Fact]
    public async Task Login_UnverifiedEmail_Returns403EmailNotVerified()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var seed = await SeedAsync(cancellationToken, verified: false);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = CreateAppClient(factory, seed.App);

        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(seed.User.Email, Password), cancellationToken);

        await AssertProblemAsync(response, HttpStatusCode.Forbidden, AuthErrors.EmailNotVerified, cancellationToken);
    }

    [Fact]
    public async Task Login_UserWithoutAccessToTheCallingApplication_Returns403NoAccess()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var seed = await SeedAsync(cancellationToken);
        var other = await SaveApplicationAsync(cancellationToken);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = CreateAppClient(factory, other);

        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(seed.User.Email, Password), cancellationToken);

        await AssertProblemAsync(response, HttpStatusCode.Forbidden, AuthErrors.NoAccessToApplication, cancellationToken);
        Assert.Empty(await ReadRefreshTokensAsync(seed.User.Id, cancellationToken));
    }

    [Fact]
    public async Task AuthEndpoints_WithoutApplicationCredentials_Return401()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();

        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("ana@example.com", Password), cancellationToken);
        var refresh = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest("token"), cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    [Fact]
    public async Task Login_FromASuspendedApplication_Returns403()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var seed = await SeedAsync(cancellationToken);
        using (var session = OpenSession())
        using (var transaction = session.BeginTransaction())
        {
            var application = await session.GetAsync<Application>(seed.App.Id, cancellationToken);
            application.Suspend();
            await transaction.CommitAsync(cancellationToken);
        }

        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = CreateAppClient(factory, seed.App);

        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(seed.User.Email, Password), cancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Refresh_RotatesTheTokensAndTheOldRefreshTokenStopsWorking()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var seed = await SeedAsync(cancellationToken);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = CreateAppClient(factory, seed.App);
        var login = await LoginAsync(client, seed, cancellationToken);

        var response = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(login.RefreshToken), cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var refreshed = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken);
        Assert.NotNull(refreshed);
        Assert.NotEqual(login.RefreshToken, refreshed.RefreshToken);
        Assert.NotEqual(login.AccessToken, refreshed.AccessToken);
        var token = await ValidateWithPublishedKeysAsync(client, refreshed.AccessToken, seed.App.AppKey);
        Assert.Equal(seed.User.Id.ToString(), token.Subject);
        var family = await ReadRefreshTokensAsync(seed.User.Id, cancellationToken);
        Assert.Equal(2, family.Count);
        Assert.Single(family.Select(candidate => candidate.FamilyId).Distinct());
    }

    [Fact]
    public async Task Refresh_WithARotatedToken_RevokesTheFamilyAndAuditsTheReuse()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var seed = await SeedAsync(cancellationToken);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = CreateAppClient(factory, seed.App);
        var login = await LoginAsync(client, seed, cancellationToken);
        var refreshed = await RefreshAsync(client, login.RefreshToken, cancellationToken);

        var reuse = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(login.RefreshToken), cancellationToken);
        var successor = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(refreshed.RefreshToken), cancellationToken);

        await AssertProblemAsync(reuse, HttpStatusCode.Unauthorized, AuthErrors.RefreshTokenReused, cancellationToken);
        await AssertProblemAsync(successor, HttpStatusCode.Unauthorized, AuthErrors.RefreshTokenRevoked, cancellationToken);
        var family = await ReadRefreshTokensAsync(seed.User.Id, cancellationToken);
        Assert.Contains(family, candidate => candidate.RevokedReason == RefreshTokenRevocationReason.ReuseDetected);
        Assert.All(family, candidate => Assert.NotNull(candidate.RevokedAtUtc));
        var audit = Assert.Single(await ReadAuditAsync(family[0].FamilyId.ToString(), cancellationToken));
        Assert.Equal(AuditActions.RefreshTokenReuseDetected, audit.Action);
        Assert.Equal(seed.App.AppKey, audit.AppKey);
        Assert.Contains(seed.User.Id.ToString(), audit.NewValues, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refresh_ConcurrentUsesOfTheSameToken_OneSucceedsTheOtherIsReuseAndTheFamilyIsRevoked()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var seed = await SeedAsync(cancellationToken);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = CreateAppClient(factory, seed.App);
        var login = await LoginAsync(client, seed, cancellationToken);

        var responses = await Task.WhenAll(
            client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(login.RefreshToken), cancellationToken),
            client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(login.RefreshToken), cancellationToken));

        var winner = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        var loser = Assert.Single(responses, response => response.StatusCode != HttpStatusCode.OK);
        await AssertProblemAsync(loser, HttpStatusCode.Unauthorized, AuthErrors.RefreshTokenReused, cancellationToken);
        var winnerTokens = await winner.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken);
        var afterwards = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(winnerTokens!.RefreshToken), cancellationToken);
        await AssertProblemAsync(afterwards, HttpStatusCode.Unauthorized, AuthErrors.RefreshTokenRevoked, cancellationToken);
        Assert.All(await ReadRefreshTokensAsync(seed.User.Id, cancellationToken), candidate => Assert.NotNull(candidate.RevokedAtUtc));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-real-refresh-token")]
    public async Task Refresh_WithAMissingOrUnknownToken_Returns401InvalidRefreshToken(string? refreshToken)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var seed = await SeedAsync(cancellationToken);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = CreateAppClient(factory, seed.App);

        var response = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(refreshToken), cancellationToken);

        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, AuthErrors.InvalidRefreshToken, cancellationToken);
    }

    [Fact]
    public async Task Refresh_WithATokenOfAnotherApplication_Returns401AndLeavesTheTokenUsable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var seed = await SeedAsync(cancellationToken);
        var other = await SaveApplicationAsync(cancellationToken);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var ownClient = CreateAppClient(factory, seed.App);
        using var otherClient = CreateAppClient(factory, other);
        var login = await LoginAsync(ownClient, seed, cancellationToken);

        var foreign = await otherClient.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(login.RefreshToken), cancellationToken);
        var own = await ownClient.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(login.RefreshToken), cancellationToken);

        await AssertProblemAsync(foreign, HttpStatusCode.Unauthorized, AuthErrors.InvalidRefreshToken, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
    }

    [Fact]
    public async Task Refresh_WithAnExpiredToken_Returns401Expired()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var seed = await SeedAsync(cancellationToken);
        var expired = RefreshToken.Issue(seed.User.Id, seed.App.Id, DateTime.UtcNow.AddDays(-31), TimeSpan.FromDays(30), null, null);
        await SaveAllAsync(cancellationToken, expired.Token);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = CreateAppClient(factory, seed.App);

        var response = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(expired.PlainToken), cancellationToken);

        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, AuthErrors.RefreshTokenExpired, cancellationToken);
    }

    [Fact]
    public async Task Refresh_ReadsRolesAndPermissionsAgain()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var seed = await SeedAsync(cancellationToken);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = CreateAppClient(factory, seed.App);
        var login = await LoginAsync(client, seed, cancellationToken);

        using (var session = OpenSession())
        using (var transaction = session.BeginTransaction())
        {
            var access = await session.GetAsync<UserApplicationAccess>(seed.Access.Id, cancellationToken);
            var viewer = await session.GetAsync<Role>(seed.Viewer.Id, cancellationToken);
            access.ReplaceRoles([viewer]);
            await transaction.CommitAsync(cancellationToken);
        }

        var refreshed = await RefreshAsync(client, login.RefreshToken, cancellationToken);

        var token = new JsonWebToken(refreshed.AccessToken);
        Assert.Equal(["Viewer"], ClaimValues(token, "roles"));
        Assert.Equal(["orders.read"], ClaimValues(token, "permissions"));
    }

    [Fact]
    public async Task Refresh_ForALockedUser_Returns403AndDoesNotRotate()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var seed = await SeedAsync(cancellationToken);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = CreateAppClient(factory, seed.App);
        var login = await LoginAsync(client, seed, cancellationToken);
        await UpdateUserAsync(seed.User.Id, user => user.Lock("Fraud", DateTime.UtcNow), cancellationToken);

        var response = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(login.RefreshToken), cancellationToken);

        await AssertProblemAsync(response, HttpStatusCode.Forbidden, AuthErrors.UserLocked, cancellationToken);
        var token = Assert.Single(await ReadRefreshTokensAsync(seed.User.Id, cancellationToken));
        Assert.Null(token.RevokedAtUtc);
    }

    [Fact]
    public async Task Refresh_AfterTheAccessIsRevoked_Returns403NoAccess()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var seed = await SeedAsync(cancellationToken);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = CreateAppClient(factory, seed.App);
        var login = await LoginAsync(client, seed, cancellationToken);

        using (var session = OpenSession())
        using (var transaction = session.BeginTransaction())
        {
            await session.DeleteAsync(await session.GetAsync<UserApplicationAccess>(seed.Access.Id, cancellationToken), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        var response = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(login.RefreshToken), cancellationToken);

        await AssertProblemAsync(response, HttpStatusCode.Forbidden, AuthErrors.NoAccessToApplication, cancellationToken);
    }

    private static async Task<JsonWebToken> ValidateWithPublishedKeysAsync(HttpClient client, string accessToken, string appKey)
    {
        var keySet = new JsonWebKeySet(await client.GetStringAsync("/.well-known/jwks.json", TestContext.Current.CancellationToken));
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(accessToken, new TokenValidationParameters
        {
            ValidIssuer = Issuer,
            ValidAudience = appKey,
            IssuerSigningKeys = keySet.GetSigningKeys(),
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
        });
        Assert.True(result.IsValid, result.Exception?.Message);
        return (JsonWebToken)result.SecurityToken;
    }

    private static IReadOnlyList<string> ClaimValues(JsonWebToken token, string type) =>
        token.Claims.Where(claim => claim.Type == type).Select(claim => claim.Value).ToList();

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, Error error, CancellationToken cancellationToken)
    {
        Assert.Equal(status, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken);
        Assert.NotNull(problem);
        Assert.Equal(error.Code, problem.Extensions["errorCode"]?.ToString());
    }

    private static async Task<TokenResponse> LoginAsync(HttpClient client, Seed seed, CancellationToken cancellationToken)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(seed.User.Email, Password), cancellationToken);
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

    private ISession OpenSession() => postgres.SessionFactory.OpenSession();

    private async Task<Seed> SeedAsync(CancellationToken cancellationToken, bool verified = true, Action<User>? configureUser = null)
    {
        var app = await SaveApplicationAsync(cancellationToken);
        var read = Permission.Create(app.Id, "orders.read", null).Value;
        var write = Permission.Create(app.Id, "orders.write", null).Value;
        var admin = Role.Create(app.Id, "Admin", null).Value;
        admin.AddPermission(read);
        admin.AddPermission(write);
        var viewer = Role.Create(app.Id, "Viewer", null).Value;
        viewer.AddPermission(read);
        var user = User.Create($"auth-{Guid.NewGuid():N}@example.com", "Auth User", Password, FastHasher).Value;

        if (verified)
        {
            user.MarkEmailVerified(DateTime.UtcNow);
        }

        configureUser?.Invoke(user);
        var access = UserApplicationAccess.Grant(user.Id, app.Id, [admin, viewer]).Value;
        await SaveAllAsync(cancellationToken, read, write, admin, viewer, user, access);
        return new Seed(app, user, access, viewer);
    }

    private async Task<SeededApplication> SaveApplicationAsync(CancellationToken cancellationToken)
    {
        var (application, secret) = Application.Create($"auth-{Guid.NewGuid():N}"[..20], "Auth App", null, false, TimeProvider.System).Value;
        await SaveAllAsync(cancellationToken, application);
        return new SeededApplication(application.Id, application.AppKey, secret);
    }

    private async Task UpdateUserAsync(Guid userId, Action<User> change, CancellationToken cancellationToken)
    {
        using var session = OpenSession();
        using var transaction = session.BeginTransaction();
        change(await session.GetAsync<User>(userId, cancellationToken));
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task SaveAllAsync(CancellationToken cancellationToken, params object[] entities)
    {
        using var session = OpenSession();
        using var transaction = session.BeginTransaction();

        foreach (var entity in entities)
        {
            await session.SaveAsync(entity, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<List<RefreshToken>> ReadRefreshTokensAsync(Guid userId, CancellationToken cancellationToken)
    {
        using var session = OpenSession();
        return await session.Query<RefreshToken>()
            .Where(candidate => candidate.UserId == userId)
            .OrderBy(candidate => candidate.CreatedAtUtc)
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

    private sealed record Seed(SeededApplication App, User User, UserApplicationAccess Access, Role Viewer);

    private sealed record AuditRow(string Action, string ActorType, string? ActorId, string? AppKey, string NewValues);
}
