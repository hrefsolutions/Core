using System.Net.Http.Headers;
using Href.Core.Api.Modules.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Href.Core.Tests.Integration;

/// <summary>
/// Clients for the admin endpoints: a bearer access token minted by the API's own <see cref="AccessTokenFactory"/> for the
/// reserved <c>core</c> application, like the one a real admin gets from <c>POST /api/auth/login</c>.
/// </summary>
public static class AdminClientExtensions
{
    public const string AdminEmail = "test-admin@example.test";

    public static readonly Guid AdminUserId = Guid.Parse("01937a00-0000-7000-8000-0000000000ad");

    public static HttpClient CreateAdminClient(this WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.CreateAccessToken([CoreApplication.AdminPermissionKey]));
        return client;
    }

    public static string CreateAccessToken(
        this WebApplicationFactory<Program> factory,
        IReadOnlyList<string> permissions,
        string audience = CoreApplication.AppKey) =>
        factory.Services.GetRequiredService<AccessTokenFactory>()
            .Create(new AccessTokenSubject(AdminUserId, AdminEmail, "Test Admin", audience, ["admin"], permissions))
            .Token;
}
