using Microsoft.Extensions.Options;

namespace Href.Core.Api.Modules.Auth;

/// <summary>
/// Validates <see cref="JwtOptions"/> on startup: issuer URL, signing and previous keys (importable PEM of the right kind,
/// at least 2048 bits, unique well-formed key ids) and token lifetimes. Outside Development a signing key is required.
/// </summary>
public sealed class JwtOptionsValidator(IHostEnvironment environment) : IValidateOptions<JwtOptions>
{
    public const int MaxAccessTokenMinutes = 60;
    public const int MaxRefreshTokenDays = 365;

    public ValidateOptionsResult Validate(string? name, JwtOptions options)
    {
        var failures = new List<string>();

        ValidateIssuer(options.Issuer, failures);
        var keyIds = ValidateSigningKey(options.SigningKey, failures);
        keyIds.AddRange(ValidatePreviousKeys(options.PreviousPublicKeys, failures));

        var duplicates = keyIds
            .GroupBy(keyId => keyId, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        if (duplicates.Count > 0)
        {
            failures.Add($"Jwt key ids must be unique across Jwt:SigningKey and Jwt:PreviousPublicKeys; repeated: {string.Join(", ", duplicates)}.");
        }

        if (options.AccessTokenMinutes is < 1 or > MaxAccessTokenMinutes)
        {
            failures.Add($"Jwt:AccessTokenMinutes must be between 1 and {MaxAccessTokenMinutes}.");
        }

        if (options.RefreshTokenDays is < 1 or > MaxRefreshTokenDays)
        {
            failures.Add($"Jwt:RefreshTokenDays must be between 1 and {MaxRefreshTokenDays}.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private void ValidateIssuer(string? issuer, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(issuer)
            || !Uri.TryCreate(issuer.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || uri.Query.Length > 0
            || uri.Fragment.Length > 0
            || uri.UserInfo.Length > 0)
        {
            failures.Add("Jwt:Issuer must be the absolute http(s) base URL of the Core, without query, fragment or user info.");
            return;
        }

        if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback && !environment.IsDevelopment())
        {
            failures.Add("Jwt:Issuer must use https outside Development (http is only allowed for loopback hosts).");
        }
    }

    private List<string> ValidateSigningKey(JwtSigningKeyOptions signingKey, List<string> failures)
    {
        var keyIds = new List<string>();
        var hasKeyId = !string.IsNullOrWhiteSpace(signingKey.KeyId);
        var hasPem = !string.IsNullOrWhiteSpace(signingKey.PrivateKeyPem);

        if (!hasKeyId && !hasPem)
        {
            if (!environment.IsDevelopment())
            {
                failures.Add("Jwt:SigningKey:PrivateKeyPem and Jwt:SigningKey:KeyId are required outside Development.");
            }

            return keyIds;
        }

        if (!hasKeyId || !hasPem)
        {
            failures.Add("Jwt:SigningKey:KeyId and Jwt:SigningKey:PrivateKeyPem must both be set or both be empty.");
            return keyIds;
        }

        if (RsaPemKeys.IsValidKeyId(signingKey.KeyId))
        {
            keyIds.Add(signingKey.KeyId!);
        }
        else
        {
            failures.Add("Jwt:SigningKey:KeyId must be 1 to 64 characters among A-Z, a-z, 0-9, '.', '_' and '-'.");
        }

        using var rsa = RsaPemKeys.TryImport(signingKey.PrivateKeyPem, expectPrivateKey: true, out var error);

        if (rsa is null)
        {
            failures.Add($"Jwt:SigningKey:PrivateKeyPem {error}");
        }

        return keyIds;
    }

    private static List<string> ValidatePreviousKeys(List<JwtPublicKeyOptions> previousKeys, List<string> failures)
    {
        var keyIds = new List<string>();

        for (var index = 0; index < previousKeys.Count; index++)
        {
            var previous = previousKeys[index];

            if (RsaPemKeys.IsValidKeyId(previous.KeyId))
            {
                keyIds.Add(previous.KeyId!);
            }
            else
            {
                failures.Add($"Jwt:PreviousPublicKeys:{index}:KeyId must be 1 to 64 characters among A-Z, a-z, 0-9, '.', '_' and '-'.");
            }

            using var rsa = RsaPemKeys.TryImport(previous.PublicKeyPem, expectPrivateKey: false, out var error);

            if (rsa is null)
            {
                failures.Add($"Jwt:PreviousPublicKeys:{index}:PublicKeyPem {error}");
            }
        }

        return keyIds;
    }
}
