using Href.Core.Api.Common.Results;

namespace Href.Core.Api.Modules.Notifications;

public static class EmailTemplateErrors
{
    public static readonly Error InvalidKey = Error.Validation(
        "Templates.InvalidKey",
        "The template key must be a lowercase slug (3 to 100 characters, letters, digits and hyphens).");

    public static readonly Error InvalidName = Error.Validation(
        "Templates.InvalidName",
        "The template name is required and must be between 1 and 100 characters.");

    public static readonly Error InvalidDescription = Error.Validation(
        "Templates.InvalidDescription",
        "The template description cannot exceed 500 characters.");

    public static readonly Error InvalidSubject = Error.Validation(
        "Templates.InvalidSubject",
        "The template subject is required, cannot exceed 500 characters and cannot contain line breaks.");

    public static readonly Error InvalidHtmlBody = Error.Validation(
        "Templates.InvalidHtmlBody",
        "The template HTML body is required and cannot contain NUL characters.");

    public static readonly Error InvalidTextBody = Error.Validation(
        "Templates.InvalidTextBody",
        "The template text body cannot contain NUL characters.");
}
