using Href.Core.Api.Modules.Notifications.Email;
using Microsoft.Extensions.Options;

namespace Href.Core.Api.Modules.Notifications.Worker;

/// <summary>
/// Logs a warning at startup when an enabled worker could still be sending a batch after <c>StuckAfterMinutes</c>
/// (<c>StuckAfterMinutes * 60 &lt; BatchSize * Smtp:TimeoutSeconds</c>): the recovery could then release rows that are
/// still being sent, which can lead to duplicate emails.
/// </summary>
public sealed partial class NotificationWorkerStartupCheck(
    IOptions<NotificationWorkerOptions> workerOptions,
    IOptions<SmtpOptions> smtpOptions,
    ILogger<NotificationWorkerStartupCheck> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var worker = workerOptions.Value;
        var smtpTimeoutSeconds = smtpOptions.Value.TimeoutSeconds;

        if (worker.Enabled && (long)worker.StuckAfterMinutes * 60 < (long)worker.BatchSize * smtpTimeoutSeconds)
        {
            LogStuckWindowTooShort(logger, worker.StuckAfterMinutes, worker.BatchSize, smtpTimeoutSeconds);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Notifications:Worker:StuckAfterMinutes ({StuckAfterMinutes} min) is less than BatchSize ({BatchSize}) x Smtp:TimeoutSeconds ({TimeoutSeconds} s); the recovery may release notifications that are still being sent and cause duplicate emails")]
    private static partial void LogStuckWindowTooShort(ILogger logger, int stuckAfterMinutes, int batchSize, int timeoutSeconds);
}
