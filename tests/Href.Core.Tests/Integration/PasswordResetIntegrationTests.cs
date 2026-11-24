using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Href.Core.Api.Common.Results;
using Href.Core.Api.Modules.Applications;
using Href.Core.Api.Modules.Audit;
using Href.Core.Api.Modules.Auth;
using Href.Core.Api.Modules.Authorization;
using Href.Core.Api.Modules.Users;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using NHibernate.Linq;
using Npgsql;

namespace Href.Core.Tests.Integration;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed partial class PasswordResetIntegrationTests(PostgresFixture postgres)
{
    private const string Password = "correct horse battery";
    private const string NewPassword = "another long passphrase";
    private const string ResetUrl = "https://app.example.test/reset?lang=es";

    [Fact]
    public async Task ForgotPassword_QueuesTheResetEmailWithA30MinuteToken()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var app = await SaveApplicationAsync(cancellationToken);
        var user = await SaveUserAsync(app, cancellationToken);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = AppClient(factory, app);

        var response = await client.PostAsJsonAsync("/api/auth/forgot-password", new ForgotPasswordRequest($" {user.Email.ToUpperInvariant()} ", ResetUrl), cancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var notification = Assert.Single(await ReadNotificationsAsync(user.Email, cancellationToken));
        Assert.Equal(AccountService.ResetPasswordTemplateKey, notification.TemplateKey);
        Assert.Equal(app.AppKey, notification.AppKey);
        Assert.Contains("https://app.example.test/reset?lang=es&token=", notification.TextBody, StringComparison.Ordinal);
        var plainToken = ExtractToken(notification.TextBody);
        using var session = postgres.SessionFactory.OpenSession();
        var stored = await session.Query<UserToken>().SingleAsync(token => token.UserId == user.Id, cancellationToken);
        Assert.Equal(UserToken.HashToken(plainToken), stored.TokenHash);
        Assert.Equal(UserTokenPurpose.PasswordReset, stored.Purpose);
        Assert.Equal(app.Id, stored.ApplicationId);
        Assert.Equal(UserToken.PasswordResetLifetime, stored.ExpiresAtUtc - stored.CreatedAtUtc);

        var audit = await ReadAuditAsync(user.Id, cancellationToken);
        Assert.Contains(audit, row => row.Action == AuditActions.PasswordResetRequested && row.AppKey == app.AppKey);
        Assert.All(audit, row => Assert.DoesNotContain(plainToken, row.NewValues, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("locked")]
    [InlineData("other-app")]
    [InlineData("malformed")]
    public async Task ForgotPassword_WhenNothingShouldBeSent_Returns202AndQueuesNothing(string scenario)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var app = await SaveApplicationAsync(cancellationToken);
        var other = await SaveApplicationAsync(cancellationToken);
        var user = await SaveUserAsync(app, cancellationToken, locked: scenario == "locked");
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = AppClient(factory, app);
        using var otherClient = AppClient(factory, other);

        var (sender, target) = scenario switch
        {
            "unknown" => (client, UniqueEmail()),
            "other-app" => (otherClient, user.Email),
            "malformed" => (client, "not an email"),
            _ => (client, user.Email),
        };

        var response = await sender.PostAsJsonAsync("/api/auth/forgot-password", new ForgotPasswordRequest(target, ResetUrl), cancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Empty(await ReadNotificationsAsync(user.Email, cancellationToken));
        Assert.Empty(await ReadNotificationsAsync(target, cancellationToken));
        using var session = postgres.SessionFactory.OpenSession();
        Assert.False(await session.Query<UserToken>().AnyAsync(token => token.UserId == user.Id, cancellationToken));
    }

    [Fact]
    public async Task ForgotPassword_AgainRevokesThePreviousToken()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var app = await SaveApplicationAsync(cancellationToken);
        var user = await SaveUserAsync(app, cancellationToken);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = AppClient(factory, app);

        var first = await ForgotAsync(client, user.Email, cancellationToken);
        await AgeTokensAsync(user.Id, cancellationToken);
        var second = await ForgotAsync(client, user.Email, cancellationToken);

        Assert.NotEqual(first, second);
        var oldReset = await client.PostAsJsonAsync("/api/auth/reset-password", new ResetPasswordRequest(first, NewPassword), cancellationToken);
        var newReset = await client.PostAsJsonAsync("/api/auth/reset-password", new ResetPasswordRequest(second, NewPassword), cancellationToken);

        await AssertProblemAsync(oldReset, HttpStatusCode.BadRequest, AuthErrors.InvalidOneTimeToken, cancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, newReset.StatusCode);
    }

    [Fact]
    public async Task ForgotPassword_WithinTheCooldown_Returns202AndKeepsTheFirstLink()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var app = await SaveApplicationAsync(cancellationToken);
        var user = await SaveUserAsync(app, cancellationToken);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = AppClient(factory, app);
        var token = await ForgotAsync(client, user.Email, cancellationToken);

        var again = await client.PostAsJsonAsync("/api/auth/forgot-password", new ForgotPasswordRequest(user.Email, ResetUrl), cancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);
        Assert.Single(await ReadNotificationsAsync(user.Email, cancellationToken));
        var reset = await client.PostAsJsonAsync("/api/auth/reset-password", new ResetPasswordRequest(token, NewPassword), cancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
    }

    [Fact]
    public async Task ForgotPassword_WithAnInvalidPageUrl_Returns400()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var app = await SaveApplicationAsync(cancellationToken);
        var user = await SaveUserAsync(app, cancellationToken);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = AppClient(factory, app);

        var response = await client.PostAsJsonAsync("/api/auth/forgot-password", new ForgotPasswordRequest(user.Email, "http://evil.example/reset"), cancellationToken);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, AuthErrors.InvalidCallbackUrl, cancellationToken);
        Assert.Empty(await ReadNotificationsAsync(user.Email, cancellationToken));
    }

    [Fact]
    public async Task ResetPassword_ChangesThePasswordAndRevokesEverySession()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var app = await SaveApplicationAsync(cancellationToken);
        var user = await SaveUserAsync(app, cancellationToken);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = AppClient(factory, app);
        var other = await SaveApplicationAsync(cancellationToken);
        await GrantAccessAsync(user.Id, other, cancellationToken);
        using var otherClient = AppClient(factory, other);
        var session = await LoginAsync(client, user.Email, cancellationToken);
        var otherSession = await LoginAsync(otherClient, user.Email, cancellationToken);
        var stampBefore = await ReadSecurityStampAsync(user.Id, cancellationToken);
        var token = await ForgotAsync(client, user.Email, cancellationToken);

        var reset = await client.PostAsJsonAsync("/api/auth/reset-password", new ResetPasswordRequest(token, NewPassword), cancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        var refresh = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(session.RefreshToken), cancellationToken);
        await AssertProblemAsync(refresh, HttpStatusCode.Unauthorized, AuthErrors.RefreshTokenRevoked, cancellationToken);
        var otherRefresh = await otherClient.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(otherSession.RefreshToken), cancellationToken);
        await AssertProblemAsync(otherRefresh, HttpStatusCode.Unauthorized, AuthErrors.RefreshTokenRevoked, cancellationToken);
        var oldPassword = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email, Password), cancellationToken);
        await AssertProblemAsync(oldPassword, HttpStatusCode.Unauthorized, AuthErrors.InvalidCredentials, cancellationToken);
        var newPassword = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email, NewPassword), cancellationToken);
        Assert.Equal(HttpStatusCode.OK, newPassword.StatusCode);

        Assert.NotEqual(stampBefore, await ReadSecurityStampAsync(user.Id, cancellationToken));
        using var verifySession = postgres.SessionFactory.OpenSession();
        var revoked = await verifySession.Query<RefreshToken>()
            .Where(candidate => candidate.UserId == user.Id && candidate.RevokedReason == RefreshTokenRevocationReason.PasswordReset)
            .ToListAsync(cancellationToken);
        Assert.Equal(2, revoked.Count);
        var stored = await verifySession.Query<UserToken>().SingleAsync(candidate => candidate.UserId == user.Id, cancellationToken);
        Assert.NotNull(stored.ConsumedAtUtc);

        var audit = await ReadAuditAsync(user.Id, cancellationToken);
        Assert.Contains(audit, row => row.Action == AuditActions.PasswordReset && row.AppKey == app.AppKey);
        Assert.Contains(audit, row => row.Action == AuditActions.SessionsRevoked && row.NewValues.Contains("PasswordReset", StringComparison.Ordinal));
        Assert.All(audit, row => Assert.DoesNotContain(token, row.NewValues, StringComparison.Ordinal));
        Assert.All(audit, row => Assert.DoesNotContain(NewPassword, row.NewValues, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResetPassword_WithAUsedExpiredForeignOrWrongToken_Returns400()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var app = await SaveApplicationAsync(cancellationToken);
        var other = await SaveApplicationAsync(cancellationToken);
        var user = await SaveUserAsync(app, cancellationToken);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = AppClient(factory, app);
        using var otherClient = AppClient(factory, other);
        var token = await ForgotAsync(client, user.Email, cancellationToken);
        var expired = UserToken.Issue(user.Id, app.Id, UserTokenPurpose.PasswordReset, DateTime.UtcNow.AddHours(-1), UserToken.PasswordResetLifetime);
        await SaveAsync(expired.Token, cancellationToken);
        var verification = UserToken.Issue(user.Id, app.Id, UserTokenPurpose.EmailVerification, DateTime.UtcNow, UserToken.EmailVerificationLifetime);
        await SaveAsync(verification.Token, cancellationToken);

        var foreign = await otherClient.PostAsJsonAsync("/api/auth/reset-password", new ResetPasswordRequest(token, NewPassword), cancellationToken);
        var expiredResponse = await client.PostAsJsonAsync("/api/auth/reset-password", new ResetPasswordRequest(expired.PlainToken, NewPassword), cancellationToken);
        var first = await client.PostAsJsonAsync("/api/auth/reset-password", new ResetPasswordRequest(token, NewPassword), cancellationToken);
        var reused = await client.PostAsJsonAsync("/api/auth/reset-password", new ResetPasswordRequest(token, "yet another passphrase"), cancellationToken);
        var wrongPurpose = await client.PostAsJsonAsync("/api/auth/reset-password", new ResetPasswordRequest(verification.PlainToken, NewPassword), cancellationToken);
        var missing = await client.PostAsJsonAsync("/api/auth/reset-password", new ResetPasswordRequest(null, NewPassword), cancellationToken);
        var unknown = await client.PostAsJsonAsync("/api/auth/reset-password", new ResetPasswordRequest("not-a-real-token", NewPassword), cancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

        foreach (var response in new[] { foreign, reused, expiredResponse, wrongPurpose, missing, unknown })
        {
            await AssertProblemAsync(response, HttpStatusCode.BadRequest, AuthErrors.InvalidOneTimeToken, cancellationToken);
        }

        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email, NewPassword), cancellationToken);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task ResetPassword_WithAWeakPassword_Returns400AndKeepsTheTokenUsable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var app = await SaveApplicationAsync(cancellationToken);
        var user = await SaveUserAsync(app, cancellationToken);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = AppClient(factory, app);
        var token = await ForgotAsync(client, user.Email, cancellationToken);

        var weak = await client.PostAsJsonAsync("/api/auth/reset-password", new ResetPasswordRequest(token, "short"), cancellationToken);
        var strong = await client.PostAsJsonAsync("/api/auth/reset-password", new ResetPasswordRequest(token, NewPassword), cancellationToken);

        await AssertProblemAsync(weak, HttpStatusCode.BadRequest, UserErrors.WeakPassword, cancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, strong.StatusCode);
    }

    [Fact]
    public async Task ResetPassword_ForAUserLockedAfterTheRequest_Returns400AndKeepsThePassword()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var app = await SaveApplicationAsync(cancellationToken);
        var user = await SaveUserAsync(app, cancellationToken);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = AppClient(factory, app);
        var token = await ForgotAsync(client, user.Email, cancellationToken);
        var hashBefore = await LockUserAsync(user.Id, cancellationToken);

        var reset = await client.PostAsJsonAsync("/api/auth/reset-password", new ResetPasswordRequest(token, NewPassword), cancellationToken);

        await AssertProblemAsync(reset, HttpStatusCode.BadRequest, AuthErrors.InvalidOneTimeToken, cancellationToken);
        using var session = postgres.SessionFactory.OpenSession();
        Assert.Equal(hashBefore, (await session.GetAsync<User>(user.Id, cancellationToken)).PasswordHash);
    }

    [Fact]
    public async Task ResetPassword_AfterTheAccessWasRevoked_Returns400AndKeepsThePassword()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var app = await SaveApplicationAsync(cancellationToken);
        var user = await SaveUserAsync(app, cancellationToken);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = AppClient(factory, app);
        var token = await ForgotAsync(client, user.Email, cancellationToken);
        string hashBefore;

        using (var session = postgres.SessionFactory.OpenSession())
        using (var transaction = session.BeginTransaction())
        {
            var access = await session.Query<UserApplicationAccess>().SingleAsync(candidate => candidate.UserId == user.Id, cancellationToken);
            await session.DeleteAsync(access, cancellationToken);
            hashBefore = (await session.GetAsync<User>(user.Id, cancellationToken)).PasswordHash;
            await transaction.CommitAsync(cancellationToken);
        }

        var reset = await client.PostAsJsonAsync("/api/auth/reset-password", new ResetPasswordRequest(token, NewPassword), cancellationToken);

        await AssertProblemAsync(reset, HttpStatusCode.BadRequest, AuthErrors.InvalidOneTimeToken, cancellationToken);
        using var verifySession = postgres.SessionFactory.OpenSession();
        Assert.Equal(hashBefore, (await verifySession.GetAsync<User>(user.Id, cancellationToken)).PasswordHash);
    }

    [Fact]
    public async Task ResetPassword_AfterAnAdminChangedTheEmail_Returns400()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var app = await SaveApplicationAsync(cancellationToken);
        var user = await SaveUserAsync(app, cancellationToken);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = AppClient(factory, app);
        using var admin = factory.CreateAdminClient();
        var token = await ForgotAsync(client, user.Email, cancellationToken);
        var verification = UserToken.Issue(user.Id, app.Id, UserTokenPurpose.EmailVerification, DateTime.UtcNow, UserToken.EmailVerificationLifetime);
        await SaveAsync(verification.Token, cancellationToken);

        var update = await admin.PutAsJsonAsync($"/api/users/{user.Id}", new UpdateUserRequest(UniqueEmail(), "Ana Pérez"), cancellationToken);
        var reset = await client.PostAsJsonAsync("/api/auth/reset-password", new ResetPasswordRequest(token, NewPassword), cancellationToken);
        var verify = await client.PostAsJsonAsync("/api/auth/verify-email", new VerifyEmailRequest(verification.PlainToken), cancellationToken);

        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        await AssertProblemAsync(reset, HttpStatusCode.BadRequest, AuthErrors.InvalidOneTimeToken, cancellationToken);
        await AssertProblemAsync(verify, HttpStatusCode.BadRequest, AuthErrors.InvalidOneTimeToken, cancellationToken);
        using var session = postgres.SessionFactory.OpenSession();
        Assert.All(
            await session.Query<UserToken>().Where(candidate => candidate.UserId == user.Id).ToListAsync(cancellationToken),
            candidate => Assert.NotNull(candidate.RevokedAtUtc));
    }

    [Fact]
    public async Task ResetPassword_ConcurrentUsesOfTheSameToken_OneSucceedsTheOtherFails()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var app = await SaveApplicationAsync(cancellationToken);
        var user = await SaveUserAsync(app, cancellationToken);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = AppClient(factory, app);
        var token = await ForgotAsync(client, user.Email, cancellationToken);
        await using var blocker = new NpgsqlConnection(postgres.ConnectionString);
        await blocker.OpenAsync(cancellationToken);
        await using var blockerTransaction = await blocker.BeginTransactionAsync(cancellationToken);
        var blockerPid = await ExecuteScalarAsync<int>(blocker, blockerTransaction, "SELECT pg_backend_pid()", user.Id, cancellationToken);
        await ExecuteScalarAsync<Guid>(blocker, blockerTransaction, "SELECT id FROM users WHERE id = @userId FOR UPDATE", user.Id, cancellationToken);

        var first = client.PostAsJsonAsync("/api/auth/reset-password", new ResetPasswordRequest(token, NewPassword), cancellationToken);
        var second = client.PostAsJsonAsync("/api/auth/reset-password", new ResetPasswordRequest(token, "yet another passphrase"), cancellationToken);
        await WaitForWaitersAsync(blockerPid, 2, cancellationToken);
        await blockerTransaction.CommitAsync(cancellationToken);
        var responses = await Task.WhenAll(first, second);

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.NoContent);
        await AssertProblemAsync(
            Assert.Single(responses, response => response.StatusCode != HttpStatusCode.NoContent),
            HttpStatusCode.BadRequest,
            AuthErrors.InvalidOneTimeToken,
            cancellationToken);
    }

    [Fact]
    public async Task PasswordResetEndpoints_WithoutApplicationCredentials_Return401()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();

        var forgot = await client.PostAsJsonAsync("/api/auth/forgot-password", new ForgotPasswordRequest(UniqueEmail(), ResetUrl), cancellationToken);
        var reset = await client.PostAsJsonAsync("/api/auth/reset-password", new ResetPasswordRequest("token", NewPassword), cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, forgot.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, reset.StatusCode);
    }

    [GeneratedRegex("token=([A-Za-z0-9_-]{43})")]
    private static partial Regex TokenPattern();

    private static string ExtractToken(string? body)
    {
        var match = TokenPattern().Match(body ?? string.Empty);
        Assert.True(match.Success, "The email does not contain a token link.");
        return match.Groups[1].Value;
    }

    private static string UniqueEmail() => $"pwd-{Guid.NewGuid():N}@example.com";

    private async Task<string> ForgotAsync(HttpClient client, string email, CancellationToken cancellationToken)
    {
        var response = await client.PostAsJsonAsync("/api/auth/forgot-password", new ForgotPasswordRequest(email, ResetUrl), cancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var notification = (await ReadNotificationsAsync(email, cancellationToken)).Last();
        Assert.Equal(AccountService.ResetPasswordTemplateKey, notification.TemplateKey);
        return ExtractToken(notification.TextBody);
    }

    private static async Task<TokenResponse> LoginAsync(HttpClient client, string email, CancellationToken cancellationToken)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, Password), cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken))!;
    }

    private Task GrantAccessAsync(Guid userId, SeededApplication app, CancellationToken cancellationToken) =>
        SaveAsync(UserApplicationAccess.Grant(userId, app.Id, []).Value, cancellationToken);

    private async Task AgeTokensAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "UPDATE user_tokens SET created_at_utc = created_at_utc - interval '2 minutes' WHERE user_id = @userId",
            connection);
        command.Parameters.AddWithValue("userId", userId);
        await command.ExecuteNonQueryAsync(cancellationToken);
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

    private async Task<SeededApplication> SaveApplicationAsync(CancellationToken cancellationToken)
    {
        var (application, secret) = Application.Create($"pwd-{Guid.NewGuid():N}"[..20], "Reset App", null, false, TimeProvider.System).Value;
        await SaveAsync(application, cancellationToken);
        return new SeededApplication(application.Id, application.AppKey, secret);
    }

    private async Task<SeededUser> SaveUserAsync(SeededApplication app, CancellationToken cancellationToken, bool locked = false)
    {
        var user = User.Create(UniqueEmail(), "Ana Pérez", Password, new PasswordHasher<User>()).Value;
        user.MarkEmailVerified(DateTime.UtcNow);

        if (locked)
        {
            user.Lock("Fraud", DateTime.UtcNow);
        }

        using var session = postgres.SessionFactory.OpenSession();
        using var transaction = session.BeginTransaction();
        await session.SaveAsync(user, cancellationToken);
        await session.SaveAsync(UserApplicationAccess.Grant(user.Id, app.Id, []).Value, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new SeededUser(user.Id, user.Email);
    }

    private async Task<string> LockUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        using var session = postgres.SessionFactory.OpenSession();
        using var transaction = session.BeginTransaction();
        var user = await session.GetAsync<User>(userId, cancellationToken);
        user.Lock("Fraud", DateTime.UtcNow);
        await transaction.CommitAsync(cancellationToken);
        return user.PasswordHash;
    }

    private async Task SaveAsync(object entity, CancellationToken cancellationToken)
    {
        using var session = postgres.SessionFactory.OpenSession();
        using var transaction = session.BeginTransaction();
        await session.SaveAsync(entity, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<Guid> ReadSecurityStampAsync(Guid userId, CancellationToken cancellationToken)
    {
        using var session = postgres.SessionFactory.OpenSession();
        return (await session.GetAsync<User>(userId, cancellationToken)).SecurityStamp;
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
            "SELECT action, app_key, COALESCE(new_values::text, '') FROM audit_logs WHERE entity_id = @userId ORDER BY occurred_at_utc",
            connection);
        command.Parameters.AddWithValue("userId", userId.ToString());
        var rows = new List<AuditRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new AuditRow(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2)));
        }

        return rows;
    }

    private static async Task<T> ExecuteScalarAsync<T>(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        Guid userId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("userId", userId);
        return (T)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>
    /// Waits until <paramref name="expected"/> sessions wait behind the blocker's session, directly or queued behind another
    /// waiter of the same row, ignoring locks of any other test.
    /// </summary>
    private async Task WaitForWaitersAsync(int blockerPid, int expected, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var deadline = DateTime.UtcNow.AddSeconds(15);

        while (true)
        {
            await using var command = new NpgsqlCommand(
                """
                WITH RECURSIVE waiters(pid) AS (
                    SELECT activity.pid FROM pg_stat_activity activity WHERE @blockerPid = ANY(pg_blocking_pids(activity.pid))
                    UNION
                    SELECT activity.pid FROM pg_stat_activity activity
                    JOIN waiters ON waiters.pid = ANY(pg_blocking_pids(activity.pid))
                )
                SELECT count(*) FROM waiters
                """,
                connection);
            command.Parameters.AddWithValue("blockerPid", blockerPid);
            var blocked = (long)(await command.ExecuteScalarAsync(cancellationToken))!;

            if (blocked >= expected)
            {
                return;
            }

            Assert.True(DateTime.UtcNow < deadline, $"Only {blocked} of {expected} requests are waiting on the blocker.");
            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        }
    }

    private sealed record SeededApplication(Guid Id, string AppKey, string Secret);

    private sealed record SeededUser(Guid Id, string Email);

    private sealed record NotificationRow(string TemplateKey, string AppKey, string? TextBody);

    private sealed record AuditRow(string Action, string? AppKey, string NewValues);
}
