using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Href.Core.Api.Modules.Audit;

/// <summary>
/// Writes exported rows to the response stream. Every write to the stream is asynchronous (Kestrel rejects synchronous I/O);
/// rows are buffered until <see cref="FlushAsync"/>, which the export calls once per chunk.
/// </summary>
internal abstract class AuditLogExportWriter : IAsyncDisposable
{
    public static readonly string[] Columns =
    [
        "id", "occurredAtUtc", "action", "entityType", "entityId", "actorType", "actorId", "appKey",
        "oldValues", "newValues", "correlationId", "ipAddress", "userAgent",
    ];

    public static AuditLogExportWriter Create(AuditLogExportFormat format, Stream destination) =>
        format == AuditLogExportFormat.Csv
            ? new CsvWriter(destination)
            : new JsonWriter(destination);

    public abstract Task StartAsync(CancellationToken cancellationToken);

    public abstract ValueTask WriteRowAsync(AuditLogExportRow row, CancellationToken cancellationToken);

    public abstract Task FlushAsync(CancellationToken cancellationToken);

    public abstract Task CompleteAsync(CancellationToken cancellationToken);

    public abstract ValueTask DisposeAsync();

    private static string FormatTimestamp(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// RFC 4180 CSV, UTF-8 with BOM (so Excel detects the encoding), CRLF line endings and a header row.
    /// </summary>
    private sealed class CsvWriter : AuditLogExportWriter
    {
        private readonly Stream _destination;
        private readonly StreamWriter _writer;

        public CsvWriter(Stream destination)
        {
            _destination = destination;
            _writer = new StreamWriter(destination, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 64 * 1024, leaveOpen: true);
        }

        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            await _destination.WriteAsync(Encoding.UTF8.Preamble.ToArray(), cancellationToken);
            await WriteRecordAsync(Columns, cancellationToken);
        }

        public override async ValueTask WriteRowAsync(AuditLogExportRow row, CancellationToken cancellationToken) =>
            await WriteRecordAsync(
                [
                    row.Id.ToString(),
                    FormatTimestamp(row.OccurredAtUtc),
                    row.Action,
                    row.EntityType,
                    row.EntityId,
                    row.ActorType,
                    row.ActorId,
                    row.AppKey,
                    row.OldValues,
                    row.NewValues,
                    row.CorrelationId,
                    row.IpAddress,
                    row.UserAgent,
                ],
                cancellationToken);

        public override Task FlushAsync(CancellationToken cancellationToken) => _writer.FlushAsync(cancellationToken);

        public override Task CompleteAsync(CancellationToken cancellationToken) => _writer.FlushAsync(cancellationToken);

        public override ValueTask DisposeAsync() => _writer.DisposeAsync();

        private async Task WriteRecordAsync(IReadOnlyList<string?> fields, CancellationToken cancellationToken)
        {
            var line = new StringBuilder();

            for (var index = 0; index < fields.Count; index++)
            {
                if (index > 0)
                {
                    line.Append(',');
                }

                line.Append(AuditLogCsv.Field(fields[index]));
            }

            line.Append("\r\n");
            await _writer.WriteAsync(line, cancellationToken);
        }
    }

    /// <summary>
    /// A JSON array of objects with the same camelCase properties as <c>GET /api/audit-logs</c> items; the snapshots are
    /// written as JSON values.
    /// </summary>
    private sealed class JsonWriter(Stream destination) : AuditLogExportWriter
    {
        private readonly Utf8JsonWriter _writer = new(destination);

        public override Task StartAsync(CancellationToken cancellationToken)
        {
            _writer.WriteStartArray();
            return Task.CompletedTask;
        }

        public override ValueTask WriteRowAsync(AuditLogExportRow row, CancellationToken cancellationToken)
        {
            _writer.WriteStartObject();
            _writer.WriteString(Columns[0], row.Id);
            _writer.WriteString(Columns[1], FormatTimestamp(row.OccurredAtUtc));
            _writer.WriteString(Columns[2], row.Action);
            WriteNullableString(Columns[3], row.EntityType);
            WriteNullableString(Columns[4], row.EntityId);
            _writer.WriteString(Columns[5], row.ActorType);
            WriteNullableString(Columns[6], row.ActorId);
            WriteNullableString(Columns[7], row.AppKey);
            WriteJson(Columns[8], row.OldValues);
            WriteJson(Columns[9], row.NewValues);
            WriteNullableString(Columns[10], row.CorrelationId);
            WriteNullableString(Columns[11], row.IpAddress);
            WriteNullableString(Columns[12], row.UserAgent);
            _writer.WriteEndObject();
            return ValueTask.CompletedTask;
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => _writer.FlushAsync(cancellationToken);

        public override async Task CompleteAsync(CancellationToken cancellationToken)
        {
            _writer.WriteEndArray();
            await _writer.FlushAsync(cancellationToken);
        }

        public override ValueTask DisposeAsync() => _writer.DisposeAsync();

        private void WriteNullableString(string name, string? value)
        {
            if (value is null)
            {
                _writer.WriteNull(name);
            }
            else
            {
                _writer.WriteString(name, value);
            }
        }

        private void WriteJson(string name, string? json)
        {
            if (json is null)
            {
                _writer.WriteNull(name);
                return;
            }

            _writer.WritePropertyName(name);
            _writer.WriteRawValue(json);
        }
    }
}

/// <summary>
/// CSV cell encoding for the audit export.
/// </summary>
internal static class AuditLogCsv
{
    /// <summary>
    /// Encodes one cell. A value starting with <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, tab or carriage return gets a leading
    /// <c>'</c> so spreadsheets do not run it as a formula (CSV injection); then a value with a comma, a double quote, CR or
    /// LF is quoted with its double quotes doubled (RFC 4180). <c>null</c> is an empty cell.
    /// </summary>
    public static string Field(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        return value.AsSpan().IndexOfAny(",\"\r\n") >= 0
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;
    }
}
