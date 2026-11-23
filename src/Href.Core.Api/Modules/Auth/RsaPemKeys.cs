using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Href.Core.Api.Modules.Auth;

/// <summary>
/// Parsing and format rules for the RSA keys and key ids of <see cref="JwtOptions"/>.
/// </summary>
internal static partial class RsaPemKeys
{
    public const int MinimumKeySizeInBits = 2048;

    private static readonly string[] PrivateKeyLabels = ["PRIVATE KEY", "RSA PRIVATE KEY"];
    private static readonly string[] PublicKeyLabels = ["PUBLIC KEY", "RSA PUBLIC KEY"];

    public static bool IsValidKeyId(string? keyId) => keyId is not null && KeyIdPattern().IsMatch(keyId);

    /// <summary>
    /// Imports a single RSA key from PEM. Returns <c>null</c> and an error (never containing key material) when the text is not
    /// exactly one PEM block of the expected kind, cannot be imported, or is shorter than <see cref="MinimumKeySizeInBits"/>.
    /// </summary>
    public static RSA? TryImport(string? pem, bool expectPrivateKey, out string? error)
    {
        var expectedLabels = expectPrivateKey ? PrivateKeyLabels : PublicKeyLabels;
        var kind = expectPrivateKey ? "an RSA private key" : "an RSA public key";

        if (string.IsNullOrWhiteSpace(pem) || !PemEncoding.TryFind(pem, out var fields))
        {
            error = $"must be {kind} in PEM format.";
            return null;
        }

        if (Array.IndexOf(expectedLabels, pem[fields.Label]) < 0)
        {
            error = $"must be {kind} in PEM format ({string.Join(" or ", expectedLabels.Select(label => $"'{label}'"))}).";
            return null;
        }

        var rsa = RSA.Create();

        try
        {
            rsa.ImportFromPem(pem);
        }
        catch (Exception exception) when (exception is ArgumentException or CryptographicException)
        {
            rsa.Dispose();
            error = $"must be {kind} in PEM format; it could not be imported.";
            return null;
        }

        if (rsa.KeySize < MinimumKeySizeInBits)
        {
            rsa.Dispose();
            error = $"must be at least {MinimumKeySizeInBits} bits (3072 recommended).";
            return null;
        }

        error = null;
        return rsa;
    }

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex KeyIdPattern();
}
