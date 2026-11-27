using Href.Core.Api.Common.Persistence;
using Href.Core.Api.Modules.Notifications;
using Href.Core.Api.Modules.Notifications.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NHibernate;
using Npgsql;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace Href.Core.Tests.Integration;

/// <summary>
/// Runs the dispatcher with the production SQL (no <c>AppKeyFilter</c>) against a database created for each test
/// inside the shared PostgreSQL container, migrated with <see cref="DatabaseMigrator"/> and dropped afterwards.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class NotificationDispatcherProductionSqlIntegrationTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset StartInstant = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    private readonly string _databaseName = $"worker_{Guid.NewGuid():N}";
    private readonly FakeTimeProvider _clock = new(StartInstant);
    private readonly FakeEmailSender _sender = new();
    private readonly CapturingLogSink _sink = new();
    private Serilog.Core.Logger? _serilog;
    private string _connectionString = null!;
    private ISessionFactory? _sessionFactory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await ExecuteAsync(postgres.ConnectionString, $"CREATE DATABASE \"{_databaseName}\"");
        _connectionString = new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { Database = _databaseName }.ConnectionString;
        DatabaseMigrator.Migrate(_connectionString, NullLogger.Instance);
        _sessionFactory = NHibernateExtensions.BuildSessionFactory(_connectionString, _clock);
        _serilog = new LoggerConfiguration().MinimumLevel.Verbose().Enrich.FromLogContext().WriteTo.Sink(_sink).CreateLogger();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _sessionFactory?.Dispose();

            if (_serilog is not null)
            {
                await _serilog.DisposeAsync();
            }
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteAsync(postgres.ConnectionString, $"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)");
        }
    }

    [Fact]
    public async Task ProductionSql_ClaimsEveryAppKeyAndRecoversStuckRows()
    {
        var first = await QueueAsync("app-one", "one@example.test");
        var second = await QueueAsync("app-two", "two@example.test");
        var dispatcher = CreateDispatcher();
        Assert.Null(dispatcher.AppKeyFilter);

        var result = await dispatcher.DispatchBatchAsync(Ct);

        Assert.Equal(new DispatchResult(2, 0), result);
        Assert.Equal(NotificationStatus.Sent, (await LoadAsync(first.Id)).Status);
        Assert.Equal(NotificationStatus.Sent, (await LoadAsync(second.Id)).Status);

        var stuck = await QueueAsync("app-three", "three@example.test");
        Assert.Equal(new[] { stuck.Id }, (await dispatcher.ClaimBatchAsync(Ct)).Select(item => item.Id));
        _clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(0, await dispatcher.RecoverStuckAsync(Ct));
        _clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(1, await dispatcher.RecoverStuckAsync(Ct));

        var recovered = await LoadAsync(stuck.Id);
        Assert.Equal(NotificationStatus.Pending, recovered.Status);
        Assert.Equal(1, recovered.Attempts);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime, recovered.NextAttemptAtUtc);
        Assert.Equal(NotificationStatus.Sent, (await LoadAsync(first.Id)).Status);
    }

    [Fact]
    public async Task DispatchBatch_WhenTheSendSucceedsButRecordingFails_LogsErrorAndReportsAnUnexpectedError()
    {
        await ExecuteAsync(
            _connectionString,
            """
            CREATE FUNCTION reject_sent() RETURNS trigger AS $$
            BEGIN
                RAISE EXCEPTION 'recording rejected by test trigger';
            END
            $$ LANGUAGE plpgsql;

            CREATE TRIGGER reject_sent BEFORE UPDATE ON notifications
            FOR EACH ROW WHEN (NEW.status = 'Sent') EXECUTE FUNCTION reject_sent();
            """);
        var queued = await QueueAsync("app-norecord", "norecord@example.test");

        var result = await CreateDispatcher().DispatchBatchAsync(Ct);

        Assert.Equal(new DispatchResult(1, 1), result);
        Assert.Single(_sender.Sent);
        var stored = await LoadAsync(queued.Id);
        Assert.Equal(NotificationStatus.Processing, stored.Status);
        Assert.Null(stored.SentAtUtc);
        Assert.Contains(_sink.Events, logEvent =>
            logEvent.Level == LogEventLevel.Error
            && logEvent.MessageTemplate.Text.Contains("was sent but its result could not be recorded", StringComparison.Ordinal)
            && logEvent.Properties.TryGetValue(NotificationDispatcher.NotificationIdProperty, out var value)
            && Equals(Assert.IsType<ScalarValue>(value).Value, queued.Id));
    }

    private NotificationDispatcher CreateDispatcher() =>
        new(
            _sessionFactory!,
            _sender,
            new TemplateRenderer(),
            _clock,
            Options.Create(new NotificationWorkerOptions { StuckAfterMinutes = 10 }),
            new SerilogLoggerFactory(_serilog!).CreateLogger<NotificationDispatcher>());

    private async Task<Notification> QueueAsync(string appKey, string toEmail)
    {
        var notification = Notification.Queue(Guid.NewGuid(), appKey, toEmail, null, "Hola", "<p>Body</p>", null, null, null, null, _clock).Value;

        using var session = _sessionFactory!.OpenSession();
        using var transaction = session.BeginTransaction();
        await session.SaveAsync(notification, Ct);
        await transaction.CommitAsync(Ct);

        return notification;
    }

    private async Task<Notification> LoadAsync(Guid id)
    {
        using var session = _sessionFactory!.OpenSession();
        var notification = await session.GetAsync<Notification>(id, Ct);
        Assert.NotNull(notification);
        return notification;
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }
}
