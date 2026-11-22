using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Href.Core.Api.Modules.Auth;
using Href.Core.Tests.Modules.Auth;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Href.Core.Tests.Integration;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class WellKnownEndpointsIntegrationTests(PostgresFixture postgres)
{
    private const string Issuer = "https://core.example.test";

    [Fact]
    public async Task GetJwks_ReturnsTheCurrentAndPreviousPublicKeysWithTheirKeyIds()
    {
        await using var factory = CreateFactory(Environments.Production);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/.well-known/jwks.json", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.Public);
        Assert.Equal(TimeSpan.FromHours(1), response.Headers.CacheControl?.MaxAge);
        var keySet = new JsonWebKeySet(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(["core-current", "core-previous"], keySet.Keys.Select(key => key.Kid));
        Assert.All(keySet.Keys, key =>
        {
            Assert.Equal("RSA", key.Kty);
            Assert.Equal("sig", key.Use);
            Assert.Equal("RS256", key.Alg);
            Assert.False(key.HasPrivateKey);
        });
    }

    [Fact]
    public async Task GetJwks_ExposesOnlyPublicParameters()
    {
        await using var factory = CreateFactory(Environments.Production);
        using var client = factory.CreateClient();

        var json = await client.GetStringAsync("/.well-known/jwks.json", TestContext.Current.CancellationToken);

        using var document = JsonDocument.Parse(json);
        foreach (var key in document.RootElement.GetProperty("keys").EnumerateArray())
        {
            Assert.Equal(
                ["alg", "e", "kid", "kty", "n", "use"],
                key.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        }
    }

    [Fact]
    public async Task TokenSignedWithTheCurrentKey_ValidatesAgainstThePublishedJwks()
    {
        await using var factory = CreateFactory(Environments.Production);
        using var client = factory.CreateClient();
        var keySet = new JsonWebKeySet(await client.GetStringAsync("/.well-known/jwks.json", TestContext.Current.CancellationToken));
        var signingCredentials = factory.Services.GetRequiredService<JwtKeyProvider>().SigningCredentials;
        var handler = new JsonWebTokenHandler();
        var token = handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = "satellite",
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = signingCredentials,
        });

        var result = await handler.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = Issuer,
            ValidAudience = "satellite",
            IssuerSigningKeys = keySet.GetSigningKeys(),
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
        });

        Assert.True(result.IsValid, result.Exception?.Message);
        Assert.Equal("core-current", new JsonWebToken(token).Kid);
    }

    [Fact]
    public async Task TokenSignedWithAnUnpublishedKey_DoesNotValidateAgainstThePublishedJwks()
    {
        await using var factory = CreateFactory(Environments.Production);
        using var client = factory.CreateClient();
        var keySet = new JsonWebKeySet(await client.GetStringAsync("/.well-known/jwks.json", TestContext.Current.CancellationToken));
        using var foreignRsa = RSA.Create(2048);
        var handler = new JsonWebTokenHandler();
        var token = handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = "satellite",
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(
                new RsaSecurityKey(foreignRsa) { KeyId = "core-current" },
                SecurityAlgorithms.RsaSha256),
        });

        var result = await handler.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = Issuer,
            ValidAudience = "satellite",
            IssuerSigningKeys = keySet.GetSigningKeys(),
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
        });

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task GetOpenIdConfiguration_ReturnsIssuerAndJwksUri()
    {
        await using var factory = CreateFactory(Environments.Production, issuer: Issuer + "/");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/.well-known/openid-configuration", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.Public);
        Assert.Equal(TimeSpan.FromHours(1), response.Headers.CacheControl?.MaxAge);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(Issuer, document.RootElement.GetProperty("issuer").GetString());
        Assert.Equal(Issuer + "/.well-known/jwks.json", document.RootElement.GetProperty("jwks_uri").GetString());
    }

    [Fact]
    public async Task OpenIdDiscovery_AsUsedByJwtBearerAuthority_FindsIssuerAndKeysThatValidateACoreToken()
    {
        await using var factory = CreateFactory(Environments.Production);
        using var client = factory.CreateClient();
        var configurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
            new Uri(client.BaseAddress!, "/.well-known/openid-configuration").ToString(),
            new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever(client) { RequireHttps = false });
        var handler = new JsonWebTokenHandler();
        var token = handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = "satellite",
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = factory.Services.GetRequiredService<JwtKeyProvider>().SigningCredentials,
        });

        var configuration = await configurationManager.GetConfigurationAsync(TestContext.Current.CancellationToken);
        var result = await handler.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = configuration.Issuer,
            ValidAudience = "satellite",
            IssuerSigningKeys = configuration.SigningKeys,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
        });

        Assert.Equal(Issuer, configuration.Issuer);
        Assert.Equal(Issuer + "/.well-known/jwks.json", configuration.JwksUri);
        Assert.Equal(["core-current", "core-previous"], configuration.SigningKeys.Select(key => key.KeyId).Order(StringComparer.Ordinal));
        Assert.True(result.IsValid, result.Exception?.Message);
    }

    [Fact]
    public async Task GetJwks_InDevelopmentWithoutKey_PublishesTheEphemeralKey()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString, overrides: WithoutSigningKey);
        using var client = factory.CreateClient();

        var keySet = new JsonWebKeySet(await client.GetStringAsync("/.well-known/jwks.json", TestContext.Current.CancellationToken));

        var key = Assert.Single(keySet.Keys);
        Assert.Equal(factory.Services.GetRequiredService<JwtKeyProvider>().CurrentKeyId, key.Kid);
        Assert.StartsWith("ephemeral-", key.Kid, StringComparison.Ordinal);
    }

    [Fact]
    public void Api_InProductionWithoutSigningKey_DoesNotStart()
    {
        using var factory = new ApiFactory(
            postgres.ConnectionString,
            overrides: WithoutSigningKey,
            environment: Environments.Production);

        var exception = Record.Exception(() => factory.CreateClient());

        Assert.NotNull(exception);
    }

    [Fact]
    public async Task Api_InProductionWithSigningKey_Starts()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString, environment: Environments.Production);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/.well-known/jwks.json", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static Dictionary<string, string?> WithoutSigningKey => new()
    {
        ["Jwt:SigningKey:KeyId"] = null,
        ["Jwt:SigningKey:PrivateKeyPem"] = null,
    };

    private ApiFactory CreateFactory(string environment, string issuer = Issuer) => new(
        postgres.ConnectionString,
        environment: environment,
        overrides: new Dictionary<string, string?>
        {
            ["Jwt:Issuer"] = issuer,
            ["Jwt:SigningKey:KeyId"] = "core-current",
            ["Jwt:SigningKey:PrivateKeyPem"] = JwtTestKeys.CurrentPrivateKeyPem,
            ["Jwt:PreviousPublicKeys:0:KeyId"] = "core-previous",
            ["Jwt:PreviousPublicKeys:0:PublicKeyPem"] = JwtTestKeys.PreviousPublicKeyPem,
        });
}
