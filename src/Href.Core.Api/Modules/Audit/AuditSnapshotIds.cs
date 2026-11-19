namespace Href.Core.Api.Modules.Audit;

internal static class AuditSnapshotIds
{
    /// <summary>
    /// Returns the entity identifier shared by the before and after snapshots. At least one must exist and, when both exist,
    /// they must describe the same entity.
    /// </summary>
    public static Guid Resolve(Guid? beforeId, Guid? afterId)
    {
        if (beforeId is null && afterId is null)
        {
            throw new ArgumentException("At least one of the before and after snapshots is required.");
        }

        if (beforeId is not null && afterId is not null && beforeId != afterId)
        {
            throw new ArgumentException("The before and after snapshots must describe the same entity.");
        }

        return beforeId ?? afterId!.Value;
    }
}
