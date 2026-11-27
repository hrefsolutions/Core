using Href.Core.Api.Common.Results;

namespace Href.Core.Api.Modules.Applications;

public static class ApplicationErrors
{
    public static readonly Error InvalidAppKey = Error.Validation(
        "Applications.InvalidAppKey",
        "The application key must be a lowercase slug (3 to 50 characters, letters, digits and hyphens).");

    public static readonly Error InvalidName = Error.Validation(
        "Applications.InvalidName",
        "The application name is required and must be between 1 and 100 characters.");

    public static readonly Error InvalidDescription = Error.Validation(
        "Applications.InvalidDescription",
        "The application description cannot exceed 500 characters.");

    public static readonly Error InvalidStatus = Error.Validation(
        "Applications.InvalidStatus",
        "The application status must be 'Active' or 'Suspended'.");

    public static readonly Error AlreadySuspended = Error.Conflict(
        "Applications.AlreadySuspended",
        "The application is already suspended.");

    public static readonly Error AlreadyActive = Error.Conflict(
        "Applications.AlreadyActive",
        "The application is already active.");

    public static readonly Error AppKeyTaken = Error.Conflict(
        "Applications.AppKeyTaken",
        "An application with the specified key already exists.");

    public static readonly Error NotFound = Error.NotFound(
        "Applications.NotFound",
        "The requested application was not found.");

    public static readonly Error Reserved = Error.Conflict(
        "Applications.Reserved",
        "The reserved core application cannot be suspended, deleted or opened to self-registration.");

    public static readonly Error DeleteRequiresSuspended = Error.Conflict(
        "Applications.DeleteRequiresSuspended",
        "Only a suspended application can be deleted. Suspend the application first.");

    public static readonly Error HasUserAccesses = Error.Conflict(
        "Applications.HasUserAccesses",
        "One or more users still have access to the application. Revoke every access before deleting the application.");
}
