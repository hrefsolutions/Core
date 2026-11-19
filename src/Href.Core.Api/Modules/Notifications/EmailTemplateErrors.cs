using Href.Core.Api.Common.Results;

namespace Href.Core.Api.Modules.Notifications;

public static class EmailTemplateErrors
{
    public static readonly Error NotFound = Error.NotFound(
        "Templates.NotFound",
        "The requested email template was not found.");

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

    /// <summary>
    /// Builds the error for a rendered part that exceeds its length limit.
    /// </summary>
    public static Error RenderedContentTooLarge(string part, int maxLength) => Error.Validation(
        "Templates.RenderedContentTooLarge",
        $"Rendered {part} exceeds {maxLength} characters.");

    /// <summary>
    /// Builds the error for variable keys that differ only by case.
    /// </summary>
    public static Error DuplicateVariables(IEnumerable<string> keys) => Error.Validation(
        "Templates.DuplicateVariables",
        $"Duplicate variables (case-insensitive): {string.Join(", ", keys)}");

    /// <summary>
    /// Builds the error for placeholders without a value, listing every missing key.
    /// </summary>
    public static Error MissingVariables(IEnumerable<string> keys) => Error.Validation(
        "Templates.MissingVariables",
        $"Missing variables: {string.Join(", ", keys)}");
}
