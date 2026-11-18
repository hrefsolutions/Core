using Href.Core.Api.Modules.Notifications;
using Microsoft.Extensions.Time.Testing;
using NHibernate;
using NHibernate.Exceptions;
using Npgsql;

namespace Href.Core.Tests.Integration;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class NotificationPersistenceIntegrationTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset StartInstant = new(2026, 11, 18, 10, 0, 0, TimeSpan.Zero);

    private static string UniqueKey(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private static Notification QueueSimple(TimeProvider timeProvider, string? htmlBody = "<p>x</p>", string? textBody = null) =>
        Notification.Queue(
            UniqueKey("app")[..20],
            "user@example.com",
            null,
            "Subject",
            htmlBody,
            textBody,
            null,
            null,
            null,
            timeProvider).Value;

    private static PostgresException? FindPostgresException(Exception? exception)
    {
        while (exception is not null)
        {
            if (exception is PostgresException postgresException)
            {
                return postgresException;
            }

            exception = exception.InnerException;
        }

        return null;
    }

    private static bool ContainsStaleObjectState(Exception? exception)
    {
        while (exception is not null)
        {
            if (exception is StaleObjectStateException)
            {
                return true;
            }

            exception = exception.InnerException;
        }

        return false;
    }

    [Fact]
    public async Task EmailTemplate_InsertAndLoad_RoundTripsAllPropertiesAndSetsAuditTimestamps()
    {
        var clock = new FakeTimeProvider(StartInstant);
        using var sessionFactory = postgres.BuildSessionFactory(clock);
        var key = UniqueKey("tpl");
        var template = EmailTemplate.Create(
            key,
            "Welcome",
            "Sent on sign-up",
            "Hello {{Name}}",
            "<p>Hello {{Name}}</p>",
            "Hello {{Name}}",
            true).Value;

        using var session = sessionFactory.OpenSession();

        using (var transaction = session.BeginTransaction())
        {
            await session.SaveAsync(template);
            await transaction.CommitAsync();
        }

        session.Clear();
        var loaded = await session.GetAsync<EmailTemplate>(template.Id);

        Assert.NotNull(loaded);
        Assert.Equal(key, loaded.Key);
        Assert.Equal("Welcome", loaded.Name);
        Assert.Equal("Sent on sign-up", loaded.Description);
        Assert.Equal("Hello {{Name}}", loaded.Subject);
        Assert.Equal("<p>Hello {{Name}}</p>", loaded.HtmlBody);
        Assert.Equal("Hello {{Name}}", loaded.TextBody);
        Assert.True(loaded.IsSystem);
        Assert.Equal(StartInstant.UtcDateTime, loaded.CreatedAtUtc);
        Assert.Equal(StartInstant.UtcDateTime, loaded.UpdatedAtUtc);
        Assert.Equal(DateTimeKind.Utc, loaded.CreatedAtUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, loaded.UpdatedAtUtc.Kind);
    }

    [Fact]
    public async Task EmailTemplate_WithNullOptionals_RoundTripsNulls()
    {
        var template = EmailTemplate.Create(UniqueKey("tpl"), "Plain", null, "Subject", "<p>x</p>", null, false).Value;

        using var session = postgres.SessionFactory.OpenSession();

        using (var transaction = session.BeginTransaction())
        {
            await session.SaveAsync(template);
            await transaction.CommitAsync();
        }

        session.Clear();
        var loaded = await session.GetAsync<EmailTemplate>(template.Id);

        Assert.NotNull(loaded);
        Assert.Null(loaded.Description);
        Assert.Null(loaded.TextBody);
        Assert.False(loaded.IsSystem);
    }

    [Fact]
    public async Task EmailTemplate_WithLongBodies_RoundTripsThemIntact()
    {
        var htmlBody = "<p>" + string.Concat(Enumerable.Repeat("0123456789", 2000)) + "</p>";
        var textBody = string.Concat(Enumerable.Repeat("abcdefghij", 2000));
        var template = EmailTemplate.Create(UniqueKey("tpl"), "Long", null, "Subject", htmlBody, textBody, false).Value;

        using var session = postgres.SessionFactory.OpenSession();

        using (var transaction = session.BeginTransaction())
        {
            await session.SaveAsync(template);
            await transaction.CommitAsync();
        }

        session.Clear();
        var loaded = await session.GetAsync<EmailTemplate>(template.Id);

        Assert.NotNull(loaded);
        Assert.True(htmlBody.Length > 10_000);
        Assert.Equal(htmlBody, loaded.HtmlBody);
        Assert.Equal(textBody, loaded.TextBody);
    }

    [Fact]
    public async Task EmailTemplate_UpdateContent_PersistsChangesAndKeepsKey()
    {
        var key = UniqueKey("tpl");
        var template = EmailTemplate.Create(key, "Original", null, "Subject", "<p>x</p>", null, true).Value;

        using var session = postgres.SessionFactory.OpenSession();

        using (var transaction = session.BeginTransaction())
        {
            await session.SaveAsync(template);
            await transaction.CommitAsync();
        }

        using (var transaction = session.BeginTransaction())
        {
            Assert.True(template.UpdateContent("Renamed", "Desc", "New subject", "<p>y</p>", "y").IsSuccess);
            await transaction.CommitAsync();
        }

        session.Clear();
        var loaded = await session.GetAsync<EmailTemplate>(template.Id);

        Assert.NotNull(loaded);
        Assert.Equal(key, loaded.Key);
        Assert.Equal("Renamed", loaded.Name);
        Assert.Equal("Desc", loaded.Description);
        Assert.Equal("New subject", loaded.Subject);
        Assert.Equal("<p>y</p>", loaded.HtmlBody);
        Assert.Equal("y", loaded.TextBody);
        Assert.True(loaded.IsSystem);
    }

    [Fact]
    public async Task EmailTemplate_WithDuplicateKey_ViolatesUniqueIndexWithSqlState23505()
    {
        var key = UniqueKey("dup");
        var first = EmailTemplate.Create(key, "First", null, "Subject", "<p>x</p>", null, false).Value;
        var second = EmailTemplate.Create(key, "Second", null, "Subject", "<p>x</p>", null, false).Value;

        using (var firstSession = postgres.SessionFactory.OpenSession())
        using (var firstTransaction = firstSession.BeginTransaction())
        {
            await firstSession.SaveAsync(first);
            await firstTransaction.CommitAsync();
        }

        using var secondSession = postgres.SessionFactory.OpenSession();
        using var secondTransaction = secondSession.BeginTransaction();
        await secondSession.SaveAsync(second);

        var exception = await Assert.ThrowsAsync<GenericADOException>(() => secondTransaction.CommitAsync());

        var postgresException = FindPostgresException(exception);
        Assert.NotNull(postgresException);
        Assert.Equal("23505", postgresException.SqlState);
        Assert.Equal("ux_email_templates_key", postgresException.ConstraintName);
    }

    [Fact]
    public async Task Notification_InsertAndLoad_RoundTripsAllPropertiesAndSetsAuditTimestamps()
    {
        var clock = new FakeTimeProvider(StartInstant);
        using var sessionFactory = postgres.BuildSessionFactory(clock);
        var appKey = UniqueKey("app")[..20];
        var notification = Notification.Queue(
            appKey,
            "user@example.com",
            "Jane Doe",
            "Welcome",
            "<p>Hello</p>",
            "Hello",
            "welcome-email",
            "ops@example.com",
            "corr-123",
            clock).Value;

        using var session = sessionFactory.OpenSession();

        using (var transaction = session.BeginTransaction())
        {
            await session.SaveAsync(notification);
            await transaction.CommitAsync();
        }

        session.Clear();
        var loaded = await session.GetAsync<Notification>(notification.Id);

        Assert.NotNull(loaded);
        Assert.Equal(appKey, loaded.AppKey);
        Assert.Equal("user@example.com", loaded.ToEmail);
        Assert.Equal("Jane Doe", loaded.ToName);
        Assert.Equal("Welcome", loaded.Subject);
        Assert.Equal("<p>Hello</p>", loaded.HtmlBody);
        Assert.Equal("Hello", loaded.TextBody);
        Assert.Equal("welcome-email", loaded.TemplateKey);
        Assert.Equal(NotificationStatus.Pending, loaded.Status);
        Assert.Equal(0, loaded.Attempts);
        Assert.Equal(3, loaded.MaxAttempts);
        Assert.Equal(1, loaded.Version);
        Assert.Equal(StartInstant.UtcDateTime, loaded.NextAttemptAtUtc);
        Assert.Null(loaded.ProcessingStartedAtUtc);
        Assert.Null(loaded.SentAtUtc);
        Assert.Null(loaded.LastError);
        Assert.Equal("ops@example.com", loaded.NotifyOnFailureTo);
        Assert.Equal("corr-123", loaded.CorrelationId);
        Assert.Equal(StartInstant.UtcDateTime, loaded.CreatedAtUtc);
        Assert.Equal(StartInstant.UtcDateTime, loaded.UpdatedAtUtc);
        Assert.Equal(DateTimeKind.Utc, loaded.CreatedAtUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, loaded.UpdatedAtUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, loaded.NextAttemptAtUtc.Kind);
    }

    [Fact]
    public async Task Notification_StatusIsPersistedAsString()
    {
        var notification = QueueSimple(TimeProvider.System);

        using var session = postgres.SessionFactory.OpenSession();

        using (var transaction = session.BeginTransaction())
        {
            await session.SaveAsync(notification);
            await transaction.CommitAsync();
        }

        var rawStatus = await session
            .CreateSQLQuery("SELECT status FROM notifications WHERE id = :id")
            .SetGuid("id", notification.Id)
            .UniqueResultAsync<string>();

        Assert.Equal("Pending", rawStatus);
    }

    [Fact]
    public async Task Notification_WithNullOptionals_RoundTripsNulls()
    {
        var notification = QueueSimple(TimeProvider.System);

        using var session = postgres.SessionFactory.OpenSession();

        using (var transaction = session.BeginTransaction())
        {
            await session.SaveAsync(notification);
            await transaction.CommitAsync();
        }

        session.Clear();
        var loaded = await session.GetAsync<Notification>(notification.Id);

        Assert.NotNull(loaded);
        Assert.Null(loaded.ToName);
        Assert.Null(loaded.TextBody);
        Assert.Null(loaded.TemplateKey);
        Assert.Null(loaded.NotifyOnFailureTo);
        Assert.Null(loaded.CorrelationId);
    }

    [Fact]
    public async Task Notification_WithLongBodies_RoundTripsThemIntact()
    {
        var htmlBody = "<p>" + string.Concat(Enumerable.Repeat("0123456789", 2000)) + "</p>";
        var textBody = string.Concat(Enumerable.Repeat("abcdefghij", 2000));
        var notification = QueueSimple(TimeProvider.System, htmlBody, textBody);

        using var session = postgres.SessionFactory.OpenSession();

        using (var transaction = session.BeginTransaction())
        {
            await session.SaveAsync(notification);
            await transaction.CommitAsync();
        }

        session.Clear();
        var loaded = await session.GetAsync<Notification>(notification.Id);

        Assert.NotNull(loaded);
        Assert.True(htmlBody.Length > 10_000);
        Assert.Equal(htmlBody, loaded.HtmlBody);
        Assert.Equal(textBody, loaded.TextBody);
    }

    [Fact]
    public async Task Notification_StatusTransitions_PersistWithExactTimestampsAndAdvanceUpdatedAt()
    {
        var clock = new FakeTimeProvider(StartInstant);
        using var sessionFactory = postgres.BuildSessionFactory(clock);
        var notification = QueueSimple(clock);

        using var session = sessionFactory.OpenSession();

        using (var transaction = session.BeginTransaction())
        {
            await session.SaveAsync(notification);
            await transaction.CommitAsync();
        }

        var processingInstant = StartInstant.AddMinutes(10);
        clock.SetUtcNow(processingInstant);

        using (var transaction = session.BeginTransaction())
        {
            Assert.True(notification.MarkProcessing(clock.GetUtcNow().UtcDateTime).IsSuccess);
            await transaction.CommitAsync();
        }

        session.Clear();
        var processing = await session.GetAsync<Notification>(notification.Id);
        Assert.NotNull(processing);
        Assert.Equal(NotificationStatus.Processing, processing.Status);
        Assert.Equal(1, processing.Attempts);
        Assert.Equal(2, processing.Version);
        Assert.Equal(processingInstant.UtcDateTime, processing.ProcessingStartedAtUtc);
        Assert.Equal(DateTimeKind.Utc, processing.ProcessingStartedAtUtc!.Value.Kind);
        Assert.Equal(StartInstant.UtcDateTime, processing.CreatedAtUtc);
        Assert.Equal(processingInstant.UtcDateTime, processing.UpdatedAtUtc);

        var failureInstant = processingInstant.AddMinutes(1);
        clock.SetUtcNow(failureInstant);

        using (var transaction = session.BeginTransaction())
        {
            Assert.True(processing.RegisterFailure("smtp down", clock.GetUtcNow().UtcDateTime).IsSuccess);
            await transaction.CommitAsync();
        }

        session.Clear();
        var retry = await session.GetAsync<Notification>(notification.Id);
        Assert.NotNull(retry);
        Assert.Equal(NotificationStatus.Pending, retry.Status);
        Assert.Equal(1, retry.Attempts);
        Assert.Equal("smtp down", retry.LastError);
        Assert.Null(retry.ProcessingStartedAtUtc);
        Assert.Equal(failureInstant.AddMinutes(1).UtcDateTime, retry.NextAttemptAtUtc);
        Assert.Equal(failureInstant.UtcDateTime, retry.UpdatedAtUtc);
        Assert.Equal(StartInstant.UtcDateTime, retry.CreatedAtUtc);

        var sentInstant = failureInstant.AddMinutes(2);
        clock.SetUtcNow(sentInstant);

        using (var transaction = session.BeginTransaction())
        {
            Assert.True(retry.MarkProcessing(clock.GetUtcNow().UtcDateTime).IsSuccess);
            Assert.True(retry.MarkSent(clock.GetUtcNow().UtcDateTime).IsSuccess);
            await transaction.CommitAsync();
        }

        session.Clear();
        var sent = await session.GetAsync<Notification>(notification.Id);
        Assert.NotNull(sent);
        Assert.Equal(NotificationStatus.Sent, sent.Status);
        Assert.Equal(2, sent.Attempts);
        Assert.Equal("smtp down", sent.LastError);
        Assert.Equal(sentInstant.UtcDateTime, sent.SentAtUtc);
        Assert.Equal(DateTimeKind.Utc, sent.SentAtUtc!.Value.Kind);
        Assert.Equal(sentInstant.UtcDateTime, sent.UpdatedAtUtc);
        Assert.Equal(StartInstant.UtcDateTime, sent.CreatedAtUtc);
    }

    [Fact]
    public async Task Notification_ConcurrentMarkProcessing_SecondCommitFailsWithStaleStateAndAttemptsStayAtOne()
    {
        var notification = QueueSimple(TimeProvider.System);

        using (var setupSession = postgres.SessionFactory.OpenSession())
        using (var setupTransaction = setupSession.BeginTransaction())
        {
            await setupSession.SaveAsync(notification);
            await setupTransaction.CommitAsync();
        }

        using var firstSession = postgres.SessionFactory.OpenSession();
        using var secondSession = postgres.SessionFactory.OpenSession();
        var first = await firstSession.GetAsync<Notification>(notification.Id);
        var second = await secondSession.GetAsync<Notification>(notification.Id);
        Assert.NotNull(first);
        Assert.NotNull(second);

        var now = DateTime.UtcNow;

        using (var firstTransaction = firstSession.BeginTransaction())
        {
            Assert.True(first.MarkProcessing(now).IsSuccess);
            await firstTransaction.CommitAsync();
        }

        using var secondTransaction = secondSession.BeginTransaction();
        Assert.True(second.MarkProcessing(now).IsSuccess);

        var exception = await Record.ExceptionAsync(() => secondTransaction.CommitAsync());

        Assert.NotNull(exception);
        Assert.True(ContainsStaleObjectState(exception), exception.ToString());

        using var verifySession = postgres.SessionFactory.OpenSession();
        var stored = await verifySession.GetAsync<Notification>(notification.Id);
        Assert.NotNull(stored);
        Assert.Equal(1, stored.Attempts);
        Assert.Equal(2, stored.Version);
        Assert.Equal(NotificationStatus.Processing, stored.Status);
    }

    [Fact]
    public async Task Migration_CreatesExpectedIndexes()
    {
        using var session = postgres.SessionFactory.OpenSession();

        var uniqueKey = await GetIndexDefinition(session, "ux_email_templates_key");
        var worker = await GetIndexDefinition(session, "ix_notifications_status_next_attempt_at_utc");
        var listing = await GetIndexDefinition(session, "ix_notifications_app_key_created_at_utc");

        Assert.NotNull(uniqueKey);
        Assert.Contains("CREATE UNIQUE INDEX", uniqueKey);
        Assert.Contains("email_templates", uniqueKey);
        Assert.Contains("(key)", uniqueKey);

        Assert.NotNull(worker);
        Assert.DoesNotContain("UNIQUE", worker);
        Assert.Contains("notifications", worker);
        Assert.Contains("(status, next_attempt_at_utc)", worker);

        Assert.NotNull(listing);
        Assert.DoesNotContain("UNIQUE", listing);
        Assert.Contains("notifications", listing);
        Assert.Contains("(app_key, created_at_utc DESC)", listing);
    }

    [Fact]
    public async Task Notification_WithBogusStatus_ViolatesCheckConstraintWithSqlState23514()
    {
        using var session = postgres.SessionFactory.OpenSession();
        using var transaction = session.BeginTransaction();

        var exception = await Assert.ThrowsAsync<GenericADOException>(() => session
            .CreateSQLQuery(
                """
                INSERT INTO notifications (id, app_key, to_email, subject, html_body, status, max_attempts, next_attempt_at_utc, created_at_utc, updated_at_utc)
                VALUES (:id, 'bogus-app', 'user@example.com', 'Subject', '<p>x</p>', 'Bogus', 3, now(), now(), now())
                """)
            .SetGuid("id", Guid.CreateVersion7())
            .ExecuteUpdateAsync());

        var postgresException = FindPostgresException(exception);
        Assert.NotNull(postgresException);
        Assert.Equal("23514", postgresException.SqlState);
        Assert.Equal("ck_notifications_status", postgresException.ConstraintName);
    }

    private static async Task<string?> GetIndexDefinition(ISession session, string indexName) =>
        await session
            .CreateSQLQuery("SELECT indexdef FROM pg_indexes WHERE indexname = :name")
            .SetString("name", indexName)
            .UniqueResultAsync<string>();
}
