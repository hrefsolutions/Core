using System.Globalization;
using System.Net;
using System.Net.Http.Json;
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
public sealed class LoginAttemptThrottleIntegrationTests(PostgresFixture postgres)
{
    private const string Password = "correct horse battery";
    private const string WrongPassword = "wrong password value";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Login_AfterTheAccountLimitFromManyIps_Returns429ForThatEmailOnly(bool existingUser)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var app = await SaveApplicationAsync(cancellationToken);
        var email = UniqueEmail();
        var otherEmail = UniqueEmail();
        Guid? userId = existingUser ? await SaveUserAsync(app, email, cancellationToken) : null;

        await using var factory = new ApiFactory(postgres.ConnectionString);
        var ip = 0;

        for (var attempt = 1; attempt <= LoginAttemptThrottle.AttemptLimit; attempt++)
        {
            var failed = await LoginAsync(factory, app, Ip(++ip), email, WrongPassword, cancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
        }

        var throttled = await LoginAsync(factory, app, Ip(++ip), email, existingUser ? Password : WrongPassword, cancellationToken);
        var other = await LoginAsync(factory, app, Ip(++ip), otherEmail, WrongPassword, cancellationToken);

        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
        var retryAfter = int.Parse(Assert.Single(throttled.Headers.GetValues("Retry-After")), CultureInfo.InvariantCulture);
        Assert.InRange(retryAfter, 1, (int)LoginAttemptThrottle.Window.TotalSeconds);
        var problem = await throttled.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken);
        Assert.NotNull(problem);
        Assert.Equal(AuthRateLimiting.ErrorCode, problem.Extensions["errorCode"]?.ToString());
        Assert.Equal("no-store", throttled.Headers.CacheControl?.ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, other.StatusCode);

        var rows = await ReadLoginFailedAsync(email, cancellationToken);
        Assert.Equal(LoginAttemptThrottle.AttemptLimit + 1, rows.Count);
        Assert.Equal(nameof(LoginFailureReason.Throttled), rows[^1].Reason);
        Assert.Equal(userId?.ToString(), rows[^1].EntityId);
    }

    [Fact]
    public async Task Login_CorrectPassword_ReleasesTheAccount()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var app = await SaveApplicationAsync(cancellationToken);
        var email = UniqueEmail();
        await SaveUserAsync(app, email, cancellationToken);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        var ip = 0;

        for (var attempt = 1; attempt < LoginAttemptThrottle.AttemptLimit; attempt++)
        {
            await LoginAsync(factory, app, Ip(++ip), email, WrongPassword, cancellationToken);
        }

        var success = await LoginAsync(factory, app, Ip(++ip), email, Password, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);

        for (var attempt = 1; attempt <= LoginAttemptThrottle.AttemptLimit; attempt++)
        {
            var failed = await LoginAsync(factory, app, Ip(++ip), email, WrongPassword, cancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
        }

        var throttled = await LoginAsync(factory, app, Ip(++ip), email, Password, cancellationToken);
        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
    }

    [Fact]
    public async Task Login_SimultaneousAttempts_NeverPassTheAccountLimit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var app = await SaveApplicationAsync(cancellationToken);
        var email = UniqueEmail();
        await using var factory = new ApiFactory(postgres.ConnectionString);

        var responses = await Task.WhenAll(Enumerable.Range(1, LoginAttemptThrottle.AttemptLimit + 10)
            .Select(index => LoginAsync(factory, app, Ip(index), email, WrongPassword, cancellationToken)));

        Assert.Equal(LoginAttemptThrottle.AttemptLimit, responses.Count(response => response.StatusCode == HttpStatusCode.Unauthorized));
        Assert.Equal(10, responses.Count(response => response.StatusCode == HttpStatusCode.TooManyRequests));
    }

    private static string Ip(int index) => $"198.18.{index / 250}.{index % 250 + 1}";

    private static string UniqueEmail() => $"throttle-{Guid.NewGuid():N}@example.com";

    private static async Task<HttpResponseMessage> LoginAsync(
        ApiFactory factory,
        SeededApplication app,
        string endUserIp,
        string email,
        string password,
        CancellationToken cancellationToken)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(AppKeyAuthentication.AppKeyHeaderName, app.AppKey);
        client.DefaultRequestHeaders.Add(AppKeyAuthentication.AppSecretHeaderName, app.Secret);
        client.DefaultRequestHeaders.Add(EndUserIp.HeaderName, endUserIp);
        return await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password), cancellationToken);
    }

    private async Task<SeededApplication> SaveApplicationAsync(CancellationToken cancellationToken)
    {
        var (application, secret) = Application.Create($"lt-{Guid.NewGuid():N}"[..20], "Throttle App", null, false, TimeProvider.System).Value;
        using var session = postgres.SessionFactory.OpenSession();
        using var transaction = session.BeginTransaction();
        await session.SaveAsync(application, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new SeededApplication(application.Id, application.AppKey, secret);
    }

    private async Task<Guid> SaveUserAsync(SeededApplication app, string email, CancellationToken cancellationToken)
    {
        var user = User.Create(email, "Ana", Password, new PasswordHasher<User>()).Value;
        user.MarkEmailVerified(DateTime.UtcNow);
        using var session = postgres.SessionFactory.OpenSession();
        using var transaction = session.BeginTransaction();
        await session.SaveAsync(user, cancellationToken);
        await session.SaveAsync(UserApplicationAccess.Grant(user.Id, app.Id, []).Value, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return user.Id;
    }

    private async Task<List<LoginFailedRow>> ReadLoginFailedAsync(string email, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT new_values ->> 'reason', entity_id FROM audit_logs WHERE action = @action AND new_values ->> 'email' = @email ORDER BY occurred_at_utc",
            connection);
        command.Parameters.AddWithValue("action", AuditActions.LoginFailed);
        command.Parameters.AddWithValue("email", email);
        var rows = new List<LoginFailedRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new LoginFailedRow(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
        }

        return rows;
    }

    private sealed record SeededApplication(Guid Id, string AppKey, string Secret);

    private sealed record LoginFailedRow(string Reason, string? EntityId);
}
