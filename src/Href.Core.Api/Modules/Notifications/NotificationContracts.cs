namespace Href.Core.Api.Modules.Notifications;

/// <summary>
/// Recipient of an email as sent by a client application.
/// </summary>
public sealed record EmailRecipientRequest(string? Email, string? Name);

/// <summary>
/// Body of <c>POST /api/notifications/email</c>. Exactly one mode is allowed: template mode
/// (<c>TemplateKey</c> with optional <c>Variables</c>) or HTML mode (<c>Html</c> and <c>Subject</c>, optional <c>Text</c>).
/// </summary>
public sealed record QueueEmailRequest(
    EmailRecipientRequest? To,
    string? TemplateKey,
    Dictionary<string, string>? Variables,
    string? Subject,
    string? Html,
    string? Text,
    string? NotifyOnFailureTo);

public sealed record QueuedNotificationResponse(Guid Id, string Status);

/// <summary>
/// A notification as returned to its owner. It never carries the HTML or text bodies.
/// </summary>
public sealed record NotificationResponse(
    Guid Id,
    string ToEmail,
    string? ToName,
    string Subject,
    string? TemplateKey,
    string Status,
    int Attempts,
    int MaxAttempts,
    DateTime NextAttemptAtUtc,
    DateTime? SentAtUtc,
    string? LastError,
    string? NotifyOnFailureTo,
    string? CorrelationId,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record NotificationPageResponse(
    IReadOnlyList<NotificationResponse> Items,
    int Page,
    int PageSize,
    long TotalCount);

/// <summary>
/// Filters and paging of the notification list. <c>From</c> is inclusive and <c>To</c> is exclusive.
/// </summary>
public sealed record ListNotificationsQuery(
    string? Status,
    DateTimeOffset? From,
    DateTimeOffset? To,
    int Page,
    int PageSize);
