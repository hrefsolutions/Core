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
public sealed partial class InvitationIntegrationTests(PostgresFixture postgres)
{
    private const string AcceptUrl = "https://app.example.test/invitation?lang=es";
    private const string ChosenPassword = "correct horse battery staple";

    [Fact]
    public async Task Invite_CreatesAnUnverifiedUserWithTheDefaultRoleAndQueuesTheInvitation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var app = await SaveApplicationAsync(withDefaultRole: true, cancellationToken);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var admin = factory.CreateAdminClient();
        var email = UniqueEmail();

        var response = await admin.PostAsJsonAsync(
            "/api/users/invitations",
            new InviteUserRequest($" {email.ToUpperInvariant()} ", "Ana Pérez", app.AppKey, null, AcceptUrl),
            cancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<UserResponse>(cancellationToken);
        Assert.Equal($"/api/users/{body!.Id}", response.Headers.Location?.AbsolutePath);
        Assert.Equal(email, body.Email);
        Assert.Null(body.EmailVerifiedAtUtc);

        using var session = postgres.SessionFactory.OpenSession();
        var access = await session.Query<UserApplicationAccess>().SingleAsync(candidate => candidate.UserId == body.Id, cancellationToken);
        Assert.Equal(app.Id, access.ApplicationId);
        Assert.Equal(["Member"], access.GetRoleNames());

        var notification = Assert.Single(await ReadNotificationsAsync(email, cancellationToken));
        Assert.Equal(InvitationService.InvitationTemplateKey, notification.TemplateKey);
        Assert.Equal(app.AppKey, notification.AppKey);
        Assert.Contains("https://app.example.test/invitation?lang=es&token=", notification.TextBody, StringComparison.Ordinal);
        Assert.Contains("72 horas", notification.TextBody, StringComparison.Ordinal);
        var plainToken = ExtractToken(notification.TextBody);
        var stored = await session.Query<UserToken>().SingleAsync(token => token.UserId == body.Id, cancellationToken);
        Assert.Equal(UserToken.HashToken(plainToken), stored.TokenHash);
        Assert.Equal(UserTokenPurpose.Invitation, stored.Purpose);
        Assert.Equal(app.Id, stored.ApplicationId);
        Assert.Equal(UserToken.InvitationLifetime, stored.ExpiresAtUtc - stored.CreatedAtUtc);

        var audit = await ReadAuditAsync(body.Id, cancellationToken);
        Assert.Contains(audit, row => row.Action == AuditActions.UserInvited);
        Assert.Contains(audit, row => row.Action == AuditActions.AccessGranted && row.AppKey == app.AppKey);
        Assert.All(audit, row => Assert.DoesNotContain(plainToken, row.NewValues, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AcceptInvitation_SetsThePasswordVerifiesTheEmailAndLetsTheUserLogIn()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var app = await SaveApplicationAsync(withDefaultRole: true, cancellationToken);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var admin = factory.CreateAdminClient();
        using var client = AppClient(factory, app);
        var email = UniqueEmail();
        var (userId, token) = await InviteAsync(admin, app, email, cancellationToken);

        var loginBefore = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, ChosenPassword), cancellationToken);
        var accept = await client.PostAsJsonAsync("/api/auth/accept-invitation", new AcceptInvitationRequest(token, ChosenPassword), cancellationToken);
        var again = await client.PostAsJsonAsync("/api/auth/accept-invitation", new AcceptInvitationRequest(token, ChosenPassword), cancellationToken);
        var loginAfter = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, ChosenPassword), cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, loginBefore.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, accept.StatusCode);
        await AssertProblemAsync(again, HttpStatusCode.BadRequest, AuthErrors.InvalidOneTimeToken, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, loginAfter.StatusCode);

        using var session = postgres.SessionFactory.OpenSession();
        var user = await session.GetAsync<User>(userId, cancellationToken);
        Assert.NotNull(user.EmailVerifiedAtUtc);
        var notifications = await ReadNotificationsAsync(email, cancellationToken);
        Assert.Equal([InvitationService.InvitationTemplateKey, AccountService.WelcomeUserTemplateKey], notifications.Select(row => row.TemplateKey));
        var audit = await ReadAuditAsync(userId, cancellationToken);
        Assert.Contains(audit, row => row.Action == AuditActions.UserInvitationAccepted && row.AppKey == app.AppKey);
        Assert.All(audit, row => Assert.DoesNotContain(ChosenPassword, row.NewValues, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AcceptInvitation_WithAWeakPassword_KeepsTheLinkUsable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var app = await SaveApplicationAsync(withDefaultRole: false, cancellationToken);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var admin = factory.CreateAdminClient();
        using var client = AppClient(factory, app);
        var (_, token) = await InviteAsync(admin, app, UniqueEmail(), cancellationToken);

        var weak = await client.PostAsJsonAsync("/api/auth/accept-invitation", new AcceptInvitationRequest(token, "short"), cancellationToken);
        var accept = await client.PostAsJsonAsync("/api/auth/accept-invitation", new AcceptInvitationRequest(token, ChosenPassword), cancellationToken);

        await AssertProblemAsync(weak, HttpStatusCode.BadRequest, UserErrors.WeakPassword, cancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, accept.StatusCode);
    }

    [Fact]
    public async Task AcceptInvitation_WithAnotherApplicationOrAnotherKindOfToken_IsRejected()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var app = await SaveApplicationAsync(withDefaultRole: false, cancellationToken);
        var other = await SaveApplicationAsync(withDefaultRole: false, cancellationToken);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var admin = factory.CreateAdminClient();
        using var client = AppClient(factory, app);
        using var otherClient = AppClient(factory, other);
        var (_, token) = await InviteAsync(admin, app, UniqueEmail(), cancellationToken);

        var foreign = await otherClient.PostAsJsonAsync("/api/auth/accept-invitation", new AcceptInvitationRequest(token, ChosenPassword), cancellationToken);
        var asReset = await client.PostAsJsonAsync("/api/auth/reset-password", new ResetPasswordRequest(token, ChosenPassword), cancellationToken);
        var asVerify = await client.PostAsJsonAsync("/api/auth/verify-email", new VerifyEmailRequest(token), cancellationToken);
        var accept = await client.PostAsJsonAsync("/api/auth/accept-invitation", new AcceptInvitationRequest(token, ChosenPassword), cancellationToken);

        await AssertProblemAsync(foreign, HttpStatusCode.BadRequest, AuthErrors.InvalidOneTimeToken, cancellationToken);
        await AssertProblemAsync(asReset, HttpStatusCode.BadRequest, AuthErrors.InvalidOneTimeToken, cancellationToken);
        await AssertProblemAsync(asVerify, HttpStatusCode.BadRequest, AuthErrors.InvalidOneTimeToken, cancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, accept.StatusCode);
    }

    [Fact]
    public async Task Invite_ValidatesTheRequestAndCreatesNothingOnError()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var app = await SaveApplicationAsync(withDefaultRole: true, cancellationToken);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var admin = factory.CreateAdminClient();
        var existing = UniqueEmail();
        await InviteAsync(admin, app, existing, cancellationToken);
        var email = UniqueEmail();

        var badUrl = await admin.PostAsJsonAsync("/api/users/invitations", new InviteUserRequest(email, "Ana", app.AppKey, null, "javascript:alert(1)"), cancellationToken);
        var unknownApp = await admin.PostAsJsonAsync("/api/users/invitations", new InviteUserRequest(email, "Ana", "no-such-app", null, AcceptUrl), cancellationToken);
        var unknownRole = await admin.PostAsJsonAsync("/api/users/invitations", new InviteUserRequest(email, "Ana", app.AppKey, [Guid.NewGuid()], AcceptUrl), cancellationToken);
        var badEmail = await admin.PostAsJsonAsync("/api/users/invitations", new InviteUserRequest("not-an-email", "Ana", app.AppKey, null, AcceptUrl), cancellationToken);
        var taken = await admin.PostAsJsonAsync("/api/users/invitations", new InviteUserRequest(existing.ToUpperInvariant(), "Ana", app.AppKey, null, AcceptUrl), cancellationToken);

        await AssertProblemAsync(badUrl, HttpStatusCode.BadRequest, AuthErrors.InvalidCallbackUrl, cancellationToken);
        await AssertProblemAsync(unknownApp, HttpStatusCode.NotFound, ApplicationErrors.NotFound, cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, unknownRole.StatusCode);
        Assert.Equal(AuthorizationErrors.UnknownRolesCode, (await unknownRole.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken))!.Extensions["errorCode"]?.ToString());
        await AssertProblemAsync(badEmail, HttpStatusCode.BadRequest, UserErrors.InvalidEmail, cancellationToken);
        await AssertProblemAsync(taken, HttpStatusCode.Conflict, UserErrors.EmailTaken, cancellationToken);
        Assert.Empty(await ReadNotificationsAsync(email, cancellationToken));
        Assert.Single(await ReadNotificationsAsync(existing, cancellationToken));

        using var session = postgres.SessionFactory.OpenSession();
        Assert.False(await session.Query<User>().AnyAsync(user => user.Email == email, cancellationToken));
    }

    [Fact]
    public async Task Invite_WithAnEmptyRoleList_GrantsTheAccessWithoutRoles()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var app = await SaveApplicationAsync(withDefaultRole: true, cancellationToken);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var admin = factory.CreateAdminClient();

        var response = await admin.PostAsJsonAsync(
            "/api/users/invitations",
            new InviteUserRequest(UniqueEmail(), "Ana", app.AppKey, [], AcceptUrl),
            cancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<UserResponse>(cancellationToken);
        using var session = postgres.SessionFactory.OpenSession();
        var access = await session.Query<UserApplicationAccess>().SingleAsync(candidate => candidate.UserId == body!.Id, cancellationToken);
        Assert.Empty(access.GetRoleNames());
    }

    [Fact]
    public async Task Resend_RevokesThePreviousLinkAndOnlyWorksWhilePending()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var app = await SaveApplicationAsync(withDefaultRole: false, cancellationToken);
        var other = await SaveApplicationAsync(withDefaultRole: false, cancellationToken);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var admin = factory.CreateAdminClient();
        using var client = AppClient(factory, app);
        var email = UniqueEmail();
        var (userId, firstToken) = await InviteAsync(admin, app, email, cancellationToken);

        var noAccess = await admin.PostAsJsonAsync($"/api/users/{userId}/invitations", new ResendInvitationRequest(other.AppKey, AcceptUrl), cancellationToken);
        var unknownUser = await admin.PostAsJsonAsync($"/api/users/{Guid.NewGuid()}/invitations", new ResendInvitationRequest(app.AppKey, AcceptUrl), cancellationToken);
        var resend = await admin.PostAsJsonAsync($"/api/users/{userId}/invitations", new ResendInvitationRequest(app.AppKey, AcceptUrl), cancellationToken);

        await AssertProblemAsync(noAccess, HttpStatusCode.NotFound, AuthorizationErrors.AccessNotFound, cancellationToken);
        await AssertProblemAsync(unknownUser, HttpStatusCode.NotFound, UserErrors.NotFound, cancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, resend.StatusCode);

        var notifications = await ReadNotificationsAsync(email, cancellationToken);
        Assert.Equal(2, notifications.Count);
        var secondToken = ExtractToken(notifications[1].TextBody);
        Assert.NotEqual(firstToken, secondToken);

        var withFirst = await client.PostAsJsonAsync("/api/auth/accept-invitation", new AcceptInvitationRequest(firstToken, ChosenPassword), cancellationToken);
        var withSecond = await client.PostAsJsonAsync("/api/auth/accept-invitation", new AcceptInvitationRequest(secondToken, ChosenPassword), cancellationToken);
        var afterAccept = await admin.PostAsJsonAsync($"/api/users/{userId}/invitations", new ResendInvitationRequest(app.AppKey, AcceptUrl), cancellationToken);

        await AssertProblemAsync(withFirst, HttpStatusCode.BadRequest, AuthErrors.InvalidOneTimeToken, cancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, withSecond.StatusCode);
        await AssertProblemAsync(afterAccept, HttpStatusCode.Conflict, UserErrors.EmailAlreadyVerified, cancellationToken);
        Assert.Contains(await ReadAuditAsync(userId, cancellationToken), row => row.Action == AuditActions.UserInvitationResent);
    }

    [GeneratedRegex("token=([A-Za-z0-9_-]{43})")]
    private static partial Regex TokenPattern();

    private static string ExtractToken(string? body)
    {
        var match = TokenPattern().Match(body ?? string.Empty);
        Assert.True(match.Success, "The email does not contain a token link.");
        return match.Groups[1].Value;
    }

    private static string UniqueEmail() => $"inv-{Guid.NewGuid():N}@example.com";

    private async Task<(Guid UserId, string Token)> InviteAsync(HttpClient admin, SeededApplication app, string email, CancellationToken cancellationToken)
    {
        var response = await admin.PostAsJsonAsync("/api/users/invitations", new InviteUserRequest(email, "Ana", app.AppKey, null, AcceptUrl), cancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var userId = (await response.Content.ReadFromJsonAsync<UserResponse>(cancellationToken))!.Id;
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

    private async Task<SeededApplication> SaveApplicationAsync(bool withDefaultRole, CancellationToken cancellationToken)
    {
        var (application, secret) = Application.Create($"inv-{Guid.NewGuid():N}"[..20], "Invitation App", null, false, TimeProvider.System).Value;
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
        string[] ids;

        using (var session = postgres.SessionFactory.OpenSession())
        {
            var accessIds = await session.Query<UserApplicationAccess>()
                .Where(access => access.UserId == userId)
                .Select(access => access.Id)
                .ToListAsync(cancellationToken);
            ids = [userId.ToString(), .. accessIds.Select(id => id.ToString())];
        }

        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT action, app_key, COALESCE(new_values::text, '') FROM audit_logs WHERE entity_id = ANY(@ids) ORDER BY occurred_at_utc",
            connection);
        command.Parameters.AddWithValue("ids", ids);
        var rows = new List<AuditRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new AuditRow(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2)));
        }

        return rows;
    }

    private sealed record SeededApplication(Guid Id, string AppKey, string Secret);

    private sealed record NotificationRow(string TemplateKey, string AppKey, string? TextBody);

    private sealed record AuditRow(string Action, string? AppKey, string NewValues);
}
