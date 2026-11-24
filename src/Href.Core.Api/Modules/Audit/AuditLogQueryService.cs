using Href.Core.Api.Common.Results;
using NHibernate.Linq;
using ISession = NHibernate.ISession;

namespace Href.Core.Api.Modules.Audit;

/// <summary>
/// Read-only admin queries over <see cref="AuditLog"/> (no transaction). Results are always ordered by
/// <c>OccurredAtUtc DESC, Id DESC</c>, the order of the <c>ix_audit_logs_*_occurred_at_utc_id</c> indexes.
/// </summary>
public sealed class AuditLogQueryService(ISession session)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    public async Task<Result<AuditLogPageResponse>> ListAsync(ListAuditLogsQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Page < 1)
        {
            return AuditLogErrors.InvalidPage;
        }

        if (request.PageSize is < 1 or > MaxPageSize)
        {
            return AuditLogErrors.InvalidPageSize;
        }

        var filterResult = AuditLogFilter.Create(request.Filter);

        if (filterResult.IsFailure)
        {
            return filterResult.Error;
        }

        var query = filterResult.Value.Apply(session.Query<AuditLog>());
        var totalCount = await query.LongCountAsync(cancellationToken);
        var offset = (long)(request.Page - 1) * request.PageSize;

        if (offset >= totalCount)
        {
            return new AuditLogPageResponse([], request.Page, request.PageSize, totalCount);
        }

        var auditLogs = await query
            .OrderByDescending(auditLog => auditLog.OccurredAtUtc)
            .ThenByDescending(auditLog => auditLog.Id)
            .Skip((int)offset)
            .Take(request.PageSize)
            .ToListAsync(cancellationToken);

        return new AuditLogPageResponse(
            auditLogs.Select(AuditLogResponse.From).ToList(),
            request.Page,
            request.PageSize,
            totalCount);
    }
}
