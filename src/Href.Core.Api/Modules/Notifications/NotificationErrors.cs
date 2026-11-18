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
        "The HTML body is required and the bodies cannot contain NUL characters.");

    public static readonly Error InvalidTemplateKey = Error.Validation(
        "Notifications.InvalidTemplateKey",
        "The template key cannot exceed 100 characters.");

    public static readonly Error InvalidNotifyOnFailureTo = Error.Validation(
        "Notifications.InvalidNotifyOnFailureTo",
        "The failure notification address must be a single valid email address of at most 254 characters.");

    public static readonly Error InvalidCorrelationId = Error.Validation(
        "Notifications.InvalidCorrelationId",
        "The correlation ID cannot exceed 64 characters.");

    public static readonly Error InvalidTransition = Error.Conflict(
        "Notifications.InvalidTransition",
        "The notification status does not allow this transition.");
}
