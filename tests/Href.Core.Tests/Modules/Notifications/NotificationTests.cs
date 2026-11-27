using Href.Core.Api.Common.Results;
using Href.Core.Api.Modules.Notifications;
using Microsoft.Extensions.Time.Testing;

namespace Href.Core.Tests.Modules.Notifications;

public sealed class NotificationTests
{
    private readonly FakeTimeProvider _timeProvider = new(new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero));

    public static TheoryData<string?> InvalidAppKeys => new()
    {
        null!,
        string.Empty,
        "   ",
        new string('a', 51),
        "app\0key",
        "app\nkey",
        "app\U00002028key",
    };

    public static TheoryData<string?> InvalidEmails => new()
    {
        null!,
        string.Empty,
        "   ",
        "not-an-email",
        "Name <a@b.co>",
        "\"Name\" <a@b.co>",
        "a@b.co,c@d.co",
        "a@b.co\r\nBcc: c@d.co",
        "a@b.co\nc@d.co",
        "a@b.co\0",
        "a\0@b.co",
        "a@b.co\U00002028c@d.co",
        $"{new string('a', 250)}@b.co",
    };

    public static TheoryData<string> InvalidSingleLines => new()
    {
        "line1\nline2",
        "line1\rline2",
        "line1\r\nBcc: x@y.co",
        "a\0b",
        "a\u0085b",
        "a\U00002028b",
        "a\U00002029b",
        "a\tb",
        "\0",
    };

    private Result<Notification> QueueValid(
        string? appKey = "billing-portal",
        string? toEmail = "user@example.com",
        string? toName = "Jane Doe",
        string? subject = "Welcome",
        string? htmlBody = "<p>Hello</p>",
        string? textBody = "Hello",
        string? templateKey = "welcome",
        string? notifyOnFailureTo = "ops@example.com",
        string? correlationId = "corr-1") =>
        Notification.Queue(
            Guid.NewGuid(),
            appKey,
            toEmail,
            toName,
            subject,
            htmlBody,
            textBody,
            templateKey,
            notifyOnFailureTo,
            correlationId,
            _timeProvider);

    private Notification NewProcessing()
    {
        var notification = QueueValid().Value;
        Assert.True(notification.MarkProcessing(_timeProvider.GetUtcNow().UtcDateTime).IsSuccess);
        return notification;
    }

    private Notification NewSent()
    {
        var notification = NewProcessing();
        Assert.True(notification.MarkSent(_timeProvider.GetUtcNow().UtcDateTime).IsSuccess);
        return notification;
    }

    private Notification NewFailed()
    {
        var notification = QueueValid().Value;

        for (var attempt = 0; attempt < Notification.DefaultMaxAttempts; attempt++)
        {
            Assert.True(notification.MarkProcessing(_timeProvider.GetUtcNow().UtcDateTime).IsSuccess);
            Assert.True(notification.RegisterFailure("smtp down", _timeProvider.GetUtcNow().UtcDateTime).IsSuccess);
        }

        Assert.Equal(NotificationStatus.Failed, notification.Status);
        return notification;
    }

    private static (NotificationStatus Status, int Attempts, DateTime NextAttemptAtUtc, DateTime? ProcessingStartedAtUtc, DateTime? SentAtUtc, string? LastError) Snapshot(Notification notification) =>
        (notification.Status, notification.Attempts, notification.NextAttemptAtUtc, notification.ProcessingStartedAtUtc, notification.SentAtUtc, notification.LastError);

    [Fact]
    public void Queue_WithValidInputs_CreatesPendingNotificationReadyToSend()
    {
        var result = QueueValid(
            appKey: " billing-portal ",
            toEmail: " user@example.com ",
            toName: " Jane Doe ",
            subject: " Welcome ",
            templateKey: " welcome ",
            correlationId: " corr-1 ");

        Assert.True(result.IsSuccess);
        var notification = result.Value;
        Assert.Equal("billing-portal", notification.AppKey);
        Assert.Equal("user@example.com", notification.ToEmail);
        Assert.Equal("Jane Doe", notification.ToName);
        Assert.Equal("Welcome", notification.Subject);
        Assert.Equal("<p>Hello</p>", notification.HtmlBody);
        Assert.Equal("Hello", notification.TextBody);
        Assert.Equal("welcome", notification.TemplateKey);
        Assert.Equal("ops@example.com", notification.NotifyOnFailureTo);
        Assert.Equal("corr-1", notification.CorrelationId);
        Assert.Equal(NotificationStatus.Pending, notification.Status);
        Assert.Equal(0, notification.Attempts);
        Assert.Equal(3, notification.MaxAttempts);
        Assert.Equal(_timeProvider.GetUtcNow().UtcDateTime, notification.NextAttemptAtUtc);
        Assert.Null(notification.ProcessingStartedAtUtc);
        Assert.Null(notification.SentAtUtc);
        Assert.Null(notification.LastError);
    }

    [Fact]
    public void Queue_WithOnlyRequiredInputs_LeavesOptionalFieldsNull()
    {
        var result = QueueValid(
            toName: "  ",
            textBody: "",
            templateKey: " ",
            notifyOnFailureTo: null,
            correlationId: "");

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value.ToName);
        Assert.Null(result.Value.TextBody);
        Assert.Null(result.Value.TemplateKey);
        Assert.Null(result.Value.NotifyOnFailureTo);
        Assert.Null(result.Value.CorrelationId);
    }

    [Theory]
    [MemberData(nameof(InvalidAppKeys))]
    public void Queue_WithInvalidAppKey_ReturnsInvalidAppKeyError(string? appKey)
    {
        var result = QueueValid(appKey: appKey);

        Assert.True(result.IsFailure);
        Assert.Equal(NotificationErrors.InvalidAppKey, result.Error);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
    }

    [Theory]
    [MemberData(nameof(InvalidEmails))]
    public void Queue_WithInvalidRecipient_ReturnsInvalidRecipientError(string? toEmail)
    {
        var result = QueueValid(toEmail: toEmail);

        Assert.True(result.IsFailure);
        Assert.Equal(NotificationErrors.InvalidRecipient, result.Error);
    }

    [Theory]
    [MemberData(nameof(InvalidSingleLines))]
    public void Queue_WithLineBreakInRecipientName_ReturnsInvalidRecipientNameError(string toName)
    {
        var result = QueueValid(toName: toName);

        Assert.True(result.IsFailure);
        Assert.Equal(NotificationErrors.InvalidRecipientName, result.Error);
    }

    [Fact]
    public void Queue_WithTooLongRecipientName_ReturnsInvalidRecipientNameError()
    {
        var result = QueueValid(toName: new string('n', 201));

        Assert.True(result.IsFailure);
        Assert.Equal(NotificationErrors.InvalidRecipientName, result.Error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("\u0085")]
    [InlineData("\U00002028")]
    public void Queue_WithBlankSubject_ReturnsInvalidSubjectError(string? subject)
    {
        var result = QueueValid(subject: subject);

        Assert.True(result.IsFailure);
        Assert.Equal(NotificationErrors.InvalidSubject, result.Error);
    }

    [Theory]
    [MemberData(nameof(InvalidSingleLines))]
    public void Queue_WithLineBreakInSubject_ReturnsInvalidSubjectError(string subject)
    {
        var result = QueueValid(subject: subject);

        Assert.True(result.IsFailure);
        Assert.Equal(NotificationErrors.InvalidSubject, result.Error);
    }

    [Fact]
    public void Queue_WithTooLongSubject_ReturnsInvalidSubjectError()
    {
        var result = QueueValid(subject: new string('s', 501));

        Assert.True(result.IsFailure);
        Assert.Equal(NotificationErrors.InvalidSubject, result.Error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Queue_WithBlankHtmlBody_ReturnsInvalidContentError(string? htmlBody)
    {
        var result = QueueValid(htmlBody: htmlBody);

        Assert.True(result.IsFailure);
        Assert.Equal(NotificationErrors.InvalidContent, result.Error);
    }

    [Theory]
    [InlineData("<p>a\0b</p>", "text")]
    [InlineData("<p>ok</p>", "te\0xt")]
    [InlineData("\0", null)]
    public void Queue_WithNulInBodies_ReturnsInvalidContentError(string htmlBody, string? textBody)
    {
        var result = QueueValid(htmlBody: htmlBody, textBody: textBody);

        Assert.True(result.IsFailure);
        Assert.Equal(NotificationErrors.InvalidContent, result.Error);
    }

    [Theory]
    [InlineData("tpl\0key")]
    [InlineData("tpl\nkey")]
    [InlineData("tpl\U00002029key")]
    public void Queue_WithControlCharsInTemplateKey_ReturnsInvalidTemplateKeyError(string templateKey)
    {
        var result = QueueValid(templateKey: templateKey);

        Assert.True(result.IsFailure);
        Assert.Equal(NotificationErrors.InvalidTemplateKey, result.Error);
    }

    [Theory]
    [InlineData("corr\0id")]
    [InlineData("corr\tid")]
    [InlineData("corr\U00002028id")]
    public void Queue_WithControlCharsInCorrelationId_ReturnsInvalidCorrelationIdError(string correlationId)
    {
        var result = QueueValid(correlationId: correlationId);

        Assert.True(result.IsFailure);
        Assert.Equal(NotificationErrors.InvalidCorrelationId, result.Error);
    }

    [Fact]
    public void Queue_WithTooLongTemplateKey_ReturnsInvalidTemplateKeyError()
    {
        var result = QueueValid(templateKey: new string('t', 101));

        Assert.True(result.IsFailure);
        Assert.Equal(NotificationErrors.InvalidTemplateKey, result.Error);
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("Name <a@b.co>")]
    [InlineData("a@b.co,c@d.co")]
    [InlineData("a@b.co\nc@d.co")]
    public void Queue_WithInvalidNotifyOnFailureTo_ReturnsInvalidNotifyOnFailureToError(string notifyOnFailureTo)
    {
        var result = QueueValid(notifyOnFailureTo: notifyOnFailureTo);

        Assert.True(result.IsFailure);
        Assert.Equal(NotificationErrors.InvalidNotifyOnFailureTo, result.Error);
    }

    [Fact]
    public void Queue_WithTooLongCorrelationId_ReturnsInvalidCorrelationIdError()
    {
        var result = QueueValid(correlationId: new string('c', 65));

        Assert.True(result.IsFailure);
        Assert.Equal(NotificationErrors.InvalidCorrelationId, result.Error);
    }

    [Fact]
    public void Queue_WithMaxLengthCorrelationId_Succeeds()
    {
        var result = QueueValid(correlationId: new string('c', 64));

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void Queue_WithNullTimeProvider_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            Notification.Queue(Guid.NewGuid(), "app", "a@b.co", null, "s", "<p>x</p>", null, null, null, null, null!));
    }

    [Fact]
    public void Queue_WithEmptyApplicationId_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            Notification.Queue(Guid.Empty, "app", "a@b.co", null, "s", "<p>x</p>", null, null, null, null, TimeProvider.System));
    }

    [Fact]
    public void Queue_KeepsTheApplicationId()
    {
        var applicationId = Guid.NewGuid();

        var result = Notification.Queue(applicationId, "app", "a@b.co", null, "s", "<p>x</p>", null, null, null, null, TimeProvider.System);

        Assert.True(result.IsSuccess);
        Assert.Equal(applicationId, result.Value.ApplicationId);
    }

    [Fact]
    public void MarkProcessing_FromPending_MovesToProcessingAndRecordsStart()
    {
        var notification = QueueValid().Value;
        var now = _timeProvider.GetUtcNow().UtcDateTime.AddSeconds(5);

        var result = notification.MarkProcessing(now);

        Assert.True(result.IsSuccess);
        Assert.Equal(NotificationStatus.Processing, notification.Status);
        Assert.Equal(now, notification.ProcessingStartedAtUtc);
        Assert.Equal(1, notification.Attempts);
    }

    [Fact]
    public void MarkSent_FromProcessing_MovesToSentWithoutCountingAgain()
    {
        var notification = NewProcessing();
        var now = _timeProvider.GetUtcNow().UtcDateTime.AddSeconds(10);

        var result = notification.MarkSent(now);

        Assert.True(result.IsSuccess);
        Assert.Equal(NotificationStatus.Sent, notification.Status);
        Assert.Equal(1, notification.Attempts);
        Assert.Equal(now, notification.SentAtUtc);
        Assert.Null(notification.ProcessingStartedAtUtc);
        Assert.Null(notification.LastError);
    }

    [Fact]
    public void RegisterFailure_FirstFailure_ReturnsToPendingWithOneMinuteBackoff()
    {
        var notification = NewProcessing();
        var now = _timeProvider.GetUtcNow().UtcDateTime.AddSeconds(10);

        var result = notification.RegisterFailure("smtp timeout", now);

        Assert.True(result.IsSuccess);
        Assert.Equal(NotificationStatus.Pending, notification.Status);
        Assert.Equal(1, notification.Attempts);
        Assert.Equal(now.AddMinutes(1), notification.NextAttemptAtUtc);
        Assert.Equal("smtp timeout", notification.LastError);
        Assert.Null(notification.ProcessingStartedAtUtc);
    }

    [Fact]
    public void RegisterFailure_SecondFailure_ReturnsToPendingWithFiveMinuteBackoff()
    {
        var notification = NewProcessing();
        notification.RegisterFailure("first", _timeProvider.GetUtcNow().UtcDateTime);
        var now = _timeProvider.GetUtcNow().UtcDateTime.AddMinutes(2);
        notification.MarkProcessing(now);

        var result = notification.RegisterFailure("second", now);

        Assert.True(result.IsSuccess);
        Assert.Equal(NotificationStatus.Pending, notification.Status);
        Assert.Equal(2, notification.Attempts);
        Assert.Equal(now.AddMinutes(5), notification.NextAttemptAtUtc);
        Assert.Equal("second", notification.LastError);
    }

    [Fact]
    public void RegisterFailure_ThirdFailure_MovesToFailed()
    {
        var notification = NewFailed();

        Assert.Equal(NotificationStatus.Failed, notification.Status);
        Assert.Equal(3, notification.Attempts);
        Assert.Equal("smtp down", notification.LastError);
        Assert.Null(notification.ProcessingStartedAtUtc);
        Assert.Null(notification.SentAtUtc);
    }

    [Fact]
    public void TwoFailuresThenSuccess_EndsSentWithThreeAttempts()
    {
        var notification = QueueValid().Value;
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        notification.MarkProcessing(now);
        Assert.Equal(1, notification.Attempts);
        notification.RegisterFailure("one", now);
        Assert.Equal(1, notification.Attempts);
        notification.MarkProcessing(now);
        Assert.Equal(2, notification.Attempts);
        notification.RegisterFailure("two", now);
        Assert.Equal(2, notification.Attempts);
        notification.MarkProcessing(now);
        Assert.Equal(3, notification.Attempts);
        var result = notification.MarkSent(now);

        Assert.True(result.IsSuccess);
        Assert.Equal(NotificationStatus.Sent, notification.Status);
        Assert.Equal(3, notification.Attempts);
        Assert.Equal(now, notification.SentAtUtc);
        Assert.Equal("two", notification.LastError);
    }

    [Fact]
    public void RecoverStuck_AfterFirstAttempt_ReturnsToPendingWithoutChangingAttempts()
    {
        var notification = NewProcessing();
        var now = _timeProvider.GetUtcNow().UtcDateTime.AddMinutes(15);

        var result = notification.RecoverStuck(now);

        Assert.True(result.IsSuccess);
        Assert.Equal(NotificationStatus.Pending, notification.Status);
        Assert.Equal(1, notification.Attempts);
        Assert.Equal(now, notification.NextAttemptAtUtc);
        Assert.Null(notification.ProcessingStartedAtUtc);
        Assert.Null(notification.LastError);
    }

    [Fact]
    public void ThreeClaimsEachFollowedByACrash_EndFailedAndAllowNoFourthClaim()
    {
        var notification = QueueValid().Value;
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        for (var attempt = 1; attempt <= Notification.DefaultMaxAttempts; attempt++)
        {
            Assert.True(notification.MarkProcessing(now).IsSuccess);
            Assert.Equal(attempt, notification.Attempts);
            Assert.True(notification.RecoverStuck(now).IsSuccess);
            Assert.Equal(attempt, notification.Attempts);
        }

        Assert.Equal(NotificationStatus.Failed, notification.Status);
        Assert.Equal("Processing timed out", notification.LastError);
        Assert.Null(notification.ProcessingStartedAtUtc);

        var fourthClaim = notification.MarkProcessing(now);

        AssertRejected(fourthClaim);
        Assert.Equal(NotificationStatus.Failed, notification.Status);
        Assert.Equal(3, notification.Attempts);
    }

    [Fact]
    public void MarkProcessing_WhenAttemptsReachedMaxAttempts_IsRejectedAndKeepsState()
    {
        var notification = QueueValid().Value;
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        typeof(Notification).GetProperty(nameof(Notification.Attempts))!.SetValue(notification, Notification.DefaultMaxAttempts);
        var before = Snapshot(notification);

        var result = notification.MarkProcessing(now);

        AssertRejected(result);
        Assert.Equal(before, Snapshot(notification));
        Assert.Equal(NotificationStatus.Pending, notification.Status);
    }

    [Fact]
    public void RecoverStuck_FromPending_IsRejectedAndKeepsState()
    {
        var notification = QueueValid().Value;
        var before = Snapshot(notification);

        AssertRejected(notification.RecoverStuck(_timeProvider.GetUtcNow().UtcDateTime));
        Assert.Equal(before, Snapshot(notification));
    }

    [Fact]
    public void RecoverStuck_FromSent_IsRejectedAndKeepsState()
    {
        var notification = NewSent();
        var before = Snapshot(notification);

        AssertRejected(notification.RecoverStuck(_timeProvider.GetUtcNow().UtcDateTime));
        Assert.Equal(before, Snapshot(notification));
    }

    [Fact]
    public void RecoverStuck_FromFailed_IsRejectedAndKeepsState()
    {
        var notification = NewFailed();
        var before = Snapshot(notification);

        AssertRejected(notification.RecoverStuck(_timeProvider.GetUtcNow().UtcDateTime));
        Assert.Equal(before, Snapshot(notification));
    }

    [Fact]
    public void RecoverStuck_WithNonUtcKind_ThrowsArgumentException()
    {
        var notification = NewProcessing();

        Assert.Throws<ArgumentException>(() => notification.RecoverStuck(new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Local)));
        Assert.Throws<ArgumentException>(() => notification.RecoverStuck(new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Unspecified)));
        Assert.Equal(NotificationStatus.Processing, notification.Status);
    }

    [Fact]
    public void ReleaseUnsent_FromProcessing_ReturnsToPendingAndGivesTheAttemptBack()
    {
        var notification = NewProcessing();
        var now = _timeProvider.GetUtcNow().UtcDateTime.AddSeconds(30);

        var result = notification.ReleaseUnsent(now);

        Assert.True(result.IsSuccess);
        Assert.Equal(NotificationStatus.Pending, notification.Status);
        Assert.Equal(0, notification.Attempts);
        Assert.Equal(now, notification.NextAttemptAtUtc);
        Assert.Null(notification.ProcessingStartedAtUtc);
        Assert.Null(notification.LastError);
    }

    [Fact]
    public void ReleaseUnsent_OnTheLastAttempt_KeepsLastErrorAndAllowsTheLastClaimAgain()
    {
        var notification = QueueValid().Value;
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        for (var attempt = 1; attempt < Notification.DefaultMaxAttempts; attempt++)
        {
            Assert.True(notification.MarkProcessing(now).IsSuccess);
            Assert.True(notification.RegisterFailure("smtp down", now).IsSuccess);
        }

        Assert.True(notification.MarkProcessing(now).IsSuccess);
        Assert.Equal(3, notification.Attempts);

        Assert.True(notification.ReleaseUnsent(now).IsSuccess);

        Assert.Equal(NotificationStatus.Pending, notification.Status);
        Assert.Equal(2, notification.Attempts);
        Assert.Equal("smtp down", notification.LastError);
        Assert.True(notification.MarkProcessing(now).IsSuccess);
        Assert.Equal(3, notification.Attempts);
    }

    [Fact]
    public void ReleaseUnsent_NeverTakesAttemptsBelowZero()
    {
        var notification = NewProcessing();
        typeof(Notification).GetProperty(nameof(Notification.Attempts))!.SetValue(notification, 0);

        Assert.True(notification.ReleaseUnsent(_timeProvider.GetUtcNow().UtcDateTime).IsSuccess);

        Assert.Equal(0, notification.Attempts);
        Assert.Equal(NotificationStatus.Pending, notification.Status);
    }

    [Fact]
    public void ReleaseUnsent_FromPending_IsRejectedAndKeepsState()
    {
        var notification = QueueValid().Value;
        var before = Snapshot(notification);

        AssertRejected(notification.ReleaseUnsent(_timeProvider.GetUtcNow().UtcDateTime));
        Assert.Equal(before, Snapshot(notification));
    }

    [Fact]
    public void ReleaseUnsent_FromSent_IsRejectedAndKeepsState()
    {
        var notification = NewSent();
        var before = Snapshot(notification);

        AssertRejected(notification.ReleaseUnsent(_timeProvider.GetUtcNow().UtcDateTime));
        Assert.Equal(before, Snapshot(notification));
    }

    [Fact]
    public void ReleaseUnsent_FromFailed_IsRejectedAndKeepsState()
    {
        var notification = NewFailed();
        var before = Snapshot(notification);

        AssertRejected(notification.ReleaseUnsent(_timeProvider.GetUtcNow().UtcDateTime));
        Assert.Equal(before, Snapshot(notification));
    }

    [Fact]
    public void ReleaseUnsent_WithNonUtcKind_ThrowsArgumentException()
    {
        var notification = NewProcessing();

        Assert.Throws<ArgumentException>(() => notification.ReleaseUnsent(new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Local)));
        Assert.Throws<ArgumentException>(() => notification.ReleaseUnsent(new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Unspecified)));
        Assert.Equal(NotificationStatus.Processing, notification.Status);
        Assert.Equal(1, notification.Attempts);
    }

    [Fact]
    public void Transitions_WithNonUtcKind_ThrowArgumentException()
    {
        var local = new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Local);
        var unspecified = new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Unspecified);

        foreach (var invalid in new[] { local, unspecified })
        {
            var pending = QueueValid().Value;
            Assert.Throws<ArgumentException>(() => pending.MarkProcessing(invalid));
            Assert.Equal(NotificationStatus.Pending, pending.Status);
            Assert.Equal(0, pending.Attempts);

            var processing = NewProcessing();
            Assert.Throws<ArgumentException>(() => processing.MarkSent(invalid));
            Assert.Throws<ArgumentException>(() => processing.RegisterFailure("boom", invalid));
            Assert.Equal(NotificationStatus.Processing, processing.Status);
        }
    }

    [Fact]
    public void RegisterFailure_WithNulInError_ReplacesItSoTheValueCanBeStored()
    {
        var notification = NewProcessing();

        notification.RegisterFailure("bad\0error", _timeProvider.GetUtcNow().UtcDateTime);

        Assert.DoesNotContain('\0', notification.LastError!);
        Assert.Equal("bad�error", notification.LastError);
    }

    [Fact]
    public void RegisterFailure_TruncatingInsideASurrogatePair_DoesNotLeaveLoneHighSurrogate()
    {
        var notification = NewProcessing();
        var error = new string('e', 1999) + "\U0001F600" + "tail";

        notification.RegisterFailure(error, _timeProvider.GetUtcNow().UtcDateTime);

        Assert.Equal(1999, notification.LastError!.Length);
        Assert.False(char.IsHighSurrogate(notification.LastError[^1]));
    }

    [Fact]
    public void RegisterFailure_TruncatingBetweenCompletePairs_KeepsExactMaxLength()
    {
        var notification = NewProcessing();
        var error = new string('e', 1998) + "\U0001F600" + "tail";

        notification.RegisterFailure(error, _timeProvider.GetUtcNow().UtcDateTime);

        Assert.Equal(2000, notification.LastError!.Length);
        Assert.True(char.IsLowSurrogate(notification.LastError[^1]));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 5)]
    [InlineData(3, 5)]
    public void GetRetryDelay_ReturnsExpectedBackoff(int attempts, int expectedMinutes)
    {
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), Notification.GetRetryDelay(attempts));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void RegisterFailure_WithBlankError_StoresUnknownError(string? error)
    {
        var notification = NewProcessing();

        notification.RegisterFailure(error, _timeProvider.GetUtcNow().UtcDateTime);

        Assert.Equal("Unknown error", notification.LastError);
    }

    [Fact]
    public void RegisterFailure_WithVeryLongError_TruncatesToMaxLength()
    {
        var notification = NewProcessing();

        notification.RegisterFailure(new string('e', 5000), _timeProvider.GetUtcNow().UtcDateTime);

        Assert.Equal(Notification.LastErrorMaxLength, notification.LastError!.Length);
        Assert.Equal(2000, notification.LastError.Length);
    }

    [Fact]
    public void MarkProcessing_FromProcessing_ReturnsInvalidTransitionAndKeepsState()
    {
        var notification = NewProcessing();
        var before = Snapshot(notification);

        var result = notification.MarkProcessing(_timeProvider.GetUtcNow().UtcDateTime.AddMinutes(1));

        AssertRejected(result);
        Assert.Equal(before, Snapshot(notification));
    }

    [Fact]
    public void MarkSent_FromPending_ReturnsInvalidTransitionAndKeepsState()
    {
        var notification = QueueValid().Value;
        var before = Snapshot(notification);

        var result = notification.MarkSent(_timeProvider.GetUtcNow().UtcDateTime);

        AssertRejected(result);
        Assert.Equal(before, Snapshot(notification));
    }

    [Fact]
    public void RegisterFailure_FromPending_ReturnsInvalidTransitionAndKeepsState()
    {
        var notification = QueueValid().Value;
        var before = Snapshot(notification);

        var result = notification.RegisterFailure("boom", _timeProvider.GetUtcNow().UtcDateTime);

        AssertRejected(result);
        Assert.Equal(before, Snapshot(notification));
    }

    [Fact]
    public void AnyTransition_FromSent_ReturnsInvalidTransitionAndKeepsState()
    {
        var notification = NewSent();
        var before = Snapshot(notification);
        var now = _timeProvider.GetUtcNow().UtcDateTime.AddHours(1);

        AssertRejected(notification.MarkProcessing(now));
        AssertRejected(notification.MarkSent(now));
        AssertRejected(notification.RegisterFailure("boom", now));

        Assert.Equal(before, Snapshot(notification));
        Assert.Equal(NotificationStatus.Sent, notification.Status);
    }

    [Fact]
    public void AnyTransition_FromFailed_ReturnsInvalidTransitionAndKeepsState()
    {
        var notification = NewFailed();
        var before = Snapshot(notification);
        var now = _timeProvider.GetUtcNow().UtcDateTime.AddHours(1);

        AssertRejected(notification.MarkProcessing(now));
        AssertRejected(notification.MarkSent(now));
        AssertRejected(notification.RegisterFailure("boom", now));

        Assert.Equal(before, Snapshot(notification));
        Assert.Equal(NotificationStatus.Failed, notification.Status);
    }

    [Fact]
    public void Queue_WithBodiesAtTheMaximumLength_Succeeds()
    {
        var body = new string('a', Notification.BodyMaxLength);

        var result = QueueValid(htmlBody: body, textBody: body);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void Queue_WithHtmlBodyOverTheMaximumLength_ReturnsContentTooLargeError()
    {
        var result = QueueValid(htmlBody: new string('a', Notification.BodyMaxLength + 1));

        Assert.True(result.IsFailure);
        Assert.Equal(NotificationErrors.ContentTooLarge, result.Error);
    }

    [Fact]
    public void Queue_WithTextBodyOverTheMaximumLength_ReturnsContentTooLargeError()
    {
        var result = QueueValid(textBody: new string('a', Notification.BodyMaxLength + 1));

        Assert.True(result.IsFailure);
        Assert.Equal(NotificationErrors.ContentTooLarge, result.Error);
    }

    private static void AssertRejected(Result result)
    {
        Assert.True(result.IsFailure);
        Assert.Equal(NotificationErrors.InvalidTransition, result.Error);
        Assert.Equal(ErrorType.Conflict, result.Error.Type);
    }
}
