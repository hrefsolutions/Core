using System.Net.Mail;
using Href.Core.Api.Common.Domain;
using Href.Core.Api.Common.Results;

namespace Href.Core.Api.Modules.Notifications;

/// <summary>
/// Represents one email queued for a single recipient. Content is already rendered when queued;
/// the worker only sends it and reports the outcome through the status transitions.
/// Concurrent updates are detected with the <c>version</c> column (optimistic concurrency). Raw SQL that changes
/// a notification must mirror the domain methods and always set <c>version = version + 1</c> and <c>updated_at_utc</c>.
/// The claim (<c>MarkProcessing</c>) sets <c>attempts = attempts + 1</c>, <c>status = 'Processing'</c> and
/// <c>processing_started_at_utc</c>, and only applies to rows with <c>attempts &lt; max_attempts</c>.
/// The recovery (<c>RecoverStuck</c>) does not touch <c>attempts</c>, sets
/// <c>status = CASE WHEN attempts &gt;= max_attempts THEN 'Failed' ELSE 'Pending' END</c> and
/// <c>processing_started_at_utc = NULL</c>.
/// </summary>
public class Notification : Entity, IAuditableEntity
{
    public const int DefaultMaxAttempts = 3;
    public const int AppKeyMaxLength = 50;
    public const int EmailMaxLength = 254;
    public const int RecipientNameMaxLength = 200;
    public const int SubjectMaxLength = 500;
    public const int TemplateKeyMaxLength = 100;
    public const int CorrelationIdMaxLength = 64;
    public const int LastErrorMaxLength = 2000;

    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5)];

    protected Notification()
    {
    }

    public virtual string AppKey { get; protected set; } = string.Empty;

    public virtual string ToEmail { get; protected set; } = string.Empty;

    public virtual string? ToName { get; protected set; }

    public virtual string Subject { get; protected set; } = string.Empty;

    public virtual string HtmlBody { get; protected set; } = string.Empty;

    public virtual string? TextBody { get; protected set; }

    public virtual string? TemplateKey { get; protected set; }

    public virtual NotificationStatus Status { get; protected set; } = NotificationStatus.Pending;

    public virtual int Attempts { get; protected set; }

    public virtual int MaxAttempts { get; protected set; } = DefaultMaxAttempts;

    public virtual DateTime NextAttemptAtUtc { get; protected set; }

    public virtual DateTime? ProcessingStartedAtUtc { get; protected set; }

    public virtual DateTime? SentAtUtc { get; protected set; }

    /// <summary>
    /// Error of the latest failed attempt. It is kept as history after a later successful send.
    /// </summary>
    public virtual string? LastError { get; protected set; }

    public virtual string? NotifyOnFailureTo { get; protected set; }

    public virtual string? CorrelationId { get; protected set; }

    public virtual int Version { get; protected set; }

    public virtual DateTime CreatedAtUtc { get; protected set; }

    public virtual DateTime UpdatedAtUtc { get; protected set; }

    public static Result<Notification> Queue(
        string? appKey,
        string? toEmail,
        string? toName,
        string? subject,
        string? htmlBody,
        string? textBody,
        string? templateKey,
        string? notifyOnFailureTo,
        string? correlationId,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        var normalizedAppKey = appKey?.Trim() ?? string.Empty;

        if (normalizedAppKey.Length is 0 or > AppKeyMaxLength || TextRules.HasControlOrLineSeparator(normalizedAppKey))
        {
            return NotificationErrors.InvalidAppKey;
        }

        var normalizedToEmail = toEmail?.Trim() ?? string.Empty;

        if (!IsSingleEmailAddress(normalizedToEmail))
        {
            return NotificationErrors.InvalidRecipient;
        }

        var normalizedToName = string.IsNullOrWhiteSpace(toName) ? null : toName.Trim();

        if (normalizedToName is not null
            && (normalizedToName.Length > RecipientNameMaxLength || TextRules.HasControlOrLineSeparator(normalizedToName)))
        {
            return NotificationErrors.InvalidRecipientName;
        }

        var normalizedSubject = subject?.Trim() ?? string.Empty;

        if (normalizedSubject.Length is 0 or > SubjectMaxLength || TextRules.HasControlOrLineSeparator(normalizedSubject))
        {
            return NotificationErrors.InvalidSubject;
        }

        if (string.IsNullOrWhiteSpace(htmlBody) || TextRules.HasNul(htmlBody) || TextRules.HasNul(textBody))
        {
            return NotificationErrors.InvalidContent;
        }

        var normalizedTemplateKey = string.IsNullOrWhiteSpace(templateKey) ? null : templateKey.Trim();

        if (normalizedTemplateKey is not null
            && (normalizedTemplateKey.Length > TemplateKeyMaxLength || TextRules.HasControlOrLineSeparator(normalizedTemplateKey)))
        {
            return NotificationErrors.InvalidTemplateKey;
        }

        var normalizedNotifyOnFailureTo = string.IsNullOrWhiteSpace(notifyOnFailureTo) ? null : notifyOnFailureTo.Trim();

        if (normalizedNotifyOnFailureTo is not null && !IsSingleEmailAddress(normalizedNotifyOnFailureTo))
        {
            return NotificationErrors.InvalidNotifyOnFailureTo;
        }

        var normalizedCorrelationId = string.IsNullOrWhiteSpace(correlationId) ? null : correlationId.Trim();

        if (normalizedCorrelationId is not null
            && (normalizedCorrelationId.Length > CorrelationIdMaxLength || TextRules.HasControlOrLineSeparator(normalizedCorrelationId)))
        {
            return NotificationErrors.InvalidCorrelationId;
        }

        return new Notification
        {
            AppKey = normalizedAppKey,
            ToEmail = normalizedToEmail,
            ToName = normalizedToName,
            Subject = normalizedSubject,
            HtmlBody = htmlBody,
            TextBody = string.IsNullOrWhiteSpace(textBody) ? null : textBody,
            TemplateKey = normalizedTemplateKey,
            Status = NotificationStatus.Pending,
            Attempts = 0,
            MaxAttempts = DefaultMaxAttempts,
            NextAttemptAtUtc = timeProvider.GetUtcNow().UtcDateTime,
            NotifyOnFailureTo = normalizedNotifyOnFailureTo,
            CorrelationId = normalizedCorrelationId,
        };
    }

    /// <summary>
    /// Returns the delay before the next attempt after the given number of failed attempts
    /// (1 minute after the first, 5 minutes after the second and any later one).
    /// </summary>
    public static TimeSpan GetRetryDelay(int attempts)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempts, 1);

        return RetryDelays[Math.Min(attempts, RetryDelays.Length) - 1];
    }

    public virtual Result MarkProcessing(DateTime utcNow)
    {
        EnsureUtc(utcNow);

        if (Status != NotificationStatus.Pending || Attempts >= MaxAttempts)
        {
            return NotificationErrors.InvalidTransition;
        }

        Attempts++;
        Status = NotificationStatus.Processing;
        ProcessingStartedAtUtc = utcNow;

        return Result.Success();
    }

    public virtual Result MarkSent(DateTime utcNow)
    {
        EnsureUtc(utcNow);

        if (Status != NotificationStatus.Processing)
        {
            return NotificationErrors.InvalidTransition;
        }

        Status = NotificationStatus.Sent;
        SentAtUtc = utcNow;
        ProcessingStartedAtUtc = null;

        return Result.Success();
    }

    public virtual Result RegisterFailure(string? error, DateTime utcNow)
    {
        EnsureUtc(utcNow);

        if (Status != NotificationStatus.Processing)
        {
            return NotificationErrors.InvalidTransition;
        }

        var normalizedError = string.IsNullOrWhiteSpace(error)
            ? "Unknown error"
            : error.Trim().Replace('\0', '\uFFFD');

        LastError = TruncateError(normalizedError);
        ProcessingStartedAtUtc = null;

        if (Attempts >= MaxAttempts)
        {
            Status = NotificationStatus.Failed;
        }
        else
        {
            Status = NotificationStatus.Pending;
            NextAttemptAtUtc = utcNow + GetRetryDelay(Attempts);
        }

        return Result.Success();
    }

    /// <summary>
    /// Releases a notification stuck in <c>Processing</c> (for example after a worker crash). It does not change
    /// <c>Attempts</c>: the claim already counted the attempt. The notification becomes <c>Failed</c> when no
    /// attempts are left, otherwise it returns to <c>Pending</c> ready to be claimed immediately.
    /// </summary>
    public virtual Result RecoverStuck(DateTime utcNow)
    {
        EnsureUtc(utcNow);

        if (Status != NotificationStatus.Processing)
        {
            return NotificationErrors.InvalidTransition;
        }

        ProcessingStartedAtUtc = null;

        if (Attempts >= MaxAttempts)
        {
            Status = NotificationStatus.Failed;
            LastError = TruncateError("Processing timed out");
        }
        else
        {
            Status = NotificationStatus.Pending;
            NextAttemptAtUtc = utcNow;
        }

        return Result.Success();
    }

    private static void EnsureUtc(DateTime utcNow)
    {
        if (utcNow.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("The timestamp must have DateTimeKind.Utc.", nameof(utcNow));
        }
    }

    private static string TruncateError(string value)
    {
        if (value.Length <= LastErrorMaxLength)
        {
            return value;
        }

        var length = char.IsHighSurrogate(value[LastErrorMaxLength - 1]) ? LastErrorMaxLength - 1 : LastErrorMaxLength;
        return value[..length];
    }

    private static bool IsSingleEmailAddress(string value) => TextRules.IsSingleEmailAddress(value, EmailMaxLength);
}
