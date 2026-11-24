using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Href.Core.Api.Common.Web;
using Href.Core.Api.Modules.Audit;
using Microsoft.AspNetCore.Mvc;
using NHibernate.Linq;

namespace Href.Core.Tests.Integration;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AuditLogsExportIntegrationTests(PostgresFixture postgres)
{
    private static readonly DateTime BaseTime = new(2021, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    private static readonly string[] ExpectedColumns =
    [
        "id", "occurredAtUtc", "action", "entityType", "entityId", "actorType", "actorId", "appKey",
        "oldValues", "newValues", "correlationId", "ipAddress", "userAgent",
    ];

    [Fact]
    public async Task Csv_ReturnsBomHeaderAndEscapedRowsOldestFirstAsAnAttachment()
    {
        var correlationId = Unique("corr");
        var newer = AuditLog.Create(
            occurredAtUtc: BaseTime.AddMinutes(1),
            action: "TEST_EXPORT",
            actorType: AuditActorType.User,
            actorId: "user-1",
            appKey: "billing",
            entityType: "Invoice",
            entityId: "inv-1",
            oldValues: """{"note":"a, \"quoted\" value"}""",
            newValues: """{"lines":[1,2]}""",
            correlationId: correlationId,
            ipAddress: "192.0.2.1",
            userAgent: "Agent, with comma");
        var older = Row(BaseTime, correlationId: correlationId);
        await SeedAsync(newer, older);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateAdminClient();

        var response = await client.GetAsync($"/api/audit-logs/export?format=csv&correlationId={correlationId}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType?.CharSet);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Matches(@"^""?audit-logs-\d{8}-\d{6}\.csv""?$", response.Content.Headers.ContentDisposition?.FileName);

        var bytes = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal(Encoding.UTF8.Preamble.ToArray(), bytes[..3]);
        var records = ParseCsv(Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));
        Assert.Equal(ExpectedColumns, records[0]);
        Assert.Equal(3, records.Count);
        Assert.Equal(older.Id.ToString(), records[1][0]);
        var row = records[2];
        Assert.Equal(newer.Id.ToString(), row[0]);
        Assert.Equal(BaseTime.AddMinutes(1), DateTime.Parse(row[1], null, System.Globalization.DateTimeStyles.RoundtripKind));
        Assert.EndsWith("Z", row[1], StringComparison.Ordinal);
        Assert.Equal(["TEST_EXPORT", "Invoice", "inv-1", "User", "user-1", "billing"], row[2..8]);
        Assert.Equal("a, \"quoted\" value", JsonDocument.Parse(row[8]).RootElement.GetProperty("note").GetString());
        Assert.Equal(2, JsonDocument.Parse(row[9]).RootElement.GetProperty("lines").GetArrayLength());
        Assert.Equal([correlationId, "192.0.2.1", "Agent, with comma"], row[10..13]);
        Assert.Equal(string.Empty, records[1][8]);
    }

    [Fact]
    public async Task Csv_PrefixesCellsThatSpreadsheetsWouldRunAsFormulas()
    {
        var correlationId = Unique("corr");
        await SeedAsync(AuditLog.Create(
            occurredAtUtc: BaseTime,
            action: "=HYPERLINK(\"http://evil.example\")",
            actorType: AuditActorType.Application,
            actorId: "+cmd",
            appKey: "@sum",
            entityType: "-2+3",
            entityId: "safe-value",
            correlationId: correlationId,
            userAgent: "=1+1, \"x\""));
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateAdminClient();

        var records = ParseCsv(await GetCsvAsync(client, $"/api/audit-logs/export?format=csv&correlationId={correlationId}"));

        var row = records[1];
        Assert.Equal("'=HYPERLINK(\"http://evil.example\")", row[2]);
        Assert.Equal("'-2+3", row[3]);
        Assert.Equal("safe-value", row[4]);
        Assert.Equal("'+cmd", row[6]);
        Assert.Equal("'@sum", row[7]);
        Assert.Equal("'=1+1, \"x\"", row[12]);
    }

    [Fact]
    public async Task Json_ReturnsAnArrayOldestFirstWithSnapshotsAsJsonValues()
    {
        var correlationId = Unique("corr");
        var newer = AuditLog.Create(
            occurredAtUtc: BaseTime.AddMinutes(1),
            action: "TEST_EXPORT",
            actorType: AuditActorType.System,
            oldValues: """{"status":"Draft"}""",
            newValues: """{"status":"Paid"}""",
            correlationId: correlationId);
        var older = Row(BaseTime, correlationId: correlationId);
        await SeedAsync(newer, older);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateAdminClient();

        var response = await client.GetAsync($"/api/audit-logs/export?format=json&correlationId={correlationId}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Matches(@"^""?audit-logs-\d{8}-\d{6}\.json""?$", response.Content.Headers.ContentDisposition?.FileName);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var items = document.RootElement.EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        Assert.Equal(older.Id, items[0].GetProperty("id").GetGuid());
        Assert.Equal(JsonValueKind.Null, items[0].GetProperty("oldValues").ValueKind);
        Assert.Equal(newer.Id, items[1].GetProperty("id").GetGuid());
        Assert.Equal(BaseTime.AddMinutes(1), items[1].GetProperty("occurredAtUtc").GetDateTime().ToUniversalTime());
        Assert.Equal("System", items[1].GetProperty("actorType").GetString());
        Assert.Equal(JsonValueKind.Null, items[1].GetProperty("actorId").ValueKind);
        Assert.Equal("Draft", items[1].GetProperty("oldValues").GetProperty("status").GetString());
        Assert.Equal("Paid", items[1].GetProperty("newValues").GetProperty("status").GetString());
        Assert.Equal(ExpectedColumns, items[1].EnumerateObject().Select(property => property.Name));
    }

    [Fact]
    public async Task Json_WithNoMatchingRows_ReturnsAnEmptyArray()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateAdminClient();

        var response = await client.GetAsync($"/api/audit-logs/export?format=json&correlationId={Unique("corr")}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("[]", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Export_AppliesTheSameFiltersAsTheList()
    {
        var correlationId = Unique("corr");
        var actorId = Unique("actor");
        var match = Row(BaseTime, correlationId: correlationId, actorType: AuditActorType.System, actorId: actorId);
        await SeedAsync(
            match,
            Row(BaseTime, correlationId: correlationId, actorType: AuditActorType.User, actorId: actorId),
            Row(BaseTime, correlationId: correlationId, actorType: AuditActorType.System, actorId: Unique("actor")),
            Row(BaseTime.AddDays(-3), correlationId: correlationId, actorType: AuditActorType.System, actorId: actorId));
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateAdminClient();

        var records = ParseCsv(await GetCsvAsync(
            client,
            $"/api/audit-logs/export?format=csv&correlationId={correlationId}&actorType=System&actorId={actorId}&from=2021-06-01"));

        Assert.Equal(2, records.Count);
        Assert.Equal(match.Id.ToString(), records[1][0]);
    }

    [Fact]
    public async Task Export_ReadsAcrossChunksWithoutDuplicatesOrGapsWhenTimestampsTie()
    {
        var correlationId = Unique("corr");
        var rows = new[]
        {
            Row(BaseTime, correlationId: correlationId),
            Row(BaseTime, correlationId: correlationId),
            Row(BaseTime, correlationId: correlationId),
            Row(BaseTime.AddSeconds(1), correlationId: correlationId),
            Row(BaseTime.AddSeconds(1), correlationId: correlationId),
        };
        await SeedAsync(rows);
        var expected = rows
            .OrderBy(row => row.OccurredAtUtc)
            .ThenBy(row => row.Id.ToString(), StringComparer.Ordinal)
            .Select(row => row.Id.ToString())
            .ToList();
        await using var factory = new ApiFactory(postgres.ConnectionString, overrides: ExportOptions(chunkSize: 2));
        using var client = factory.CreateAdminClient();

        var csv = ParseCsv(await GetCsvAsync(client, $"/api/audit-logs/export?format=csv&correlationId={correlationId}"));
        var json = await client.GetFromJsonAsync<JsonElement>($"/api/audit-logs/export?format=json&correlationId={correlationId}", TestContext.Current.CancellationToken);

        Assert.Equal(expected, csv.Skip(1).Select(record => record[0]));
        Assert.Equal(expected, json.EnumerateArray().Select(item => item.GetProperty("id").GetString()));
    }

    [Fact]
    public async Task Export_AboveTheRowLimit_Returns400ExportTooLargeAndRecordsNothing()
    {
        var correlationId = Unique("corr");
        await SeedAsync(Enumerable.Range(0, 4).Select(second => Row(BaseTime.AddSeconds(second), correlationId: correlationId)).ToArray());
        await using var factory = new ApiFactory(postgres.ConnectionString, overrides: ExportOptions(maxRows: 3));
        using var client = factory.CreateAdminClient();

        var response = await client.GetAsync($"/api/audit-logs/export?format=csv&correlationId={correlationId}", TestContext.Current.CancellationToken);

        await AssertProblemAsync(response, AuditLogErrors.ExportTooLarge.Code);
        Assert.Empty(await ExportAuditRowsAsync(correlationId));
    }

    [Fact]
    public async Task Export_AtTheRowLimit_ReturnsEveryRow()
    {
        var correlationId = Unique("corr");
        await SeedAsync(Enumerable.Range(0, 3).Select(second => Row(BaseTime.AddSeconds(second), correlationId: correlationId)).ToArray());
        await using var factory = new ApiFactory(postgres.ConnectionString, overrides: ExportOptions(maxRows: 3, chunkSize: 2));
        using var client = factory.CreateAdminClient();

        var records = ParseCsv(await GetCsvAsync(client, $"/api/audit-logs/export?format=csv&correlationId={correlationId}"));

        Assert.Equal(4, records.Count);
    }

    [Fact]
    public async Task Export_RecordsAuditLogExportedWithTheFiltersFormatAndRowCount()
    {
        var correlationId = Unique("corr");
        await SeedAsync(Row(BaseTime, correlationId: correlationId), Row(BaseTime.AddMinutes(1), correlationId: correlationId));
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateAdminClient();

        await GetCsvAsync(client, $"/api/audit-logs/export?format=json&correlationId={correlationId}&actorType=User&from=2021-06-01&to=2021-06-01");

        var auditRow = Assert.Single(await ExportAuditRowsAsync(correlationId));
        Assert.Equal(AuditActorType.User, auditRow.ActorType);
        Assert.Equal(AdminClientExtensions.AdminUserId.ToString(), auditRow.ActorId);
        Assert.Null(auditRow.OldValues);
        using var snapshot = JsonDocument.Parse(auditRow.NewValues!);
        var root = snapshot.RootElement;
        Assert.Equal("json", root.GetProperty("format").GetString());
        Assert.Equal(2, root.GetProperty("rowCount").GetInt32());
        Assert.Equal(correlationId, root.GetProperty("correlationId").GetString());
        Assert.Equal("User", root.GetProperty("actorType").GetString());
        Assert.Equal(new DateTime(2021, 6, 1, 0, 0, 0, DateTimeKind.Utc), root.GetProperty("fromUtc").GetDateTime().ToUniversalTime());
        Assert.Equal(new DateTime(2021, 6, 1, 23, 59, 59, DateTimeKind.Utc).AddTicks(9_999_990), root.GetProperty("toUtc").GetDateTime().ToUniversalTime());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("appKey").ValueKind);
        Assert.True(root.GetProperty("cutoffUtc").GetDateTime() > BaseTime);
    }

    [Fact]
    public async Task Export_ExcludesRowsRecordedAfterItStarted_IncludingItsOwnAuditRow()
    {
        var correlationId = Unique("corr");
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateAdminClient();
        client.DefaultRequestHeaders.Add(CorrelationIdMiddleware.HeaderName, correlationId);

        var first = await client.GetFromJsonAsync<JsonElement>($"/api/audit-logs/export?format=json&correlationId={correlationId}", TestContext.Current.CancellationToken);
        var second = await client.GetFromJsonAsync<JsonElement>($"/api/audit-logs/export?format=json&correlationId={correlationId}", TestContext.Current.CancellationToken);

        Assert.Equal(0, first.GetArrayLength());
        var previousExport = Assert.Single(second.EnumerateArray().ToList());
        Assert.Equal(AuditActions.AuditLogExported, previousExport.GetProperty("action").GetString());
        Assert.Equal(2, (await ExportAuditRowsAsync(correlationId)).Count);
    }

    public static TheoryData<string, string> InvalidQueries => new()
    {
        { "", AuditLogErrors.InvalidFormat.Code },
        { "format=", AuditLogErrors.InvalidFormat.Code },
        { "format=xml", AuditLogErrors.InvalidFormat.Code },
        { "format=CSV", AuditLogErrors.InvalidFormat.Code },
        { "format=csv,json", AuditLogErrors.InvalidFormat.Code },
        { "format=csv&actorType=1", AuditLogErrors.InvalidActorType.Code },
        { "format=json&from=2021-06-02&to=2021-06-01", AuditLogErrors.InvalidRange.Code },
        { "format=json&to=not-a-date", AuditLogErrors.InvalidDate.Code },
        { "format=csv&action=A%0AB", AuditLogErrors.InvalidFilter.Code },
    };

    [Theory]
    [MemberData(nameof(InvalidQueries))]
    public async Task Export_WithInvalidQuery_Returns400WithErrorCode(string query, string expectedCode)
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateAdminClient();

        var response = await client.GetAsync($"/api/audit-logs/export?{query}", TestContext.Current.CancellationToken);

        await AssertProblemAsync(response, expectedCode);
    }

    private static Dictionary<string, string?> ExportOptions(int? maxRows = null, int? chunkSize = null)
    {
        var overrides = new Dictionary<string, string?>();

        if (maxRows is not null)
        {
            overrides[$"{AuditLogExportOptions.SectionName}:MaxRows"] = maxRows.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (chunkSize is not null)
        {
            overrides[$"{AuditLogExportOptions.SectionName}:ChunkSize"] = chunkSize.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return overrides;
    }

    private static AuditLog Row(
        DateTime occurredAtUtc,
        string? correlationId = null,
        AuditActorType actorType = AuditActorType.User,
        string? actorId = null) =>
        AuditLog.Create(occurredAtUtc, "TEST_EXPORT", actorType, actorId: actorId, correlationId: correlationId);

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private async Task SeedAsync(params AuditLog[] rows)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var session = postgres.SessionFactory.OpenSession();
        using var transaction = session.BeginTransaction();

        foreach (var row in rows)
        {
            await session.SaveAsync(row, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<List<AuditLog>> ExportAuditRowsAsync(string filterCorrelationId)
    {
        using var session = postgres.SessionFactory.OpenSession();
        var rows = await session.Query<AuditLog>()
            .Where(auditLog => auditLog.Action == AuditActions.AuditLogExported)
            .ToListAsync(TestContext.Current.CancellationToken);

        return rows
            .Where(row => row.NewValues is not null && JsonDocument.Parse(row.NewValues).RootElement.GetProperty("correlationId").GetString() == filterCorrelationId)
            .ToList();
    }

    private static async Task<string> GetCsvAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var bytes = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        var preamble = Encoding.UTF8.Preamble.ToArray();
        return bytes.AsSpan().StartsWith(preamble)
            ? Encoding.UTF8.GetString(bytes, preamble.Length, bytes.Length - preamble.Length)
            : Encoding.UTF8.GetString(bytes);
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, string expectedCode)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(TestContext.Current.CancellationToken);
        Assert.NotNull(problem);
        Assert.Equal(expectedCode, problem.Extensions["errorCode"]?.ToString());
    }

    /// <summary>
    /// Minimal RFC 4180 reader: CRLF record separators, comma field separators, double-quoted fields with doubled quotes.
    /// </summary>
    private static List<string[]> ParseCsv(string text)
    {
        var records = new List<string[]>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];

            if (inQuotes)
            {
                if (character == '"' && index + 1 < text.Length && text[index + 1] == '"')
                {
                    field.Append('"');
                    index++;
                }
                else if (character == '"')
                {
                    inQuotes = false;
                }
                else
                {
                    field.Append(character);
                }
            }
            else if (character == '"')
            {
                inQuotes = true;
            }
            else if (character == ',')
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else if (character == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
            {
                fields.Add(field.ToString());
                field.Clear();
                records.Add([.. fields]);
                fields.Clear();
                index++;
            }
            else
            {
                field.Append(character);
            }
        }

        Assert.False(inQuotes, "The CSV ends inside a quoted field.");
        Assert.True(field.Length == 0 && fields.Count == 0, "The CSV must end with CRLF.");
        return records;
    }
}
