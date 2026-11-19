namespace Href.Core.Api.Modules.Notifications;

/// <summary>
/// The result of rendering an email template: every placeholder is already replaced.
/// </summary>
public sealed record RenderedEmail(string Subject, string HtmlBody, string? TextBody);
