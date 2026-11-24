using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Href.Core.Api.Modules.Auth;

[ApiController]
[AllowAnonymous]
[Route(".well-known")]
[ResponseCache(Duration = 3600, Location = ResponseCacheLocation.Any)]
public sealed class WellKnownController(JwtKeyProvider keyProvider, IOptions<JwtOptions> options) : ControllerBase
{
    public const string JwksPath = "/.well-known/jwks.json";

    /// <summary>
    /// Returns the public signing keys (current first, then previous) so satellite apps can validate RS256 tokens offline.
    /// </summary>
    /// <response code="200">The JWK set `{ keys: [{ kty, use, alg, kid, n, e }] }` with public parameters only, cacheable for one hour.</response>
    [HttpGet("jwks.json")]
    [ProducesResponseType<JsonWebKeySetResponse>(StatusCodes.Status200OK)]
    public ActionResult<JsonWebKeySetResponse> GetJsonWebKeySet() => keyProvider.JsonWebKeySet;

    /// <summary>
    /// Returns the minimal OpenID Provider metadata: <c>issuer</c> and <c>jwks_uri</c>.
    /// </summary>
    /// <response code="200">The metadata `{ issuer, jwks_uri }`, cacheable for one hour.</response>
    [HttpGet("openid-configuration")]
    [ProducesResponseType<OpenIdConfigurationResponse>(StatusCodes.Status200OK)]
    public ActionResult<OpenIdConfigurationResponse> GetOpenIdConfiguration()
    {
        var issuer = options.Value.Issuer;
        return new OpenIdConfigurationResponse(issuer, issuer + JwksPath);
    }
}
