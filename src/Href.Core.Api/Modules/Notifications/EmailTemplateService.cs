using Href.Core.Api.Common.Results;
using Href.Core.Api.Modules.Audit;
using NHibernate;
using NHibernate.Linq;
using Npgsql;
using ISession = NHibernate.ISession;

namespace Href.Core.Api.Modules.Notifications;

/// <summary>
/// Administers the global email templates over <see cref="ISession"/>. Every change and its audit record are committed in the same transaction.
/// Template keys are matched exactly (case-sensitive); a key that is not a valid slug is treated as unknown.
/// </summary>
public sealed class EmailTemplateService(
    ISession session,
    ITemplateRenderer templateRenderer,
    IAuditService auditService)
{
    private const string KeyUniqueIndexName = "ux_email_templates_key";

    /// <summary>
    /// Lists every template ordered by key. The catalog is small, so the list is not paged.
    /// </summary>
    public async Task<IReadOnlyList<EmailTemplateSummaryResponse>> ListAsync(CancellationToken cancellationToken)
    {
        var rows = await session.Query<EmailTemplate>()
            .Select(template => new SummaryRow
            {
                Key = template.Key,
                Name = template.Name,
                IsSystem = template.IsSystem,
                UpdatedAtUtc = template.UpdatedAtUtc,
            })
            .ToListAsync(cancellationToken);

        return rows
            .OrderBy(row => row.Key, StringComparer.Ordinal)
            .Select(row => new EmailTemplateSummaryResponse(row.Key, row.Name, row.IsSystem, row.UpdatedAtUtc))
            .ToList();
    }

    public async Task<Result<EmailTemplateResponse>> GetByKeyAsync(string key, CancellationToken cancellationToken)
    {
        var template = await FindAsync(key, cancellationToken);

        if (template is null)
        {
            return EmailTemplateErrors.NotFound;
        }

        return ToResponse(template);
    }

    /// <summary>
    /// Creates a custom template (never a system one). A duplicate key, including one lost to a concurrent request, returns <c>Templates.KeyTaken</c>.
    /// </summary>
    public async Task<Result<EmailTemplateResponse>> CreateAsync(
        CreateEmailTemplateRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var createResult = EmailTemplate.Create(
            request.Key,
            request.Name,
            request.Description,
            request.Subject,
            request.HtmlBody,
            request.TextBody,
            isSystem: false);

        if (createResult.IsFailure)
        {
            return createResult.Error;
        }

        var template = createResult.Value;

        using var transaction = session.BeginTransaction();

        var exists = await session.Query<EmailTemplate>()
            .AnyAsync(existing => existing.Key == template.Key, cancellationToken);

        if (exists)
        {
            return EmailTemplateErrors.KeyTaken;
        }

        try
        {
            await session.SaveAsync(template, cancellationToken);
            await auditService.RecordAsync(
                new AuditEntry(
                    AuditActions.TemplateCreated,
                    EntityType: nameof(EmailTemplate),
                    EntityId: template.Id.ToString(),
                    NewValues: EmailTemplateSnapshot.From(template)),
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception exception) when (IsKeyUniqueViolation(exception))
        {
            return EmailTemplateErrors.KeyTaken;
        }

        return ToResponse(template);
    }

    /// <summary>
    /// Replaces the content of a template. The key never changes. A system template must keep exactly the placeholders the Core flows provide.
    /// </summary>
    public async Task<Result<EmailTemplateResponse>> UpdateAsync(
        string key,
        UpdateEmailTemplateRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var transaction = session.BeginTransaction();

        var template = await FindForUpdateAsync(key, cancellationToken);

        if (template is null)
        {
            return EmailTemplateErrors.NotFound;
        }

        var validation = EmailTemplate.ValidateContent(
            request.Name,
            request.Description,
            request.Subject,
            request.HtmlBody,
            request.TextBody);

        if (validation.IsFailure)
        {
            return validation.Error;
        }

        if (template.IsSystem)
        {
            var placeholderResult = CheckSystemTemplate(
                template,
                request.Subject!.Trim(),
                request.HtmlBody!,
                request.TextBody);

            if (placeholderResult.IsFailure)
            {
                return placeholderResult.Error;
            }
        }

        var oldValues = EmailTemplateSnapshot.From(template);

        var updateResult = template.UpdateContent(
            request.Name,
            request.Description,
            request.Subject,
            request.HtmlBody,
            request.TextBody);

        if (updateResult.IsFailure)
        {
            return updateResult.Error;
        }

        var newValues = EmailTemplateSnapshot.From(template);

        if (newValues == oldValues)
        {
            await transaction.CommitAsync(cancellationToken);
            return ToResponse(template);
        }

        await auditService.RecordAsync(
            new AuditEntry(
                AuditActions.TemplateUpdated,
                EntityType: nameof(EmailTemplate),
                EntityId: template.Id.ToString(),
                OldValues: oldValues,
                NewValues: newValues),
            cancellationToken);
        await session.FlushAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return ToResponse(template);
    }

    /// <summary>
    /// Deletes a custom template. Existing notifications keep their <c>TemplateKey</c> text, since there is no foreign key.
    /// </summary>
    public async Task<Result> DeleteAsync(string key, CancellationToken cancellationToken)
    {
        using var transaction = session.BeginTransaction();

        var template = await FindForUpdateAsync(key, cancellationToken);

        if (template is null)
        {
            return EmailTemplateErrors.NotFound;
        }

        if (template.IsSystem)
        {
            return EmailTemplateErrors.SystemTemplate;
        }

        var oldValues = EmailTemplateSnapshot.From(template);

        await session.DeleteAsync(template, cancellationToken);
        await auditService.RecordAsync(
            new AuditEntry(
                AuditActions.TemplateDeleted,
                EntityType: nameof(EmailTemplate),
                EntityId: template.Id.ToString(),
                OldValues: oldValues),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return Result.Success();
    }

    /// <summary>
    /// Renders a stored template with the supplied variables without storing anything or recording an audit entry.
    /// </summary>
    public async Task<Result<EmailTemplatePreviewResponse>> PreviewAsync(
        string key,
        PreviewEmailTemplateRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var template = await FindAsync(key, cancellationToken);

        if (template is null)
        {
            return EmailTemplateErrors.NotFound;
        }

        var renderResult = templateRenderer.Render(template, request.Variables);

        if (renderResult.IsFailure)
        {
            return renderResult.Error;
        }

        var rendered = renderResult.Value;

        return new EmailTemplatePreviewResponse(rendered.Subject, rendered.HtmlBody, rendered.TextBody);
    }

    internal static bool IsKeyUniqueViolation(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: KeyUniqueIndexName })
            {
                return true;
            }
        }

        return false;
    }

    private async Task<EmailTemplate?> FindAsync(string key, CancellationToken cancellationToken)
    {
        if (!EmailTemplate.IsValidKey(key))
        {
            return null;
        }

        return await session.Query<EmailTemplate>()
            .FirstOrDefaultAsync(template => template.Key == key, cancellationToken);
    }

    private async Task<EmailTemplate?> FindForUpdateAsync(string key, CancellationToken cancellationToken)
    {
        if (!EmailTemplate.IsValidKey(key))
        {
            return null;
        }

        var rows = await session.Query<EmailTemplate>()
            .WithLock(LockMode.Upgrade)
            .Where(template => template.Key == key)
            .ToListAsync(cancellationToken);

        return rows.FirstOrDefault();
    }

    private Result CheckSystemTemplate(EmailTemplate template, string subject, string htmlBody, string? textBody)
    {
        var used = AllPlaceholders(subject, htmlBody, textBody);

        var required = SystemTemplateRequirements.TryGet(template.Key, out var mapped)
            ? mapped
            : AllPlaceholders(template.Subject, template.HtmlBody, template.TextBody);

        var missing = required
            .Where(name => !used.Contains(name, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (missing.Count > 0)
        {
            return EmailTemplateErrors.MissingRequiredPlaceholders(missing);
        }

        var unknown = used
            .Where(name => !required.Contains(name, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (unknown.Count > 0)
        {
            return EmailTemplateErrors.UnknownPlaceholders(unknown);
        }

        if (SystemTemplateRequirements.TryGetWorstCaseVariables(template.Key, out var worstCase))
        {
            var rendered = templateRenderer.Render(subject, htmlBody, textBody, worstCase);

            if (rendered.IsFailure)
            {
                return EmailTemplateErrors.SystemTemplateNotRenderable(rendered.Error.Message);
            }
        }

        return Result.Success();
    }

    private List<string> AllPlaceholders(string subject, string htmlBody, string? textBody)
    {
        var distinct = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var part in new[] { subject, htmlBody, textBody })
        {
            foreach (var name in templateRenderer.GetPlaceholders(part))
            {
                distinct.TryAdd(name, name);
            }
        }

        return distinct.Values.ToList();
    }

    private EmailTemplateResponse ToResponse(EmailTemplate template) =>
        new(
            template.Id,
            template.Key,
            template.Name,
            template.Description,
            template.Subject,
            template.HtmlBody,
            template.TextBody,
            template.IsSystem,
            AllPlaceholders(template.Subject, template.HtmlBody, template.TextBody),
            template.CreatedAtUtc,
            template.UpdatedAtUtc);

    private sealed class SummaryRow
    {
        public string Key { get; init; } = string.Empty;

        public string Name { get; init; } = string.Empty;

        public bool IsSystem { get; init; }

        public DateTime UpdatedAtUtc { get; init; }
    }
}

/// <summary>
/// Audit snapshot of an email template. Template content is written by administrators and holds no secrets, so it is recorded in full.
/// </summary>
public sealed record EmailTemplateSnapshot(
    string Key,
    string Name,
    string? Description,
    string Subject,
    string HtmlBody,
    string? TextBody,
    bool IsSystem)
{
    /// <summary>
    /// Copies the current values of <paramref name="template"/>.
    /// </summary>
    public static EmailTemplateSnapshot From(EmailTemplate template) =>
        new(
            template.Key,
            template.Name,
            template.Description,
            template.Subject,
            template.HtmlBody,
            template.TextBody,
            template.IsSystem);
}
