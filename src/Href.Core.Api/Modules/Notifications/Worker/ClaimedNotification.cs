namespace Href.Core.Api.Modules.Notifications.Worker;

/// <summary>
/// A notification returned by the claim, with the <c>version</c> the claim wrote.
/// </summary>
internal readonly record struct ClaimedNotification(Guid Id, int Version);
