namespace Href.Core.Api.Modules.Notifications.Email;

/// <summary>
/// One email for a single recipient, ready to send. The subject is a single line.
/// </summary>
public sealed record EmailMessage(string ToEmail, string? ToName, string Subject, string HtmlBody, string? TextBody = null)
{
    /// <summary>
    /// Throws <see cref="ArgumentException"/> when a field could break the message (invalid recipient, line breaks or
    /// control characters in single-line fields, empty HTML body).
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ToEmail) || !TextRules.IsSingleEmailAddress(ToEmail, Notification.EmailMaxLength))
        {
            throw new ArgumentException("The recipient must be a single bare email address.", nameof(ToEmail));
        }

        if (ToName is not null && TextRules.HasControlOrLineSeparator(ToName))
        {
            throw new ArgumentException("The recipient name must not contain control characters.", nameof(ToName));
        }

        if (string.IsNullOrWhiteSpace(Subject) || TextRules.HasControlOrLineSeparator(Subject))
        {
            throw new ArgumentException("The subject must be a single line without control characters.", nameof(Subject));
        }

        if (string.IsNullOrWhiteSpace(HtmlBody))
        {
            throw new ArgumentException("The HTML body is required.", nameof(HtmlBody));
        }
    }
}
