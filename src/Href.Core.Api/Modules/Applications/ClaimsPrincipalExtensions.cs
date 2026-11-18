using System.Security.Claims;

namespace Href.Core.Api.Modules.Applications;

public static class ClaimsPrincipalExtensions
{
    public static string? GetAppKey(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return principal.FindFirstValue(AppKeyAuthentication.AppKeyClaimType);
    }

    public static Guid? GetAppId(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var raw = principal.FindFirstValue(AppKeyAuthentication.AppIdClaimType);
        return Guid.TryParse(raw, out var id) ? id : null;
    }
}
