using System.Data.Common;
using System.Globalization;
using Href.Core.Api.Modules.Applications;
using Href.Core.Api.Modules.Notifications.Email;
using Microsoft.Extensions.Options;
using NHibernate;
using NHibernate.Linq;
using Npgsql;
using NpgsqlTypes;
using Serilog.Context;
using ISession = NHibernate.ISession;

namespace Href.Core.Api.Modules.Notifications.Worker;

/// <summary>
/// Delivers queued notifications (outbox pattern). Every timestamp comes from the injected <see cref="TimeProvider"/>;
/// PostgreSQL <c>now()</c> is never used, so the claim, the recovery and the domain transitions share one clock.
/// The claim and the recovery are raw SQL that mirror <see cref="Notification.MarkProcessing"/> and
/// <see cref="Notification.RecoverStuck"/> (see the <see cref="Notification"/> summary). The claim uses
/// <c>FOR UPDATE SKIP LOCKED</c> and commits before any email is sent, so several instances never claim the same row.
/// Each notification is then loaded in its own session and sent only if it is still <c>Processing</c> with the version
/// written by this claim (otherwise another instance recovered or claimed it again). Optimistic concurrency
/// (<c>version</c>) rejects the record when the recovery changed the row during the send. A notification not sent
/// because the worker is stopping is released with <see cref="Notification.ReleaseUnsent"/>. Delivery is
/// at-least-once: an email can be sent twice when the send succeeds but its result cannot be recorded, or when a send
/// cancelled by a shutdown had already been accepted by the server.
/// </summary>
public sealed partial class NotificationDispatcher(
    ISessionFactory sessionFactory,
    IEmailSender emailSender,
    ITemplateRenderer templateRenderer,
    TimeProvider timeProvider,
    IOptions<NotificationWorkerOptions> options,
    ILogger<NotificationDispatcher> logger) : INotificationDispatcher
{
    public const string FailureAlertTemplateKey = "delivery-failed-alert";
    public const string NotificationIdProperty = "NotificationId";
    public const string CorrelationIdProperty = "CorrelationId";

    private const string AppKeyFilterToken = "{appKeyFilter}";
    private const string AppKeyFilterClause = " AND app_key = @appKey";

    private const string ClaimSqlTemplate = """
        WITH claimed AS (
            SELECT id
            FROM notifications
            WHERE status = 'Pending'
              AND next_attempt_at_utc <= @now
              AND attempts < max_attempts{appKeyFilter}
            ORDER BY next_attempt_at_utc, id
            LIMIT @batch
            FOR UPDATE SKIP LOCKED
        )
        UPDATE notifications AS n
        SET status = 'Processing',
            attempts = n.attempts + 1,
            processing_started_at_utc = @now,
            updated_at_utc = @now,
            version = n.version + 1
        FROM claimed
        WHERE n.id = claimed.id
        RETURNING n.id, n.version
        """;

    private const string RecoverSqlTemplate = """
        UPDATE notifications
        SET status = CASE WHEN attempts >= max_attempts THEN 'Failed' ELSE 'Pending' END,
            next_attempt_at_utc = CASE WHEN attempts >= max_attempts THEN next_attempt_at_utc ELSE @now END,
            last_error = CASE WHEN attempts >= max_attempts THEN @timedOutError ELSE last_error END,
            processing_started_at_utc = NULL,
            updated_at_utc = @now,
            version = version + 1
        WHERE status = 'Processing'
          AND processing_started_at_utc < @stuckBefore{appKeyFilter}
        RETURNING id, status, app_key, correlation_id
        """;

    private static readonly string ClaimSql = ClaimSqlTemplate.Replace(AppKeyFilterToken, string.Empty, StringComparison.Ordinal);
    private static readonly string ClaimSqlForAppKey = ClaimSqlTemplate.Replace(AppKeyFilterToken, AppKeyFilterClause, StringComparison.Ordinal);
    private static readonly string RecoverSql = RecoverSqlTemplate.Replace(AppKeyFilterToken, string.Empty, StringComparison.Ordinal);
    private static readonly string RecoverSqlForAppKey = RecoverSqlTemplate.Replace(AppKeyFilterToken, AppKeyFilterClause, StringComparison.Ordinal);

    private readonly NotificationWorkerOptions _options = options.Value;

    private enum SendOutcome
    {
        Sent,
        Failed,
        Cancelled,
    }

    /// <summary>
    /// Test-only isolation: when set, the claim and the recovery only touch notifications of this <c>AppKey</c>.
    /// Production code never sets it, so the production SQL has no <c>app_key</c> condition.
    /// </summary>
    internal string? AppKeyFilter { get; set; }

    /// <inheritdoc />
    public async Task<int> RecoverStuckAsync(CancellationToken cancellationToken)
    {
        var now = UtcNow();
        var stuckBefore = now.AddMinutes(-_options.StuckAfterMinutes);
        var recovered = new List<RecoveredNotification>();

        using var session = sessionFactory.OpenSession();
        using var transaction = session.BeginTransaction();

        await using (var command = CreateCommand(session, transaction, AppKeyFilter is null ? RecoverSql : RecoverSqlForAppKey))
        {
            AddParameter(command, "now", NpgsqlDbType.TimestampTz, now);
            AddParameter(command, "stuckBefore", NpgsqlDbType.TimestampTz, stuckBefore);
            AddParameter(command, "timedOutError", NpgsqlDbType.Varchar, Notification.ProcessingTimedOutError);
            AddAppKeyFilter(command);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            while (await reader.ReadAsync(cancellationToken))
            {
                recovered.Add(new RecoveredNotification(
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3)));
            }
        }

        if (recovered.Count == 0)
        {
            await transaction.CommitAsync(CancellationToken.None);
            return 0;
        }

        foreach (var item in recovered.Where(item => item.Status == nameof(NotificationStatus.Failed)))
        {
            using var notificationIdProperty = LogContext.PushProperty(NotificationIdProperty, item.Id);
            using var appKeyProperty = LogContext.PushProperty(AppKeyLogContextMiddleware.PropertyName, item.AppKey);
            using var correlationIdProperty = PushCorrelationId(item.CorrelationId);

            var notification = await session.GetAsync<Notification>(item.Id, cancellationToken);

            if (notification?.NotifyOnFailureTo is not null)
            {
                await TryQueueFailureAlertAsync(session, notification, cancellationToken);
            }
        }

        await transaction.CommitAsync(CancellationToken.None);

        foreach (var item in recovered)
        {
            using var notificationIdProperty = LogContext.PushProperty(NotificationIdProperty, item.Id);
            using var appKeyProperty = LogContext.PushProperty(AppKeyLogContextMiddleware.PropertyName, item.AppKey);
            using var correlationIdProperty = PushCorrelationId(item.CorrelationId);

            LogRecoveredStuck(logger, item.Id, item.Status);
        }

        return recovered.Count;
    }

    /// <inheritdoc />
    public async Task<DispatchResult> DispatchBatchAsync(CancellationToken cancellationToken)
    {
        var claimed = await ClaimBatchAsync(cancellationToken);
        var unexpectedErrors = 0;

        foreach (var item in claimed)
        {
            if (await ProcessAsync(item, cancellationToken))
            {
                unexpectedErrors++;
            }
        }

        return new DispatchResult(claimed.Count, unexpectedErrors);
    }

    /// <summary>
    /// Claims up to <c>BatchSize</c> due notifications in one committed transaction and returns their ids with the
    /// version written by the claim.
    /// </summary>
    internal async Task<IReadOnlyList<ClaimedNotification>> ClaimBatchAsync(CancellationToken cancellationToken)
    {
        var now = UtcNow();
        var claimed = new List<ClaimedNotification>();

        using var session = sessionFactory.OpenSession();
        using var transaction = session.BeginTransaction();

        await using (var command = CreateCommand(session, transaction, AppKeyFilter is null ? ClaimSql : ClaimSqlForAppKey))
        {
            AddParameter(command, "now", NpgsqlDbType.TimestampTz, now);
            AddParameter(command, "batch", NpgsqlDbType.Integer, _options.BatchSize);
            AddAppKeyFilter(command);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            while (await reader.ReadAsync(cancellationToken))
            {
                claimed.Add(new ClaimedNotification(reader.GetGuid(0), reader.GetInt32(1)));
            }
        }

        await transaction.CommitAsync(CancellationToken.None);

        if (claimed.Count > 0)
        {
            LogBatchClaimed(logger, claimed.Count);
        }

        return claimed;
    }

    /// <summary>
    /// Sends one claimed notification and records the outcome. It never throws. Returns true when an unexpected error
    /// (not a send failure) prevented the outcome from being recorded.
    /// </summary>
    internal async Task<bool> ProcessAsync(ClaimedNotification claimed, CancellationToken stoppingToken)
    {
        var id = claimed.Id;
        using var notificationIdProperty = LogContext.PushProperty(NotificationIdProperty, id);

        try
        {
            using var session = sessionFactory.OpenSession();
            var notification = await session.GetAsync<Notification>(id, CancellationToken.None);

            if (notification is null)
            {
                LogNotFound(logger, id);
                return false;
            }

            using var appKeyProperty = LogContext.PushProperty(AppKeyLogContextMiddleware.PropertyName, notification.AppKey);
            using var correlationIdProperty = PushCorrelationId(notification.CorrelationId);

            if (notification.Status != NotificationStatus.Processing || notification.Version != claimed.Version)
            {
                LogClaimLost(logger, id, notification.Status, notification.Version, claimed.Version);
                return false;
            }

            var (outcome, failure) = await SendAsync(notification, stoppingToken);
            return await RecordOutcomeAsync(session, notification, outcome, failure);
        }
        catch (Exception exception)
        {
            LogProcessingFailed(logger, exception, id);
            return true;
        }
    }

    private async Task<(SendOutcome Outcome, Exception? Failure)> SendAsync(Notification notification, CancellationToken stoppingToken)
    {
        if (stoppingToken.IsCancellationRequested)
        {
            return (SendOutcome.Cancelled, null);
        }

        var message = new EmailMessage(
            notification.ToEmail,
            notification.ToName,
            notification.Subject,
            notification.HtmlBody,
            notification.TextBody);

        try
        {
            await emailSender.SendAsync(message, stoppingToken);
            return (SendOutcome.Sent, null);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return (SendOutcome.Cancelled, null);
        }
        catch (Exception exception)
        {
            return (SendOutcome.Failed, exception);
        }
    }

    /// <summary>
    /// Records the outcome in one transaction. Returns true only when the email was sent but the result could not be
    /// committed for a reason other than optimistic concurrency.
    /// </summary>
    private async Task<bool> RecordOutcomeAsync(ISession session, Notification notification, SendOutcome outcome, Exception? failure)
    {
        using var transaction = session.BeginTransaction();
        var now = UtcNow();

        var transition = outcome switch
        {
            SendOutcome.Sent => notification.MarkSent(now),
            SendOutcome.Failed => notification.RegisterFailure(failure?.Message, now),
            _ => notification.ReleaseUnsent(now),
        };

        if (transition.IsFailure)
        {
            LogTransitionRejected(logger, notification.Id, notification.Status, transition.Error.Code);
            return false;
        }

        if (notification.Status == NotificationStatus.Failed && notification.NotifyOnFailureTo is not null)
        {
            await TryQueueFailureAlertAsync(session, notification, CancellationToken.None);
        }

        try
        {
            await transaction.CommitAsync(CancellationToken.None);
        }
        catch (Exception exception) when (IsStaleState(exception))
        {
            LogConcurrentUpdate(logger, exception, notification.Id, outcome.ToString());
            return false;
        }
        catch (Exception exception) when (outcome == SendOutcome.Sent)
        {
            LogSentButNotRecorded(logger, exception, notification.Id);
            return true;
        }

        switch (outcome)
        {
            case SendOutcome.Sent:
                LogSent(logger, notification.Id, notification.Attempts);
                break;
            case SendOutcome.Failed when notification.Status == NotificationStatus.Failed:
                LogFailedPermanently(logger, failure, notification.Id, notification.Attempts);
                break;
            case SendOutcome.Failed:
                LogFailedWillRetry(logger, failure, notification.Id, notification.Attempts, notification.MaxAttempts, notification.NextAttemptAtUtc);
                break;
            default:
                LogReleasedOnShutdown(logger, notification.Id, notification.Attempts);
                break;
        }

        return false;
    }

    /// <summary>
    /// Adds the <c>delivery-failed-alert</c> notification for a failed notification to the current session and transaction.
    /// Problems with the template or the alert content are logged and never prevent the failure from being recorded.
    /// </summary>
    private async Task TryQueueFailureAlertAsync(ISession session, Notification failed, CancellationToken cancellationToken)
    {
        try
        {
            var template = await session
                .Query<EmailTemplate>()
                .Where(candidate => candidate.Key == FailureAlertTemplateKey)
                .SingleOrDefaultAsync(cancellationToken);

            if (template is null)
            {
                LogAlertTemplateMissing(logger, failed.Id, FailureAlertTemplateKey);
                return;
            }

            var variables = new Dictionary<string, string>
            {
                ["OriginalTo"] = failed.ToEmail,
                ["OriginalSubject"] = failed.Subject,
                ["NotificationId"] = failed.Id.ToString(),
                ["Attempts"] = failed.Attempts.ToString(CultureInfo.InvariantCulture),
                ["LastError"] = failed.LastError ?? string.Empty,
            };

            var rendered = templateRenderer.Render(template, variables);

            if (rendered.IsFailure)
            {
                LogAlertNotQueued(logger, failed.Id, rendered.Error.Code, rendered.Error.Message);
                return;
            }

            var alert = Notification.Queue(
                failed.AppKey,
                failed.NotifyOnFailureTo,
                null,
                rendered.Value.Subject,
                rendered.Value.HtmlBody,
                rendered.Value.TextBody,
                FailureAlertTemplateKey,
                null,
                failed.CorrelationId,
                timeProvider);

            if (alert.IsFailure)
            {
                LogAlertNotQueued(logger, failed.Id, alert.Error.Code, alert.Error.Message);
                return;
            }

            await session.SaveAsync(alert.Value, cancellationToken);
            LogAlertQueued(logger, alert.Value.Id, failed.Id);
        }
        catch (Exception exception) when (exception is not OperationCanceledException && !IsStaleState(exception))
        {
            LogAlertFailed(logger, exception, failed.Id);
        }
    }

    private DateTime UtcNow() => timeProvider.GetUtcNow().UtcDateTime;

    private void AddAppKeyFilter(DbCommand command)
    {
        if (AppKeyFilter is not null)
        {
            AddParameter(command, "appKey", NpgsqlDbType.Varchar, AppKeyFilter);
        }
    }

    private static DbCommand CreateCommand(ISession session, ITransaction transaction, string sql)
    {
        var command = session.Connection.CreateCommand();
        transaction.Enlist(command);
        command.CommandText = sql;
        return command;
    }

    private static void AddParameter(DbCommand command, string name, NpgsqlDbType type, object value) =>
        command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value });

    private static IDisposable? PushCorrelationId(string? correlationId) =>
        correlationId is null ? null : LogContext.PushProperty(CorrelationIdProperty, correlationId);

    private static bool IsStaleState(Exception? exception)
    {
        while (exception is not null)
        {
            if (exception is StaleStateException)
            {
                return true;
            }

            exception = exception.InnerException;
        }

        return false;
    }

    private sealed record RecoveredNotification(Guid Id, string Status, string AppKey, string? CorrelationId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Claimed {Count} notifications for delivery")]
    private static partial void LogBatchClaimed(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Notification {NotificationId} sent on attempt {Attempts}")]
    private static partial void LogSent(ILogger logger, Guid notificationId, int attempts);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Notification {NotificationId} failed on attempt {Attempts} of {MaxAttempts}; next attempt at {NextAttemptAtUtc:O}")]
    private static partial void LogFailedWillRetry(ILogger logger, Exception? exception, Guid notificationId, int attempts, int maxAttempts, DateTime nextAttemptAtUtc);

    [LoggerMessage(Level = LogLevel.Error, Message = "Notification {NotificationId} failed permanently after {Attempts} attempts")]
    private static partial void LogFailedPermanently(ILogger logger, Exception? exception, Guid notificationId, int attempts);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Notification {NotificationId} was released unsent because the worker is stopping; the attempt was given back ({Attempts} attempts counted)")]
    private static partial void LogReleasedOnShutdown(ILogger logger, Guid notificationId, int attempts);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Notification {NotificationId} was changed concurrently (probably recovered as stuck); the {Outcome} result was not recorded")]
    private static partial void LogConcurrentUpdate(ILogger logger, Exception exception, Guid notificationId, string outcome);

    [LoggerMessage(Level = LogLevel.Error, Message = "Notification {NotificationId} was sent but its result could not be recorded; it will be sent again after recovery")]
    private static partial void LogSentButNotRecorded(ILogger logger, Exception exception, Guid notificationId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Notification {NotificationId} was not found after the claim")]
    private static partial void LogNotFound(ILogger logger, Guid notificationId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Notification {NotificationId} is no longer held by this claim ({Status}, version {Version}, claimed version {ClaimedVersion}); it was not sent")]
    private static partial void LogClaimLost(ILogger logger, Guid notificationId, NotificationStatus status, int version, int claimedVersion);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Notification {NotificationId} rejected the transition from {Status}: {ErrorCode}")]
    private static partial void LogTransitionRejected(ILogger logger, Guid notificationId, NotificationStatus status, string errorCode);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unexpected error while processing notification {NotificationId}")]
    private static partial void LogProcessingFailed(ILogger logger, Exception exception, Guid notificationId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Notification {NotificationId} was stuck in Processing and was recovered as {Status}")]
    private static partial void LogRecoveredStuck(ILogger logger, Guid notificationId, string status);

    [LoggerMessage(Level = LogLevel.Information, Message = "Delivery failure alert {AlertId} queued for notification {NotificationId}")]
    private static partial void LogAlertQueued(ILogger logger, Guid alertId, Guid notificationId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Delivery failure alert for notification {NotificationId} was not queued: template {TemplateKey} does not exist")]
    private static partial void LogAlertTemplateMissing(ILogger logger, Guid notificationId, string templateKey);

    [LoggerMessage(Level = LogLevel.Error, Message = "Delivery failure alert for notification {NotificationId} was not queued: {ErrorCode} {ErrorMessage}")]
    private static partial void LogAlertNotQueued(ILogger logger, Guid notificationId, string errorCode, string errorMessage);

    [LoggerMessage(Level = LogLevel.Error, Message = "Delivery failure alert for notification {NotificationId} was not queued because of an unexpected error")]
    private static partial void LogAlertFailed(ILogger logger, Exception exception, Guid notificationId);
}
