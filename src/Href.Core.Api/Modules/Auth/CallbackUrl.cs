namespace Href.Core.Api.Modules.Auth;

/// <summary>
/// Rules for the page URLs a satellite sends for account emails (<c>verifyEmailUrl</c>, <c>resetPasswordUrl</c>). The Core
/// appends <c>token=...</c> and puts the result in the <c>href</c> of an email signed with its own sender address, so the URL
/// must be an absolute <c>https</c> URL (<c>http</c> only for a loopback host), without user info or fragment, on a single
/// line, of at most <see cref="MaxLength"/> characters, and without its own <c>token</c> query parameter.
/// </summary>
public static class CallbackUrl
{
    public const int MaxLength = 2048;
    public const string TokenParameterName = "token";

    public static bool TryParse(string? value, out Uri uri)
    {
        uri = null!;

        if (string.IsNullOrWhiteSpace(value)
            || value.Length > MaxLength
            || value.Any(char.IsControl)
            || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var parsed))
        {
            return false;
        }

        var allowedScheme = parsed.Scheme == Uri.UriSchemeHttps
            || (parsed.Scheme == Uri.UriSchemeHttp && parsed.IsLoopback);

        if (!allowedScheme
            || parsed.UserInfo.Length > 0
            || parsed.Fragment.Length > 0
            || string.IsNullOrEmpty(parsed.Host)
            || parsed.AbsoluteUri.Length > MaxLength
            || HasTokenParameter(parsed.Query))
        {
            return false;
        }

        uri = parsed;
        return true;
    }

    /// <summary>
    /// Returns the URL with <c>token=&lt;plain token&gt;</c> appended to its query.
    /// </summary>
    public static string WithToken(Uri uri, string plainToken)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentException.ThrowIfNullOrEmpty(plainToken);

        var separator = uri.Query.Length > 1 ? "&" : (uri.Query.Length == 1 ? string.Empty : "?");
        return $"{uri.AbsoluteUri}{separator}{TokenParameterName}={Uri.EscapeDataString(plainToken)}";
    }

    private static bool HasTokenParameter(string query) =>
        query.TrimStart('?')
            .Split(new[] { '&', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => Uri.UnescapeDataString(pair.Split('=', 2)[0]))
            .Any(name => string.Equals(name, TokenParameterName, StringComparison.OrdinalIgnoreCase));
}
