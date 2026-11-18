using Href.Core.Api.Common.Persistence;
using Href.Core.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Href.Core.Tests.Integration;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class PersistenceIntegrationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task SessionFactory_ExecutesSelectOneAgainstPostgres()
    {
        using var session = postgres.SessionFactory.OpenSession();

        var result = await session.CreateSQLQuery("SELECT 1").UniqueResultAsync<int>();

        Assert.Equal(1, result);
    }

    [Fact]
    public async Task DatabaseMigrator_CreatesSchemaVersionsJournalTable()
    {
        using var session = postgres.SessionFactory.OpenSession();

        var count = await session
            .CreateSQLQuery("SELECT COUNT(*) FROM schemaversions WHERE scriptname LIKE '%0000_baseline.sql'")
            .UniqueResultAsync<long>();

        Assert.Equal(1L, count);
    }

    [Fact]
    public void DatabaseMigrator_WhenScriptFails_ThrowsInvalidOperationException()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            DatabaseMigrator.Migrate(
                postgres.ConnectionString,
                NullLogger.Instance,
                scriptsAssembly: typeof(PersistenceIntegrationTests).Assembly,
                scriptFilter: name => name.EndsWith("9999_failing_script.sql", StringComparison.Ordinal)));

        Assert.Contains("9999_failing_script.sql", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuditTimestampListener_RoundTripsCreatedAndUpdatedTimestampsAsUtc()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 11, 18, 15, 30, 0, TimeSpan.Zero));
        using var sessionFactory = postgres.BuildSessionFactory(
            timeProvider,
            typeof(AuditTimestampListenerTests).Assembly);
        using var session = sessionFactory.OpenSession();

        await session.CreateSQLQuery(
            """
            CREATE TABLE IF NOT EXISTS sample_auditable_entities (
                id UUID PRIMARY KEY,
                name VARCHAR(200) NOT NULL,
                created_at_utc TIMESTAMPTZ NOT NULL,
                updated_at_utc TIMESTAMPTZ NOT NULL
            );
            """).ExecuteUpdateAsync();

        var entity = new AuditTimestampListenerTests.SampleAuditableEntity
        {
            Name = $"item-{Guid.NewGuid():N}",
        };
        var initialUtc = timeProvider.GetUtcNow().UtcDateTime;

        using (var insertTransaction = session.BeginTransaction())
        {
            await session.SaveAsync(entity);
            await insertTransaction.CommitAsync();
        }

        timeProvider.Advance(TimeSpan.FromMinutes(30));
        var updatedUtc = timeProvider.GetUtcNow().UtcDateTime;

        using (var updateTransaction = session.BeginTransaction())
        {
            entity.Name = $"updated-{Guid.NewGuid():N}";
            await session.FlushAsync();
            await updateTransaction.CommitAsync();
        }

        session.Clear();
        var reloaded = await session.GetAsync<AuditTimestampListenerTests.SampleAuditableEntity>(entity.Id);

        Assert.NotNull(reloaded);
        Assert.Equal(initialUtc, reloaded.CreatedAtUtc);
        Assert.Equal(updatedUtc, reloaded.UpdatedAtUtc);
        Assert.Equal(DateTimeKind.Utc, reloaded.CreatedAtUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, reloaded.UpdatedAtUtc.Kind);
    }
}
