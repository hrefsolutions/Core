using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Href.Core.Api.Common.Web;
using Href.Core.Api.Modules.Applications;
using Href.Core.Api.Modules.Audit;
using NHibernate.Linq;

namespace Href.Core.Tests.Integration;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ApplicationAuditIntegrationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task ApplicationWrites_RecordOneAuditRowEachWithActorCorrelationAndSnapshots()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateAdminClient();
        var appKey = $"aud-{Guid.NewGuid():N}"[..20];

        var created = await SendAsync(client, "corr-app-create", HttpMethod.Post, "/api/apps", new CreateApplicationRequest(appKey, "Audit App", "First", false), cancellationToken);
        var createdBody = await created.Content.ReadFromJsonAsync<ApplicationCreatedResponse>(cancellationToken);
        var updated = await SendAsync(client, "corr-app-update", HttpMethod.Put, $"/api/apps/{appKey}", new UpdateApplicationRequest("Audit App 2", "Second", true), cancellationToken);
        var unchanged = await SendAsync(client, "corr-app-noop", HttpMethod.Put, $"/api/apps/{appKey}", new UpdateApplicationRequest("  Audit App 2 ", "Second", true), cancellationToken);
        var invalid = await SendAsync(client, "corr-app-invalid", HttpMethod.Put, $"/api/apps/{appKey}", new UpdateApplicationRequest(" ", "Second", true), cancellationToken);
        var suspended = await SendAsync(client, "corr-app-suspend", HttpMethod.Patch, $"/api/apps/{appKey}/status", new UpdateApplicationStatusRequest("Suspended"), cancellationToken);
        var suspendedAgain = await SendAsync(client, "corr-app-suspend-again", HttpMethod.Patch, $"/api/apps/{appKey}/status", new UpdateApplicationStatusRequest("Suspended"), cancellationToken);
        var rotated = await SendAsync(client, "corr-app-rotate", HttpMethod.Post, $"/api/apps/{appKey}/secret/rotate", null, cancellationToken);
        var rotatedBody = await rotated.Content.ReadFromJsonAsync<RotateClientSecretResponse>(cancellationToken);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal(HttpStatusCode.OK, unchanged.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.OK, suspended.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, suspendedAgain.StatusCode);
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        Assert.NotNull(createdBody);
        Assert.NotNull(rotatedBody);

        using var session = postgres.SessionFactory.OpenSession();
        var rows = await session.Query<AuditLog>()
            .Where(row => row.EntityType == ApplicationAuditing.EntityType && row.EntityId == createdBody.Id.ToString())
            .OrderBy(row => row.OccurredAtUtc)
            .ToListAsync(cancellationToken);

        Assert.Equal(
            [
                $"{AuditActions.ApplicationCreated}|corr-app-create",
                $"{AuditActions.ApplicationUpdated}|corr-app-update",
                $"{AuditActions.ApplicationStatusChanged}|corr-app-suspend",
                $"{AuditActions.ApplicationSecretRotated}|corr-app-rotate",
            ],
            rows.Select(row => $"{row.Action}|{row.CorrelationId}"));

        Assert.All(rows, row =>
        {
            Assert.Equal(AuditActorType.User, row.ActorType);
            Assert.Equal(AdminClientExtensions.AdminUserId.ToString(), row.ActorId);
            Assert.Equal(appKey, row.AppKey);
            var text = (row.OldValues ?? string.Empty) + (row.NewValues ?? string.Empty);
            Assert.DoesNotContain(createdBody.ClientSecret, text, StringComparison.Ordinal);
            Assert.DoesNotContain(rotatedBody.ClientSecret, text, StringComparison.Ordinal);
            Assert.DoesNotContain("hash", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret\"", text, StringComparison.OrdinalIgnoreCase);
        });

        Assert.Null(rows[0].OldValues);
        Assert.Equal("First", Property(rows[0].NewValues, "description"));
        Assert.Equal("Audit App", Property(rows[1].OldValues, "name"));
        Assert.Equal("Audit App 2", Property(rows[1].NewValues, "name"));
        Assert.Equal("Active", Property(rows[2].OldValues, "status"));
        Assert.Equal("Suspended", Property(rows[2].NewValues, "status"));
        Assert.NotEqual(Property(rows[3].OldValues, "clientSecretRotatedAtUtc"), Property(rows[3].NewValues, "clientSecretRotatedAtUtc"));
    }

    [Fact]
    public async Task ConcurrentUpdates_RecordAChainWhereEachBeforeIsThePreviousAfter()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateAdminClient();
        var appKey = $"race-{Guid.NewGuid():N}"[..20];
        var created = await client.PostAsJsonAsync("/api/apps", new CreateApplicationRequest(appKey, "Race 0", null, false), cancellationToken);
        var createdBody = await created.Content.ReadFromJsonAsync<ApplicationCreatedResponse>(cancellationToken);
        Assert.NotNull(createdBody);

        var updates = await Task.WhenAll(Enumerable.Range(1, 8).Select(index =>
            client.PutAsJsonAsync($"/api/apps/{appKey}", new UpdateApplicationRequest($"Race {index}", null, false), cancellationToken)));

        Assert.All(updates, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        using var session = postgres.SessionFactory.OpenSession();
        var rows = await session.Query<AuditLog>()
            .Where(row => row.EntityId == createdBody.Id.ToString() && row.Action == AuditActions.ApplicationUpdated)
            .OrderBy(row => row.OccurredAtUtc)
            .ToListAsync(cancellationToken);

        Assert.Equal(8, rows.Count);
        var previous = "Race 0";

        foreach (var row in rows)
        {
            Assert.Equal(previous, Property(row.OldValues, "name"));
            previous = Property(row.NewValues, "name")!;
        }

        var persisted = await session.Query<Application>().SingleAsync(application => application.AppKey == appKey, cancellationToken);
        Assert.Equal(previous, persisted.Name);
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        string correlationId,
        HttpMethod method,
        string uri,
        object? body,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Add(CorrelationIdMiddleware.HeaderName, correlationId);

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType());
        }

        return await client.SendAsync(request, cancellationToken);
    }

    private static string? Property(string? json, string name)
    {
        Assert.NotNull(json);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty(name).ToString();
    }
}
