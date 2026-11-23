using System.Security.Cryptography;

namespace Href.Core.Tests.Modules.Auth;

/// <summary>
/// RSA key material for JWT signing tests, generated once per test run.
/// </summary>
public static class JwtTestKeys
{
    private static readonly Lazy<(string PrivatePem, string PublicPem)> CurrentPair = new(() => CreatePair(2048));
    private static readonly Lazy<(string PrivatePem, string PublicPem)> PreviousPair = new(() => CreatePair(2048));

    public static string CurrentPrivateKeyPem => CurrentPair.Value.PrivatePem;

    public static string CurrentPublicKeyPem => CurrentPair.Value.PublicPem;

    public static string PreviousPrivateKeyPem => PreviousPair.Value.PrivatePem;

    public static string PreviousPublicKeyPem => PreviousPair.Value.PublicPem;

    public static (string PrivatePem, string PublicPem) CreatePair(int keySizeInBits)
    {
        using var rsa = RSA.Create(keySizeInBits);
        return (rsa.ExportPkcs8PrivateKeyPem(), rsa.ExportSubjectPublicKeyInfoPem());
    }
}
