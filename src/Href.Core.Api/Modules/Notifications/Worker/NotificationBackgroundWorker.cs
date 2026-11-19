using Microsoft.Extensions.Options;

namespace Href.Core.Api.Modules.Notifications.Worker;

/// <summary>
/// Polls for notifications to deliver. Each iteration runs in a new DI scope: it recovers stuck notifications and
/// dispatches one batch through <see cref="INotificationDispatcher"/>. A failing recovery is logged and never blocks
/// the dispatch. When the batch was full it starts the next iteration immediately, unless every claimed notification hit
/// an unexpected error; otherwise it waits <c>PollIntervalSeconds</c> on the injected <see cref="TimeProvider"/>.
/// An exception in an iteration is logged and the loop continues, so the worker never stops the host.
/// </summary>
public sealed partial class NotificationBackgroundWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<NotificationWorkerOptions> options,
    TimeProvider timeProvider,
    ILogger<NotificationBackgroundWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;

        if (!settings.Enabled)
        {
            LogDisabled(logger);
            return;
        }

        LogStarted(logger, settings.PollIntervalSeconds, settings.BatchSize, settings.StuckAfterMinutes);
        var pollInterval = TimeSpan.FromSeconds(settings.PollIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            var batchWasFull = false;

            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var dispatcher = scope.ServiceProvider.GetRequiredService<INotificationDispatcher>();

                try
                {
                    await dispatcher.RecoverStuckAsync(stoppingToken);
                }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                {
                    LogRecoveryFailed(logger, exception);
                }

                var result = await dispatcher.DispatchBatchAsync(stoppingToken);
                batchWasFull = result.Claimed >= settings.BatchSize && result.UnexpectedErrors < result.Claimed;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                LogIterationFailed(logger, exception);
            }

            if (batchWasFull)
            {
                continue;
            }

            try
            {
                await Task.Delay(pollInterval, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        LogStopped(logger);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Notification worker is disabled (Notifications:Worker:Enabled = false)")]
    private static partial void LogDisabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Notification worker started (poll every {PollIntervalSeconds} s, batch {BatchSize}, stuck after {StuckAfterMinutes} min)")]
    private static partial void LogStarted(ILogger logger, int pollIntervalSeconds, int batchSize, int stuckAfterMinutes);

    [LoggerMessage(Level = LogLevel.Information, Message = "Notification worker stopped")]
    private static partial void LogStopped(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Recovery of stuck notifications failed; dispatch continues")]
    private static partial void LogRecoveryFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Notification worker iteration failed; retrying after the poll interval")]
    private static partial void LogIterationFailed(ILogger logger, Exception exception);
}
