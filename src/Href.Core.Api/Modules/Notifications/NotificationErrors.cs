using Href.Core.Api.Common.Results;

namespace Href.Core.Api.Modules.Notifications;

public static class NotificationErrors
{
    public static readonly Error InvalidAppKey = Error.Validation(
        "Notifications.InvalidAppKey",
        "The requesting application key is required and cannot exceed 50 characters.");

    public static readonly Error InvalidRecipient = Error.Validation(
        "Notifications.InvalidRecipient",
        "The recipient must be a single valid email address of at most 254 characters.");

    public static readonly Error InvalidRecipientName = Error.Validation(
        "Notifications.InvalidRecipientName",
        "The recipient name cannot exceed 200 characters and cannot contain line breaks.");

    public static readonly Error InvalidSubject = Error.Validation(
        "Notifications.InvalidSubject",
        "The subject is required, cannot exceed 500 characters and cannot contain line breaks.");

    public static readonly Error InvalidContent = Error.Validation(
        "Notifications.InvalidContent",
        "Provide either a templateKey (with optional variables) or an html body with a subject, never both; the bodies cannot contain NUL characters.");

    public static readonly Error InvalidTemplateKey = Error.Validation(
        "Notifications.InvalidTemplateKey",
        "The template key cannot exceed 100 characters.");

    public static readonly Error InvalidNotifyOnFailureTo = Error.Validation(
        "Notifications.InvalidNotifyOnFailureTo",
        "The failure notification address must be a single valid email address of at most 254 characters.");

    public static readonly Error InvalidCorrelationId = Error.Validation(
        "Notifications.InvalidCorrelationId",
        "The correlation ID cannot exceed 64 characters.");

    public static readonly Error ContentTooLarge = Error.Validation(
        "Notifications.ContentTooLarge",
        "The HTML body and the text body of an email cannot exceed 512 KB (524288 bytes) each, measured as UTF-8.");

    public static readonly Error InvalidStatus = Error.Validation(
        "Notifications.InvalidStatus",
        "The status must be one of 'Pending', 'Processing', 'Sent' or 'Failed'.");

    public static readonly Error InvalidDateRange = Error.Validation(
        "Notifications.InvalidDateRange",
        "The 'from' date cannot be later than the 'to' date.");

    public static readonly Error InvalidPage = Error.Validation(
        "Notifications.InvalidPage",
        "The page must be 1 or greater.");

    public static readonly Error InvalidPageSize = Error.Validation(
        "Notifications.InvalidPageSize",
        "The page size must be between 1 and 200.");

    public static readonly Error NotFound = Error.NotFound(
        "Notifications.NotFound",
        "The requested notification was not found.");

    public static readonly Error InvalidTransition = Error.Conflict(
        "Notifications.InvalidTransition",
        "The notification status does not allow this transition.");
}
