using System.Net;
using System.Security.Claims;
using Href.Core.Api.Modules.Applications;
using Microsoft.AspNetCore.Http;

namespace Href.Core.Tests.Modules.Applications;

public sealed class EndUserIpTests
{
    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7")]
    [InlineData("  203.0.113.7  ", "203.0.113.7")]
    [InlineData("2001:DB8::1", "2001:db8::1")]
    [InlineData("::ffff:203.0.113.7", "203.0.113.7")]
    [InlineData("fe80::1%4", "fe80::1")]
    public void Resolve_ForAnAuthenticatedApplication_UsesAValidHeader(string header, string expected)
    {
        var context = CreateContext(authenticatedApp: true, header);

        Assert.Equal(expected, EndUserIp.Resolve(context));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-ip")]
    [InlineData("10")]
    [InlineData("10.1")]
    [InlineData("203.0.113.7, 198.51.100.1")]
    [InlineData("999.0.0.1")]
    public void Resolve_WithAnInvalidHeader_UsesTheConnectionAddress(string header)
    {
        var context = CreateContext(authenticatedApp: true, header);

        Assert.Equal("192.0.2.10", EndUserIp.Resolve(context));
    }

    [Fact]
    public void Resolve_WithSeveralHeaderValues_UsesTheConnectionAddress()
    {
        var context = CreateContext(authenticatedApp: true, header: null);
        context.Request.Headers.Append(EndUserIp.HeaderName, "203.0.113.7");
        context.Request.Headers.Append(EndUserIp.HeaderName, "198.51.100.1");

        Assert.Equal("192.0.2.10", EndUserIp.Resolve(context));
    }

    [Fact]
    public void Resolve_WithoutAnAuthenticatedApplication_IgnoresTheHeader()
    {
        var context = CreateContext(authenticatedApp: false, "203.0.113.7");

        Assert.Equal("192.0.2.10", EndUserIp.Resolve(context));
    }

    [Fact]
    public void Resolve_ForAnAdminBearerPrincipalWithAnAppKeyClaim_IgnoresTheHeader()
    {
        var context = CreateContext(authenticatedApp: false, "203.0.113.7");
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(AppKeyAuthentication.AppKeyClaimType, "core"), new Claim("sub", Guid.NewGuid().ToString())],
            "AuthenticationTypes.Federation"));

        Assert.Equal("192.0.2.10", EndUserIp.Resolve(context));
    }

    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7")]
    [InlineData("2001:db8:1:2:3:4:5:6", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:2::ffff", "2001:db8:1:2::/64")]
    [InlineData(null, null)]
    public void ToRateLimitKey_ReducesIpv6ToItsSlash64(string? address, string? expected)
    {
        Assert.Equal(expected, EndUserIp.ToRateLimitKey(address));
    }

    [Fact]
    public void Resolve_WithoutHeaderOrConnectionAddress_ReturnsNull()
    {
        var context = CreateContext(authenticatedApp: true, header: null);
        context.Connection.RemoteIpAddress = null;

        Assert.Null(EndUserIp.Resolve(context));
    }

    [Fact]
    public void Resolve_WithAMappedConnectionAddress_ReturnsTheIpv4Form()
    {
        var context = CreateContext(authenticatedApp: false, header: null);
        context.Connection.RemoteIpAddress = IPAddress.Parse("::ffff:192.0.2.10");

        Assert.Equal("192.0.2.10", EndUserIp.Resolve(context));
    }

    private static DefaultHttpContext CreateContext(bool authenticatedApp, string? header)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.10");

        if (authenticatedApp)
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(AppKeyAuthentication.AppKeyClaimType, "crm"), new Claim(AppKeyAuthentication.AppIdClaimType, Guid.NewGuid().ToString())],
                AppKeyAuthentication.SchemeName));
        }

        if (header is not null)
        {
            context.Request.Headers[EndUserIp.HeaderName] = header;
        }

        return context;
    }
}
