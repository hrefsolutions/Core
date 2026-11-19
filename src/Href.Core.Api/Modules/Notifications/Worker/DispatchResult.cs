namespace Href.Core.Api.Modules.Notifications.Worker;

/// <summary>
/// Outcome of one dispatched batch: how many notifications were claimed and how many of them hit an unexpected
/// (non-send) error, such as a database failure while recording the result.
/// </summary>
public readonly record struct DispatchResult(int Claimed, int UnexpectedErrors);
