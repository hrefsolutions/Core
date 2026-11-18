using Href.Core.Api.Common.Domain;
using Href.Core.Api.Common.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using ISession = NHibernate.ISession;

namespace Href.Core.Tests.Common;

public sealed class NHibernateExtensionsTests
{
    private const string SampleConnectionString = "Host=localhost;Port=5432;Database=href_core;Username=href;Password=href";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AddNHibernate_WhenConnectionStringIsMissing_ThrowsInvalidOperationException(string? connectionString)
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = connectionString,
            })
            .Build();

        Assert.Throws<InvalidOperationException>(() => services.AddNHibernate(configuration));
    }

    [Fact]
    public void AddNHibernate_RegistersSingletonSessionFactoryAndScopedSession()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = SampleConnectionString,
            })
            .Build();

        services.AddNHibernate(configuration);

        var factoryDescriptor = Assert.Single(services, descriptor => descriptor.ServiceType == typeof(ISessionFactory));
        var sessionDescriptor = Assert.Single(services, descriptor => descriptor.ServiceType == typeof(ISession));
        Assert.Equal(ServiceLifetime.Singleton, factoryDescriptor.Lifetime);
        Assert.Equal(ServiceLifetime.Scoped, sessionDescriptor.Lifetime);
    }

    [Fact]
    public void BuildSessionFactory_WithAdditionalMappingAssembly_RegistersMappingsWithoutDatabaseConnection()
    {
        using var sessionFactory = NHibernateExtensions.BuildSessionFactory(
            SampleConnectionString,
            TimeProvider.System,
            typeof(NHibernateExtensionsTests).Assembly);

        var metadata = sessionFactory.GetClassMetadata(typeof(SampleMappedEntity));

        Assert.NotNull(metadata);
    }

    public class SampleMappedEntity : Entity
    {
        public virtual string Name { get; set; } = string.Empty;
    }

    public sealed class SampleMappedEntityMap : ClassMapping<SampleMappedEntity>
    {
        public SampleMappedEntityMap()
        {
            Table("sample_mapped_entities");
            Id(entity => entity.Id, mapper =>
            {
                mapper.Column("id");
                mapper.Generator(Generators.Assigned);
            });
            Property(entity => entity.Name, mapper =>
            {
                mapper.Column("name");
                mapper.NotNullable(true);
            });
        }
    }
}
