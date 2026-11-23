using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Href.Core.Api.Common.Results;
using Href.Core.Api.Modules.Applications;
using Href.Core.Api.Modules.Audit;
using Href.Core.Api.Modules.Auth;
using Href.Core.Api.Modules.Authorization;
using Href.Core.Api.Modules.Users;
using Microsoft.AspNetCore.Mvc;
using NHibernate.Linq;
using Npgsql;

namespace Href.Core.Tests.Integration;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed partial class AccountEndpointsIntegrationTests(PostgresFixture postgres)
{
    private const string Password = "correct horse battery";
    private const string VerifyUrl = "https://app.example.test/verify?lang=es";

    [Fact]
    public async Task Register_CreatesAnUnverifiedUserWithTheDefaultRoleAndQueuesTheVerificationEmail()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var app = await SaveApplicationAsync(selfRegistration: true, withDefaultRole: true, cancellationToken);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = AppClient(factory, app);
        var email = UniqueEmail();

        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest($" {email.ToUpperInvariant()} ", Password, "Ana Pérez", VerifyUrl), cancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<RegisterResponse>(cancellationToken);
        using var session = postgres.SessionFactory.OpenSession();
        var user = await session.GetAsync<User>(body!.UserId, cancellationToken);
        Assert.Equal(email, user.Email);
        Assert.Null(user.EmailVerifiedAtUtc);
        var access = await session.Query<UserApplicationAccess>().SingleAsync(candidate => candidate.UserId == user.Id, cancellationToken);
        Assert.Equal(app.Id, access.ApplicationId);
        Assert.Equal(["Member"], access.GetRoleNames());

        var notification = Assert.Single(await ReadNotificationsAsync(email, cancellationToken));
        Assert.Equal(AccountService.VerifyEmailTemplateKey, notification.TemplateKey);
        Assert.Equal(app.AppKey, notification.AppKey);
        Assert.Contains("https://app.example.test/verify?lang=es&token=", notification.TextBody, StringComparison.Ordinal);
        var plainToken = ExtractToken(notification.TextBody);
        var stored = await session.Query<UserToken>().SingleAsync(token => token.UserId == user.Id, cancellationToken);
        Assert.Equal(UserToken.HashToken(plainToken), stored.TokenHash);
        Assert.Equal(UserTokenPurpose.EmailVerification, stored.Purpose);
        Assert.Equal(app.Id, stored.ApplicationId);

        var audit = await ReadAuditAsync(user.Id, cancellationToken);
        Assert.Contains(audit, row => row.Action == AuditActions.UserRegistered && row.AppKey == app.AppKey);
        Assert.Contains(audit, row => row.Action == AuditActions.AccessGranted && row.AppKey == app.AppKey);
        Assert.All(audit, row => Assert.DoesNotContain(plainToken, row.NewValues, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Register_ForAnApplicationWithoutDefaultRole_GrantsAccessWithoutRoles()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var app = await SaveApplicationAsync(selfRegistration: true, withDefaultRole: false, cancellationToken);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = AppClient(factory, app);

        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(UniqueEmail(), Password, "Ana", VerifyUrl), cancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var userId = (await response.Content.ReadFromJsonAsync<RegisterResponse>(cancellationToken))!.UserId;
        using var session = postgres.SessionFactory.OpenSession();
        var access = await session.Query<UserApplicationAccess>().SingleAsync(candidate => candidate.UserId == userId, cancellationToken);
        Assert.Empty(access.GetRoleNames());
    }

    [Fact]
    public async Task Register_ForAnApplicationWithoutSelfRegistration_Returns403AndCreatesNothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var app = await SaveApplicationAsync(selfRegistration: false, withDefaultRole: true, cancellationToken);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = AppClient(factory, app);
        var email = UniqueEmail();

        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(email, Password, "Ana", VerifyUrl), cancellationToken);

        await AssertProblemAsync(response, HttpStatusCode.Forbidden, AuthErrors.SelfRegistrationDisabled, cancellationToken);
        Assert.False(await UserExistsAsync(email, cancellationToken));
    }

    [Theory]
    [InlineData("http://app.example.test/verify")]
    [InlineData("https://app.example.test/verify?token=x")]
    [InlineData("https://user:pass@app.example.test/verify")]
    [InlineData("not a url")]
    [InlineData(null)]
    public async Task Register_WithAnInvalidPageUrl_Returns400AndCreatesNothing(string? url)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var app = await SaveApplicationAsync(selfRegistration: true, withDefaultRole: true, cancellationToken);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = AppClient(factory, app);
        var email = UniqueEmail();

        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(email, Password, "Ana", url), cancellationToken);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, AuthErrors.InvalidCallbackUrl, cancellationToken);
        Assert.False(await UserExistsAsync(email, cancellationToken));
    }

    [Fact]
    public async Task Register_WithAWeakPassword_Returns400WeakPassword()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var app = await SaveApplicationAsync(selfRegistration: true, withDefaultRole: true, cancellationToken);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = AppClient(factory, app);

        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(UniqueEmail(), "short", "Ana", VerifyUrl), cancellationToken);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, UserErrors.WeakPassword, cancellationToken);
    }

    [Fact]
    public async Task Register_WithAnExistingEmailInAnyCase_Returns409()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var app = await SaveApplicationAsync(selfRegistration: true, withDefaultRole: true, cancellationToken);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = AppClient(factory, app);
        var email = UniqueEmail();
        await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(email, Password, "Ana", VerifyUrl), cancellationToken);

        var again = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(email.ToUpperInvariant(), Password, "Ana", VerifyUrl), cancellationToken);

        await AssertProblemAsync(again, HttpStatusCode.Conflict, AuthErrors.EmailAlreadyRegistered, cancellationToken);
        Assert.Single(await ReadNotificationsAsync(email, cancellationToken));
    }

    [Fact]
    public async Task Register_ConcurrentRequestsForTheSameEmail_CreateOneUserAndReturn409ForTheOther()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var app = await SaveApplicationAsync(selfRegistration: true, withDefaultRole: true, cancellationToken);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = AppClient(factory, app);
        var email = UniqueEmail();

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(email, Password, "Ana", VerifyUrl), cancellationToken)));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
        Assert.All(
            responses.Where(response => response.StatusCode != HttpStatusCode.Created),
            response => Assert.Equal(HttpStatusCode.Conflict, response.StatusCode));
        Assert.Single(await ReadNotificationsAsync(email, cancellationToken));
    }

    [Fact]
    public async Task VerifyEmail_ThenLogin_FullFlowWorks()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var app = await SaveApplicationAsync(selfRegistration: true, withDefaultRole: true, cancellationToken);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = AppClient(factory, app);
        var email = UniqueEmail();
        var registered = await RegisterAsync(client, email, cancellationToken);

        var loginBefore = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, Password), cancellationToken);
        var verify = await client.PostAsJsonAsync("/api/auth/verify-email", new VerifyEmailRequest(registered.Token), cancellationToken);
        var loginAfter = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, Password), cancellationToken);

        await AssertProblemAsync(loginBefore, HttpStatusCode.Forbidden, AuthErrors.EmailNotVerified, cancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, verify.StatusCode);
        Assert.Equal(HttpStatusCode.OK, loginAfter.StatusCode);
        var notifications = await ReadNotificationsAsync(email, cancellationToken);
        Assert.Equal([AccountService.VerifyEmailTemplateKey, AccountService.WelcomeUserTemplateKey], notifications.Select(row => row.TemplateKey));
        Assert.Contains(await ReadAuditAsync(registered.UserId, cancellationToken), row => row.Action == AuditActions.EmailVerified);
    }

    [Fact]
    public async Task VerifyEmail_WithAUsedExpiredForeignOrMissingToken_Returns400()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var app = await SaveApplicationAsync(selfRegistration: true, withDefaultRole: true, cancellationToken);
        var other = await SaveApplicationAsync(selfRegistration: true, withDefaultRole: true, cancellationToken);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = AppClient(factory, app);
        using var otherClient = AppClient(factory, other);
        var registered = await RegisterAsync(client, UniqueEmail(), cancellationToken);
        var expired = UserToken.Issue(registered.UserId, app.Id, UserTokenPurpose.EmailVerification, DateTime.UtcNow.AddDays(-2), UserToken.EmailVerificationLifetime);
        await SaveAsync(expired.Token, cancellationToken);
        var reset = UserToken.Issue(registered.UserId, app.Id, UserTokenPurpose.PasswordReset, DateTime.UtcNow, UserToken.PasswordResetLifetime);
        await SaveAsync(reset.Token, cancellationToken);

        var foreign = await otherClient.PostAsJsonAsync("/api/auth/verify-email", new VerifyEmailRequest(registered.Token), cancellationToken);
        await client.PostAsJsonAsync("/api/auth/verify-email", new VerifyEmailRequest(registered.Token), cancellationToken);
        var reused = await client.PostAsJsonAsync("/api/auth/verify-email", new VerifyEmailRequest(registered.Token), cancellationToken);
        var expiredResponse = await client.PostAsJsonAsync("/api/auth/verify-email", new VerifyEmailRequest(expired.PlainToken), cancellationToken);
        var wrongPurpose = await client.PostAsJsonAsync("/api/auth/verify-email", new VerifyEmailRequest(reset.PlainToken), cancellationToken);
        var missing = await client.PostAsJsonAsync("/api/auth/verify-email", new VerifyEmailRequest(null), cancellationToken);
        var unknown = await client.PostAsJsonAsync("/api/auth/verify-email", new VerifyEmailRequest("not-a-real-token"), cancellationToken);

        foreach (var response in new[] { foreign, reused, expiredResponse, wrongPurpose, missing, unknown })
        {
            await AssertProblemAsync(response, HttpStatusCode.BadRequest, AuthErrors.InvalidOneTimeToken, cancellationToken);
        }
    }

    [Fact]
    public async Task VerifyEmail_ForAnAlreadyVerifiedUser_ConsumesTheTokenWithoutWelcomeOrAudit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var app = await SaveApplicationAsync(selfRegistration: true, withDefaultRole: true, cancellationToken);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = AppClient(factory, app);
        var email = UniqueEmail();
        var registered = await RegisterAsync(client, email, cancellationToken);

        using (var session = postgres.SessionFactory.OpenSession())
        using (var transaction = session.BeginTransaction())
        {
            (await session.GetAsync<User>(registered.UserId, cancellationToken)).MarkEmailVerified(DateTime.UtcNow);
            await transaction.CommitAsync(cancellationToken);
        }

        var verify = await client.PostAsJsonAsync("/api/auth/verify-email", new VerifyEmailRequest(registered.Token), cancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, verify.StatusCode);
        Assert.Single(await ReadNotificationsAsync(email, cancellationToken));
        Assert.DoesNotContain(await ReadAuditAsync(registered.UserId, cancellationToken), row => row.Action == AuditActions.EmailVerified);
        using var verifySession = postgres.SessionFactory.OpenSession();
        Assert.NotNull((await verifySession.Query<UserToken>().SingleAsync(token => token.UserId == registered.UserId, cancellationToken)).ConsumedAtUtc);
    }

    [Fact]
    public async Task VerifyEmail_ConcurrentUsesOfTheSameToken_OneSucceedsTheOtherFails()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var app = await SaveApplicationAsync(selfRegistration: true, withDefaultRole: true, cancellationToken);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = AppClient(factory, app);
        var email = UniqueEmail();
        var registered = await RegisterAsync(client, email, cancellationToken);

        await using var blocker = new NpgsqlConnection(postgres.ConnectionString);
        await blocker.OpenAsync(cancellationToken);
        await using var blockerTransaction = await blocker.BeginTransactionAsync(cancellationToken);
        await using (var hold = new NpgsqlCommand("SELECT id FROM user_tokens WHERE user_id = @userId FOR UPDATE", blocker, blockerTransaction))
        {
            hold.Parameters.AddWithValue("userId", registered.UserId);
            await hold.ExecuteScalarAsync(cancellationToken);
        }

        var first = client.PostAsJsonAsync("/api/auth/verify-email", new VerifyEmailRequest(registered.Token), cancellationToken);
        var second = client.PostAsJsonAsync("/api/auth/verify-email", new VerifyEmailRequest(registered.Token), cancellationToken);
        await WaitForBlockedLocksAsync(2, cancellationToken);
        await blockerTransaction.CommitAsync(cancellationToken);
        var responses = await Task.WhenAll(first, second);

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.NoContent);
        await AssertProblemAsync(
            Assert.Single(responses, response => response.StatusCode != HttpStatusCode.NoContent),
            HttpStatusCode.BadRequest,
            AuthErrors.InvalidOneTimeToken,
            cancellationToken);
        Assert.Equal(2, (await ReadNotificationsAsync(email, cancellationToken)).Count);
    }

    [Fact]
    public async Task ResendVerification_RevokesThePreviousTokenAndQueuesANewEmail()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var app = await SaveApplicationAsync(selfRegistration: true, withDefaultRole: true, cancellationToken);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = AppClient(factory, app);
        var email = UniqueEmail();
        var registered = await RegisterAsync(client, email, cancellationToken);

        var resend = await client.PostAsJsonAsync("/api/auth/resend-verification", new ResendVerificationRequest(email, VerifyUrl), cancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, resend.StatusCode);
        var notifications = await ReadNotificationsAsync(email, cancellationToken);
        Assert.Equal(2, notifications.Count);
        var newToken = ExtractToken(notifications[1].TextBody);
        var old = await client.PostAsJsonAsync("/api/auth/verify-email", new VerifyEmailRequest(registered.Token), cancellationToken);
        var current = await client.PostAsJsonAsync("/api/auth/verify-email", new VerifyEmailRequest(newToken), cancellationToken);
        await AssertProblemAsync(old, HttpStatusCode.BadRequest, AuthErrors.InvalidOneTimeToken, cancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, current.StatusCode);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("verified")]
    [InlineData("locked")]
    [InlineData("other-app")]
    [InlineData("malformed")]
    public async Task ResendVerification_WhenNothingShouldBeSent_Returns202AndQueuesNothing(string scenario)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var app = await SaveApplicationAsync(selfRegistration: true, withDefaultRole: true, cancellationToken);
        var other = await SaveApplicationAsync(selfRegistration: true, withDefaultRole: true, cancellationToken);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = AppClient(factory, app);
        using var otherClient = AppClient(factory, other);
        var email = UniqueEmail();
        var registered = await RegisterAsync(client, email, cancellationToken);

        if (scenario is "verified" or "locked")
        {
            using var session = postgres.SessionFactory.OpenSession();
            using var transaction = session.BeginTransaction();
            var user = await session.GetAsync<User>(registered.UserId, cancellationToken);

            if (scenario == "verified")
            {
                user.MarkEmailVerified(DateTime.UtcNow);
            }
            else
            {
                user.Lock("Fraud", DateTime.UtcNow);
            }

            await transaction.CommitAsync(cancellationToken);
        }

        var (sender, target) = scenario switch
        {
            "unknown" => (client, UniqueEmail()),
            "other-app" => (otherClient, email),
            "malformed" => (client, "not an email"),
            _ => (client, email),
        };

        var response = await sender.PostAsJsonAsync("/api/auth/resend-verification", new ResendVerificationRequest(target, VerifyUrl), cancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Single(await ReadNotificationsAsync(email, cancellationToken));
    }

    [Fact]
    public async Task ResendVerification_WithAnInvalidPageUrl_Returns400()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var app = await SaveApplicationAsync(selfRegistration: true, withDefaultRole: true, cancellationToken);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = AppClient(factory, app);

        var response = await client.PostAsJsonAsync("/api/auth/resend-verification", new ResendVerificationRequest(UniqueEmail(), "http://evil.example/verify"), cancellationToken);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, AuthErrors.InvalidCallbackUrl, cancellationToken);
    }

    [Fact]
    public async Task AccountEndpoints_WithoutApplicationCredentials_Return401()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();

        var register = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(UniqueEmail(), Password, "Ana", VerifyUrl), cancellationToken);
        var verify = await client.PostAsJsonAsync("/api/auth/verify-email", new VerifyEmailRequest("token"), cancellationToken);
        var resend = await client.PostAsJsonAsync("/api/auth/resend-verification", new ResendVerificationRequest(UniqueEmail(), VerifyUrl), cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, register.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, verify.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, resend.StatusCode);
    }

    [GeneratedRegex("token=([A-Za-z0-9_-]{43})")]
    private static partial Regex TokenPattern();

    private static string ExtractToken(string? body)
    {
        var match = TokenPattern().Match(body ?? string.Empty);
        Assert.True(match.Success, "The email does not contain a token link.");
        return match.Groups[1].Value;
    }

    private static string UniqueEmail() => $"acc-{Guid.NewGuid():N}@example.com";

    private async Task<(Guid UserId, string Token)> RegisterAsync(HttpClient client, string email, CancellationToken cancellationToken)
    {
        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(email, Password, "Ana", VerifyUrl), cancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var userId = (await response.Content.ReadFromJsonAsync<RegisterResponse>(cancellationToken))!.UserId;
        var notification = Assert.Single(await ReadNotificationsAsync(email, cancellationToken));
        return (userId, ExtractToken(notification.TextBody));
    }

    private static HttpClient AppClient(ApiFactory factory, SeededApplication app)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(AppKeyAuthentication.AppKeyHeaderName, app.AppKey);
        client.DefaultRequestHeaders.Add(AppKeyAuthentication.AppSecretHeaderName, app.Secret);
        return client;
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, Error error, CancellationToken cancellationToken)
    {
        Assert.Equal(status, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken);
        Assert.NotNull(problem);
        Assert.Equal(error.Code, problem.Extensions["errorCode"]?.ToString());
    }

    private async Task<SeededApplication> SaveApplicationAsync(bool selfRegistration, bool withDefaultRole, CancellationToken cancellationToken)
    {
        var (application, secret) = Application.Create($"acc-{Guid.NewGuid():N}"[..20], "Account App", null, selfRegistration, TimeProvider.System).Value;
        using var session = postgres.SessionFactory.OpenSession();
        using var transaction = session.BeginTransaction();
        await session.SaveAsync(application, cancellationToken);

        if (withDefaultRole)
        {
            var role = Role.Create(application.Id, "Member", null).Value;
            role.MakeDefault(null);
            await session.SaveAsync(role, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new SeededApplication(application.Id, application.AppKey, secret);
    }

    private async Task SaveAsync(object entity, CancellationToken cancellationToken)
    {
        using var session = postgres.SessionFactory.OpenSession();
        using var transaction = session.BeginTransaction();
        await session.SaveAsync(entity, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<bool> UserExistsAsync(string email, CancellationToken cancellationToken)
    {
        using var session = postgres.SessionFactory.OpenSession();
        return await session.Query<User>().AnyAsync(user => user.Email == email, cancellationToken);
    }

    private async Task<List<NotificationRow>> ReadNotificationsAsync(string email, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT template_key, app_key, text_body FROM notifications WHERE to_email = @email ORDER BY created_at_utc, id",
            connection);
        command.Parameters.AddWithValue("email", email);
        var rows = new List<NotificationRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new NotificationRow(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
        }

        return rows;
    }

    private async Task<List<AuditRow>> ReadAuditAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT action, app_key, COALESCE(new_values::text, '') FROM audit_logs WHERE entity_id = ANY(@ids) ORDER BY occurred_at_utc",
            connection);
        command.Parameters.AddWithValue("ids", await RelatedEntityIdsAsync(userId, cancellationToken));
        var rows = new List<AuditRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new AuditRow(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2)));
        }

        return rows;
    }

    private async Task<string[]> RelatedEntityIdsAsync(Guid userId, CancellationToken cancellationToken)
    {
        using var session = postgres.SessionFactory.OpenSession();
        var accessIds = await session.Query<UserApplicationAccess>()
            .Where(access => access.UserId == userId)
            .Select(access => access.Id)
            .ToListAsync(cancellationToken);
        return [userId.ToString(), .. accessIds.Select(id => id.ToString())];
    }

    private async Task WaitForBlockedLocksAsync(int expected, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var deadline = DateTime.UtcNow.AddSeconds(15);

        while (true)
        {
            await using var command = new NpgsqlCommand(
                "SELECT count(DISTINCT pid) FROM pg_locks WHERE NOT granted",
                connection);
            var blocked = (long)(await command.ExecuteScalarAsync(cancellationToken))!;

            if (blocked >= expected)
            {
                return;
            }

            Assert.True(DateTime.UtcNow < deadline, $"Only {blocked} of {expected} requests are waiting on the token row.");
            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        }
    }

    private sealed record SeededApplication(Guid Id, string AppKey, string Secret);

    private sealed record NotificationRow(string TemplateKey, string AppKey, string? TextBody);

    private sealed record AuditRow(string Action, string? AppKey, string NewValues);
}
