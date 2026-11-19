using Href.Core.Api.Modules.Audit;

namespace Href.Core.Tests.Modules.Audit;

internal sealed class CapturingAuditService : IAuditService
{
    public List<AuditEntry> Entries { get; } = [];

    public Task<AuditLog> RecordAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        Entries.Add(entry);
        return Task.FromResult(AuditLog.Create(DateTime.UtcNow, entry.Action, AuditActorType.System));
    }
}
