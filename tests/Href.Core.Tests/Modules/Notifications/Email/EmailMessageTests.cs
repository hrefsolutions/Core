using System.Net;
using System.Net.Sockets;
using Href.Core.Api.Modules.Notifications.Email;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Href.Core.Tests.Modules.Notifications.Email;

public sealed class EmailMessageTests
{
    private static readonly EmailMessage Valid = new("user@example.test", "Ana", "Hola", "<p>Hola</p>", "Hola");

    [Fact]
    public void Validate_WithValidMessage_DoesNotThrow() => Valid.Validate();

    [Theory]
    [InlineData("Hola\r\nBcc: x@y.z")]
    [InlineData("Hola\nBcc: x@y.z")]
    [InlineData("Hola\U00002028")]
    [InlineData("")]
    public void Validate_WithBadSubject_Throws(string subject) =>
        Assert.Throws<ArgumentException>(() => (Valid with { Subject = subject }).Validate());

    [Theory]
    [InlineData("")]
    [InlineData("Name <a@b.c>")]
    [InlineData("a@b.c,c@d.e")]
    public void Validate_WithBadRecipient_Throws(string toEmail) =>
        Assert.Throws<ArgumentException>(() => (Valid with { ToEmail = toEmail }).Validate());

    [Fact]
    public void Validate_WithEmptyHtml_Throws() =>
        Assert.Throws<ArgumentException>(() => (Valid with { HtmlBody = " " }).Validate());

    [Fact]
    public async Task SendAsync_WithNullMessage_ThrowsArgumentNullException()
    {
        var sender = new SmtpEmailSender(Options.Create(new SmtpOptions()), NullLogger<SmtpEmailSender>.Instance);

        await Assert.ThrowsAsync<ArgumentNullException>(() => sender.SendAsync(null!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SendAsync_WithSubjectContainingLineBreak_ThrowsBeforeConnecting()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var unboundPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var sender = new SmtpEmailSender(
            Options.Create(new SmtpOptions
            {
                Host = "localhost",
                Port = unboundPort,
                SecureSocket = SmtpSecureSocket.None,
                FromEmail = "href.notifier@gmail.com",
                FromName = "Href",
            }),
            NullLogger<SmtpEmailSender>.Instance);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            sender.SendAsync(Valid with { Subject = "a\r\nb" }, TestContext.Current.CancellationToken));

        Assert.Equal("Subject", exception.ParamName);
    }
}
