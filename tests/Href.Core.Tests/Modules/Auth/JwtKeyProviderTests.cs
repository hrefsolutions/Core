using System.Security.Cryptography;
using Href.Core.Api.Modules.Auth;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Href.Core.Tests.Modules.Auth;

public sealed class JwtKeyProviderTests
{
    [Fact]
    public void JsonWebKeySet_ListsTheCurrentKeyFirstAndThenThePreviousKeys()
    {
        using var provider = CreateProvider(JwtOptionsValidatorTests.ValidOptions(), Environments.Production);

        var keys = provider.JsonWebKeySet.Keys;

        Assert.Equal(["core-2026-11", "core-2026-05"], keys.Select(key => key.Kid));
        Assert.All(keys, key =>
        {
            Assert.Equal("RSA", key.Kty);
            Assert.Equal("sig", key.Use);
            Assert.Equal("RS256", key.Alg);
        });
    }

    [Fact]
    public void JsonWebKeySet_PublishesTheModulusAndExponentOfEachKey()
    {
        using var provider = CreateProvider(JwtOptionsValidatorTests.ValidOptions(), Environments.Production);
        using var current = RSA.Create();
        current.ImportFromPem(JwtTestKeys.CurrentPublicKeyPem);
        using var previous = RSA.Create();
        previous.ImportFromPem(JwtTestKeys.PreviousPublicKeyPem);

        var keys = provider.JsonWebKeySet.Keys;

        AssertPublicParameters(current.ExportParameters(false), keys[0]);
        AssertPublicParameters(previous.ExportParameters(false), keys[1]);
    }

    [Fact]
    public void SigningCredentials_UseRs256AndTheCurrentKeyId()
    {
        using var provider = CreateProvider(JwtOptionsValidatorTests.ValidOptions(), Environments.Production);

        Assert.Equal(SecurityAlgorithms.RsaSha256, provider.SigningCredentials.Algorithm);
        Assert.Equal("core-2026-11", provider.SigningCredentials.Key.KeyId);
        Assert.Equal("core-2026-11", provider.CurrentKeyId);
        Assert.False(provider.IsEphemeral);
    }

    [Fact]
    public async Task TokenSignedWithTheSigningCredentials_ValidatesAgainstThePublishedKeyOnly()
    {
        using var provider = CreateProvider(JwtOptionsValidatorTests.ValidOptions(), Environments.Production);
        var handler = new JsonWebTokenHandler();
        var token = handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "https://core.example.com",
            Audience = "satellite",
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = provider.SigningCredentials,
        });

        var withCurrent = await handler.ValidateTokenAsync(token, Parameters(ToSecurityKey(provider.JsonWebKeySet.Keys[0])));
        var withPrevious = await handler.ValidateTokenAsync(token, Parameters(ToSecurityKey(provider.JsonWebKeySet.Keys[1])));

        Assert.True(withCurrent.IsValid, withCurrent.Exception?.Message);
        Assert.False(withPrevious.IsValid);
    }

    [Fact]
    public async Task TwoProvidersWithTheSamePem_SignAfterTheFirstOneIsDisposed()
    {
        var handler = new JsonWebTokenHandler();
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = "https://core.example.com",
            Audience = "satellite",
            Expires = DateTime.UtcNow.AddMinutes(5),
        };

        using (var first = CreateProvider(JwtOptionsValidatorTests.ValidOptions(), Environments.Production))
        {
            descriptor.SigningCredentials = first.SigningCredentials;
            handler.CreateToken(descriptor);
        }

        using var second = CreateProvider(JwtOptionsValidatorTests.ValidOptions(), Environments.Production);
        descriptor.SigningCredentials = second.SigningCredentials;
        var token = handler.CreateToken(descriptor);

        var result = await handler.ValidateTokenAsync(token, Parameters(ToSecurityKey(second.JsonWebKeySet.Keys[0])));
        Assert.True(result.IsValid, result.Exception?.Message);
    }

    [Fact]
    public void Development_WithoutConfiguredKey_GeneratesAnEphemeral3072BitKey()
    {
        var options = JwtOptionsValidatorTests.ValidOptions();
        options.SigningKey = new JwtSigningKeyOptions();
        options.PreviousPublicKeys = [];

        using var provider = CreateProvider(options, Environments.Development);

        Assert.True(provider.IsEphemeral);
        Assert.StartsWith("ephemeral-", provider.CurrentKeyId, StringComparison.Ordinal);
        var key = Assert.Single(provider.JsonWebKeySet.Keys);
        Assert.Equal(provider.CurrentKeyId, key.Kid);
        Assert.Equal(3072 / 8, Base64UrlEncoder.DecodeBytes(key.N).Length);
    }

    [Fact]
    public void Development_WithConfiguredKey_UsesIt()
    {
        using var provider = CreateProvider(JwtOptionsValidatorTests.ValidOptions(), Environments.Development);

        Assert.False(provider.IsEphemeral);
        Assert.Equal("core-2026-11", provider.CurrentKeyId);
    }

    [Fact]
    public void Production_WithoutConfiguredKey_Throws()
    {
        var options = JwtOptionsValidatorTests.ValidOptions();
        options.SigningKey = new JwtSigningKeyOptions();

        Assert.Throws<InvalidOperationException>(() => CreateProvider(options, Environments.Production));
    }

    internal static JwtKeyProvider CreateProvider(JwtOptions options, string environmentName) =>
        new(Options.Create(options), new HostingEnvironment { EnvironmentName = environmentName });

    private static void AssertPublicParameters(RSAParameters expected, PublicJsonWebKey actual)
    {
        Assert.Equal(Base64UrlEncoder.Encode(expected.Modulus!), actual.N);
        Assert.Equal(Base64UrlEncoder.Encode(expected.Exponent!), actual.E);
    }

    private static JsonWebKey ToSecurityKey(PublicJsonWebKey key) => new()
    {
        Kty = key.Kty,
        Use = key.Use,
        Alg = key.Alg,
        Kid = key.Kid,
        N = key.N,
        E = key.E,
    };

    private static TokenValidationParameters Parameters(SecurityKey key) => new()
    {
        ValidIssuer = "https://core.example.com",
        ValidAudience = "satellite",
        IssuerSigningKey = key,
        ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
    };
}
