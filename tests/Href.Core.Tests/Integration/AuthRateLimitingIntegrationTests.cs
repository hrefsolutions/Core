using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Href.Core.Api.Modules.Applications;
using Href.Core.Api.Modules.Audit;
using Href.Core.Api.Modules.Auth;
using Href.Core.Api.Modules.Authorization;
using Href.Core.Api.Modules.Users;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace Href.Core.Tests.Integration;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AuthRateLimitingIntegrationTests(PostgresFixture postgres)
{
    private const string Password = "correct horse battery";
    private const string EndUserAddress = "203.0.113.7";

    [Fact]
    public async Task Login_The11thRequestInAMinute_Returns429WithRetryAfter()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var app = await SaveApplicationAsync(cancellationToken);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = AppClient(factory, app, EndUserAddress);
        var email = UniqueEmail();

        for (var attempt = 1; attempt <= AuthRateLimiting.StrictPermitLimit; attempt++)
        {
            var allowed = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, Password), cancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, allowed.StatusCode);
        }

        var rejected = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, Password), cancellationToken);

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        var retryAfter = Assert.Single(rejected.Headers.GetValues("Retry-After"));
        Assert.InRange(int.Parse(retryAfter, System.Globalization.CultureInfo.InvariantCulture), 1, 60);
        var problem = await rejected.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken);
        Assert.NotNull(problem);
        Assert.Equal(429, problem.Status);
        Assert.Equal(AuthRateLimiting.ErrorCode, problem.Extensions["errorCode"]?.ToString());
        Assert.True(problem.Extensions.ContainsKey("correlationId"));
        Assert.Equal(AuthRateLimiting.StrictPermitLimit, await CountLoginFailedAsync(email, cancellationToken));
    }

    [Fact]
    public async Task Login_LimitIsPerApplicationAndEndUserIp()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var app = await SaveApplicationAsync(cancellationToken);
        var other = await SaveApplicationAsync(cancellationToken);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = AppClient(factory, app, EndUserAddress);
        using var otherUserClient = AppClient(factory, app, "198.51.100.20");
        using var otherAppClient = AppClient(factory, other, EndUserAddress);
        var request = new LoginRequest(UniqueEmail(), Password);

        for (var attempt = 1; attempt <= AuthRateLimiting.StrictPermitLimit; attempt++)
        {
            await client.PostAsJsonAsync("/api/auth/login", request, cancellationToken);
        }

        var sameKey = await client.PostAsJsonAsync("/api/auth/login", request, cancellationToken);
        var otherUser = await otherUserClient.PostAsJsonAsync("/api/auth/login", request, cancellationToken);
        var otherApp = await otherAppClient.PostAsJsonAsync("/api/auth/login", request, cancellationToken);

        Assert.Equal(HttpStatusCode.TooManyRequests, sameKey.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, otherUser.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, otherApp.StatusCode);
    }

    [Fact]
    public async Task StrictPolicy_IsSharedByLoginRegisterForgotPasswordAndResendVerification()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var app = await SaveApplicationAsync(cancellationToken);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = AppClient(factory, app, EndUserAddress);
        var email = UniqueEmail();
        const string pageUrl = "https://app.example.test/page";

        Func<Task<HttpResponseMessage>>[] calls =
        [
            () => client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, Password), cancellationToken),
            () => client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(email, Password, "Ana", pageUrl), cancellationToken),
            () => client.PostAsJsonAsync("/api/auth/forgot-password", new ForgotPasswordRequest(email, pageUrl), cancellationToken),
            () => client.PostAsJsonAsync("/api/auth/resend-verification", new ResendVerificationRequest(email, pageUrl), cancellationToken),
        ];

        for (var attempt = 0; attempt < AuthRateLimiting.StrictPermitLimit; attempt++)
        {
            var response = await calls[attempt % calls.Length]();
            Assert.NotEqual(HttpStatusCode.TooManyRequests, response.StatusCode);
        }

        foreach (var call in calls)
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, (await call()).StatusCode);
        }
    }

    [Fact]
    public async Task Refresh_The61stRequestInAMinute_Returns429AndDoesNotUseTheStrictLimit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var app = await SaveApplicationAsync(cancellationToken);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = AppClient(factory, app, EndUserAddress);

        for (var attempt = 1; attempt <= AuthRateLimiting.RefreshPermitLimit; attempt++)
        {
            var allowed = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest("not-a-real-token"), cancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, allowed.StatusCode);
        }

        var rejected = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest("not-a-real-token"), cancellationToken);
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(UniqueEmail(), Password), cancellationToken);

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.True(rejected.Headers.Contains("Retry-After"));
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    [Theory]
    [InlineData(LoginFailureReason.UnknownEmail)]
    [InlineData(LoginFailureReason.InvalidPassword)]
    [InlineData(LoginFailureReason.EmailNotVerified)]
    [InlineData(LoginFailureReason.UserLocked)]
    [InlineData(LoginFailureReason.NoAccessToApplication)]
    public async Task Login_EachFailureReason_RecordsLoginFailedWithTheEndUserIp(LoginFailureReason reason)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var app = await SaveApplicationAsync(cancellationToken);
        var email = UniqueEmail();
        Guid? userId = reason == LoginFailureReason.UnknownEmail
            ? null
            : await SaveUserAsync(
                app,
                email,
                verified: reason != LoginFailureReason.EmailNotVerified,
                locked: reason == LoginFailureReason.UserLocked,
                withAccess: reason != LoginFailureReason.NoAccessToApplication,
                cancellationToken);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = AppClient(factory, app, EndUserAddress);
        var password = reason == LoginFailureReason.InvalidPassword ? "wrong password value" : Password;

        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email.ToUpperInvariant(), password), cancellationToken);

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        var row = Assert.Single(await ReadLoginFailedAsync(email, cancellationToken));
        Assert.Equal(app.AppKey, row.AppKey);
        Assert.Equal(EndUserAddress, row.IpAddress);
        Assert.Equal(userId?.ToString(), row.EntityId);
        using var values = JsonDocument.Parse(row.NewValues);
        Assert.Equal(email, values.RootElement.GetProperty("email").GetString());
        Assert.Equal(reason.ToString(), values.RootElement.GetProperty("reason").GetString());
        Assert.DoesNotContain(password, row.NewValues, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_WithAnInvalidEndUserIpHeader_RecordsTheConnectionAddressInstead()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var app = await SaveApplicationAsync(cancellationToken);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = AppClient(factory, app, "not-an-ip");
        var email = UniqueEmail();

        await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, Password), cancellationToken);

        var row = Assert.Single(await ReadLoginFailedAsync(email, cancellationToken));
        Assert.NotEqual("not-an-ip", row.IpAddress);
    }

    [Fact]
    public async Task Login_Successful_RecordsNoLoginFailed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var app = await SaveApplicationAsync(cancellationToken);
        var email = UniqueEmail();
        await SaveUserAsync(app, email, verified: true, locked: false, withAccess: true, cancellationToken);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = AppClient(factory, app, EndUserAddress);

        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, Password), cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await ReadLoginFailedAsync(email, cancellationToken));
    }

    private static string UniqueEmail() => $"rl-{Guid.NewGuid():N}@example.com";

    private static HttpClient AppClient(ApiFactory factory, SeededApplication app, string endUserIp)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(AppKeyAuthentication.AppKeyHeaderName, app.AppKey);
        client.DefaultRequestHeaders.Add(AppKeyAuthentication.AppSecretHeaderName, app.Secret);
        client.DefaultRequestHeaders.Add(EndUserIp.HeaderName, endUserIp);
        return client;
    }

    private async Task<SeededApplication> SaveApplicationAsync(CancellationToken cancellationToken)
    {
        var (application, secret) = Application.Create($"rl-{Guid.NewGuid():N}"[..20], "Rate Limit App", null, true, TimeProvider.System).Value;
        using var session = postgres.SessionFactory.OpenSession();
        using var transaction = session.BeginTransaction();
        await session.SaveAsync(application, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new SeededApplication(application.Id, application.AppKey, secret);
    }

    private async Task<Guid> SaveUserAsync(
        SeededApplication app,
        string email,
        bool verified,
        bool locked,
        bool withAccess,
        CancellationToken cancellationToken)
    {
        var user = User.Create(email, "Ana", Password, new PasswordHasher<User>()).Value;

        if (verified)
        {
            user.MarkEmailVerified(DateTime.UtcNow);
        }

        if (locked)
        {
            user.Lock("Fraud", DateTime.UtcNow);
        }

        using var session = postgres.SessionFactory.OpenSession();
        using var transaction = session.BeginTransaction();
        await session.SaveAsync(user, cancellationToken);

        if (withAccess)
        {
            await session.SaveAsync(UserApplicationAccess.Grant(user.Id, app.Id, []).Value, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return user.Id;
    }

    private async Task<int> CountLoginFailedAsync(string email, CancellationToken cancellationToken) =>
        (await ReadLoginFailedAsync(email, cancellationToken)).Count;

    private async Task<List<LoginFailedRow>> ReadLoginFailedAsync(string email, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT app_key, ip_address, entity_id, new_values::text
            FROM audit_logs
            WHERE action = @action AND new_values ->> 'email' = @email
            ORDER BY occurred_at_utc
            """,
            connection);
        command.Parameters.AddWithValue("action", AuditActions.LoginFailed);
        command.Parameters.AddWithValue("email", email);
        var rows = new List<LoginFailedRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new LoginFailedRow(
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3)));
        }

        return rows;
    }

    private sealed record SeededApplication(Guid Id, string AppKey, string Secret);

    private sealed record LoginFailedRow(string? AppKey, string? IpAddress, string? EntityId, string NewValues);
}
