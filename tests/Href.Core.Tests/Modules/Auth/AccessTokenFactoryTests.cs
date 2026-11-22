using System.Text.Json;
using Href.Core.Api.Modules.Auth;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Href.Core.Tests.Modules.Auth;

public sealed class AccessTokenFactoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid UserId = Guid.CreateVersion7();

    [Fact]
    public async Task Create_SignsAnRs256TokenWithTheCurrentKeyAndTheExpectedClaims()
    {
        using var provider = JwtKeyProviderTests.CreateProvider(JwtOptionsValidatorTests.ValidOptions(), Environments.Production);
        var factory = CreateFactory(provider);

        var issued = factory.Create(new AccessTokenSubject(UserId, "ana@example.com", "Ana Pérez", "billing", ["Admin", "Viewer"], ["orders.read", "orders.write"]));

        var token = new JsonWebToken(issued.Token);
        Assert.Equal("RS256", token.Alg);
        Assert.Equal("core-2026-11", token.Kid);
        Assert.Equal("https://core.example.com", token.Issuer);
        Assert.Equal(["billing"], token.Audiences);
        Assert.Equal(UserId.ToString(), token.Subject);
        Assert.Equal("ana@example.com", token.GetClaim("email").Value);
        Assert.Equal("Ana Pérez", token.GetClaim("name").Value);
        Assert.Equal(["Admin", "Viewer"], token.Claims.Where(claim => claim.Type == "roles").Select(claim => claim.Value));
        Assert.Equal(["orders.read", "orders.write"], token.Claims.Where(claim => claim.Type == "permissions").Select(claim => claim.Value));
        Assert.True(Guid.TryParse(token.Id, out _));
        Assert.Equal(Now.UtcDateTime, token.IssuedAt);
        Assert.Equal(Now.UtcDateTime, token.ValidFrom);
        Assert.Equal(Now.UtcDateTime.AddMinutes(15), token.ValidTo);
        Assert.Equal(Now.UtcDateTime.AddMinutes(15), issued.ExpiresAtUtc);
        Assert.Equal(900, issued.ExpiresInSeconds);

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(issued.Token, new TokenValidationParameters
        {
            ValidIssuer = "https://core.example.com",
            ValidAudience = "billing",
            IssuerSigningKey = new JsonWebKey
            {
                Kty = provider.JsonWebKeySet.Keys[0].Kty,
                Kid = provider.JsonWebKeySet.Keys[0].Kid,
                N = provider.JsonWebKeySet.Keys[0].N,
                E = provider.JsonWebKeySet.Keys[0].E,
            },
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            LifetimeValidator = (_, _, _, _) => true,
        });
        Assert.True(result.IsValid, result.Exception?.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Create_AlwaysWritesRolesAndPermissionsAsJsonArrays(int count)
    {
        using var provider = JwtKeyProviderTests.CreateProvider(JwtOptionsValidatorTests.ValidOptions(), Environments.Production);
        var values = Enumerable.Range(0, count).Select(index => $"value-{index}").ToList();

        var issued = CreateFactory(provider).Create(new AccessTokenSubject(UserId, "ana@example.com", "Ana", "billing", values, values));

        using var payload = JsonDocument.Parse(Base64UrlEncoder.Decode(new JsonWebToken(issued.Token).EncodedPayload));
        Assert.Equal(JsonValueKind.Array, payload.RootElement.GetProperty("roles").ValueKind);
        Assert.Equal(JsonValueKind.Array, payload.RootElement.GetProperty("permissions").ValueKind);
        Assert.Equal(count, payload.RootElement.GetProperty("roles").GetArrayLength());
    }

    [Fact]
    public void Create_GivesEachTokenADifferentId()
    {
        using var provider = JwtKeyProviderTests.CreateProvider(JwtOptionsValidatorTests.ValidOptions(), Environments.Production);
        var factory = CreateFactory(provider);
        var subject = new AccessTokenSubject(UserId, "ana@example.com", "Ana", "billing", [], []);

        var first = new JsonWebToken(factory.Create(subject).Token);
        var second = new JsonWebToken(factory.Create(subject).Token);

        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public void Create_UsesTheConfiguredAccessTokenLifetime()
    {
        using var provider = JwtKeyProviderTests.CreateProvider(JwtOptionsValidatorTests.ValidOptions(), Environments.Production);
        var options = JwtOptionsValidatorTests.ValidOptions();
        options.AccessTokenMinutes = 5;
        var factory = new AccessTokenFactory(provider, Options.Create(options), new FakeTimeProvider(Now));

        var issued = factory.Create(new AccessTokenSubject(UserId, "ana@example.com", "Ana", "billing", [], []));

        Assert.Equal(300, issued.ExpiresInSeconds);
        Assert.Equal(Now.UtcDateTime.AddMinutes(5), new JsonWebToken(issued.Token).ValidTo);
    }

    private static AccessTokenFactory CreateFactory(JwtKeyProvider provider) =>
        new(provider, Options.Create(JwtOptionsValidatorTests.ValidOptions()), new FakeTimeProvider(Now));
}
