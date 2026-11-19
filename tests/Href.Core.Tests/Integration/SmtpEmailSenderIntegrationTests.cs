using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Href.Core.Api.Modules.Notifications.Email;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Href.Core.Tests.Integration;

[Collection(MailpitCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SmtpEmailSenderIntegrationTests(MailpitFixture mailpit)
{
    private SmtpEmailSender CreateSender(string host, int port, int timeoutSeconds = 30) =>
        new(
            Options.Create(new SmtpOptions
            {
                Host = host,
                Port = port,
                SecureSocket = SmtpSecureSocket.None,
                FromEmail = "href.notifier@gmail.com",
                FromName = "Href",
                TimeoutSeconds = timeoutSeconds,
            }),
            NullLogger<SmtpEmailSender>.Instance);

    [Fact]
    public async Task SendAsync_DeliversHtmlAndTextWithAccentsAndEmoji()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid().ToString("N");
        var subject = $"Verificá tu correo \U0001F600 {id}";
        var message = new EmailMessage(
            "ana.garcia@example.test",
            "Ana García",
            subject,
            "<p>Hola <b>Ana</b>, confirmá tu dirección \U0001F600</p>",
            "Hola Ana, confirmá tu dirección \U0001F600");

        await CreateSender(mailpit.Host, mailpit.MappedSmtpPort).SendAsync(message, cancellationToken);

        var messages = await mailpit.FindMessagesBySubjectAsync(id, cancellationToken);
        var stored = Assert.Single(messages);
        Assert.Equal(subject, stored.GetProperty("Subject").GetString());
        Assert.Equal("href.notifier@gmail.com", stored.GetProperty("From").GetProperty("Address").GetString());
        Assert.Equal("Href", stored.GetProperty("From").GetProperty("Name").GetString());
        var to = Assert.Single(stored.GetProperty("To").EnumerateArray().ToList());
        Assert.Equal("ana.garcia@example.test", to.GetProperty("Address").GetString());
        Assert.Equal("Ana García", to.GetProperty("Name").GetString());
        Assert.Contains("confirmá tu dirección \U0001F600", stored.GetProperty("HTML").GetString());
        Assert.Contains("confirmá tu dirección \U0001F600", stored.GetProperty("Text").GetString());
    }

    [Fact]
    public async Task SendAsync_WithoutTextBodyOrName_DeliversHtmlOnly()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid().ToString("N");
        var message = new EmailMessage("solo.html@example.test", null, $"Solo HTML {id}", "<p>Solo HTML</p>");

        await CreateSender(mailpit.Host, mailpit.MappedSmtpPort).SendAsync(message, cancellationToken);

        var stored = Assert.Single(await mailpit.FindMessagesBySubjectAsync(id, cancellationToken));
        Assert.Contains("Solo HTML", stored.GetProperty("HTML").GetString());
        var to = Assert.Single(stored.GetProperty("To").EnumerateArray().ToList());
        Assert.Equal("solo.html@example.test", to.GetProperty("Address").GetString());
    }

    [Fact]
    public async Task SendAsync_WhenServerNeverSendsGreeting_ThrowsWithinTimeout()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        TcpClient? accepted = null;

        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var acceptTask = listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken).AsTask();
            var sender = CreateSender(IPAddress.Loopback.ToString(), port, timeoutSeconds: 1);
            var stopwatch = Stopwatch.StartNew();

            await Assert.ThrowsAnyAsync<Exception>(() => sender.SendAsync(
                new EmailMessage("nobody@example.test", null, "Silent server", "<p>x</p>"),
                TestContext.Current.CancellationToken));

            stopwatch.Stop();
            accepted = await acceptTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"SendAsync took {stopwatch.Elapsed}");
        }
        finally
        {
            accepted?.Dispose();
            listener.Stop();
        }
    }

    [Fact]
    public async Task SendAsync_WhenServerIsUnreachable_Throws()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var closedPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var sender = CreateSender("localhost", closedPort, timeoutSeconds: 2);

        await Assert.ThrowsAnyAsync<Exception>(() => sender.SendAsync(
            new EmailMessage("nobody@example.test", null, "Unreachable", "<p>x</p>"),
            TestContext.Current.CancellationToken));
    }
}
