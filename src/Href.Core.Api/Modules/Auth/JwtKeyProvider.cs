using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Href.Core.Api.Modules.Auth;

/// <summary>
/// Holds the RSA keys of <see cref="JwtOptions"/>: the RS256 <see cref="SigningCredentials"/> (with <c>kid</c>) of the current key
/// and the public JWK set (current key first, then the previous keys). In Development without a configured key it generates an
/// ephemeral 3072-bit key, so tokens stop validating after a restart. The signing key uses its own
/// <see cref="CryptoProviderFactory"/> without the signature provider cache: the default factory caches providers in a static
/// cache keyed by the key thumbprint, so another provider built from the same PEM could get a provider holding a disposed key.
/// </summary>
public sealed class JwtKeyProvider : IDisposable
{
    public const int EphemeralKeySizeInBits = 3072;
    public const string EphemeralKeyIdPrefix = "ephemeral-";

    private readonly RSA _signingRsa;

    public JwtKeyProvider(IOptions<JwtOptions> options, IHostEnvironment environment)
    {
        var jwt = options.Value;
        var previousKeys = new List<PublicJsonWebKey>();

        foreach (var previous in jwt.PreviousPublicKeys)
        {
            using var rsa = Import(previous.PublicKeyPem, expectPrivateKey: false);
            previousKeys.Add(ToPublicJsonWebKey(previous.KeyId!, rsa));
        }

        RSA signingRsa;

        if (string.IsNullOrWhiteSpace(jwt.SigningKey.PrivateKeyPem))
        {
            if (!environment.IsDevelopment())
            {
                throw new InvalidOperationException("Jwt:SigningKey:PrivateKeyPem is required outside Development.");
            }

            signingRsa = RSA.Create(EphemeralKeySizeInBits);
            CurrentKeyId = EphemeralKeyIdPrefix + Guid.CreateVersion7().ToString("N");
            IsEphemeral = true;
        }
        else
        {
            signingRsa = Import(jwt.SigningKey.PrivateKeyPem, expectPrivateKey: true);
            CurrentKeyId = jwt.SigningKey.KeyId!;
        }

        _signingRsa = signingRsa;
        var cryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false };
        SigningCredentials = new SigningCredentials(
            new RsaSecurityKey(signingRsa) { KeyId = CurrentKeyId, CryptoProviderFactory = cryptoProviderFactory },
            SecurityAlgorithms.RsaSha256)
        {
            CryptoProviderFactory = cryptoProviderFactory,
        };

        JsonWebKeySet = new JsonWebKeySetResponse([ToPublicJsonWebKey(CurrentKeyId, signingRsa), .. previousKeys]);
        ValidationKeys = JsonWebKeySet.Keys
            .Select(key => (SecurityKey)new JsonWebKey
            {
                Kty = key.Kty,
                Use = key.Use,
                Alg = key.Alg,
                Kid = key.Kid,
                N = key.N,
                E = key.E,
            })
            .ToList();
    }

    /// <summary>
    /// The published public keys (current first, then previous) as security keys, to validate the Core's own tokens.
    /// </summary>
    public IReadOnlyList<SecurityKey> ValidationKeys { get; }

    public string CurrentKeyId { get; }

    public bool IsEphemeral { get; }

    public SigningCredentials SigningCredentials { get; }

    public JsonWebKeySetResponse JsonWebKeySet { get; }

    public void Dispose() => _signingRsa.Dispose();

    private static RSA Import(string? pem, bool expectPrivateKey) =>
        RsaPemKeys.TryImport(pem, expectPrivateKey, out var error)
        ?? throw new InvalidOperationException($"Invalid JWT key: the PEM {error}");

    private static PublicJsonWebKey ToPublicJsonWebKey(string keyId, RSA rsa)
    {
        var parameters = rsa.ExportParameters(includePrivateParameters: false);

        return new PublicJsonWebKey(
            Kty: "RSA",
            Use: "sig",
            Alg: SecurityAlgorithms.RsaSha256,
            Kid: keyId,
            N: Base64UrlEncoder.Encode(parameters.Modulus!),
            E: Base64UrlEncoder.Encode(parameters.Exponent!));
    }
}
