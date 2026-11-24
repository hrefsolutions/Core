using System.Net;
using System.Net.Sockets;

namespace Href.Core.Api.Modules.Applications;

/// <summary>
/// IP address of the end user behind a request. Satellite backends call the Core on behalf of their users, so the
/// connection address is the satellite's; they forward the user's address in <see cref="HeaderName"/>. The header is used
/// only when the request is authenticated with M2M credentials (the <c>AppKey</c> scheme, not an admin bearer token) and
/// holds exactly one valid IPv4 (dotted quad) or IPv6 address; otherwise the connection address is used (the real caller behind the Railway proxy, see
/// <c>UseForwardedHeaders</c>). IPv4-mapped IPv6 addresses are returned in IPv4 form and IPv6 scope ids are dropped.
/// Any authenticated satellite can set the header, so it identifies end users for limits and audit, not for trust.
/// </summary>
public static class EndUserIp
{
    public const string HeaderName = "X-End-User-Ip";

    public static string? Resolve(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (IsApplicationRequest(context)
            && context.Request.Headers.TryGetValue(HeaderName, out var values)
            && values.Count == 1
            && TryParse(values[0], out var forwarded))
        {
            return Format(forwarded);
        }

        return context.Connection.RemoteIpAddress is { } remote ? Format(remote) : null;
    }

    /// <summary>
    /// Rate limit key for an address from <see cref="Resolve"/>: an IPv6 address is reduced to its /64 network, because one
    /// end user usually controls a whole /64 and could otherwise get a new budget per address. IPv4 addresses and values
    /// that are not addresses are returned as they are.
    /// </summary>
    public static string? ToRateLimitKey(string? address)
    {
        if (address is null || !IPAddress.TryParse(address, out var parsed) || parsed.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address;
        }

        var bytes = parsed.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return $"{new IPAddress(bytes)}/64";
    }

    /// <summary>
    /// True only for a principal of the M2M <c>AppKey</c> scheme. Admin bearer principals also carry an <c>app_key</c>
    /// claim (<c>core</c>), so the claim alone is not enough.
    /// </summary>
    private static bool IsApplicationRequest(HttpContext context) =>
        context.User.Identities.Any(identity =>
            identity.IsAuthenticated
            && string.Equals(identity.AuthenticationType, AppKeyAuthentication.SchemeName, StringComparison.Ordinal));

    private static bool TryParse(string? value, out IPAddress address)
    {
        address = IPAddress.None;
        var trimmed = value?.Trim();

        if (string.IsNullOrEmpty(trimmed) || !IPAddress.TryParse(trimmed, out var parsed))
        {
            return false;
        }

        if (parsed.AddressFamily == AddressFamily.InterNetwork && trimmed.Count(character => character == '.') != 3)
        {
            return false;
        }

        address = parsed;
        return true;
    }

    private static string Format(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            return address.MapToIPv4().ToString();
        }

        return address.AddressFamily == AddressFamily.InterNetworkV6
            ? new IPAddress(address.GetAddressBytes()).ToString()
            : address.ToString();
    }
}
