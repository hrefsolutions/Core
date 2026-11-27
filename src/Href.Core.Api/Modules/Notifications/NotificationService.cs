using System.Text;
using Href.Core.Api.Common.Results;
using Href.Core.Api.Modules.Applications;
using Href.Core.Api.Modules.Audit;
using NHibernate;
using NHibernate.Linq;
using ISession = NHibernate.ISession;

namespace Href.Core.Api.Modules.Notifications;

/// <summary>
/// Queues and reads email notifications over <see cref="ISession"/>. A queued notification and its audit record
/// are committed in the same transaction.
/// </summary>
public sealed class NotificationService(
    ISession session,
    ITemplateRenderer templateRenderer,
    IAuditService auditService,
    TimeProvider timeProvider)
{
    public const int MaxPageSize = 200;
    public const int DefaultPageSize = 50;

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false);

    /// <summary>
    /// Validates the request mode (template or HTML) and queues the email.
    /// </summary>
    public Task<Result<Notification>> QueueEmailAsync(
        string appKey,
        QueueEmailRequest request,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.To?.Email is null)
        {
            return Task.FromResult<Result<Notification>>(NotificationErrors.InvalidRecipient);
        }

        if (request.TemplateKey is not null)
        {
            if (string.IsNullOrWhiteSpace(request.TemplateKey)
                || request.Html is not null
                || request.Subject is not null
                || request.Text is not null)
            {
                return Task.FromResult<Result<Notification>>(NotificationErrors.InvalidContent);
            }

            return QueueTemplateAsync(
                appKey,
                request.To,
                request.TemplateKey,
                request.Variables,
                request.NotifyOnFailureTo,
                correlationId,
                cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(request.Html)
            || string.IsNullOrWhiteSpace(request.Subject)
            || request.Variables is { Count: > 0 })
        {
            return Task.FromResult<Result<Notification>>(NotificationErrors.InvalidContent);
        }

        return QueueHtmlAsync(
            appKey,
            request.To,
            request.Subject,
            request.Html,
            request.Text,
            request.NotifyOnFailureTo,
            correlationId,
            cancellationToken);
    }

    /// <summary>
    /// Renders the template identified by <paramref name="templateKey"/> (exact, case-sensitive match) and queues the result.
    /// If the session already has an active transaction (a caller composing several changes), the notification and its
    /// audit record join it and the caller owns the commit or rollback; otherwise this method opens and commits its own transaction.
    /// </summary>
    public async Task<Result<Notification>> QueueTemplateAsync(
        string appKey,
        EmailRecipientRequest? to,
        string templateKey,
        IReadOnlyDictionary<string, string>? variables,
        string? notifyOnFailureTo,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(templateKey);

        if (to?.Email is null)
        {
            return NotificationErrors.InvalidRecipient;
        }

        var template = await session.Query<EmailTemplate>()
            .Where(candidate => candidate.Key == templateKey)
            .SingleOrDefaultAsync(cancellationToken);

        if (template is null)
        {
            return EmailTemplateErrors.NotFound;
        }

        var renderResult = templateRenderer.Render(template, variables);

        if (renderResult.IsFailure)
        {
            return renderResult.Error;
        }

        var rendered = renderResult.Value;

        return await PersistAsync(
            appKey,
            to,
            rendered.Subject,
            rendered.HtmlBody,
            rendered.TextBody,
            template.Key,
            notifyOnFailureTo,
            correlationId,
            cancellationToken);
    }

    /// <summary>
    /// Queues an email whose content was rendered by the caller. Each body is limited to 512 KB of UTF-8.
    /// If the session already has an active transaction (a caller composing several changes), the notification and its
    /// audit record join it and the caller owns the commit or rollback; otherwise this method opens and commits its own transaction.
    /// </summary>
    public async Task<Result<Notification>> QueueHtmlAsync(
        string appKey,
        EmailRecipientRequest? to,
        string subject,
        string html,
        string? text,
        string? notifyOnFailureTo,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(html);

        if (to?.Email is null)
        {
            return NotificationErrors.InvalidRecipient;
        }

        if (StrictUtf8.GetByteCount(html) > Notification.BodyMaxLength
            || (text is not null && StrictUtf8.GetByteCount(text) > Notification.BodyMaxLength))
        {
            return NotificationErrors.ContentTooLarge;
        }

        return await PersistAsync(
            appKey,
            to,
            subject,
            html,
            text,
            null,
            notifyOnFailureTo,
            correlationId,
            cancellationToken);
    }

    public async Task<Result<NotificationPageResponse>> ListAsync(
        Guid applicationId,
        ListNotificationsQuery request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        NotificationStatus? status = null;

        if (request.Status is not null)
        {
            if (!TryParseStatus(request.Status, out var parsedStatus))
            {
                return NotificationErrors.InvalidStatus;
            }

            status = parsedStatus;
        }

        if (request.Page < 1)
        {
            return NotificationErrors.InvalidPage;
        }

        if (request.PageSize is < 1 or > MaxPageSize)
        {
            return NotificationErrors.InvalidPageSize;
        }

        if (request.From.HasValue && request.To.HasValue && request.From.Value > request.To.Value)
        {
            return NotificationErrors.InvalidDateRange;
        }

        var query = session.Query<Notification>().Where(notification => notification.ApplicationId == applicationId);

        if (status.HasValue)
        {
            var statusValue = status.Value;
            query = query.Where(notification => notification.Status == statusValue);
        }

        if (request.From.HasValue)
        {
            var fromUtc = request.From.Value.UtcDateTime;
            query = query.Where(notification => notification.CreatedAtUtc >= fromUtc);
        }

        if (request.To.HasValue)
        {
            var toUtc = request.To.Value.UtcDateTime;
            query = query.Where(notification => notification.CreatedAtUtc < toUtc);
        }

        var totalCount = await query.LongCountAsync(cancellationToken);
        var offset = (long)(request.Page - 1) * request.PageSize;

        if (offset >= totalCount)
        {
            return new NotificationPageResponse([], request.Page, request.PageSize, totalCount);
        }

        var rows = await Project(query
                .OrderByDescending(notification => notification.CreatedAtUtc)
                .ThenByDescending(notification => notification.Id)
                .Skip((int)offset)
                .Take(request.PageSize))
            .ToListAsync(cancellationToken);

        return new NotificationPageResponse(
            rows.Select(ToResponse).ToList(),
            request.Page,
            request.PageSize,
            totalCount);
    }

    public async Task<Result<NotificationResponse>> GetByIdAsync(
        Guid applicationId,
        Guid id,
        CancellationToken cancellationToken)
    {
        var row = await Project(session.Query<Notification>()
                .Where(notification => notification.Id == id && notification.ApplicationId == applicationId))
            .SingleOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            return NotificationErrors.NotFound;
        }

        return ToResponse(row);
    }

    private static bool TryParseStatus(string value, out NotificationStatus status)
    {
        foreach (var name in Enum.GetNames<NotificationStatus>())
        {
            if (string.Equals(name, value, StringComparison.Ordinal))
            {
                status = Enum.Parse<NotificationStatus>(name);
                return true;
            }
        }

        status = default;
        return false;
    }

    private static IQueryable<NotificationRow> Project(IQueryable<Notification> query) =>
        query.Select(notification => new NotificationRow
        {
            Id = notification.Id,
            ToEmail = notification.ToEmail,
            ToName = notification.ToName,
            Subject = notification.Subject,
            TemplateKey = notification.TemplateKey,
            Status = notification.Status,
            Attempts = notification.Attempts,
            MaxAttempts = notification.MaxAttempts,
            NextAttemptAtUtc = notification.NextAttemptAtUtc,
            SentAtUtc = notification.SentAtUtc,
            LastError = notification.LastError,
            NotifyOnFailureTo = notification.NotifyOnFailureTo,
            CorrelationId = notification.CorrelationId,
            CreatedAtUtc = notification.CreatedAtUtc,
            UpdatedAtUtc = notification.UpdatedAtUtc,
        });

    private static NotificationResponse ToResponse(NotificationRow row) =>
        new(
            row.Id,
            row.ToEmail,
            row.ToName,
            row.Subject,
            row.TemplateKey,
            row.Status.ToString(),
            row.Attempts,
            row.MaxAttempts,
            row.NextAttemptAtUtc,
            row.SentAtUtc,
            row.LastError,
            row.NotifyOnFailureTo,
            row.CorrelationId,
            row.CreatedAtUtc,
            row.UpdatedAtUtc);

    private async Task<Result<Notification>> PersistAsync(
        string appKey,
        EmailRecipientRequest to,
        string subject,
        string html,
        string? text,
        string? templateKey,
        string? notifyOnFailureTo,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        var outerTransaction = session.GetCurrentTransaction();

        if (outerTransaction is { IsActive: true })
        {
            return await QueueInTransactionAsync(
                appKey, to, subject, html, text, templateKey, notifyOnFailureTo, correlationId, cancellationToken);
        }

        using var transaction = session.BeginTransaction();

        var result = await QueueInTransactionAsync(
            appKey, to, subject, html, text, templateKey, notifyOnFailureTo, correlationId, cancellationToken);

        if (result.IsFailure)
        {
            return result.Error;
        }

        await transaction.CommitAsync(cancellationToken);

        return result.Value;
    }

    /// <summary>
    /// Resolves the application that holds <paramref name="appKey"/> with <c>FOR SHARE</c>, so a concurrent deletion of the
    /// application (which locks the row <c>FOR UPDATE</c>) either waits for this notification and cancels it, or runs first
    /// and leaves nothing to resolve.
    /// </summary>
    private async Task<Result<Notification>> QueueInTransactionAsync(
        string appKey,
        EmailRecipientRequest to,
        string subject,
        string html,
        string? text,
        string? templateKey,
        string? notifyOnFailureTo,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        var applicationIds = await session
            .CreateSQLQuery("SELECT id FROM applications WHERE app_key = :appKey FOR SHARE")
            .AddScalar("id", NHibernateUtil.Guid)
            .SetParameter("appKey", appKey.Trim())
            .ListAsync<Guid>(cancellationToken);

        if (applicationIds.Count != 1)
        {
            return ApplicationErrors.NotFound;
        }

        var queueResult = Notification.Queue(
            applicationIds[0],
            appKey,
            to.Email,
            to.Name,
            subject,
            html,
            text,
            templateKey,
            notifyOnFailureTo,
            correlationId,
            timeProvider);

        if (queueResult.IsFailure)
        {
            return queueResult.Error;
        }

        var notification = queueResult.Value;
        await SaveWithAuditAsync(notification, cancellationToken);
        return notification;
    }

    private async Task SaveWithAuditAsync(Notification notification, CancellationToken cancellationToken)
    {
        await session.SaveAsync(notification, cancellationToken);
        await auditService.RecordAsync(
            new AuditEntry(
                AuditActions.NotificationQueued,
                EntityType: nameof(Notification),
                EntityId: notification.Id.ToString(),
                NewValues: new NotificationQueuedSnapshot(
                    notification.Id,
                    notification.ToEmail,
                    notification.TemplateKey,
                    notification.TemplateKey is null ? notification.Subject : null,
                    notification.Status.ToString(),
                    notification.NotifyOnFailureTo,
                    notification.CorrelationId),
                AppKeyOverride: notification.AppKey,
                ApplicationIdOverride: notification.ApplicationId),
            cancellationToken);
    }

    private sealed class NotificationRow
    {
        public Guid Id { get; init; }

        public string ToEmail { get; init; } = string.Empty;

        public string? ToName { get; init; }

        public string Subject { get; init; } = string.Empty;

        public string? TemplateKey { get; init; }

        public NotificationStatus Status { get; init; }

        public int Attempts { get; init; }

        public int MaxAttempts { get; init; }

        public DateTime NextAttemptAtUtc { get; init; }

        public DateTime? SentAtUtc { get; init; }

        public string? LastError { get; init; }

        public string? NotifyOnFailureTo { get; init; }

        public string? CorrelationId { get; init; }

        public DateTime CreatedAtUtc { get; init; }

        public DateTime UpdatedAtUtc { get; init; }
    }
}

/// <summary>
/// Audit snapshot of a queued notification. It never includes the bodies or the template variables, and omits the
/// subject of template notifications because a rendered subject can contain variable values.
/// </summary>
public sealed record NotificationQueuedSnapshot(
    Guid Id,
    string ToEmail,
    string? TemplateKey,
    string? Subject,
    string Status,
    string? NotifyOnFailureTo,
    string? CorrelationId);
