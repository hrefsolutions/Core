using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Href.Core.Api.Common.Results;
using Href.Core.Api.Modules.Applications;
using Href.Core.Api.Modules.Auth;
using Href.Core.Api.Modules.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Href.Core.Tests.Integration;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AdminAuthorizationIntegrationTests(PostgresFixture postgres)
{
    private const string RoleId = "01937a00-0000-7000-8000-0000000000f1";
    private const string UserId = "01937a00-0000-7000-8000-0000000000f2";

    public static TheoryData<string, string> AdminRoutes => new()
    {
        { "GET", "/api/apps" },
        { "POST", "/api/apps" },
        { "GET", "/api/apps/some-app" },
        { "PUT", "/api/apps/some-app" },
        { "PATCH", "/api/apps/some-app/status" },
        { "POST", "/api/apps/some-app/secret/rotate" },
        { "GET", "/api/apps/some-app/permissions" },
        { "POST", "/api/apps/some-app/permissions" },
        { "GET", "/api/apps/some-app/permissions/orders.read" },
        { "PUT", "/api/apps/some-app/permissions/orders.read" },
        { "DELETE", "/api/apps/some-app/permissions/orders.read" },
        { "GET", "/api/apps/some-app/roles" },
        { "POST", "/api/apps/some-app/roles" },
        { "GET", $"/api/apps/some-app/roles/{RoleId}" },
        { "PUT", $"/api/apps/some-app/roles/{RoleId}" },
        { "PUT", $"/api/apps/some-app/roles/{RoleId}/permissions" },
        { "DELETE", $"/api/apps/some-app/roles/{RoleId}" },
        { "GET", "/api/apps/some-app/users" },
        { "PUT", $"/api/apps/some-app/users/{UserId}/roles" },
        { "DELETE", $"/api/apps/some-app/users/{UserId}" },
        { "GET", "/api/users" },
        { "POST", "/api/users" },
        { "GET", $"/api/users/{UserId}" },
        { "PUT", $"/api/users/{UserId}" },
        { "POST", $"/api/users/{UserId}/lock" },
        { "POST", $"/api/users/{UserId}/unlock" },
        { "GET", $"/api/users/{UserId}/sessions" },
        { "DELETE", $"/api/users/{UserId}/sessions" },
        { "GET", "/api/notifications/templates" },
        { "POST", "/api/notifications/templates" },
        { "GET", "/api/notifications/templates/some-template" },
        { "PUT", "/api/notifications/templates/some-template" },
        { "DELETE", "/api/notifications/templates/some-template" },
        { "POST", "/api/notifications/templates/some-template/preview" },
        { "GET", "/api/audit-logs" },
    };

    [Theory]
    [MemberData(nameof(AdminRoutes))]
    public async Task AdminRoute_WithoutToken_Returns401InvalidToken(string method, string path)
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Request(method, path), TestContext.Current.CancellationToken);

        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, AdminAuthentication.InvalidTokenError);
        Assert.Contains(response.Headers.WwwAuthenticate, header => header.Scheme == "Bearer");
    }

    [Theory]
    [MemberData(nameof(AdminRoutes))]
    public async Task AdminRoute_WithATokenWithoutCoreAdmin_Returns403AdminRequired(string method, string path)
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = ClientWithToken(factory, factory.CreateAccessToken(["core.audit"]));

        var response = await client.SendAsync(Request(method, path), TestContext.Current.CancellationToken);

        await AssertProblemAsync(response, HttpStatusCode.Forbidden, AdminAuthentication.AdminRequiredError);
    }

    [Theory]
    [MemberData(nameof(AdminRoutes))]
    public async Task AdminRoute_WithAnAdminToken_IsAuthorized(string method, string path)
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateAdminClient();

        var response = await client.SendAsync(Request(method, path), TestContext.Current.CancellationToken);

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AdminRoute_WithTokensThatAreNotCoreAccessTokens_Returns401()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ApiFactory(postgres.ConnectionString);
        var signingCredentials = factory.Services.GetRequiredService<JwtKeyProvider>().SigningCredentials;
        string[] tokens =
        [
            factory.CreateAccessToken([CoreApplication.AdminPermissionKey], audience: "billing"),
            CreateToken(signingCredentials, tokenType: null, expires: DateTime.UtcNow.AddMinutes(5)),
            CreateToken(signingCredentials, AccessTokenFactory.AccessTokenType, expires: DateTime.UtcNow.AddMinutes(-5)),
            CreateToken(signingCredentials, AccessTokenFactory.AccessTokenType, DateTime.UtcNow.AddMinutes(5), issuer: "https://evil.example"),
            CreateToken(
                new SigningCredentials(new RsaSecurityKey(System.Security.Cryptography.RSA.Create(2048)) { KeyId = "foreign" }, SecurityAlgorithms.RsaSha256),
                AccessTokenFactory.AccessTokenType,
                DateTime.UtcNow.AddMinutes(5)),
            CreateToken(
                new SigningCredentials(
                    new SymmetricSecurityKey(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)) { KeyId = signingCredentials.Key.KeyId },
                    SecurityAlgorithms.HmacSha256),
                AccessTokenFactory.AccessTokenType,
                DateTime.UtcNow.AddMinutes(5)),
            "not-a-jwt",
        ];

        foreach (var token in tokens)
        {
            using var client = ClientWithToken(factory, token);

            var response = await client.GetAsync("/api/users", cancellationToken);

            await AssertProblemAsync(response, HttpStatusCode.Unauthorized, AdminAuthentication.InvalidTokenError);
        }
    }

    [Fact]
    public async Task AdminRoute_WithATokenSignedByAPreviousKey_IsAuthorized()
    {
        await using var factory = new ApiFactory(
            postgres.ConnectionString,
            overrides: new Dictionary<string, string?>
            {
                ["Jwt:PreviousPublicKeys:0:KeyId"] = "tests-previous",
                ["Jwt:PreviousPublicKeys:0:PublicKeyPem"] = Href.Core.Tests.Modules.Auth.JwtTestKeys.PreviousPublicKeyPem,
            });
        using var previousRsa = System.Security.Cryptography.RSA.Create();
        previousRsa.ImportFromPem(Href.Core.Tests.Modules.Auth.JwtTestKeys.PreviousPrivateKeyPem);
        var token = CreateToken(
            new SigningCredentials(
                new RsaSecurityKey(previousRsa)
                {
                    KeyId = "tests-previous",
                    CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false },
                },
                SecurityAlgorithms.RsaSha256),
            AccessTokenFactory.AccessTokenType,
            DateTime.UtcNow.AddMinutes(5));
        using var client = ClientWithToken(factory, token);

        var response = await client.GetAsync("/api/users", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AdminWrite_IsAuditedWithTheTokenUserAndTheCoreAppKey()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var admin = factory.CreateAdminClient();

        var response = await admin.PostAsJsonAsync(
            "/api/users",
            new Href.Core.Api.Modules.Users.CreateUserRequest($"audited-{Guid.NewGuid():N}@example.com", "Audited User", "correct horse battery"),
            cancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<Href.Core.Api.Modules.Users.UserResponse>(cancellationToken);
        await using var connection = new Npgsql.NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new Npgsql.NpgsqlCommand(
            "SELECT actor_type, actor_id, app_key FROM audit_logs WHERE entity_id = @id AND action = 'USER_CREATED'",
            connection);
        command.Parameters.AddWithValue("id", created!.Id.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        Assert.Equal("User", reader.GetString(0));
        Assert.Equal(AdminClientExtensions.AdminUserId.ToString(), reader.GetString(1));
        Assert.Equal(CoreApplication.AppKey, reader.GetString(2));
    }

    [Fact]
    public async Task CoreApplication_CannotBeOpenedToSelfRegistration()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var admin = factory.CreateAdminClient();
        var core = await admin.GetFromJsonAsync<ApplicationResponse>("/api/apps/core", cancellationToken);

        var open = await admin.PutAsJsonAsync("/api/apps/core", new UpdateApplicationRequest(core!.Name, core.Description, true), cancellationToken);
        var keepClosed = await admin.PutAsJsonAsync("/api/apps/core", new UpdateApplicationRequest(core.Name, core.Description, false), cancellationToken);

        await AssertProblemAsync(open, HttpStatusCode.Conflict, ApplicationErrors.Reserved);
        Assert.Equal(HttpStatusCode.OK, keepClosed.StatusCode);
    }

    [Fact]
    public async Task AdminRoute_WithApplicationCredentialsOnly_Returns401()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(AppKeyAuthentication.AppKeyHeaderName, CoreApplication.AppKey);
        client.DefaultRequestHeaders.Add(AppKeyAuthentication.AppSecretHeaderName, "whatever-secret-value");

        var response = await client.GetAsync("/api/users", TestContext.Current.CancellationToken);

        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, AdminAuthentication.InvalidTokenError);
    }

    [Fact]
    public async Task EveryEndpoint_DeclaresItsAccessAndAdminRoutesRequireTheAdminPolicy()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();

        Assert.NotEmpty(endpoints);

        foreach (var endpoint in endpoints)
        {
            var route = endpoint.RoutePattern.RawText ?? endpoint.DisplayName ?? "?";
            var anonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;
            var policies = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(data => data.Policy).ToList();

            Assert.True(anonymous || policies.Count > 0, $"{route} declares neither a policy nor [AllowAnonymous].");
            Assert.All(policies, policy => Assert.Contains(policy, new[] { AdminAuthentication.PolicyName, AppKeyAuthentication.PolicyName }));
            Assert.False(
                policies.Contains(AdminAuthentication.PolicyName) && policies.Contains(AppKeyAuthentication.PolicyName),
                $"{route} mixes the Admin and Application policies.");

            var isAdminRoute = (route.StartsWith("api/apps", StringComparison.Ordinal) && route != "api/apps/me")
                || route.StartsWith("api/users", StringComparison.Ordinal)
                || route.StartsWith("api/notifications/templates", StringComparison.Ordinal)
                || route.StartsWith("api/audit-logs", StringComparison.Ordinal);

            if (isAdminRoute)
            {
                Assert.True(
                    policies.SequenceEqual([AdminAuthentication.PolicyName]) && !anonymous,
                    $"{route} must require only the Admin policy.");
            }
        }
    }

    [Fact]
    public async Task ReservedCoreData_CannotBeSuspendedDeletedOrStrippedOfCoreAdmin()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var admin = factory.CreateAdminClient();

        var suspend = await admin.PatchAsJsonAsync("/api/apps/core/status", new UpdateApplicationStatusRequest("Suspended"), cancellationToken);
        var deletePermission = await admin.DeleteAsync("/api/apps/core/permissions/core.admin", cancellationToken);
        var deleteRole = await admin.DeleteAsync($"/api/apps/core/roles/{CoreApplication.AdminRoleId}", cancellationToken);
        var stripRole = await admin.PutAsJsonAsync(
            $"/api/apps/core/roles/{CoreApplication.AdminRoleId}/permissions",
            new ReplaceRolePermissionsRequest([]),
            cancellationToken);
        var keepRole = await admin.PutAsJsonAsync(
            $"/api/apps/core/roles/{CoreApplication.AdminRoleId}/permissions",
            new ReplaceRolePermissionsRequest([CoreApplication.AdminPermissionKey]),
            cancellationToken);

        await AssertProblemAsync(suspend, HttpStatusCode.Conflict, ApplicationErrors.Reserved);
        await AssertProblemAsync(deletePermission, HttpStatusCode.Conflict, AuthorizationErrors.Reserved);
        await AssertProblemAsync(deleteRole, HttpStatusCode.Conflict, AuthorizationErrors.Reserved);
        await AssertProblemAsync(stripRole, HttpStatusCode.Conflict, AuthorizationErrors.Reserved);
        Assert.Equal(HttpStatusCode.OK, keepRole.StatusCode);
        var core = await admin.GetFromJsonAsync<ApplicationResponse>("/api/apps/core", cancellationToken);
        Assert.Equal("Active", core!.Status);
    }

    private static HttpRequestMessage Request(string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);

        if (method is "POST" or "PUT" or "PATCH")
        {
            request.Content = JsonContent.Create(new { });
        }

        return request;
    }

    private static HttpClient ClientWithToken(ApiFactory factory, string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static string CreateToken(
        SigningCredentials signingCredentials,
        string? tokenType,
        DateTime expires,
        string issuer = "http://localhost")
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = CoreApplication.AppKey,
            IssuedAt = expires.AddMinutes(-15),
            NotBefore = expires.AddMinutes(-15),
            Expires = expires,
            SigningCredentials = signingCredentials,
            Claims = new Dictionary<string, object>
            {
                ["sub"] = Guid.CreateVersion7().ToString(),
                [AccessTokenFactory.PermissionsClaimType] = new[] { CoreApplication.AdminPermissionKey },
            },
        };

        if (tokenType is not null)
        {
            descriptor.TokenType = tokenType;
        }

        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(descriptor);
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, Error error)
    {
        Assert.Equal(status, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(TestContext.Current.CancellationToken);
        Assert.NotNull(problem);
        Assert.Equal(error.Code, problem.Extensions["errorCode"]?.ToString());
    }
}
