using System.ComponentModel.DataAnnotations;

namespace Href.Core.Api.Modules.Notifications.Worker;

/// <summary>
/// Settings of the notification delivery worker, bound from the <c>Notifications:Worker</c> configuration section
/// and validated on startup.
/// </summary>
public sealed class NotificationWorkerOptions
{
    public const string SectionName = "Notifications:Worker";

    /// <summary>
    /// When false the worker does not poll (the API still queues notifications).
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Seconds to wait after a batch that was not full before polling again.
    /// </summary>
    [Range(1, 3600)]
    public int PollIntervalSeconds { get; set; } = 5;

    /// <summary>
    /// Maximum number of notifications claimed in one transaction.
    /// </summary>
    [Range(1, 500)]
    public int BatchSize { get; set; } = 20;

    /// <summary>
    /// Minutes after which a notification still in <c>Processing</c> is considered stuck and is recovered.
    /// </summary>
    [Range(1, 1440)]
    public int StuckAfterMinutes { get; set; } = 10;
}
