using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Href.Core.Api.Modules.Audit;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace Href.Core.Tests.Integration;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AuditLogsEndpointIntegrationTests(PostgresFixture postgres)
{
    private static readonly DateTime BaseTime = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task List_ByCorrelationId_ReturnsOnlyItsRowsNewestFirstWithIdAsTieBreaker()
    {
        var correlationId = Unique("corr");
        var oldest = Row(BaseTime.AddMinutes(-10), correlationId: correlationId);
        var tiedA = Row(BaseTime, correlationId: correlationId);
        var tiedB = Row(BaseTime, correlationId: correlationId);
        var other = Row(BaseTime.AddMinutes(5), correlationId: Unique("corr"));
        await SeedAsync(oldest, tiedA, tiedB, other);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateAdminClient();

        var page = await GetPageAsync(client, $"/api/audit-logs?correlationId={correlationId}");

        var tied = new[] { tiedA.Id, tiedB.Id }.OrderByDescending(id => id.ToString(), StringComparer.Ordinal).ToArray();
        Assert.Equal([tied[0], tied[1], oldest.Id], page.Items.Select(item => item.Id));
        Assert.Equal(3, page.TotalCount);
        Assert.Equal(1, page.Page);
        Assert.Equal(AuditLogQueryService.DefaultPageSize, page.PageSize);
    }

    [Fact]
    public async Task List_ReturnsEveryFieldWithSnapshotsAsJsonObjectsAndNoStore()
    {
        var correlationId = Unique("corr");
        var row = AuditLog.Create(
            occurredAtUtc: BaseTime,
            action: "TEST_FIELDS",
            actorType: AuditActorType.User,
            actorId: "user-7",
            appKey: "billing",
            entityType: "Invoice",
            entityId: "inv-1",
            oldValues: """{"status":"Draft"}""",
            newValues: """{"status":"Paid","lines":[1,2]}""",
            correlationId: correlationId,
            ipAddress: "192.0.2.10",
            userAgent: "Satellite/1.0");
        await SeedAsync(row);
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateAdminClient();

        var response = await client.GetAsync($"/api/audit-logs?correlationId={correlationId}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var item = Assert.Single(document.RootElement.GetProperty("items").EnumerateArray().ToList());
        Assert.Equal(row.Id, item.GetProperty("id").GetGuid());
        Assert.Equal(BaseTime, item.GetProperty("occurredAtUtc").GetDateTime().ToUniversalTime());
        Assert.EndsWith("Z", item.GetProperty("occurredAtUtc").GetString(), StringComparison.Ordinal);
        Assert.Equal("TEST_FIELDS", item.GetProperty("action").GetString());
        Assert.Equal("User", item.GetProperty("actorType").GetString());
        Assert.Equal("user-7", item.GetProperty("actorId").GetString());
        Assert.Equal("billing", item.GetProperty("appKey").GetString());
        Assert.Equal("Invoice", item.GetProperty("entityType").GetString());
        Assert.Equal("inv-1", item.GetProperty("entityId").GetString());
        Assert.Equal(correlationId, item.GetProperty("correlationId").GetString());
        Assert.Equal("192.0.2.10", item.GetProperty("ipAddress").GetString());
        Assert.Equal("Satellite/1.0", item.GetProperty("userAgent").GetString());
        Assert.Equal(JsonValueKind.Object, item.GetProperty("oldValues").ValueKind);
        Assert.Equal("Draft", item.GetProperty("oldValues").GetProperty("status").GetString());
        Assert.Equal(2, item.GetProperty("newValues").GetProperty("lines").GetArrayLength());
    }

    [Fact]
    public async Task List_WithoutSnapshots_ReturnsNullValues()
    {
        var correlationId = Unique("corr");
        await SeedAsync(Row(BaseTime, correlationId: correlationId));
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateAdminClient();

        var response = await client.GetAsync($"/api/audit-logs?correlationId={correlationId}", TestContext.Current.CancellationToken);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var item = document.RootElement.GetProperty("items")[0];
        Assert.Equal(JsonValueKind.Null, item.GetProperty("oldValues").ValueKind);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("newValues").ValueKind);
    }

    [Fact]
    public async Task List_ByAppKey_ReturnsOnlyThatApplication()
    {
        var appKey = Unique("app");
        var match = Row(BaseTime, appKey: appKey);
        await SeedAsync(match, Row(BaseTime, appKey: Unique("app")), Row(BaseTime, appKey: null));

        var page = await GetAdminPageAsync($"/api/audit-logs?appKey={appKey}");

        Assert.Equal(match.Id, Assert.Single(page.Items).Id);
    }

    [Fact]
    public async Task List_ByActorTypeAndActorId_ReturnsOnlyThatActor()
    {
        var actorId = Unique("actor");
        var asUser = Row(BaseTime, actorType: AuditActorType.User, actorId: actorId);
        var asApplication = Row(BaseTime, actorType: AuditActorType.Application, actorId: actorId);
        await SeedAsync(asUser, asApplication, Row(BaseTime, actorType: AuditActorType.User, actorId: Unique("actor")));

        var byActor = await GetAdminPageAsync($"/api/audit-logs?actorId={actorId}");
        var byActorAndType = await GetAdminPageAsync($"/api/audit-logs?actorId={actorId}&actorType=Application");

        Assert.Equal(2, byActor.TotalCount);
        Assert.Equal(asApplication.Id, Assert.Single(byActorAndType.Items).Id);
    }

    [Fact]
    public async Task List_ByActorType_ReturnsOnlyThatType()
    {
        var correlationId = Unique("corr");
        var system = Row(BaseTime, actorType: AuditActorType.System, correlationId: correlationId);
        await SeedAsync(system, Row(BaseTime, actorType: AuditActorType.Anonymous, correlationId: correlationId));

        var page = await GetAdminPageAsync($"/api/audit-logs?correlationId={correlationId}&actorType=System");

        Assert.Equal(system.Id, Assert.Single(page.Items).Id);
    }

    [Fact]
    public async Task List_ByAction_ReturnsOnlyThatActionMatchedExactly()
    {
        var action = Unique("TEST_ACTION").ToUpperInvariant();
        var match = Row(BaseTime, action: action);
        await SeedAsync(match, Row(BaseTime, action: Unique("TEST_ACTION").ToUpperInvariant()));

        var exact = await GetAdminPageAsync($"/api/audit-logs?action={action}");
        var otherCase = await GetAdminPageAsync($"/api/audit-logs?action={action.ToLowerInvariant()}");

        Assert.Equal(match.Id, Assert.Single(exact.Items).Id);
        Assert.Empty(otherCase.Items);
    }

    [Fact]
    public async Task List_ByEntityTypeAndEntityId_ReturnsOnlyThatEntity()
    {
        var entityType = Unique("Entity");
        var entityId = Unique("id");
        var match = Row(BaseTime, entityType: entityType, entityId: entityId);
        var sameTypeOtherId = Row(BaseTime, entityType: entityType, entityId: Unique("id"));
        var otherTypeSameId = Row(BaseTime, entityType: Unique("Entity"), entityId: entityId);
        await SeedAsync(match, sameTypeOtherId, otherTypeSameId);

        var byType = await GetAdminPageAsync($"/api/audit-logs?entityType={entityType}");
        var byId = await GetAdminPageAsync($"/api/audit-logs?entityId={entityId}");
        var byBoth = await GetAdminPageAsync($"/api/audit-logs?entityType={entityType}&entityId={entityId}");

        Assert.Equal(2, byType.TotalCount);
        Assert.Equal(2, byId.TotalCount);
        Assert.Equal(match.Id, Assert.Single(byBoth.Items).Id);
    }

    [Fact]
    public async Task List_ByDateRange_IsInclusiveAndAcceptsUtcOffsetsAndAssumesUtcWithoutOffset()
    {
        var correlationId = Unique("corr");
        var day = new DateTime(1990, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        var at10 = Row(day.AddHours(10), correlationId: correlationId);
        var at11 = Row(day.AddHours(11), correlationId: correlationId);
        var at12 = Row(day.AddHours(12), correlationId: correlationId);
        var at13 = Row(day.AddHours(13), correlationId: correlationId);
        await SeedAsync(at10, at11, at12, at13);

        var withZulu = await GetAdminPageAsync("/api/audit-logs?from=1990-05-01T11:00:00Z&to=1990-05-01T12:00:00Z");
        var withOffset = await GetAdminPageAsync("/api/audit-logs?from=1990-05-01T14:00:00%2B03:00&to=1990-05-01T12:00:00.0000000Z");
        var withoutOffset = await GetAdminPageAsync("/api/audit-logs?from=1990-05-01T11:00&to=1990-05-01T12:00:00");
        var onlyFrom = await GetAdminPageAsync($"/api/audit-logs?correlationId={correlationId}&from=1990-05-01T12:00:00Z");
        var onlyTo = await GetAdminPageAsync($"/api/audit-logs?correlationId={correlationId}&to=1990-05-01T11:00:00Z");
        var sameInstant = await GetAdminPageAsync("/api/audit-logs?from=1990-05-01T13:00:00Z&to=1990-05-01T13:00:00Z");

        Assert.Equal([at12.Id, at11.Id], withZulu.Items.Select(item => item.Id));
        Assert.Equal([at12.Id, at11.Id], withOffset.Items.Select(item => item.Id));
        Assert.Equal([at12.Id, at11.Id], withoutOffset.Items.Select(item => item.Id));
        Assert.Equal([at13.Id, at12.Id], onlyFrom.Items.Select(item => item.Id));
        Assert.Equal([at11.Id, at10.Id], onlyTo.Items.Select(item => item.Id));
        Assert.Equal(at13.Id, Assert.Single(sameInstant.Items).Id);
    }

    [Fact]
    public async Task List_WithCombinedFilters_AppliesThemAll()
    {
        var appKey = Unique("app");
        var actorId = Unique("actor");
        var match = Row(BaseTime, appKey: appKey, actorId: actorId, action: "TEST_COMBINED");
        var otherAction = Row(BaseTime, appKey: appKey, actorId: actorId, action: "TEST_OTHER");
        var otherActor = Row(BaseTime, appKey: appKey, actorId: Unique("actor"), action: "TEST_COMBINED");
        var tooOld = Row(BaseTime.AddDays(-2), appKey: appKey, actorId: actorId, action: "TEST_COMBINED");
        await SeedAsync(match, otherAction, otherActor, tooOld);

        var page = await GetAdminPageAsync(
            $"/api/audit-logs?appKey={appKey}&actorType=User&actorId={actorId}&action=TEST_COMBINED&from=2026-11-19T00:00:00Z&to=2026-11-21T00:00:00Z");

        Assert.Equal(match.Id, Assert.Single(page.Items).Id);
        Assert.Equal(1, page.TotalCount);
    }

    [Fact]
    public async Task List_WithBlankFilters_IgnoresThem()
    {
        var correlationId = Unique("corr");
        await SeedAsync(Row(BaseTime, correlationId: correlationId), Row(BaseTime, correlationId: correlationId));

        var page = await GetAdminPageAsync($"/api/audit-logs?correlationId=%20{correlationId}%20&appKey=&actorType=%20&action=&from=&to=");

        Assert.Equal(2, page.TotalCount);
    }

    [Fact]
    public async Task List_PagesThroughResultsAndReturnsEmptyPagesPastTheEnd()
    {
        var correlationId = Unique("corr");
        var rows = Enumerable.Range(0, 5).Select(minute => Row(BaseTime.AddMinutes(minute), correlationId: correlationId)).ToArray();
        await SeedAsync(rows);
        var expected = rows.OrderByDescending(row => row.OccurredAtUtc).Select(row => row.Id).ToList();

        var pages = new List<AuditLogPageResponse>();
        for (var page = 1; page <= 4; page++)
        {
            pages.Add(await GetAdminPageAsync($"/api/audit-logs?correlationId={correlationId}&page={page}&pageSize=2"));
        }

        Assert.Equal([2, 2, 1, 0], pages.Select(page => page.Items.Count));
        Assert.All(pages, page => Assert.Equal(5, page.TotalCount));
        Assert.All(pages, page => Assert.Equal(2, page.PageSize));
        Assert.Equal(expected, pages.SelectMany(page => page.Items).Select(item => item.Id));
    }

    [Fact]
    public async Task List_AcceptsTheMaximumPageSize()
    {
        var page = await GetAdminPageAsync($"/api/audit-logs?correlationId={Unique("corr")}&pageSize={AuditLogQueryService.MaxPageSize}");

        Assert.Equal(AuditLogQueryService.MaxPageSize, page.PageSize);
        Assert.Empty(page.Items);
    }

    public static TheoryData<string, string> InvalidQueries => new()
    {
        { "page=0", AuditLogErrors.InvalidPage.Code },
        { "page=-1", AuditLogErrors.InvalidPage.Code },
        { "pageSize=0", AuditLogErrors.InvalidPageSize.Code },
        { "pageSize=201", AuditLogErrors.InvalidPageSize.Code },
        { "from=2026-11-02T00:00:00Z&to=2026-11-01T23:59:59Z", AuditLogErrors.InvalidRange.Code },
        { "from=not-a-date", AuditLogErrors.InvalidDate.Code },
        { "to=11/23/2026", AuditLogErrors.InvalidDate.Code },
        { "from=2026-13-01", AuditLogErrors.InvalidDate.Code },
        { "from=2026-11-01T10:00:00 03:00", AuditLogErrors.InvalidDate.Code },
        { "actorType=1", AuditLogErrors.InvalidActorType.Code },
        { "actorType=User,System", AuditLogErrors.InvalidActorType.Code },
        { "actorType=user", AuditLogErrors.InvalidActorType.Code },
        { "actorType=Robot", AuditLogErrors.InvalidActorType.Code },
        { $"appKey={new string('a', AuditLog.AppKeyMaxLength + 1)}", AuditLogErrors.InvalidFilter.Code },
        { $"actorId={new string('a', AuditLog.ActorIdMaxLength + 1)}", AuditLogErrors.InvalidFilter.Code },
        { $"action={new string('A', AuditLog.ActionMaxLength + 1)}", AuditLogErrors.InvalidFilter.Code },
        { $"entityType={new string('a', AuditLog.EntityTypeMaxLength + 1)}", AuditLogErrors.InvalidFilter.Code },
        { $"entityId={new string('a', AuditLog.EntityIdMaxLength + 1)}", AuditLogErrors.InvalidFilter.Code },
        { $"correlationId={new string('a', AuditLog.CorrelationIdMaxLength + 1)}", AuditLogErrors.InvalidFilter.Code },
        { "action=A%0AB", AuditLogErrors.InvalidFilter.Code },
        { "appKey=a%00b", AuditLogErrors.InvalidFilter.Code },
    };

    [Theory]
    [MemberData(nameof(InvalidQueries))]
    public async Task List_WithInvalidQuery_Returns400WithErrorCode(string query, string expectedCode)
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateAdminClient();

        var response = await client.GetAsync($"/api/audit-logs?{query}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(TestContext.Current.CancellationToken);
        Assert.NotNull(problem);
        Assert.Equal(expectedCode, problem.Extensions["errorCode"]?.ToString());
    }

    [Fact]
    public async Task List_WithAFilterAtItsMaximumLength_IsAccepted()
    {
        var page = await GetAdminPageAsync($"/api/audit-logs?correlationId={new string('c', AuditLog.CorrelationIdMaxLength)}");

        Assert.Empty(page.Items);
    }

    public static TheoryData<string, string> IndexedQueries => new()
    {
        {
            "SELECT id FROM audit_logs WHERE occurred_at_utc >= now() - interval '1 day' ORDER BY occurred_at_utc DESC, id DESC LIMIT 50",
            "ix_audit_logs_occurred_at_utc_id"
        },
        {
            "SELECT id FROM audit_logs WHERE app_key = 'billing' AND occurred_at_utc >= now() - interval '1 day' ORDER BY occurred_at_utc DESC, id DESC LIMIT 50",
            "ix_audit_logs_app_key_occurred_at_utc_id"
        },
        {
            "SELECT id FROM audit_logs WHERE actor_type = 'User' AND actor_id = 'user-1' ORDER BY occurred_at_utc DESC, id DESC LIMIT 50",
            "ix_audit_logs_actor_occurred_at_utc_id"
        },
        {
            "SELECT id FROM audit_logs WHERE correlation_id = 'corr-1' ORDER BY occurred_at_utc DESC, id DESC LIMIT 50",
            "ix_audit_logs_correlation_id"
        },
        {
            "SELECT id FROM audit_logs WHERE entity_type = 'User' AND entity_id = 'u-1' ORDER BY occurred_at_utc DESC, id DESC LIMIT 50",
            "ix_audit_logs_entity"
        },
    };

    [Theory]
    [MemberData(nameof(IndexedQueries))]
    public async Task CommonQueries_UseTheirIndex(string sql, string expectedIndex)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var disable = new NpgsqlCommand("SET LOCAL enable_seqscan = off", connection, transaction))
        {
            await disable.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var explain = new NpgsqlCommand($"EXPLAIN {sql}", connection, transaction);
        var plan = new List<string>();
        await using (var reader = await explain.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                plan.Add(reader.GetString(0));
            }
        }

        await transaction.RollbackAsync(cancellationToken);
        Assert.Contains(plan, line => line.Contains(expectedIndex, StringComparison.Ordinal));
    }

    [Fact]
    public async Task DateAppKeyAndActorQueries_ReadRowsInIndexOrderWithoutSorting()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var disable = new NpgsqlCommand("SET LOCAL enable_seqscan = off; SET LOCAL enable_sort = off", connection, transaction))
        {
            await disable.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var (sql, expectedIndex) in new[]
        {
            ("SELECT id FROM audit_logs ORDER BY occurred_at_utc DESC, id DESC LIMIT 50", "ix_audit_logs_occurred_at_utc_id"),
            ("SELECT id FROM audit_logs WHERE app_key = 'billing' ORDER BY occurred_at_utc DESC, id DESC LIMIT 50", "ix_audit_logs_app_key_occurred_at_utc_id"),
            ("SELECT id FROM audit_logs WHERE actor_type = 'User' AND actor_id = 'user-1' ORDER BY occurred_at_utc DESC, id DESC LIMIT 50", "ix_audit_logs_actor_occurred_at_utc_id"),
        })
        {
            await using var explain = new NpgsqlCommand($"EXPLAIN {sql}", connection, transaction);
            var plan = new List<string>();
            await using (var reader = await explain.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    plan.Add(reader.GetString(0));
                }
            }

            Assert.DoesNotContain(plan, line => line.Contains("Sort", StringComparison.Ordinal));
            Assert.Contains(plan, line => line.Contains($"Scan using {expectedIndex} ", StringComparison.Ordinal));
        }

        await transaction.RollbackAsync(cancellationToken);
    }

    private static AuditLog Row(
        DateTime occurredAtUtc,
        string action = "TEST_ACTION",
        AuditActorType actorType = AuditActorType.User,
        string? actorId = null,
        string? appKey = null,
        string? entityType = null,
        string? entityId = null,
        string? correlationId = null) =>
        AuditLog.Create(
            occurredAtUtc,
            action,
            actorType,
            actorId: actorId,
            appKey: appKey,
            entityType: entityType,
            entityId: entityId,
            correlationId: correlationId);

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

    private async Task<AuditLogPageResponse> GetAdminPageAsync(string url)
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateAdminClient();
        return await GetPageAsync(client, url);
    }

    private static async Task<AuditLogPageResponse> GetPageAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<AuditLogPageResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(page);
        return page;
    }
}
