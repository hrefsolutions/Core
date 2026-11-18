using Href.Core.Api.Common.Domain;
using NHibernate.Event;
using NHibernate.Persister.Entity;

namespace Href.Core.Api.Common.Persistence;

/// <summary>
/// Sets <see cref="IAuditableEntity.CreatedAtUtc"/> and <see cref="IAuditableEntity.UpdatedAtUtc"/>
/// on both the entity and the state NHibernate writes to the database.
/// </summary>
public sealed class AuditTimestampListener(TimeProvider timeProvider) : IPreInsertEventListener, IPreUpdateEventListener
{
    public bool OnPreInsert(PreInsertEvent @event)
    {
        if (@event.Entity is IAuditableEntity)
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
            SetValue(@event.Persister, @event.State, @event.Entity, nameof(IAuditableEntity.CreatedAtUtc), now);
            SetValue(@event.Persister, @event.State, @event.Entity, nameof(IAuditableEntity.UpdatedAtUtc), now);
        }

        return false;
    }

    public bool OnPreUpdate(PreUpdateEvent @event)
    {
        if (@event.Entity is IAuditableEntity)
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
            SetValue(@event.Persister, @event.State, @event.Entity, nameof(IAuditableEntity.UpdatedAtUtc), now);
        }

        return false;
    }

    public Task<bool> OnPreInsertAsync(PreInsertEvent @event, CancellationToken cancellationToken) =>
        Task.FromResult(OnPreInsert(@event));

    public Task<bool> OnPreUpdateAsync(PreUpdateEvent @event, CancellationToken cancellationToken) =>
        Task.FromResult(OnPreUpdate(@event));

    private static void SetValue(IEntityPersister persister, object[] state, object entity, string propertyName, DateTime value)
    {
        var index = Array.IndexOf(persister.PropertyNames, propertyName);

        if (index < 0)
        {
            return;
        }

        state[index] = value;
        persister.SetPropertyValue(entity, index, value);
    }
}
