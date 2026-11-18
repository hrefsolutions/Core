using System.Reflection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NHibernate;
using NHibernate.Cfg;
using NHibernate.Dialect;
using NHibernate.Driver;
using NHibernate.Mapping.ByCode;
using ISession = NHibernate.ISession;

namespace Href.Core.Api.Common.Persistence;

public static class NHibernateExtensions
{
    /// <summary>
    /// Registers a singleton <see cref="ISessionFactory"/> built from every <c>ClassMapping</c>
    /// in this assembly and a scoped <see cref="ISession"/> per request.
    /// </summary>
    public static IServiceCollection AddNHibernate(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
        }

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(provider =>
        {
            var configured = provider.GetService<IConfiguration>()?.GetConnectionString("DefaultConnection");
            var effectiveConnectionString = string.IsNullOrWhiteSpace(configured) ? connectionString : configured;
            return BuildSessionFactory(effectiveConnectionString, provider.GetRequiredService<TimeProvider>());
        });
        services.AddScoped(provider => provider.GetRequiredService<ISessionFactory>().OpenSession());

        return services;
    }

    internal static ISessionFactory BuildSessionFactory(
        string connectionString,
        TimeProvider timeProvider,
        params Assembly[] mappingAssemblies)
    {
        var mapper = new ModelMapper();
        var assemblies = mappingAssemblies.Length == 0
            ? [typeof(NHibernateExtensions).Assembly]
            : new[] { typeof(NHibernateExtensions).Assembly }.Concat(mappingAssemblies).Distinct();

        foreach (var assembly in assemblies)
        {
            mapper.AddMappings(assembly.GetExportedTypes());
        }

        var configuration = new Configuration();
        configuration.DataBaseIntegration(database =>
        {
            database.ConnectionString = connectionString;
            database.Dialect<PostgreSQL83Dialect>();
            database.Driver<NpgsqlDriver>();
            database.KeywordsAutoImport = Hbm2DDLKeyWords.None;
        });
        configuration.AddMapping(mapper.CompileMappingForAllExplicitlyAddedEntities());

        var auditListener = new AuditTimestampListener(timeProvider);
        configuration.EventListeners.PreInsertEventListeners = [auditListener];
        configuration.EventListeners.PreUpdateEventListeners = [auditListener];

        return configuration.BuildSessionFactory();
    }
}
