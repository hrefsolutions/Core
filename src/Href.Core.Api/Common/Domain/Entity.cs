using NHibernate;

namespace Href.Core.Api.Common.Domain;

/// <summary>
/// Base class for persistent entities. The identifier is a UUIDv7 assigned on creation,
/// so mappings use <c>Generators.Assigned</c>. Members are virtual to allow NHibernate proxies.
/// </summary>
public abstract class Entity
{
    public virtual Guid Id { get; protected set; } = Guid.CreateVersion7();

    public override bool Equals(object? obj)
    {
        if (obj is not Entity other)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return NHibernateUtil.GetClass(this) == NHibernateUtil.GetClass(other) && Id == other.Id;
    }

    public override int GetHashCode() => HashCode.Combine(NHibernateUtil.GetClass(this), Id);

    public static bool operator ==(Entity? left, Entity? right) => Equals(left, right);

    public static bool operator !=(Entity? left, Entity? right) => !Equals(left, right);
}
