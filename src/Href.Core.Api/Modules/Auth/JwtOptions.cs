namespace Href.Core.Api.Modules.Auth;

/// <summary>
/// JWT issuance settings bound from the <c>Jwt</c> configuration section and validated on startup by <see cref="JwtOptionsValidator"/>.
/// </summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    /// <summary>
    /// Public base URL of the Core, used as the <c>iss</c> claim and as the base of the <c>jwks_uri</c>. A trailing slash is removed.
    /// </summary>
    public string Issuer { get; set; } = string.Empty;

    public JwtSigningKeyOptions SigningKey { get; set; } = new();

    /// <summary>
    /// Public keys of retired signing keys, still published so that tokens signed before a rotation keep validating.
    /// </summary>
    public List<JwtPublicKeyOptions> PreviousPublicKeys { get; set; } = [];

    public int AccessTokenMinutes { get; set; } = 15;

    public int RefreshTokenDays { get; set; } = 30;
}

/// <summary>
/// Current RSA signing key: its <c>kid</c> and its private key in PEM (PKCS#8 <c>PRIVATE KEY</c> or PKCS#1 <c>RSA PRIVATE KEY</c>).
/// </summary>
public sealed class JwtSigningKeyOptions
{
    public string? KeyId { get; set; }

    public string? PrivateKeyPem { get; set; }
}

/// <summary>
/// Retired RSA key: its <c>kid</c> and its public key in PEM (<c>PUBLIC KEY</c> or <c>RSA PUBLIC KEY</c>).
/// </summary>
public sealed class JwtPublicKeyOptions
{
    public string? KeyId { get; set; }

    public string? PublicKeyPem { get; set; }
}
