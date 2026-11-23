using System.Text.Json.Serialization;

namespace Href.Core.Api.Modules.Auth;

/// <summary>
/// Public RSA signing key in JWK format (RFC 7517), without private parameters.
/// </summary>
public sealed record PublicJsonWebKey(string Kty, string Use, string Alg, string Kid, string N, string E);

/// <summary>
/// JWK set published at <c>GET /.well-known/jwks.json</c>.
/// </summary>
public sealed record JsonWebKeySetResponse(IReadOnlyList<PublicJsonWebKey> Keys);

/// <summary>
/// Minimal OpenID Provider metadata published at <c>GET /.well-known/openid-configuration</c>, enough for
/// <c>AddJwtBearer(options => options.Authority = issuer)</c> to discover the signing keys.
/// </summary>
public sealed record OpenIdConfigurationResponse(
    [property: JsonPropertyName("issuer")] string Issuer,
    [property: JsonPropertyName("jwks_uri")] string JwksUri);
