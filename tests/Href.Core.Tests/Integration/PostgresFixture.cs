using System.Reflection;
using Href.Core.Api.Common.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using NHibernate;
using Testcontainers.PostgreSql;

namespace Href.Core.Tests.Integration;

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "Postgres";
}

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("href_core")
        .WithUsername("href")
        .WithPassword("href")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public ISessionFactory SessionFactory { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        DatabaseMigrator.Migrate(ConnectionString, NullLogger.Instance);
        SessionFactory = NHibernateExtensions.BuildSessionFactory(ConnectionString, TimeProvider.System);
    }

    public ISessionFactory BuildSessionFactory(TimeProvider timeProvider, params Assembly[] mappingAssemblies) =>
        NHibernateExtensions.BuildSessionFactory(ConnectionString, timeProvider, mappingAssemblies);

    public async ValueTask DisposeAsync()
    {
        SessionFactory?.Dispose();
        await _container.DisposeAsync();
    }
}
