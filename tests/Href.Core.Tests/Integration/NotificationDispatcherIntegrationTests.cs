using System.Net;
using Href.Core.Api.Common.Results;
using Href.Core.Api.Modules.Applications;
using Href.Core.Api.Modules.Notifications;
using Href.Core.Api.Modules.Notifications.Email;
using Href.Core.Api.Modules.Notifications.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NHibernate;
using NHibernate.Linq;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace Href.Core.Tests.Integration;

/// <summary>
/// Dispatcher tests against a real PostgreSQL. Isolation: every test uses its own <c>AppKey</c> and the dispatcher's
/// internal <c>AppKeyFilter</c>, so the claim and the recovery never touch rows left by other tests (and other tests
/// never see these rows as claimed). All timestamps come from one <see cref="FakeTimeProvider"/> shared by the session
/// factory, <see cref="Notification.Queue"/> and the dispatcher.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class NotificationDispatcherIntegrationTests : IDisposable
{
    private static readonly DateTimeOffset StartInstant = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _clock = new(StartInstant);
    private readonly ISessionFactory _sessionFactory;
    private readonly FakeEmailSender _sender = new();
    private readonly CapturingLogSink _sink = new();
    private readonly Serilog.Core.Logger _serilog;
    private readonly string _appKey = $"wrk-{Guid.NewGuid():N}"[..20];

    public NotificationDispatcherIntegrationTests(PostgresFixture postgres)
    {
        _sessionFactory = postgres.BuildSessionFactory(_clock);
        _serilog = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .Enrich.FromLogContext()
            .WriteTo.Sink(_sink)
            .CreateLogger();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    public void Dispose()
    {
        _serilog.Dispose();
        _sessionFactory.Dispose();
    }

    [Fact]
    public async Task DispatchBatch_WhenSendSucceeds_MarksSentWithOneAttemptAndClockTimestamp()
    {
        var to = UniqueEmail("ok");
        var queued = await QueueAsync(to);
        _clock.Advance(TimeSpan.FromSeconds(30));

        var claimed = await CreateDispatcher().DispatchBatchAsync(Ct);

        Assert.Equal(1, claimed.Claimed);
        var stored = await LoadAsync(queued.Id);
        Assert.Equal(NotificationStatus.Sent, stored.Status);
        Assert.Equal(1, stored.Attempts);
        Assert.Equal(3, stored.Version);
        Assert.Equal(Now, stored.SentAtUtc);
        Assert.Equal(Now, stored.UpdatedAtUtc);
        Assert.Null(stored.ProcessingStartedAtUtc);
        Assert.Null(stored.LastError);
        var message = Assert.Single(_sender.Sent);
        Assert.Equal(to, message.ToEmail);
        Assert.Equal("Ana García", message.ToName);
        Assert.Equal("Hola", message.Subject);
        Assert.Equal("<p>Body</p>", message.HtmlBody);
        Assert.Equal("Body", message.TextBody);
    }

    [Fact]
    public async Task ClaimBatch_MirrorsMarkProcessingAndCommitsBeforeSending()
    {
        var queued = await QueueAsync(UniqueEmail("claim"));
        _clock.Advance(TimeSpan.FromSeconds(10));

        var claimed = await CreateDispatcher().ClaimBatchAsync(Ct);

        Assert.Equal(new[] { queued.Id }, claimed.Select(item => item.Id));
        var stored = await LoadAsync(queued.Id);
        Assert.Equal(NotificationStatus.Processing, stored.Status);
        Assert.Equal(1, stored.Attempts);
        Assert.Equal(2, stored.Version);
        Assert.Equal(Now, stored.ProcessingStartedAtUtc);
        Assert.Equal(Now, stored.UpdatedAtUtc);
        Assert.Equal(StartInstant.UtcDateTime, stored.CreatedAtUtc);
        Assert.Equal(StartInstant.UtcDateTime, stored.NextAttemptAtUtc);
        Assert.Empty(_sender.Attempts);
    }

    [Fact]
    public async Task DispatchBatch_WhenNotDueYet_DoesNotClaimUntilNextAttemptIsReached()
    {
        var queueClock = new FakeTimeProvider(StartInstant.AddHours(1));
        var queued = await QueueAsync(UniqueEmail("later"), queueClock: queueClock);
        var dispatcher = CreateDispatcher();

        Assert.Equal(0, (await dispatcher.DispatchBatchAsync(Ct)).Claimed);
        var pending = await LoadAsync(queued.Id);
        Assert.Equal(NotificationStatus.Pending, pending.Status);
        Assert.Equal(0, pending.Attempts);
        Assert.Equal(1, pending.Version);
        Assert.Empty(_sender.Attempts);

        _clock.SetUtcNow(StartInstant.AddHours(1));

        Assert.Equal(1, (await dispatcher.DispatchBatchAsync(Ct)).Claimed);
        Assert.Equal(NotificationStatus.Sent, (await LoadAsync(queued.Id)).Status);
    }

    [Fact]
    public async Task DispatchBatch_WhenPendingHasNoAttemptsLeft_NeverClaimsIt()
    {
        var queued = await QueueAsync(UniqueEmail("exhausted"));
        await ExecuteSqlAsync("UPDATE notifications SET attempts = max_attempts WHERE id = :id", queued.Id);

        var claimed = await CreateDispatcher().DispatchBatchAsync(Ct);

        Assert.Equal(0, claimed.Claimed);
        var stored = await LoadAsync(queued.Id);
        Assert.Equal(NotificationStatus.Pending, stored.Status);
        Assert.Equal(3, stored.Attempts);
        Assert.Empty(_sender.Attempts);
    }

    [Fact]
    public async Task DispatchBatch_TwoFailuresThenSuccess_EndsSentWithThreeAttemptsAndRespectsBackoff()
    {
        var to = UniqueEmail("retry");
        _sender.FailNext(to, 2);
        var queued = await QueueAsync(to);
        var dispatcher = CreateDispatcher();

        Assert.Equal(1, (await dispatcher.DispatchBatchAsync(Ct)).Claimed);
        var afterFirst = await LoadAsync(queued.Id);
        Assert.Equal(NotificationStatus.Pending, afterFirst.Status);
        Assert.Equal(1, afterFirst.Attempts);
        Assert.Equal(StartInstant.AddMinutes(1).UtcDateTime, afterFirst.NextAttemptAtUtc);
        Assert.Equal("SMTP server unavailable", afterFirst.LastError);
        Assert.Null(afterFirst.ProcessingStartedAtUtc);

        _clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(0, (await dispatcher.DispatchBatchAsync(Ct)).Claimed);

        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, (await dispatcher.DispatchBatchAsync(Ct)).Claimed);
        var afterSecond = await LoadAsync(queued.Id);
        Assert.Equal(NotificationStatus.Pending, afterSecond.Status);
        Assert.Equal(2, afterSecond.Attempts);
        Assert.Equal(StartInstant.AddMinutes(6).UtcDateTime, afterSecond.NextAttemptAtUtc);

        _clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
        Assert.Equal(0, (await dispatcher.DispatchBatchAsync(Ct)).Claimed);

        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, (await dispatcher.DispatchBatchAsync(Ct)).Claimed);
        var sent = await LoadAsync(queued.Id);
        Assert.Equal(NotificationStatus.Sent, sent.Status);
        Assert.Equal(3, sent.Attempts);
        Assert.Equal(StartInstant.AddMinutes(6).UtcDateTime, sent.SentAtUtc);
        Assert.Equal("SMTP server unavailable", sent.LastError);
        Assert.Equal(3, _sender.Attempts.Count);
        Assert.Single(_sender.Sent);
    }

    [Fact]
    public async Task DispatchBatch_ThreeFailuresWithNotifyOnFailureTo_EndsFailedAndQueuesAlertForTheSameApp()
    {
        var to = UniqueEmail("dead");
        var ops = UniqueEmail("ops");
        const string subject = "Oferta \"única\" & <especial>";
        _sender.AlwaysFail(to);
        var queued = await QueueAsync(to, notifyOnFailureTo: ops, correlationId: "corr-alert", subject: subject);

        await FailThreeTimesAsync(CreateDispatcher());

        var failed = await LoadAsync(queued.Id);
        Assert.Equal(NotificationStatus.Failed, failed.Status);
        Assert.Equal(3, failed.Attempts);
        Assert.Equal("SMTP server unavailable", failed.LastError);
        Assert.Null(failed.ProcessingStartedAtUtc);

        var alert = Assert.Single(await LoadAllAsync(), notification => notification.Id != queued.Id);
        Assert.Equal(_appKey, alert.AppKey);
        Assert.Equal(ops, alert.ToEmail);
        Assert.Null(alert.ToName);
        Assert.Equal(NotificationDispatcher.FailureAlertTemplateKey, alert.TemplateKey);
        Assert.Null(alert.NotifyOnFailureTo);
        Assert.Equal("corr-alert", alert.CorrelationId);
        Assert.Equal(NotificationStatus.Pending, alert.Status);
        Assert.Equal(0, alert.Attempts);
        Assert.Equal(Now, alert.NextAttemptAtUtc);
        Assert.Contains(queued.Id.ToString(), alert.Subject);
        Assert.Contains(queued.Id.ToString(), alert.HtmlBody);
        Assert.Contains(to, alert.HtmlBody);
        Assert.Contains(WebUtility.HtmlEncode(subject), alert.HtmlBody);
        Assert.Contains("SMTP server unavailable", alert.HtmlBody);
        Assert.NotNull(alert.TextBody);
        Assert.Contains(queued.Id.ToString(), alert.TextBody);
        Assert.Contains(subject, alert.TextBody);
        Assert.Contains("Intentos: 3", alert.TextBody);
    }

    [Fact]
    public async Task DispatchBatch_ThreeFailuresWithoutNotifyOnFailureTo_EndsFailedWithoutAlert()
    {
        var to = UniqueEmail("dead-quiet");
        _sender.AlwaysFail(to);
        var queued = await QueueAsync(to);

        await FailThreeTimesAsync(CreateDispatcher());

        var stored = Assert.Single(await LoadAllAsync());
        Assert.Equal(queued.Id, stored.Id);
        Assert.Equal(NotificationStatus.Failed, stored.Status);
        Assert.Equal(3, stored.Attempts);
    }

    [Fact]
    public async Task DispatchBatch_WhenTheAlertAlsoFailsThreeTimes_EndsFailedWithoutAnotherAlert()
    {
        var to = UniqueEmail("dead-loop");
        var ops = UniqueEmail("ops-loop");
        _sender.AlwaysFail(to);
        _sender.AlwaysFail(ops);
        await QueueAsync(to, notifyOnFailureTo: ops);
        var dispatcher = CreateDispatcher();

        await FailThreeTimesAsync(dispatcher);
        await FailThreeTimesAsync(dispatcher);
        _clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(0, (await dispatcher.DispatchBatchAsync(Ct)).Claimed);

        var all = await LoadAllAsync();
        Assert.Equal(2, all.Count);
        Assert.All(all, notification => Assert.Equal(NotificationStatus.Failed, notification.Status));
        var alert = Assert.Single(all, notification => notification.ToEmail == ops);
        Assert.Null(alert.NotifyOnFailureTo);
        Assert.Equal(3, alert.Attempts);
        Assert.Equal(3, _sender.Attempts.Count(message => message.ToEmail == ops));
    }

    [Fact]
    public async Task DispatchBatch_WithLongSubjectAndLongError_StillQueuesTheAlert()
    {
        var to = UniqueEmail("long");
        var ops = UniqueEmail("ops-long");
        var subject = new string('s', Notification.SubjectMaxLength);
        var sender = new FakeEmailSender { FailureMessage = new string('e', 5000) + "\r\n<script>alert(1)</script>" };
        sender.AlwaysFail(to);
        var queued = await QueueAsync(to, notifyOnFailureTo: ops, subject: subject);

        await FailThreeTimesAsync(CreateDispatcher(sender: sender));

        var failed = await LoadAsync(queued.Id);
        Assert.Equal(NotificationStatus.Failed, failed.Status);
        Assert.Equal(Notification.LastErrorMaxLength, failed.LastError!.Length);
        var alert = Assert.Single(await LoadAllAsync(), notification => notification.Id != queued.Id);
        Assert.Contains(subject, alert.HtmlBody);
        Assert.Contains(failed.LastError, alert.TextBody);
    }

    [Fact]
    public async Task DispatchBatch_WhenAlertRenderingFails_StillRecordsFailedAndLogsError()
    {
        var to = UniqueEmail("render-fails");
        _sender.AlwaysFail(to);
        var queued = await QueueAsync(to, notifyOnFailureTo: UniqueEmail("ops-render"));

        await FailThreeTimesAsync(CreateDispatcher(renderer: new FailingTemplateRenderer(throws: false)));

        Assert.Equal(NotificationStatus.Failed, (await LoadAsync(queued.Id)).Status);
        Assert.Single(await LoadAllAsync());
        AssertErrorLoggedFor(queued.Id, "was not queued");
    }

    [Fact]
    public async Task DispatchBatch_WhenAlertRenderingThrows_StillRecordsFailedAndLogsError()
    {
        var to = UniqueEmail("render-throws");
        _sender.AlwaysFail(to);
        var queued = await QueueAsync(to, notifyOnFailureTo: UniqueEmail("ops-throw"));

        await FailThreeTimesAsync(CreateDispatcher(renderer: new FailingTemplateRenderer(throws: true)));

        Assert.Equal(NotificationStatus.Failed, (await LoadAsync(queued.Id)).Status);
        Assert.Single(await LoadAllAsync());
        AssertErrorLoggedFor(queued.Id, "unexpected error");
    }

    [Fact]
    public async Task DispatchBatch_WhenAlertTemplateIsMissing_StillRecordsFailedAndLogsError()
    {
        var to = UniqueEmail("no-template");
        _sender.AlwaysFail(to);
        var queued = await QueueAsync(to, notifyOnFailureTo: UniqueEmail("ops-missing"));
        var hiddenKey = $"hidden-{Guid.NewGuid():N}";
        await ExecuteSqlAsync($"UPDATE email_templates SET key = '{hiddenKey}' WHERE key = '{NotificationDispatcher.FailureAlertTemplateKey}'");

        try
        {
            await FailThreeTimesAsync(CreateDispatcher());
        }
        finally
        {
            await ExecuteSqlAsync($"UPDATE email_templates SET key = '{NotificationDispatcher.FailureAlertTemplateKey}' WHERE key = '{hiddenKey}'");
        }

        Assert.Equal(NotificationStatus.Failed, (await LoadAsync(queued.Id)).Status);
        Assert.Single(await LoadAllAsync());
        AssertErrorLoggedFor(queued.Id, "does not exist");
    }

    [Fact]
    public async Task ClaimBatch_ConcurrentClaims_NeverReturnTheSameRowAndTogetherClaimEveryDueRow()
    {
        const int rounds = 5;
        const int rows = 200;
        const int claimers = 4;
        const int batchSize = 60;
        var dispatchers = Enumerable.Range(0, claimers).Select(_ => CreateDispatcher(batchSize: batchSize)).ToList();

        for (var round = 0; round < rounds; round++)
        {
            var queuedIds = await QueueManyAsync(rows);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var claims = dispatchers
                .Select(dispatcher => Task.Run(async () =>
                {
                    await start.Task;
                    return await dispatcher.ClaimBatchAsync(Ct);
                }, Ct))
                .ToList();
            start.SetResult();
            var results = await Task.WhenAll(claims);

            var all = results.SelectMany(ids => ids.Select(item => item.Id)).ToList();
            Assert.All(results, ids => Assert.True(ids.Count <= batchSize));
            Assert.Equal(all.Count, all.Distinct().Count());
            Assert.Equal(queuedIds.ToHashSet(), all.ToHashSet());
        }

        var stored = await LoadAllAsync();
        Assert.Equal(rounds * rows, stored.Count);
        Assert.All(stored, notification =>
        {
            Assert.Equal(NotificationStatus.Processing, notification.Status);
            Assert.Equal(1, notification.Attempts);
            Assert.Equal(2, notification.Version);
        });
    }

    [Fact]
    public async Task DispatchBatch_RespectsBatchSizeAndClaimsTheOldestFirst()
    {
        var queued = new List<Notification>();

        for (var index = 0; index < 5; index++)
        {
            var queueClock = new FakeTimeProvider(StartInstant.AddMinutes(index - 5));
            queued.Add(await QueueAsync(UniqueEmail($"batch{index}"), queueClock: queueClock));
        }

        var dispatcher = CreateDispatcher(batchSize: 2);

        Assert.Equal(2, (await dispatcher.DispatchBatchAsync(Ct)).Claimed);
        Assert.Equal(
            new[] { queued[0].ToEmail, queued[1].ToEmail }.ToHashSet(),
            _sender.Sent.Select(message => message.ToEmail).ToHashSet());
        Assert.Equal(3, (await LoadAllAsync()).Count(notification => notification.Status == NotificationStatus.Pending));

        Assert.Equal(2, (await dispatcher.DispatchBatchAsync(Ct)).Claimed);
        Assert.Equal(1, (await dispatcher.DispatchBatchAsync(Ct)).Claimed);
        Assert.Equal(0, (await dispatcher.DispatchBatchAsync(Ct)).Claimed);
        Assert.All(await LoadAllAsync(), notification => Assert.Equal(NotificationStatus.Sent, notification.Status));
    }

    [Fact]
    public async Task DispatchBatch_WhenOneSendFails_TheOthersInTheBatchAreStillSent()
    {
        var first = await QueueAsync(UniqueEmail("one"));
        var failing = await QueueAsync(UniqueEmail("two"));
        var third = await QueueAsync(UniqueEmail("three"));
        _sender.FailNext(failing.ToEmail, 1);

        Assert.Equal(3, (await CreateDispatcher().DispatchBatchAsync(Ct)).Claimed);

        Assert.Equal(NotificationStatus.Sent, (await LoadAsync(first.Id)).Status);
        Assert.Equal(NotificationStatus.Sent, (await LoadAsync(third.Id)).Status);
        var retry = await LoadAsync(failing.Id);
        Assert.Equal(NotificationStatus.Pending, retry.Status);
        Assert.Equal("SMTP server unavailable", retry.LastError);
    }

    [Fact]
    public async Task DispatchBatch_WhenRecoveryChangesTheRowDuringTheSend_LogsWarningAndKeepsProcessingTheBatch()
    {
        var touched = await QueueAsync(UniqueEmail("stale"));
        var other = await QueueAsync(UniqueEmail("fresh"));
        _sender.OnSend = async (message, _) =>
        {
            if (message.ToEmail == touched.ToEmail)
            {
                await ExecuteSqlAsync(
                    """
                    UPDATE notifications
                    SET status = 'Pending', processing_started_at_utc = NULL, next_attempt_at_utc = updated_at_utc, version = version + 1
                    WHERE id = :id
                    """,
                    touched.Id);
            }
        };

        var claimed = await CreateDispatcher().DispatchBatchAsync(Ct);

        Assert.Equal(2, claimed.Claimed);
        var stale = await LoadAsync(touched.Id);
        Assert.Equal(NotificationStatus.Pending, stale.Status);
        Assert.Equal(3, stale.Version);
        Assert.Null(stale.SentAtUtc);
        Assert.Equal(NotificationStatus.Sent, (await LoadAsync(other.Id)).Status);
        var warning = Assert.Single(_sink.Events, logEvent =>
            logEvent.Level == LogEventLevel.Warning
            && logEvent.MessageTemplate.Text.Contains("changed concurrently", StringComparison.Ordinal));
        Assert.Equal(touched.Id, ScalarOf(warning, NotificationDispatcher.NotificationIdProperty));
    }

    [Fact]
    public async Task DispatchBatch_LogsDuringProcessingCarryNotificationIdAppKeyAndCorrelationId()
    {
        const string bodyMarker = "body-marker-never-logged";
        var queued = await QueueAsync(UniqueEmail("log"), correlationId: "corr-log-123", htmlBody: $"<p>{bodyMarker}</p>");
        _sender.OnSend = (_, _) =>
        {
            _serilog.Information("Inside the email sender");
            return Task.CompletedTask;
        };

        await CreateDispatcher().DispatchBatchAsync(Ct);

        var inside = Assert.Single(_sink.Events, logEvent => logEvent.MessageTemplate.Text == "Inside the email sender");
        Assert.Equal(queued.Id, ScalarOf(inside, NotificationDispatcher.NotificationIdProperty));
        Assert.Equal(_appKey, ScalarOf(inside, AppKeyLogContextMiddleware.PropertyName));
        Assert.Equal("corr-log-123", ScalarOf(inside, NotificationDispatcher.CorrelationIdProperty));
        var sent = Assert.Single(_sink.Events, logEvent => logEvent.MessageTemplate.Text.Contains("sent on attempt", StringComparison.Ordinal));
        Assert.Equal(_appKey, ScalarOf(sent, AppKeyLogContextMiddleware.PropertyName));
        Assert.Equal("corr-log-123", ScalarOf(sent, NotificationDispatcher.CorrelationIdProperty));
        Assert.DoesNotContain(_sink.Events, logEvent =>
            logEvent.RenderMessage().Contains(bodyMarker, StringComparison.Ordinal)
            || logEvent.Properties.Values.Any(value => value.ToString().Contains(bodyMarker, StringComparison.Ordinal)));
        Assert.DoesNotContain(_sink.Events, logEvent =>
            logEvent.RenderMessage().Contains(queued.ToEmail, StringComparison.Ordinal)
            || logEvent.Properties.Values.Any(value => value.ToString().Contains(queued.ToEmail, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task DispatchBatch_WhenStoppedAfterASuccessfulSend_RecordsSentAndReleasesTheRest()
    {
        await QueueAsync(UniqueEmail("stop-a"));
        await QueueAsync(UniqueEmail("stop-b"));
        using var stopping = new CancellationTokenSource();
        _sender.OnSend = async (_, _) => await stopping.CancelAsync();

        var claimed = await CreateDispatcher().DispatchBatchAsync(stopping.Token);

        Assert.Equal(2, claimed.Claimed);
        var stored = await LoadAllAsync();
        var sent = Assert.Single(stored, notification => notification.Status == NotificationStatus.Sent);
        Assert.Equal(sent.ToEmail, Assert.Single(_sender.Sent).ToEmail);
        var released = Assert.Single(stored, notification => notification.Status == NotificationStatus.Pending);
        Assert.Equal(0, released.Attempts);
        Assert.Equal(Now, released.NextAttemptAtUtc);
        Assert.Null(released.ProcessingStartedAtUtc);
        Assert.Null(released.LastError);
        Assert.Single(_sender.Attempts);
    }

    [Fact]
    public async Task DispatchBatch_WhenStoppedDuringTheSend_ReleasesTheNotificationWithoutRecordingAFailure()
    {
        var queued = await QueueAsync(UniqueEmail("stop-during"));
        using var stopping = new CancellationTokenSource();
        _sender.OnSend = async (_, cancellationToken) =>
        {
            await stopping.CancelAsync();
            cancellationToken.ThrowIfCancellationRequested();
        };

        await CreateDispatcher().DispatchBatchAsync(stopping.Token);

        var released = await LoadAsync(queued.Id);
        Assert.Equal(NotificationStatus.Pending, released.Status);
        Assert.Equal(0, released.Attempts);
        Assert.Null(released.LastError);
        Assert.Empty(_sender.Sent);
        Assert.Contains(_sink.Events, logEvent => logEvent.MessageTemplate.Text.Contains("worker is stopping", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DispatchBatch_WhenStoppedDuringTheLastAttempt_ReleasesItAsPendingWithoutAlert()
    {
        var to = UniqueEmail("stop-last");
        _sender.FailNext(to, 2);
        var queued = await QueueAsync(to, notifyOnFailureTo: UniqueEmail("ops-stop-last"));
        var dispatcher = CreateDispatcher();
        await dispatcher.DispatchBatchAsync(Ct);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await dispatcher.DispatchBatchAsync(Ct);
        _clock.Advance(TimeSpan.FromMinutes(5));
        using var stopping = new CancellationTokenSource();
        _sender.OnSend = async (_, cancellationToken) =>
        {
            await stopping.CancelAsync();
            cancellationToken.ThrowIfCancellationRequested();
        };

        await dispatcher.DispatchBatchAsync(stopping.Token);

        var released = await LoadAsync(queued.Id);
        Assert.Equal(NotificationStatus.Pending, released.Status);
        Assert.Equal(2, released.Attempts);
        Assert.Equal(Now, released.NextAttemptAtUtc);
        Assert.Equal("SMTP server unavailable", released.LastError);
        Assert.Single(await LoadAllAsync());
        Assert.Empty(_sender.Sent);
    }

    [Fact]
    public async Task Process_WhenAnotherInstanceRecoveredAndClaimedTheRowAgain_DoesNotSend()
    {
        var queued = await QueueAsync(UniqueEmail("reclaimed"));
        var dispatcher = CreateDispatcher();
        var claimed = Assert.Single(await dispatcher.ClaimBatchAsync(Ct));
        Assert.Equal(2, claimed.Version);
        await ExecuteSqlAsync(
            """
            UPDATE notifications
            SET attempts = attempts + 1, processing_started_at_utc = updated_at_utc, version = version + 2
            WHERE id = :id
            """,
            queued.Id);

        var unexpected = await dispatcher.ProcessAsync(claimed, Ct);

        Assert.False(unexpected);
        Assert.Empty(_sender.Attempts);
        var stored = await LoadAsync(queued.Id);
        Assert.Equal(NotificationStatus.Processing, stored.Status);
        Assert.Equal(4, stored.Version);
        Assert.Equal(2, stored.Attempts);
        Assert.Contains(_sink.Events, logEvent =>
            logEvent.Level == LogEventLevel.Warning
            && logEvent.MessageTemplate.Text.Contains("no longer held by this claim", StringComparison.Ordinal)
            && Equals(ScalarOf(logEvent, NotificationDispatcher.NotificationIdProperty), queued.Id));
    }

    [Fact]
    public async Task Process_WhenTheRowWasRecoveredToPendingAfterTheClaim_DoesNotSend()
    {
        var queued = await QueueAsync(UniqueEmail("recovered-before-send"));
        var dispatcher = CreateDispatcher(stuckAfterMinutes: 10);
        var claimed = Assert.Single(await dispatcher.ClaimBatchAsync(Ct));
        _clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(1, await CreateDispatcher(stuckAfterMinutes: 10).RecoverStuckAsync(Ct));

        Assert.False(await dispatcher.ProcessAsync(claimed, Ct));

        Assert.Empty(_sender.Attempts);
        Assert.Equal(NotificationStatus.Pending, (await LoadAsync(queued.Id)).Status);
    }

    [Fact]
    public async Task Process_WhenTheRowWasDeletedAfterTheClaim_LogsWarningAndDoesNotSend()
    {
        var queued = await QueueAsync(UniqueEmail("deleted"));
        var dispatcher = CreateDispatcher();
        var claimed = Assert.Single(await dispatcher.ClaimBatchAsync(Ct));
        await ExecuteSqlAsync("DELETE FROM notifications WHERE id = :id", queued.Id);

        var unexpected = await dispatcher.ProcessAsync(claimed, Ct);

        Assert.False(unexpected);
        Assert.Empty(_sender.Attempts);
        Assert.Contains(_sink.Events, logEvent =>
            logEvent.Level == LogEventLevel.Warning
            && logEvent.MessageTemplate.Text.Contains("was not found after the claim", StringComparison.Ordinal)
            && Equals(ScalarOf(logEvent, NotificationDispatcher.NotificationIdProperty), queued.Id));
    }

    [Fact]
    public async Task DispatchBatch_WhenAlreadyStopping_ClaimsNothing()
    {
        var queued = await QueueAsync(UniqueEmail("stopped"));
        using var stopping = new CancellationTokenSource();
        await stopping.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateDispatcher().DispatchBatchAsync(stopping.Token));

        var stored = await LoadAsync(queued.Id);
        Assert.Equal(NotificationStatus.Pending, stored.Status);
        Assert.Equal(0, stored.Attempts);
    }

    [Fact]
    public async Task RecoverStuck_ReturnsOldProcessingToPendingAndLeavesYoungerOnesUntouched()
    {
        var old = await QueueAsync(UniqueEmail("stuck-old"));
        var dispatcher = CreateDispatcher(stuckAfterMinutes: 10);
        Assert.Equal(new[] { old.Id }, (await dispatcher.ClaimBatchAsync(Ct)).Select(item => item.Id));

        _clock.Advance(TimeSpan.FromMinutes(5));
        var young = await QueueAsync(UniqueEmail("stuck-young"));
        Assert.Equal(new[] { young.Id }, (await dispatcher.ClaimBatchAsync(Ct)).Select(item => item.Id));

        _clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(0, await dispatcher.RecoverStuckAsync(Ct));

        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, await dispatcher.RecoverStuckAsync(Ct));

        var recovered = await LoadAsync(old.Id);
        Assert.Equal(NotificationStatus.Pending, recovered.Status);
        Assert.Equal(1, recovered.Attempts);
        Assert.Equal(Now, recovered.NextAttemptAtUtc);
        Assert.Equal(Now, recovered.UpdatedAtUtc);
        Assert.Null(recovered.ProcessingStartedAtUtc);
        Assert.Null(recovered.LastError);
        Assert.Equal(3, recovered.Version);
        var untouched = await LoadAsync(young.Id);
        Assert.Equal(NotificationStatus.Processing, untouched.Status);
        Assert.Equal(2, untouched.Version);
        Assert.Contains(_sink.Events, logEvent =>
            logEvent.Level == LogEventLevel.Warning
            && logEvent.MessageTemplate.Text.Contains("recovered as", StringComparison.Ordinal)
            && Equals(ScalarOf(logEvent, NotificationDispatcher.NotificationIdProperty), old.Id));

        Assert.Equal(1, (await dispatcher.DispatchBatchAsync(Ct)).Claimed);
        var sent = await LoadAsync(old.Id);
        Assert.Equal(NotificationStatus.Sent, sent.Status);
        Assert.Equal(2, sent.Attempts);
    }

    [Fact]
    public async Task RecoverStuck_WithNoAttemptsLeft_EndsFailedAndQueuesAlert()
    {
        var to = UniqueEmail("stuck-last");
        var ops = UniqueEmail("ops-stuck");
        _sender.FailNext(to, 2);
        var queued = await QueueAsync(to, notifyOnFailureTo: ops, correlationId: "corr-stuck");
        var dispatcher = CreateDispatcher(stuckAfterMinutes: 10);

        await dispatcher.DispatchBatchAsync(Ct);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await dispatcher.DispatchBatchAsync(Ct);
        _clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(new[] { queued.Id }, (await dispatcher.ClaimBatchAsync(Ct)).Select(item => item.Id));
        _clock.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));

        Assert.Equal(1, await dispatcher.RecoverStuckAsync(Ct));

        var failed = await LoadAsync(queued.Id);
        Assert.Equal(NotificationStatus.Failed, failed.Status);
        Assert.Equal(3, failed.Attempts);
        Assert.Equal(Notification.ProcessingTimedOutError, failed.LastError);
        Assert.Null(failed.ProcessingStartedAtUtc);
        Assert.Equal(StartInstant.AddMinutes(6).UtcDateTime, failed.NextAttemptAtUtc);
        var alert = Assert.Single(await LoadAllAsync(), notification => notification.Id != queued.Id);
        Assert.Equal(_appKey, alert.AppKey);
        Assert.Equal(ops, alert.ToEmail);
        Assert.Null(alert.NotifyOnFailureTo);
        Assert.Equal("corr-stuck", alert.CorrelationId);
        Assert.Equal(NotificationDispatcher.FailureAlertTemplateKey, alert.TemplateKey);
        Assert.Contains(queued.Id.ToString(), alert.TextBody);
        Assert.Contains(Notification.ProcessingTimedOutError, alert.TextBody);
    }

    private static string UniqueEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}@example.test";

    private static object? ScalarOf(LogEvent logEvent, string propertyName) =>
        logEvent.Properties.TryGetValue(propertyName, out var value) ? Assert.IsType<ScalarValue>(value).Value : null;

    private void AssertErrorLoggedFor(Guid notificationId, string messageFragment) =>
        Assert.Contains(_sink.Events, logEvent =>
            logEvent.Level == LogEventLevel.Error
            && logEvent.MessageTemplate.Text.Contains(messageFragment, StringComparison.Ordinal)
            && Equals(ScalarOf(logEvent, NotificationDispatcher.NotificationIdProperty), notificationId));

    private NotificationDispatcher CreateDispatcher(
        int batchSize = 20,
        int stuckAfterMinutes = 10,
        IEmailSender? sender = null,
        ITemplateRenderer? renderer = null) =>
        new(
            _sessionFactory,
            sender ?? _sender,
            renderer ?? new TemplateRenderer(),
            _clock,
            Options.Create(new NotificationWorkerOptions { BatchSize = batchSize, StuckAfterMinutes = stuckAfterMinutes }),
            new SerilogLoggerFactory(_serilog).CreateLogger<NotificationDispatcher>())
        {
            AppKeyFilter = _appKey,
        };

    private async Task FailThreeTimesAsync(NotificationDispatcher dispatcher)
    {
        await dispatcher.DispatchBatchAsync(Ct);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await dispatcher.DispatchBatchAsync(Ct);
        _clock.Advance(TimeSpan.FromMinutes(5));
        await dispatcher.DispatchBatchAsync(Ct);
    }

    private Notification BuildNotification(
        string toEmail,
        string? notifyOnFailureTo = null,
        string? correlationId = null,
        TimeProvider? queueClock = null,
        string subject = "Hola",
        string htmlBody = "<p>Body</p>") =>
        Notification.Queue(
            _appKey,
            toEmail,
            "Ana García",
            subject,
            htmlBody,
            "Body",
            "custom-template",
            notifyOnFailureTo,
            correlationId,
            queueClock ?? _clock).Value;

    private async Task<Notification> QueueAsync(
        string toEmail,
        string? notifyOnFailureTo = null,
        string? correlationId = null,
        TimeProvider? queueClock = null,
        string subject = "Hola",
        string htmlBody = "<p>Body</p>")
    {
        var notification = BuildNotification(toEmail, notifyOnFailureTo, correlationId, queueClock, subject, htmlBody);

        using var session = _sessionFactory.OpenSession();
        using var transaction = session.BeginTransaction();
        await session.SaveAsync(notification, Ct);
        await transaction.CommitAsync(Ct);

        return notification;
    }

    private async Task<IReadOnlyList<Guid>> QueueManyAsync(int count)
    {
        var ids = new List<Guid>();

        using var session = _sessionFactory.OpenSession();
        using var transaction = session.BeginTransaction();

        for (var index = 0; index < count; index++)
        {
            var notification = BuildNotification(UniqueEmail($"many{index}"));
            await session.SaveAsync(notification, Ct);
            ids.Add(notification.Id);
        }

        await transaction.CommitAsync(Ct);
        return ids;
    }

    private async Task<Notification> LoadAsync(Guid id)
    {
        using var session = _sessionFactory.OpenSession();
        var notification = await session.GetAsync<Notification>(id, Ct);
        Assert.NotNull(notification);
        return notification;
    }

    private async Task<List<Notification>> LoadAllAsync()
    {
        var appKey = _appKey;
        using var session = _sessionFactory.OpenSession();
        return await session.Query<Notification>().Where(notification => notification.AppKey == appKey).ToListAsync(Ct);
    }

    private async Task ExecuteSqlAsync(string sql, Guid? id = null)
    {
        using var session = _sessionFactory.OpenSession();
        using var transaction = session.BeginTransaction();
        var query = session.CreateSQLQuery(sql);

        if (id is not null)
        {
            query.SetGuid("id", id.Value);
        }

        await query.ExecuteUpdateAsync(Ct);
        await transaction.CommitAsync(Ct);
    }

    private sealed class FailingTemplateRenderer(bool throws) : ITemplateRenderer
    {
        private readonly TemplateRenderer _inner = new();

        public Result<RenderedEmail> Render(EmailTemplate template, IReadOnlyDictionary<string, string>? variables) =>
            throws
                ? throw new InvalidOperationException("Renderer exploded")
                : EmailTemplateErrors.MissingVariables(["Unknown"]);

        public Result<RenderedEmail> Render(string subject, string htmlBody, string? textBody, IReadOnlyDictionary<string, string>? variables) =>
            _inner.Render(subject, htmlBody, textBody, variables);

        public Result<string> Render(string text, IReadOnlyDictionary<string, string>? variables, bool htmlEncode, int maxLength = TemplateRenderer.MaxRenderedBodyLength) =>
            _inner.Render(text, variables, htmlEncode, maxLength);

        public IReadOnlyList<string> GetPlaceholders(string? text) => _inner.GetPlaceholders(text);
    }
}
