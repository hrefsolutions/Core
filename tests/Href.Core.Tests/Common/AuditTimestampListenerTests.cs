using Href.Core.Api.Common.Domain;
using Href.Core.Api.Common.Persistence;
using Microsoft.Extensions.Time.Testing;
using NHibernate;
using NHibernate.Event;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using NHibernate.Persister.Entity;

namespace Href.Core.Tests.Common;

public sealed class AuditTimestampListenerTests : IDisposable
{
    private const string SampleConnectionString = "Host=localhost;Port=5432;Database=href_core;Username=href;Password=href";

    private readonly FakeTimeProvider _timeProvider = new(new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero));
    private readonly ISessionFactory _sessionFactory;
    private readonly AuditTimestampListener _listener;

    public AuditTimestampListenerTests()
    {
        _sessionFactory = NHibernateExtensions.BuildSessionFactory(
            SampleConnectionString,
            _timeProvider,
            typeof(AuditTimestampListenerTests).Assembly);
        _listener = new AuditTimestampListener(_timeProvider);
    }

    public void Dispose() => _sessionFactory.Dispose();

    [Fact]
    public void OnPreInsert_WhenEntityIsAuditable_SetsCreatedAndUpdatedAtUtcOnEntityAndState()
    {
        var persister = GetPersister<SampleAuditableEntity>();
        var entity = new SampleAuditableEntity { Name = "Initial" };
        var state = persister.GetPropertyValuesToInsert(entity, new Dictionary<object, object>(), null);
        var preInsertEvent = new PreInsertEvent(entity, entity.Id, state, persister, null);

        var veto = _listener.OnPreInsert(preInsertEvent);

        var expectedUtc = _timeProvider.GetUtcNow().UtcDateTime;
        Assert.False(veto);
        Assert.Equal(expectedUtc, entity.CreatedAtUtc);
        Assert.Equal(expectedUtc, entity.UpdatedAtUtc);
        Assert.Equal(DateTimeKind.Utc, entity.CreatedAtUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, entity.UpdatedAtUtc.Kind);
        Assert.Equal(expectedUtc, state[Array.IndexOf(persister.PropertyNames, nameof(IAuditableEntity.CreatedAtUtc))]);
        Assert.Equal(expectedUtc, state[Array.IndexOf(persister.PropertyNames, nameof(IAuditableEntity.UpdatedAtUtc))]);
    }

    [Fact]
    public void OnPreUpdate_WhenEntityIsAuditable_UpdatesOnlyUpdatedAtUtcOnEntityAndState()
    {
        var persister = GetPersister<SampleAuditableEntity>();
        var entity = new SampleAuditableEntity { Name = "Initial" };
        var insertState = persister.GetPropertyValuesToInsert(entity, new Dictionary<object, object>(), null);
        _listener.OnPreInsert(new PreInsertEvent(entity, entity.Id, insertState, persister, null));
        var createdAtUtc = entity.CreatedAtUtc;

        _timeProvider.Advance(TimeSpan.FromMinutes(15));
        entity.Name = "Modified";
        var updateState = (object[])insertState.Clone();
        var preUpdateEvent = new PreUpdateEvent(entity, entity.Id, updateState, insertState, persister, null);

        var veto = _listener.OnPreUpdate(preUpdateEvent);

        var expectedUpdatedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
        Assert.False(veto);
        Assert.Equal(createdAtUtc, entity.CreatedAtUtc);
        Assert.Equal(expectedUpdatedAtUtc, entity.UpdatedAtUtc);
        Assert.Equal(DateTimeKind.Utc, entity.UpdatedAtUtc.Kind);
        Assert.Equal(createdAtUtc, updateState[Array.IndexOf(persister.PropertyNames, nameof(IAuditableEntity.CreatedAtUtc))]);
        Assert.Equal(expectedUpdatedAtUtc, updateState[Array.IndexOf(persister.PropertyNames, nameof(IAuditableEntity.UpdatedAtUtc))]);
    }

    [Fact]
    public async Task AsyncListeners_WhenEntityIsAuditable_SetTimestamps()
    {
        var persister = GetPersister<SampleAuditableEntity>();
        var entity = new SampleAuditableEntity { Name = "Async" };
        var state = persister.GetPropertyValuesToInsert(entity, new Dictionary<object, object>(), null);

        var insertVeto = await _listener.OnPreInsertAsync(
            new PreInsertEvent(entity, entity.Id, state, persister, null),
            CancellationToken.None);

        var createdAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
        Assert.False(insertVeto);
        Assert.Equal(createdAtUtc, entity.CreatedAtUtc);

        _timeProvider.Advance(TimeSpan.FromMinutes(5));
        var updateState = (object[])state.Clone();
        var updateVeto = await _listener.OnPreUpdateAsync(
            new PreUpdateEvent(entity, entity.Id, updateState, state, persister, null),
            CancellationToken.None);

        Assert.False(updateVeto);
        Assert.Equal(createdAtUtc, entity.CreatedAtUtc);
        Assert.Equal(_timeProvider.GetUtcNow().UtcDateTime, entity.UpdatedAtUtc);
    }

    [Fact]
    public void OnPreInsertAndUpdate_WhenEntityIsNotAuditable_LeavesStateUntouched()
    {
        var persister = GetPersister<NHibernateExtensionsTests.SampleMappedEntity>();
        var entity = new NHibernateExtensionsTests.SampleMappedEntity { Name = "Plain" };
        var state = persister.GetPropertyValuesToInsert(entity, new Dictionary<object, object>(), null);
        var originalState = (object[])state.Clone();

        var insertVeto = _listener.OnPreInsert(new PreInsertEvent(entity, entity.Id, state, persister, null));
        var updateVeto = _listener.OnPreUpdate(new PreUpdateEvent(entity, entity.Id, state, originalState, persister, null));

        Assert.False(insertVeto);
        Assert.False(updateVeto);
        Assert.Equal(originalState, state);
    }

    private IEntityPersister GetPersister<TEntity>() =>
        (IEntityPersister)_sessionFactory.GetClassMetadata(typeof(TEntity));

    public class SampleAuditableEntity : Entity, IAuditableEntity
    {
        public virtual string Name { get; set; } = string.Empty;

        public virtual DateTime CreatedAtUtc { get; protected set; }

        public virtual DateTime UpdatedAtUtc { get; protected set; }
    }

    public sealed class SampleAuditableEntityMap : ClassMapping<SampleAuditableEntity>
    {
        public SampleAuditableEntityMap()
        {
            Table("sample_auditable_entities");
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
            Property(entity => entity.CreatedAtUtc, mapper =>
            {
                mapper.Column("created_at_utc");
                mapper.Type(NHibernateUtil.UtcDateTime);
                mapper.NotNullable(true);
                mapper.Update(false);
            });
            Property(entity => entity.UpdatedAtUtc, mapper =>
            {
                mapper.Column("updated_at_utc");
                mapper.Type(NHibernateUtil.UtcDateTime);
                mapper.NotNullable(true);
            });
        }
    }
}
