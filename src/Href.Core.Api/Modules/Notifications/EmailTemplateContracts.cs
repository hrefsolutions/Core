namespace Href.Core.Api.Modules.Notifications;

/// <summary>
/// Body of <c>POST /api/notifications/templates</c>. Clients can never create system templates: any <c>isSystem</c> in the JSON is ignored.
/// </summary>
public sealed record CreateEmailTemplateRequest(
    string? Key,
    string? Name,
    string? Description,
    string? Subject,
    string? HtmlBody,
    string? TextBody);

/// <summary>
/// Body of <c>PUT /api/notifications/templates/{key}</c>. The key comes from the route and cannot change; a <c>key</c> in the JSON is ignored.
/// </summary>
public sealed record UpdateEmailTemplateRequest(
    string? Name,
    string? Description,
    string? Subject,
    string? HtmlBody,
    string? TextBody);

/// <summary>
/// Body of <c>POST /api/notifications/templates/{key}/preview</c>.
/// </summary>
public sealed record PreviewEmailTemplateRequest(Dictionary<string, string>? Variables);

public sealed record EmailTemplateSummaryResponse(string Key, string Name, bool IsSystem, DateTime UpdatedAtUtc);

/// <summary>
/// A full template. <c>Placeholders</c> are the distinct placeholders of the subject, HTML body and text body, in order of appearance.
/// </summary>
public sealed record EmailTemplateResponse(
    Guid Id,
    string Key,
    string Name,
    string? Description,
    string Subject,
    string HtmlBody,
    string? TextBody,
    bool IsSystem,
    IReadOnlyList<string> Placeholders,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record EmailTemplatePreviewResponse(string Subject, string Html, string? Text);
