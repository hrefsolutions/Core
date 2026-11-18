using System.Net;
using System.Net.Http.Json;
using Href.Core.Api.Modules.Applications;
using Microsoft.AspNetCore.Mvc;
using NHibernate.Linq;

namespace Href.Core.Tests.Integration;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ApplicationsEndpointIntegrationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task CreateAndGetByAppKey_Returns201WithOneTimeSecretAnd200WithoutSecretHash()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();
        var appKey = $"crm-{Guid.NewGuid():N}"[..20];

        var createResponse = await client.PostAsJsonAsync(
            "/api/apps",
            new CreateApplicationRequest(appKey, "CRM Portal", "Main CRM", true),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        Assert.EndsWith($"/api/apps/{appKey}", createResponse.Headers.Location?.ToString(), StringComparison.OrdinalIgnoreCase);

        var createJson = await createResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("clientSecretHash", createJson, StringComparison.OrdinalIgnoreCase);

        var created = await createResponse.Content.ReadFromJsonAsync<ApplicationCreatedResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(created);
        Assert.Equal(appKey, created.AppKey);
        Assert.Equal("CRM Portal", created.Name);
        Assert.Equal("Main CRM", created.Description);
        Assert.Equal("Active", created.Status);
        Assert.True(created.AllowSelfRegistration);
        Assert.Equal(43, created.ClientSecret.Length);

        var getResponse = await client.GetAsync($"/api/apps/{appKey}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var getJson = await getResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("\"clientSecret\":", getJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"clientSecretHash\":", getJson, StringComparison.OrdinalIgnoreCase);

        var fetched = await getResponse.Content.ReadFromJsonAsync<ApplicationResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(fetched);
        Assert.Equal(created.Id, fetched.Id);
        Assert.Equal(appKey, fetched.AppKey);
    }

    [Fact]
    public async Task Create_WhenAppKeyAlreadyExists_Returns409ConflictProblemDetails()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();
        var appKey = $"dup-{Guid.NewGuid():N}"[..20];

        var first = await client.PostAsJsonAsync(
            "/api/apps",
            new CreateApplicationRequest(appKey, "First App", null, false),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var duplicate = await client.PostAsJsonAsync(
            "/api/apps",
            new CreateApplicationRequest(appKey, "Second App", null, false),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        var problem = await duplicate.Content.ReadFromJsonAsync<ProblemDetails>(TestContext.Current.CancellationToken);
        Assert.NotNull(problem);
        Assert.Equal(ApplicationErrors.AppKeyTaken.Code, problem.Extensions["errorCode"]?.ToString());
    }

    [Fact]
    public async Task Create_WhenAppKeyHasInvalidSlugFormat_Returns400BadRequest()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/apps",
            new CreateApplicationRequest("Invalid_Slug", "Valid Name", null, false),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(TestContext.Current.CancellationToken);
        Assert.NotNull(problem);
        Assert.Equal(ApplicationErrors.InvalidAppKey.Code, problem.Extensions["errorCode"]?.ToString());
    }

    [Fact]
    public async Task List_ReturnsApplicationsOrderedByName()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();
        var prefix = $"ord-{Guid.NewGuid():N}"[..10];
        var zuluKey = $"{prefix}-zulu";
        var alphaKey = $"{prefix}-alpha";

        await client.PostAsJsonAsync(
            "/api/apps",
            new CreateApplicationRequest(zuluKey, $"{prefix} Zulu", null, false),
            TestContext.Current.CancellationToken);
        await client.PostAsJsonAsync(
            "/api/apps",
            new CreateApplicationRequest(alphaKey, $"{prefix} Alpha", null, false),
            TestContext.Current.CancellationToken);

        var list = await client.GetFromJsonAsync<List<ApplicationResponse>>("/api/apps", TestContext.Current.CancellationToken);

        Assert.NotNull(list);
        var filtered = list.Where(item => item.AppKey == zuluKey || item.AppKey == alphaKey).ToList();
        Assert.Equal(2, filtered.Count);
        Assert.Equal(alphaKey, filtered[0].AppKey);
        Assert.Equal(zuluKey, filtered[1].AppKey);
    }

    [Fact]
    public async Task UpdateAndStatusAndRotateSecret_ModifiesEntityAndInvalidatesOldSecret()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();
        var appKey = $"flow-{Guid.NewGuid():N}"[..20];

        var createResponse = await client.PostAsJsonAsync(
            "/api/apps",
            new CreateApplicationRequest(appKey, "Initial Name", "Initial Description", false),
            TestContext.Current.CancellationToken);
        var created = await createResponse.Content.ReadFromJsonAsync<ApplicationCreatedResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(created);

        var updateResponse = await client.PutAsJsonAsync(
            $"/api/apps/{appKey}",
            new UpdateApplicationRequest("Updated Name", "Updated Description", true),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var updated = await updateResponse.Content.ReadFromJsonAsync<ApplicationResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(updated);
        Assert.Equal("Updated Name", updated.Name);
        Assert.Equal("Updated Description", updated.Description);
        Assert.True(updated.AllowSelfRegistration);

        var suspendResponse = await client.PatchAsJsonAsync(
            $"/api/apps/{appKey}/status",
            new UpdateApplicationStatusRequest("Suspended"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, suspendResponse.StatusCode);
        var suspended = await suspendResponse.Content.ReadFromJsonAsync<ApplicationResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(suspended);
        Assert.Equal("Suspended", suspended.Status);

        var invalidStatusResponse = await client.PatchAsJsonAsync(
            $"/api/apps/{appKey}/status",
            new UpdateApplicationStatusRequest("BrokenStatus"),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, invalidStatusResponse.StatusCode);

        var rotateResponse = await client.PostAsync(
            $"/api/apps/{appKey}/secret/rotate",
            content: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, rotateResponse.StatusCode);
        var rotated = await rotateResponse.Content.ReadFromJsonAsync<RotateClientSecretResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(rotated);
        Assert.Equal(43, rotated.ClientSecret.Length);
        Assert.NotEqual(created.ClientSecret, rotated.ClientSecret);

        using var session = postgres.SessionFactory.OpenSession();
        var persisted = await session.Query<Application>()
            .FirstAsync(application => application.AppKey == appKey, TestContext.Current.CancellationToken);

        Assert.False(persisted.VerifySecret(created.ClientSecret));
        Assert.True(persisted.VerifySecret(rotated.ClientSecret));
    }

    [Fact]
    public async Task Endpoints_WhenAppKeyDoesNotExist_Return404NotFound()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();
        var missingKey = $"missing-{Guid.NewGuid():N}"[..20];

        var getResponse = await client.GetAsync($"/api/apps/{missingKey}", TestContext.Current.CancellationToken);
        var putResponse = await client.PutAsJsonAsync(
            $"/api/apps/{missingKey}",
            new UpdateApplicationRequest("Name", null, false),
            TestContext.Current.CancellationToken);
        var patchResponse = await client.PatchAsJsonAsync(
            $"/api/apps/{missingKey}/status",
            new UpdateApplicationStatusRequest("Suspended"),
            TestContext.Current.CancellationToken);
        var rotateResponse = await client.PostAsync(
            $"/api/apps/{missingKey}/secret/rotate",
            content: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, putResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, patchResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, rotateResponse.StatusCode);
    }
}
