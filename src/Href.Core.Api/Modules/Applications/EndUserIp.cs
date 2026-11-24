using System.Net;
using System.Net.Sockets;

namespace Href.Core.Api.Modules.Applications;

/// <summary>
/// IP address of the end user behind a request. Satellite backends call the Core on behalf of their users, so the
/// connection address is the satellite's; they forward the user's address in <see cref="HeaderName"/>. The header is used
/// only when the request is authenticated with M2M credentials and holds exactly one valid IPv4 (dotted quad) or IPv6
/// address; otherwise the connection address is used (the real caller behind the Railway proxy, see
/// <c>UseForwardedHeaders</c>). IPv4-mapped IPv6 addresses are returned in IPv4 form and IPv6 scope ids are dropped.
/// Any authenticated satellite can set the header, so it identifies end users for limits and audit, not for trust.
/// </summary>
public static class EndUserIp
{
    public const string HeaderName = "X-End-User-Ip";

    public static string? Resolve(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!string.IsNullOrEmpty(context.User.GetAppKey())
            && context.Request.Headers.TryGetValue(HeaderName, out var values)
            && values.Count == 1
            && TryParse(values[0], out var forwarded))
        {
            return Format(forwarded);
        }

        return context.Connection.RemoteIpAddress is { } remote ? Format(remote) : null;
    }

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
