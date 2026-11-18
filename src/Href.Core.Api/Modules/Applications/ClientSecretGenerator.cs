using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Href.Core.Api.Modules.Applications;

/// <summary>
/// Generates 32-byte cryptographically random client secrets (encoded as 43-character base64url strings)
/// and hashes or verifies them with SHA-256 in constant time.
/// </summary>
public static class ClientSecretGenerator
{
    private const int SecretByteLength = 32;
    private const int Sha256HexLength = 64;

    public static string Generate()
    {
        Span<byte> bytes = stackalloc byte[SecretByteLength];
        RandomNumberGenerator.Fill(bytes);
        return Base64Url.EncodeToString(bytes);
    }

    public static string Hash(string plainSecret)
    {
        ArgumentException.ThrowIfNullOrEmpty(plainSecret);
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(plainSecret));
        return Convert.ToHexStringLower(hashBytes);
    }

    public static bool Verify(string? plainSecret, string? expectedHashHex)
    {
        if (string.IsNullOrEmpty(plainSecret)
            || string.IsNullOrEmpty(expectedHashHex)
            || expectedHashHex.Length != Sha256HexLength)
        {
            return false;
        }

        var actualHashHex = Hash(plainSecret);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(actualHashHex),
            Encoding.ASCII.GetBytes(expectedHashHex));
    }
}
