using Href.Core.Api.Modules.Notifications.Email;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Href.Core.Tests.Modules.Notifications.Email;

public sealed class SmtpOptionsValidationTests
{
    private static Dictionary<string, string?> GmailConfig() => new()
    {
        ["Smtp:Host"] = "smtp.gmail.com",
        ["Smtp:Port"] = "465",
        ["Smtp:SecureSocket"] = "SslOnConnect",
        ["Smtp:Username"] = "href.notifier@gmail.com",
        ["Smtp:Password"] = "app password placeholder",
        ["Smtp:FromEmail"] = "href.notifier@gmail.com",
        ["Smtp:FromName"] = "Href",
    };

    private static Dictionary<string, string?> MailpitConfig() => new()
    {
        ["Smtp:Host"] = "localhost",
        ["Smtp:Port"] = "1025",
        ["Smtp:SecureSocket"] = "None",
        ["Smtp:FromEmail"] = "href.notifier@gmail.com",
        ["Smtp:FromName"] = "Href",
    };

    private static SmtpOptions Resolve(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddSmtpEmail(configuration);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<SmtpOptions>>().Value;
    }

    private static void AssertInvalid(Dictionary<string, string?> values) =>
        Assert.Throws<OptionsValidationException>(() => Resolve(values));

    [Fact]
    public void GmailConfig_IsValid()
    {
        var options = Resolve(GmailConfig());

        Assert.Equal("smtp.gmail.com", options.Host);
        Assert.Equal(465, options.Port);
        Assert.Equal(SmtpSecureSocket.SslOnConnect, options.SecureSocket);
        Assert.Equal(30, options.TimeoutSeconds);
    }

    [Fact]
    public void MailpitConfig_IsValid()
    {
        var options = Resolve(MailpitConfig());

        Assert.Equal(SmtpSecureSocket.None, options.SecureSocket);
        Assert.Null(options.Username);
    }

    [Fact]
    public void StartTlsWithCredentials_IsValid()
    {
        var values = GmailConfig();
        values["Smtp:Port"] = "587";
        values["Smtp:SecureSocket"] = "StartTls";

        Assert.Equal(SmtpSecureSocket.StartTls, Resolve(values).SecureSocket);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingHost_IsInvalid(string host)
    {
        var values = MailpitConfig();
        values["Smtp:Host"] = host;

        AssertInvalid(values);
    }

    [Fact]
    public void AbsentHost_IsInvalid()
    {
        var values = MailpitConfig();
        values.Remove("Smtp:Host");

        AssertInvalid(values);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingFromEmail_IsInvalid(string fromEmail)
    {
        var values = MailpitConfig();
        values["Smtp:FromEmail"] = fromEmail;

        AssertInvalid(values);
    }

    [Fact]
    public void AbsentFromEmail_IsInvalid()
    {
        var values = MailpitConfig();
        values.Remove("Smtp:FromEmail");

        AssertInvalid(values);
    }

    [Theory]
    [InlineData("Name <a@b.c>")]
    [InlineData("a@b.c,c@d.e")]
    [InlineData("not-an-email")]
    [InlineData("a@b.c\r\nBcc: x@y.z")]
    public void InvalidFromEmail_IsInvalid(string fromEmail)
    {
        var values = MailpitConfig();
        values["Smtp:FromEmail"] = fromEmail;

        AssertInvalid(values);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Href\r\nBcc: x@y.z")]
    public void MissingOrInvalidFromName_IsInvalid(string fromName)
    {
        var values = MailpitConfig();
        values["Smtp:FromName"] = fromName;

        AssertInvalid(values);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("65536")]
    [InlineData("70000")]
    public void PortOutOfRange_IsInvalid(string port)
    {
        var values = MailpitConfig();
        values["Smtp:Port"] = port;

        AssertInvalid(values);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("301")]
    public void TimeoutOutOfRange_IsInvalid(string timeoutSeconds)
    {
        var values = MailpitConfig();
        values["Smtp:TimeoutSeconds"] = timeoutSeconds;

        AssertInvalid(values);
    }

    [Theory]
    [InlineData("5")]
    [InlineData("-1")]
    [InlineData("99")]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("None,StartTls")]
    [InlineData("starttls")]
    public void NonExactSecureSocket_IsInvalid(string secureSocket)
    {
        var values = MailpitConfig();
        values["Smtp:SecureSocket"] = secureSocket;

        AssertInvalid(values);
    }

    [Fact]
    public void UnknownSecureSocketName_IsInvalid()
    {
        var values = MailpitConfig();
        values["Smtp:SecureSocket"] = "Tls13";

        Assert.ThrowsAny<Exception>(() => Resolve(values));
    }

    [Fact]
    public void PasswordWithoutUsername_IsInvalid()
    {
        var values = GmailConfig();
        values.Remove("Smtp:Username");

        AssertInvalid(values);
    }

    [Fact]
    public void UsernameWithoutPassword_IsInvalid()
    {
        var values = GmailConfig();
        values.Remove("Smtp:Password");

        AssertInvalid(values);
    }

    [Fact]
    public void CredentialsWithNoSecureSocket_IsInvalid()
    {
        var values = GmailConfig();
        values["Smtp:SecureSocket"] = "None";

        AssertInvalid(values);
    }

    [Fact]
    public void ValidationFailureMessage_DoesNotContainThePassword()
    {
        var values = GmailConfig();
        values["Smtp:Password"] = "super-secret-value";
        values["Smtp:SecureSocket"] = "None";

        var exception = Assert.Throws<OptionsValidationException>(() => Resolve(values));

        Assert.DoesNotContain("super-secret-value", exception.Message);
    }
}
