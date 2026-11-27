using Href.Core.Api.Modules.Notifications;
using Href.Core.Api.Modules.Notifications.Email;
using Href.Core.Api.Modules.Notifications.Worker;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NHibernate;

namespace Href.Core.Tests.Integration;

/// <summary>
/// The worker inside the full application. The live dispatcher gets a unique <c>AppKeyFilter</c> so it only delivers
/// the notification queued by the test.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class NotificationWorkerIntegrationTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Api_WithWorkerDisabled_WorkerReturnsImmediately()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);

        var worker = Assert.Single(factory.Services.GetServices<IHostedService>().OfType<NotificationBackgroundWorker>());
        Assert.False(factory.Services.GetRequiredService<IOptions<NotificationWorkerOptions>>().Value.Enabled);
        Assert.NotNull(worker.ExecuteTask);
        await worker.ExecuteTask.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.True(worker.ExecuteTask.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Api_WithWorkerEnabled_DeliversAQueuedNotificationWithinSeconds()
    {
        var appKey = $"live-{Guid.NewGuid():N}"[..20];
        var to = $"live-{Guid.NewGuid():N}@example.test";
        var sender = new FakeEmailSender();
        await using var baseFactory = new ApiFactory(
            postgres.ConnectionString,
            overrides: new Dictionary<string, string?>
            {
                ["Notifications:Worker:Enabled"] = "true",
                ["Notifications:Worker:PollIntervalSeconds"] = "1",
            });
        await using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(sender);
            services.RemoveAll<INotificationDispatcher>();
            services.AddScoped<INotificationDispatcher>(provider =>
            {
                var dispatcher = ActivatorUtilities.CreateInstance<NotificationDispatcher>(provider);
                dispatcher.AppKeyFilter = appKey;
                return dispatcher;
            });
        }));

        var sessionFactory = factory.Services.GetRequiredService<ISessionFactory>();
        var notification = Notification.Queue(
            Guid.NewGuid(),
            appKey,
            to,
            null,
            "Live worker",
            "<p>Live</p>",
            null,
            null,
            null,
            null,
            TimeProvider.System).Value;

        using (var session = sessionFactory.OpenSession())
        using (var transaction = session.BeginTransaction())
        {
            await session.SaveAsync(notification, Ct);
            await transaction.CommitAsync(Ct);
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        Notification? stored;

        do
        {
            await Task.Delay(TimeSpan.FromMilliseconds(200), Ct);
            using var session = sessionFactory.OpenSession();
            stored = await session.GetAsync<Notification>(notification.Id, Ct);
        }
        while (stored?.Status != NotificationStatus.Sent && DateTime.UtcNow < deadline);

        Assert.NotNull(stored);
        Assert.Equal(NotificationStatus.Sent, stored.Status);
        Assert.Equal(1, stored.Attempts);
        Assert.Equal(to, Assert.Single(sender.Sent).ToEmail);
    }
}
