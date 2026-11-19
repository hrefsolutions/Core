using Href.Core.Api.Common.Results;

namespace Href.Core.Api.Modules.Notifications;

/// <summary>
/// Replaces <c>{{Key}}</c> placeholders in email content with caller supplied variables.
/// Values inserted in the HTML body are HTML-encoded, which is safe only in element text and in quoted attributes.
/// URLs placed in <c>href</c> or <c>src</c> must be validated by the caller, and callers must not pre-encode values.
/// Variables whose key is empty, padded with whitespace or does not match <c>[A-Za-z0-9_.]+</c> are ignored like extra variables.
/// Each rendered part has a length limit (see <see cref="TemplateRenderer"/>); exceeding it returns <c>Templates.RenderedContentTooLarge</c>.
/// </summary>
public interface ITemplateRenderer
{
    /// <summary>
    /// Renders the subject, HTML body and text body of a template.
    /// </summary>
    Result<RenderedEmail> Render(EmailTemplate template, IReadOnlyDictionary<string, string>? variables);

    /// <summary>
    /// Renders raw email parts without an entity (preview of unsaved content, validation of seeded templates).
    /// Values are HTML-encoded in the HTML body, inserted as is in the text body and collapsed to a single line in the subject
    /// (control characters and U+2028/U+2029 become spaces). NUL characters in values are not stripped here.
    /// </summary>
    Result<RenderedEmail> Render(string subject, string htmlBody, string? textBody, IReadOnlyDictionary<string, string>? variables);

    /// <summary>
    /// Renders a single text. When <paramref name="htmlEncode"/> is true the values are HTML-encoded; values are never trimmed or collapsed.
    /// The rendered text cannot exceed <paramref name="maxLength"/> characters.
    /// </summary>
    Result<string> Render(
        string text,
        IReadOnlyDictionary<string, string>? variables,
        bool htmlEncode,
        int maxLength = TemplateRenderer.MaxRenderedBodyLength);

    /// <summary>
    /// Lists the distinct placeholder keys of a text (case-insensitive, first spelling wins) in order of first appearance.
    /// </summary>
    IReadOnlyList<string> GetPlaceholders(string? text);
}
