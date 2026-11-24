using Href.Core.Api.Modules.Auth;

namespace Href.Core.Tests.Modules.Auth;

public sealed class CallbackUrlTests
{
    [Theory]
    [InlineData("https://app.example.com/verify")]
    [InlineData("https://app.example.com:8443/account/verify?lang=es")]
    [InlineData("  https://app.example.com/verify  ")]
    [InlineData("http://localhost:3000/verify")]
    [InlineData("http://127.0.0.1/verify")]
    public void TryParse_AcceptsHttpsAndLoopbackHttp(string url)
    {
        Assert.True(CallbackUrl.TryParse(url, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/verify")]
    [InlineData("app.example.com/verify")]
    [InlineData("http://app.example.com/verify")]
    [InlineData("ftp://app.example.com/verify")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://user:secret@app.example.com/verify")]
    [InlineData("https://app.example.com/verify#section")]
    [InlineData("https://app.example.com/verify?token=abc")]
    [InlineData("https://app.example.com/verify?x=1&TOKEN=abc")]
    [InlineData("https://app.example.com/verify?%74oken=abc")]
    [InlineData("https://app.example.com/verify?a=1;token=abc")]
    [InlineData("https://app.example.com/verify\nX-Injected: 1")]
    public void TryParse_RejectsUnsafeOrAmbiguousUrls(string? url)
    {
        Assert.False(CallbackUrl.TryParse(url, out _));
    }

    [Fact]
    public void TryParse_RejectsUrlsLongerThan2048Characters()
    {
        var url = "https://app.example.com/" + new string('a', CallbackUrl.MaxLength);

        Assert.False(CallbackUrl.TryParse(url, out _));
    }

    [Fact]
    public void TryParse_RejectsUrlsWhoseEscapedFormIsLongerThan2048Characters()
    {
        var url = "https://app.example.com/" + new string('ñ', 500);

        Assert.True(url.Length <= CallbackUrl.MaxLength);
        Assert.False(CallbackUrl.TryParse(url, out _));
    }

    [Theory]
    [InlineData("https://app.example.com/verify", "https://app.example.com/verify?token=abc_-1")]
    [InlineData("https://app.example.com/verify?lang=es", "https://app.example.com/verify?lang=es&token=abc_-1")]
    [InlineData("https://app.example.com/verify?", "https://app.example.com/verify?token=abc_-1")]
    public void WithToken_AppendsTheTokenParameter(string url, string expected)
    {
        Assert.True(CallbackUrl.TryParse(url, out var uri));

        Assert.Equal(expected, CallbackUrl.WithToken(uri, "abc_-1"));
    }
}
