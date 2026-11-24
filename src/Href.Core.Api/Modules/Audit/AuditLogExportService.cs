using System.Text;
using Href.Core.Api.Common.Results;
using Microsoft.Extensions.Options;
using NHibernate;
using NHibernate.Type;
using ISession = NHibernate.ISession;

namespace Href.Core.Api.Modules.Audit;

/// <summary>
/// Admin export of <see cref="AuditLog"/> rows as CSV or JSON. <see cref="PrepareAsync"/> validates the request, fixes a
/// cutoff (rows recorded later, including this export's own audit row, are excluded), counts the matching rows against
/// <see cref="AuditLogExportOptions.MaxRows"/> and records <see cref="AuditActions.AuditLogExported"/> in its own
/// transaction. <see cref="WriteAsync"/> then streams the rows oldest first, reading <see cref="AuditLogExportOptions.ChunkSize"/>
/// rows per query with a keyset on <c>(occurred_at_utc, id)</c>, so the whole result is never held in memory.
/// </summary>
public sealed class AuditLogExportService(
    ISession session,
    IAuditService auditService,
    TimeProvider timeProvider,
    IOptions<AuditLogExportOptions> options)
{
    private const string SelectColumns =
        "SELECT id, occurred_at_utc, action, entity_type, entity_id, actor_type, actor_id, app_key, "
        + "CAST(old_values AS text) AS old_values, CAST(new_values AS text) AS new_values, correlation_id, ip_address, user_agent "
        + "FROM audit_logs";

    private readonly AuditLogExportOptions _options = options.Value;

    public async Task<Result<AuditLogExport>> PrepareAsync(ExportAuditLogsQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!TryParseFormat(request.Format, out var format))
        {
            return AuditLogErrors.InvalidFormat;
        }

        var filterResult = AuditLogFilter.Create(request.Filter);

        if (filterResult.IsFailure)
        {
            return filterResult.Error;
        }

        var filter = filterResult.Value;
        var cutoffUtc = timeProvider.GetUtcNow().UtcDateTime;
        var rowCount = await CountAsync(filter, cutoffUtc, _options.MaxRows + 1, cancellationToken);

        if (rowCount > _options.MaxRows)
        {
            return AuditLogErrors.ExportTooLarge;
        }

        var export = new AuditLogExport(format, filter, cutoffUtc, rowCount);

        using var transaction = session.BeginTransaction();
        await auditService.RecordAsync(
            new AuditEntry(AuditActions.AuditLogExported, NewValues: Snapshot(export)),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return export;
    }

    public async Task WriteAsync(AuditLogExport export, Stream destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(export);
        ArgumentNullException.ThrowIfNull(destination);

        await using var writer = AuditLogExportWriter.Create(export.Format, destination);
        await writer.StartAsync(cancellationToken);

        AuditLogExportRow? last = null;
        var written = 0;

        while (written < _options.MaxRows)
        {
            var limit = Math.Min(_options.ChunkSize, _options.MaxRows - written);
            var chunk = await ReadChunkAsync(export, last, limit, cancellationToken);

            foreach (var row in chunk)
            {
                await writer.WriteRowAsync(row, cancellationToken);
            }

            await writer.FlushAsync(cancellationToken);
            written += chunk.Count;

            if (chunk.Count < limit)
            {
                break;
            }

            last = chunk[^1];
        }

        await writer.CompleteAsync(cancellationToken);
    }

    private async Task<long> CountAsync(AuditLogFilter filter, DateTime cutoffUtc, int limit, CancellationToken cancellationToken)
    {
        var sql = new StringBuilder("SELECT count(*) FROM (SELECT 1 FROM audit_logs");
        var parameters = AppendWhere(sql, filter, cutoffUtc);
        sql.Append(" LIMIT :limit) AS matching");

        var query = session.CreateSQLQuery(sql.ToString());
        Bind(query, parameters);
        query.SetInt32("limit", limit);

        var count = await query.UniqueResultAsync<object>(cancellationToken);
        return Convert.ToInt64(count, System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<List<AuditLogExportRow>> ReadChunkAsync(
        AuditLogExport export,
        AuditLogExportRow? after,
        int limit,
        CancellationToken cancellationToken)
    {
        var sql = new StringBuilder(SelectColumns);
        var parameters = AppendWhere(sql, export.Filter, export.CutoffUtc);

        if (after is not null)
        {
            sql.Append(" AND (occurred_at_utc, id) > (:afterOccurredAtUtc, :afterId)");
            parameters.Add(new QueryParameter("afterOccurredAtUtc", after.OccurredAtUtc, NHibernateUtil.UtcDateTime));
            parameters.Add(new QueryParameter("afterId", after.Id, NHibernateUtil.Guid));
        }

        sql.Append(" ORDER BY occurred_at_utc, id LIMIT :limit");

        var query = session.CreateSQLQuery(sql.ToString())
            .AddScalar("id", NHibernateUtil.Guid)
            .AddScalar("occurred_at_utc", NHibernateUtil.UtcDateTime)
            .AddScalar("action", NHibernateUtil.String)
            .AddScalar("entity_type", NHibernateUtil.String)
            .AddScalar("entity_id", NHibernateUtil.String)
            .AddScalar("actor_type", NHibernateUtil.String)
            .AddScalar("actor_id", NHibernateUtil.String)
            .AddScalar("app_key", NHibernateUtil.String)
            .AddScalar("old_values", NHibernateUtil.String)
            .AddScalar("new_values", NHibernateUtil.String)
            .AddScalar("correlation_id", NHibernateUtil.String)
            .AddScalar("ip_address", NHibernateUtil.String)
            .AddScalar("user_agent", NHibernateUtil.String);
        Bind(query, parameters);
        query.SetInt32("limit", limit);

        var rows = await query.ListAsync<object[]>(cancellationToken);

        return rows
            .Select(row => new AuditLogExportRow(
                (Guid)row[0],
                (DateTime)row[1],
                (string)row[2],
                (string?)row[3],
                (string?)row[4],
                (string)row[5],
                (string?)row[6],
                (string?)row[7],
                (string?)row[8],
                (string?)row[9],
                (string?)row[10],
                (string?)row[11],
                (string?)row[12]))
            .ToList();
    }

    private static List<QueryParameter> AppendWhere(StringBuilder sql, AuditLogFilter filter, DateTime cutoffUtc)
    {
        var parameters = new List<QueryParameter> { new("cutoffUtc", cutoffUtc, NHibernateUtil.UtcDateTime) };
        sql.Append(" WHERE occurred_at_utc < :cutoffUtc");

        if (filter.FromUtc is { } fromUtc)
        {
            sql.Append(" AND occurred_at_utc >= :fromUtc");
            parameters.Add(new QueryParameter("fromUtc", fromUtc, NHibernateUtil.UtcDateTime));
        }

        if (filter.ToUtc is { } toUtc)
        {
            sql.Append(" AND occurred_at_utc <= :toUtc");
            parameters.Add(new QueryParameter("toUtc", toUtc, NHibernateUtil.UtcDateTime));
        }

        AppendEquals(sql, parameters, "app_key", "appKey", filter.AppKey);
        AppendEquals(sql, parameters, "actor_type", "actorType", filter.ActorType?.ToString());
        AppendEquals(sql, parameters, "actor_id", "actorId", filter.ActorId);
        AppendEquals(sql, parameters, "action", "action", filter.Action);
        AppendEquals(sql, parameters, "entity_type", "entityType", filter.EntityType);
        AppendEquals(sql, parameters, "entity_id", "entityId", filter.EntityId);
        AppendEquals(sql, parameters, "correlation_id", "correlationId", filter.CorrelationId);

        return parameters;
    }

    private static void AppendEquals(StringBuilder sql, List<QueryParameter> parameters, string column, string name, string? value)
    {
        if (value is null)
        {
            return;
        }

        sql.Append(" AND ").Append(column).Append(" = :").Append(name);
        parameters.Add(new QueryParameter(name, value, NHibernateUtil.String));
    }

    private static void Bind(ISQLQuery query, List<QueryParameter> parameters)
    {
        foreach (var parameter in parameters)
        {
            query.SetParameter(parameter.Name, parameter.Value, parameter.Type);
        }
    }

    private static bool TryParseFormat(string? value, out AuditLogExportFormat format)
    {
        switch (value?.Trim())
        {
            case "csv":
                format = AuditLogExportFormat.Csv;
                return true;
            case "json":
                format = AuditLogExportFormat.Json;
                return true;
            default:
                format = default;
                return false;
        }
    }

    private static AuditLogExportAuditSnapshot Snapshot(AuditLogExport export) =>
        new(
            export.Format == AuditLogExportFormat.Csv ? "csv" : "json",
            export.Filter.FromUtc,
            export.Filter.ToUtc,
            export.Filter.AppKey,
            export.Filter.ActorType?.ToString(),
            export.Filter.ActorId,
            export.Filter.Action,
            export.Filter.EntityType,
            export.Filter.EntityId,
            export.Filter.CorrelationId,
            export.CutoffUtc,
            export.RowCount);

    private sealed record QueryParameter(string Name, object Value, IType Type);
}
