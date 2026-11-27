using Href.Core.Api.Modules.Notifications;
using Href.Core.Api.Modules.Notifications.Email;
using Href.Core.Api.Modules.Notifications.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Href.Core.Tests.Integration;

/// <summary>
/// End to end: a queued notification is claimed by the dispatcher, sent with the real <see cref="SmtpEmailSender"/>
/// and shows up in Mailpit.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class NotificationDeliveryMailpitIntegrationTests(PostgresFixture postgres, MailpitFixture mailpit)
    : IClassFixture<MailpitFixture>
{
    [Fact]
    public async Task DispatchBatch_WithSmtpEmailSender_DeliversTheQueuedNotificationToMailpit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var appKey = $"e2e-{Guid.NewGuid():N}"[..20];
        var marker = Guid.NewGuid().ToString("N");
        var subject = $"Te damos la bienvenida {marker}";
        var sender = new SmtpEmailSender(
            Options.Create(new SmtpOptions
            {
                Host = mailpit.Host,
                Port = mailpit.MappedSmtpPort,
                SecureSocket = SmtpSecureSocket.None,
                FromEmail = "href.notifier@gmail.com",
                FromName = "Href",
            }),
            NullLogger<SmtpEmailSender>.Instance);
        var dispatcher = new NotificationDispatcher(
            postgres.SessionFactory,
            sender,
            new TemplateRenderer(),
            TimeProvider.System,
            Options.Create(new NotificationWorkerOptions()),
            NullLogger<NotificationDispatcher>.Instance)
        {
            AppKeyFilter = appKey,
        };
        var notification = Notification.Queue(
            Guid.NewGuid(),
            appKey,
            "ana.garcia@example.test",
            "Ana García",
            subject,
            "<p>Hola <b>Ana</b>, tu cuenta está lista.</p>",
            "Hola Ana, tu cuenta está lista.",
            "welcome-user",
            null,
            null,
            TimeProvider.System).Value;

        using (var session = postgres.SessionFactory.OpenSession())
        using (var transaction = session.BeginTransaction())
        {
            await session.SaveAsync(notification, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        Assert.Equal(1, (await dispatcher.DispatchBatchAsync(cancellationToken)).Claimed);

        using (var session = postgres.SessionFactory.OpenSession())
        {
            var stored = await session.GetAsync<Notification>(notification.Id, cancellationToken);
            Assert.NotNull(stored);
            Assert.Equal(NotificationStatus.Sent, stored.Status);
        }

        var message = Assert.Single(await mailpit.FindMessagesBySubjectAsync(marker, cancellationToken));
        Assert.Equal(subject, message.GetProperty("Subject").GetString());
        var to = Assert.Single(message.GetProperty("To").EnumerateArray().ToList());
        Assert.Equal("ana.garcia@example.test", to.GetProperty("Address").GetString());
        Assert.Contains("tu cuenta está lista", message.GetProperty("HTML").GetString());
        Assert.Contains("tu cuenta está lista", message.GetProperty("Text").GetString());
    }
}
