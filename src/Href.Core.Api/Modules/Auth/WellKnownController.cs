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
    [HttpGet("jwks.json")]
    public ActionResult<JsonWebKeySetResponse> GetJsonWebKeySet() => keyProvider.JsonWebKeySet;

    /// <summary>
    /// Returns the minimal OpenID Provider metadata: <c>issuer</c> and <c>jwks_uri</c>.
    /// </summary>
    [HttpGet("openid-configuration")]
    public ActionResult<OpenIdConfigurationResponse> GetOpenIdConfiguration()
    {
        var issuer = options.Value.Issuer;
        return new OpenIdConfigurationResponse(issuer, issuer + JwksPath);
    }
}
