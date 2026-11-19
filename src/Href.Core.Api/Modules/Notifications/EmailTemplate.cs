using System.Text.RegularExpressions;
using Href.Core.Api.Common.Domain;
using Href.Core.Api.Common.Results;

namespace Href.Core.Api.Modules.Notifications;

/// <summary>
/// Represents a reusable email template whose subject and bodies contain <c>{{Key}}</c> placeholders.
/// </summary>
public partial class EmailTemplate : Entity, IAuditableEntity
{
    public const int KeyMinLength = 3;
    public const int KeyMaxLength = 100;
    public const int NameMaxLength = 100;
    public const int DescriptionMaxLength = 500;
    public const int SubjectMaxLength = 500;

    protected EmailTemplate()
    {
    }

    public virtual string Key { get; protected set; } = string.Empty;

    public virtual string Name { get; protected set; } = string.Empty;

    public virtual string? Description { get; protected set; }

    public virtual string Subject { get; protected set; } = string.Empty;

    public virtual string HtmlBody { get; protected set; } = string.Empty;

    public virtual string? TextBody { get; protected set; }

    public virtual bool IsSystem { get; protected set; }

    public virtual DateTime CreatedAtUtc { get; protected set; }

    public virtual DateTime UpdatedAtUtc { get; protected set; }

    public static Result<EmailTemplate> Create(
        string? key,
        string? name,
        string? description,
        string? subject,
        string? htmlBody,
        string? textBody,
        bool isSystem)
    {
        var normalizedKey = key?.Trim() ?? string.Empty;

        if (normalizedKey.Length is < KeyMinLength or > KeyMaxLength || !KeyRegex().IsMatch(normalizedKey)
            || TextRules.HasControlOrLineSeparator(normalizedKey))
        {
            return EmailTemplateErrors.InvalidKey;
        }

        var contentResult = ValidateContent(name, description, subject, htmlBody, textBody, out var content);

        if (contentResult.IsFailure)
        {
            return contentResult.Error;
        }

        return new EmailTemplate
        {
            Key = normalizedKey,
            Name = content.Name,
            Description = content.Description,
            Subject = content.Subject,
            HtmlBody = content.HtmlBody,
            TextBody = content.TextBody,
            IsSystem = isSystem,
        };
    }

    public virtual Result UpdateContent(
        string? name,
        string? description,
        string? subject,
        string? htmlBody,
        string? textBody)
    {
        var contentResult = ValidateContent(name, description, subject, htmlBody, textBody, out var content);

        if (contentResult.IsFailure)
        {
            return contentResult;
        }

        Name = content.Name;
        Description = content.Description;
        Subject = content.Subject;
        HtmlBody = content.HtmlBody;
        TextBody = content.TextBody;

        return Result.Success();
    }

    private static Result ValidateContent(
        string? name,
        string? description,
        string? subject,
        string? htmlBody,
        string? textBody,
        out Content content)
    {
        content = default;

        var normalizedName = name?.Trim() ?? string.Empty;

        if (normalizedName.Length is 0 or > NameMaxLength || TextRules.HasControlOrLineSeparator(normalizedName))
        {
            return EmailTemplateErrors.InvalidName;
        }

        var normalizedDescription = string.IsNullOrWhiteSpace(description) ? null : description.Trim();

        if (normalizedDescription is not null
            && (normalizedDescription.Length > DescriptionMaxLength || TextRules.HasNul(normalizedDescription)))
        {
            return EmailTemplateErrors.InvalidDescription;
        }

        var normalizedSubject = subject?.Trim() ?? string.Empty;

        if (normalizedSubject.Length is 0 or > SubjectMaxLength
            || TextRules.HasControlOrLineSeparator(normalizedSubject))
        {
            return EmailTemplateErrors.InvalidSubject;
        }

        if (string.IsNullOrWhiteSpace(htmlBody) || TextRules.HasNul(htmlBody))
        {
            return EmailTemplateErrors.InvalidHtmlBody;
        }

        if (TextRules.HasNul(textBody))
        {
            return EmailTemplateErrors.InvalidTextBody;
        }

        content = new Content(
            normalizedName,
            normalizedDescription,
            normalizedSubject,
            htmlBody,
            string.IsNullOrWhiteSpace(textBody) ? null : textBody);

        return Result.Success();
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex KeyRegex();

    private readonly record struct Content(
        string Name,
        string? Description,
        string Subject,
        string HtmlBody,
        string? TextBody);
}
