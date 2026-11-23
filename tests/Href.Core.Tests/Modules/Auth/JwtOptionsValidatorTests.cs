using Href.Core.Api.Modules.Auth;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Options;

namespace Href.Core.Tests.Modules.Auth;

public sealed class JwtOptionsValidatorTests
{
    [Fact]
    public void Validate_InProductionWithoutSigningKey_Fails()
    {
        var options = ValidOptions();
        options.SigningKey = new JwtSigningKeyOptions();

        var result = Validate(options, Environments.Production);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains("Jwt:SigningKey:PrivateKeyPem", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_InDevelopmentWithoutSigningKey_Succeeds()
    {
        var options = ValidOptions();
        options.SigningKey = new JwtSigningKeyOptions();

        var result = Validate(options, Environments.Development);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_InProductionWithValidKeys_Succeeds()
    {
        var result = Validate(ValidOptions(), Environments.Production);

        Assert.True(result.Succeeded, string.Join(" | ", result.Failures ?? []));
    }

    [Fact]
    public void Validate_PrivateKeyPemOnOneLine_Succeeds()
    {
        var options = ValidOptions();
        options.SigningKey.PrivateKeyPem = JwtTestKeys.CurrentPrivateKeyPem.Replace("\r", string.Empty).Replace("\n", string.Empty);

        var result = Validate(options, Environments.Production);

        Assert.True(result.Succeeded, string.Join(" | ", result.Failures ?? []));
    }

    [Theory]
    [InlineData("not a pem")]
    [InlineData("-----BEGIN PRIVATE KEY-----\nAAAA\n-----END PRIVATE KEY-----")]
    public void Validate_SigningKeyThatIsNotAValidPem_Fails(string pem)
    {
        var options = ValidOptions();
        options.SigningKey.PrivateKeyPem = pem;

        var result = Validate(options, Environments.Production);

        Assert.True(result.Failed);
    }

    [Fact]
    public void Validate_SigningKeyThatIsAPublicKey_Fails()
    {
        var options = ValidOptions();
        options.SigningKey.PrivateKeyPem = JwtTestKeys.CurrentPublicKeyPem;

        var result = Validate(options, Environments.Production);

        Assert.True(result.Failed);
    }

    [Fact]
    public void Validate_SigningKeyShorterThan2048Bits_Fails()
    {
        var options = ValidOptions();
        options.SigningKey.PrivateKeyPem = JwtTestKeys.CreatePair(1024).PrivatePem;

        var result = Validate(options, Environments.Production);

        Assert.True(result.Failed);
    }

    [Fact]
    public void Validate_SigningKeyIdWithoutPem_Fails()
    {
        var options = ValidOptions();
        options.SigningKey.PrivateKeyPem = null;

        var result = Validate(options, Environments.Development);

        Assert.True(result.Failed);
    }

    [Fact]
    public void Validate_InProductionWithKeyIdButNoPem_ReportsTheKeyAsRequired()
    {
        var options = ValidOptions();
        options.SigningKey.PrivateKeyPem = string.Empty;

        var result = Validate(options, Environments.Production);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains("required outside Development", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_SigningPemWithoutKeyId_Fails()
    {
        var options = ValidOptions();
        options.SigningKey.KeyId = "  ";

        var result = Validate(options, Environments.Development);

        Assert.True(result.Failed);
    }

    [Theory]
    [InlineData("key 1")]
    [InlineData("key/1")]
    [InlineData("clave-ñ")]
    [InlineData("key-1\n")]
    [InlineData("0123456789012345678901234567890123456789012345678901234567890123x")]
    public void Validate_InvalidSigningKeyId_Fails(string keyId)
    {
        var options = ValidOptions();
        options.SigningKey.KeyId = keyId;

        var result = Validate(options, Environments.Production);

        Assert.True(result.Failed);
    }

    [Fact]
    public void Validate_PreviousKeyIdEqualToCurrentKeyId_Fails()
    {
        var options = ValidOptions();
        options.PreviousPublicKeys[0].KeyId = options.SigningKey.KeyId;

        var result = Validate(options, Environments.Production);

        Assert.True(result.Failed);
    }

    [Fact]
    public void Validate_DuplicatePreviousKeyIds_Fails()
    {
        var options = ValidOptions();
        options.PreviousPublicKeys.Add(new JwtPublicKeyOptions
        {
            KeyId = options.PreviousPublicKeys[0].KeyId,
            PublicKeyPem = JwtTestKeys.CurrentPublicKeyPem,
        });

        var result = Validate(options, Environments.Production);

        Assert.True(result.Failed);
    }

    [Fact]
    public void Validate_PreviousKeyThatIsAPrivateKey_Fails()
    {
        var options = ValidOptions();
        options.PreviousPublicKeys[0].PublicKeyPem = JwtTestKeys.PreviousPrivateKeyPem;

        var result = Validate(options, Environments.Production);

        Assert.True(result.Failed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a pem")]
    public void Validate_PreviousKeyWithInvalidPem_Fails(string? pem)
    {
        var options = ValidOptions();
        options.PreviousPublicKeys[0].PublicKeyPem = pem;

        var result = Validate(options, Environments.Production);

        Assert.True(result.Failed);
    }

    [Fact]
    public void Validate_PreviousKeyWithInvalidKeyId_Fails()
    {
        var options = ValidOptions();
        options.PreviousPublicKeys[0].KeyId = "bad kid";

        var result = Validate(options, Environments.Production);

        Assert.True(result.Failed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("core.example.com")]
    [InlineData("/relative")]
    [InlineData("ftp://core.example.com")]
    [InlineData("https://core.example.com?tenant=1")]
    [InlineData("https://core.example.com#fragment")]
    [InlineData("https://user:secret@core.example.com")]
    [InlineData("https://core.example.com/core")]
    [InlineData("https://core.example.com/core/")]
    public void Validate_InvalidIssuer_Fails(string issuer)
    {
        var options = ValidOptions();
        options.Issuer = issuer;

        var result = Validate(options, Environments.Development);

        Assert.True(result.Failed);
    }

    [Fact]
    public void Validate_HttpIssuerOutsideDevelopment_Fails()
    {
        var options = ValidOptions();
        options.Issuer = "http://core.example.com";

        var result = Validate(options, Environments.Production);

        Assert.True(result.Failed);
    }

    [Fact]
    public void Validate_HttpIssuerInDevelopment_Succeeds()
    {
        var options = ValidOptions();
        options.Issuer = "http://core.example.com";

        var result = Validate(options, Environments.Development);

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData("https://core.example.com/")]
    [InlineData("https://core.example.com:8443")]
    public void Validate_HttpsIssuerWithoutPath_Succeeds(string issuer)
    {
        var options = ValidOptions();
        options.Issuer = issuer;

        var result = Validate(options, Environments.Production);

        Assert.True(result.Succeeded, string.Join(" | ", result.Failures ?? []));
    }

    [Theory]
    [InlineData("http://localhost:8080")]
    [InlineData("http://127.0.0.1:8080")]
    public void Validate_HttpLoopbackIssuerOutsideDevelopment_Succeeds(string issuer)
    {
        var options = ValidOptions();
        options.Issuer = issuer;

        var result = Validate(options, Environments.Production);

        Assert.True(result.Succeeded, string.Join(" | ", result.Failures ?? []));
    }

    [Theory]
    [InlineData(0, 30)]
    [InlineData(61, 30)]
    [InlineData(15, 0)]
    [InlineData(15, 366)]
    public void Validate_LifetimesOutOfRange_Fails(int accessTokenMinutes, int refreshTokenDays)
    {
        var options = ValidOptions();
        options.AccessTokenMinutes = accessTokenMinutes;
        options.RefreshTokenDays = refreshTokenDays;

        var result = Validate(options, Environments.Production);

        Assert.True(result.Failed);
    }

    [Fact]
    public void Validate_FailureMessages_NeverIncludeKeyMaterial()
    {
        var options = ValidOptions();
        options.SigningKey.PrivateKeyPem = JwtTestKeys.CurrentPublicKeyPem;
        options.PreviousPublicKeys[0].PublicKeyPem = JwtTestKeys.PreviousPrivateKeyPem;

        var result = Validate(options, Environments.Production);

        Assert.True(result.Failed);
        var base64Lines = new[] { JwtTestKeys.CurrentPublicKeyPem, JwtTestKeys.PreviousPrivateKeyPem }
            .SelectMany(pem => pem.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(line => !line.StartsWith("-----", StringComparison.Ordinal));
        Assert.All(result.Failures!, failure =>
            Assert.DoesNotContain(base64Lines, line => failure.Contains(line, StringComparison.Ordinal)));
    }

    [Fact]
    public void Defaults_Are15MinutesAnd30Days()
    {
        var options = new JwtOptions();

        Assert.Equal(15, options.AccessTokenMinutes);
        Assert.Equal(30, options.RefreshTokenDays);
    }

    internal static JwtOptions ValidOptions() => new()
    {
        Issuer = "https://core.example.com",
        SigningKey = new JwtSigningKeyOptions
        {
            KeyId = "core-2026-11",
            PrivateKeyPem = JwtTestKeys.CurrentPrivateKeyPem,
        },
        PreviousPublicKeys =
        [
            new JwtPublicKeyOptions
            {
                KeyId = "core-2026-05",
                PublicKeyPem = JwtTestKeys.PreviousPublicKeyPem,
            },
        ],
    };

    private static ValidateOptionsResult Validate(JwtOptions options, string environmentName) =>
        new JwtOptionsValidator(new HostingEnvironment { EnvironmentName = environmentName }).Validate(null, options);
}
