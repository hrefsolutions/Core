namespace Href.Core.Api.Modules.Notifications.Worker;

/// <summary>
/// One iteration of notification delivery: recovery of stuck notifications and dispatch of one batch.
/// </summary>
public interface INotificationDispatcher
{
    /// <summary>
    /// Returns notifications stuck in <c>Processing</c> for longer than <c>StuckAfterMinutes</c> to <c>Pending</c>,
    /// or to <c>Failed</c> when no attempts are left. Returns the number of recovered notifications.
    /// </summary>
    Task<int> RecoverStuckAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Claims up to <c>BatchSize</c> due notifications, sends each one and records the outcome.
    /// Returns the number of claimed notifications and how many of them hit an unexpected error.
    /// </summary>
    Task<DispatchResult> DispatchBatchAsync(CancellationToken cancellationToken);
}
