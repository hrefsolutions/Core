namespace Href.Core.Api.Common.Domain;

/// <summary>
/// Marks an entity whose timestamps are set by <c>AuditTimestampListener</c>.
/// Implement both properties as <c>public virtual DateTime ... { get; protected set; }</c>.
/// </summary>
public interface IAuditableEntity
{
    DateTime CreatedAtUtc { get; }

    DateTime UpdatedAtUtc { get; }
}
