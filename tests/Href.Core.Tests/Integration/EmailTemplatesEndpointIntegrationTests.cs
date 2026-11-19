using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Href.Core.Api.Modules.Applications;
using Href.Core.Api.Modules.Audit;
using Href.Core.Api.Modules.Notifications;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using NHibernate.Linq;

namespace Href.Core.Tests.Integration;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed partial class EmailTemplatesEndpointIntegrationTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string BaseUrl = "/api/notifications/templates";
    private const string SeedResourceSuffix = "0004_seed_default_email_templates.sql";

    private ApiFactory _factory = null!;
    private HttpClient _client = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new ApiFactory(postgres.ConnectionString);
        _client = _factory.CreateClient();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    public static TheoryData<string, string> InvalidCreateBodies() =>
        new TheoryData<string, string>
        {
            { JsonSerializer.Serialize(Body("Bad_Key")), "Templates.InvalidKey" },
            { JsonSerializer.Serialize(Body("ab")), "Templates.InvalidKey" },
            { JsonSerializer.Serialize(Body("UpperCase-key")), "Templates.InvalidKey" },
            { JsonSerializer.Serialize(Body("t-valid-key", name: "   ")), "Templates.InvalidName" },
            { JsonSerializer.Serialize(Body("t-valid-key", subject: "   ")), "Templates.InvalidSubject" },
            { JsonSerializer.Serialize(Body("t-valid-key", subject: "Line one\nLine two")), "Templates.InvalidSubject" },
            { JsonSerializer.Serialize(Body("t-valid-key", html: "   ")), "Templates.InvalidHtmlBody" },
            { JsonSerializer.Serialize(Body("t-valid-key", html: "<p>a\0b</p>")), "Templates.InvalidHtmlBody" },
            { JsonSerializer.Serialize(Body("t-valid-key", text: "a\0b")), "Templates.InvalidTextBody" },
            { """{"name":"Only a name"}""", "Templates.InvalidKey" },
        };

    private static string UniqueKey() => $"t-{Guid.NewGuid():N}"[..14];

    private static object Body(
        string key,
        string name = "Custom template",
        string? description = "A test template",
        string subject = "Hello {{Name}}",
        string html = "<p>Hello {{Name}}</p>",
        string? text = "Hello {{Name}}") =>
        new { key, name, description, subject, htmlBody = html, textBody = text };

    private static object UpdateBody(
        string name = "Renamed template",
        string? description = "Changed description",
        string subject = "Bye {{Name}}",
        string html = "<p>Bye {{Name}}</p>",
        string? text = "Bye {{Name}}") =>
        new { name, description, subject, htmlBody = html, textBody = text };

    private static async Task<ProblemDetails> ReadProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(TestContext.Current.CancellationToken);
        Assert.NotNull(problem);
        return problem;
    }

    private static async Task<ProblemDetails> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string errorCode)
    {
        Assert.Equal(status, response.StatusCode);
        var problem = await ReadProblemAsync(response);
        Assert.Equal(errorCode, problem.Extensions["errorCode"]?.ToString());
        return problem;
    }

    private static async Task<EmailTemplateResponse> ReadTemplateAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        Assert.Equal(expected, response.StatusCode);
        var template = await response.Content.ReadFromJsonAsync<EmailTemplateResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(template);
        return template;
    }

    private Task<HttpResponseMessage> PostAsync(string url, object body) =>
        _client.PostAsJsonAsync(url, body, TestContext.Current.CancellationToken);

    private Task<HttpResponseMessage> PostRawAsync(string url, string json) =>
        _client.PostAsync(url, new StringContent(json, System.Text.Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);

    private Task<HttpResponseMessage> PutAsync(string key, object body) =>
        _client.PutAsJsonAsync($"{BaseUrl}/{key}", body, TestContext.Current.CancellationToken);

    private Task<HttpResponseMessage> GetAsync(string url) =>
        _client.GetAsync(url, TestContext.Current.CancellationToken);

    private Task<HttpResponseMessage> DeleteAsync(string key) =>
        _client.DeleteAsync($"{BaseUrl}/{key}", TestContext.Current.CancellationToken);

    private async Task<EmailTemplateResponse> CreateAsync(string? key = null, object? body = null)
    {
        key ??= UniqueKey();
        var response = await PostAsync(BaseUrl, body ?? Body(key));
        var created = await ReadTemplateAsync(response, HttpStatusCode.Created);
        return await GetTemplateAsync(created.Key);
    }

    private async Task<EmailTemplateResponse> GetTemplateAsync(string key) =>
        await ReadTemplateAsync(await GetAsync($"{BaseUrl}/{key}"));

    private async Task<List<AuditLog>> AuditRowsAsync(Guid templateId)
    {
        using var session = postgres.SessionFactory.OpenSession();
        var entityId = templateId.ToString();
        var rows = await session.Query<AuditLog>()
            .Where(log => log.EntityId == entityId)
            .ToListAsync(TestContext.Current.CancellationToken);

        return rows.OrderBy(log => log.OccurredAtUtc).ToList();
    }

    private static JsonElement Parse(string? json)
    {
        Assert.NotNull(json);
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static string ReadSeedScript()
    {
        var assembly = typeof(EmailTemplate).Assembly;
        var resourceName = assembly.GetManifestResourceNames().Single(name => name.EndsWith(SeedResourceSuffix, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [GeneratedRegex(@"gen_random_uuid\(\),\s*'([a-z0-9-]+)'")]
    private static partial Regex SeededKeyRegex();

    [Fact]
    public async Task List_ReturnsSeededAndCustomTemplatesOrderedByKeyWithoutBodies()
    {
        var custom = await CreateAsync();

        var response = await GetAsync(BaseUrl);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var items = json.EnumerateArray().ToList();
        var keys = items.Select(item => item.GetProperty("key").GetString()!).ToList();
        Assert.Equal(keys.Order(StringComparer.Ordinal), keys);
        foreach (var systemKey in SystemTemplateRequirements.Keys)
        {
            var item = items.Single(candidate => candidate.GetProperty("key").GetString() == systemKey);
            Assert.True(item.GetProperty("isSystem").GetBoolean());
        }

        var customItem = items.Single(candidate => candidate.GetProperty("key").GetString() == custom.Key);
        Assert.False(customItem.GetProperty("isSystem").GetBoolean());
        Assert.Equal("Custom template", customItem.GetProperty("name").GetString());
        Assert.Equal(custom.UpdatedAtUtc, customItem.GetProperty("updatedAtUtc").GetDateTime());
        Assert.Equal(
            ["isSystem", "key", "name", "updatedAtUtc"],
            customItem.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task List_WithoutCredentials_IsServedByTheTemplatesControllerNotTheNotificationsOne()
    {
        var templates = await GetAsync(BaseUrl);
        var notificationById = await GetAsync($"/api/notifications/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.OK, templates.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, notificationById.StatusCode);
    }

    [Fact]
    public async Task GetByKey_ReturnsFullTemplateWithDistinctPlaceholdersInOrderOfAppearance()
    {
        var key = UniqueKey();
        await CreateAsync(key, Body(
            key,
            subject: "Hi {{Name}}",
            html: "<p>{{ company }} {{name}} {{Link}}</p>",
            text: "{{Extra}} {{link}}"));

        var template = await GetTemplateAsync(key);

        Assert.NotEqual(Guid.Empty, template.Id);
        Assert.Equal(key, template.Key);
        Assert.Equal("Custom template", template.Name);
        Assert.Equal("A test template", template.Description);
        Assert.Equal("Hi {{Name}}", template.Subject);
        Assert.Equal("<p>{{ company }} {{name}} {{Link}}</p>", template.HtmlBody);
        Assert.Equal("{{Extra}} {{link}}", template.TextBody);
        Assert.False(template.IsSystem);
        Assert.Equal(["Name", "company", "Link", "Extra"], template.Placeholders);
        Assert.True(template.CreatedAtUtc > DateTime.UtcNow.AddMinutes(-5));
        Assert.True(template.UpdatedAtUtc >= template.CreatedAtUtc);
    }

    [Fact]
    public async Task GetByKey_OfSeededTemplate_ReturnsSystemTemplateWithItsDocumentedPlaceholders()
    {
        var template = await GetTemplateAsync("verify-email");

        Assert.True(template.IsSystem);
        Assert.Equal(
            SystemTemplateRequirements.TryGet("verify-email", out var required) ? required.Order(StringComparer.Ordinal) : [],
            template.Placeholders.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("does-not-exist")]
    [InlineData("WELCOME-USER")]
    [InlineData("Welcome-User")]
    [InlineData("bad_key")]
    [InlineData("ab")]
    [InlineData("welcome-user%20")]
    public async Task GetByKey_WithUnknownOrNonSlugOrWrongCaseKey_Returns404(string key)
    {
        var response = await GetAsync($"{BaseUrl}/{key}");

        await AssertProblemAsync(response, HttpStatusCode.NotFound, "Templates.NotFound");
    }

    [Fact]
    public async Task Create_ReturnsCreatedWithLocationAndFullTemplate()
    {
        var key = UniqueKey();

        var response = await PostAsync(BaseUrl, Body(key));

        var template = await ReadTemplateAsync(response, HttpStatusCode.Created);
        Assert.EndsWith($"{BaseUrl}/{key}", response.Headers.Location?.AbsolutePath);
        Assert.Equal(key, template.Key);
        Assert.False(template.IsSystem);
        Assert.Equal(["Name"], template.Placeholders);

        var fetched = await GetTemplateAsync(key);
        Assert.Equal(template.Id, fetched.Id);
        Assert.Equal("<p>Hello {{Name}}</p>", fetched.HtmlBody);
    }

    [Fact]
    public async Task Create_IgnoresIsSystemSentByTheClient()
    {
        var key = UniqueKey();
        var json = $$"""{"key":"{{key}}","name":"N","subject":"S","htmlBody":"<p>x</p>","isSystem":true}""";

        var response = await PostRawAsync(BaseUrl, json);

        var created = await ReadTemplateAsync(response, HttpStatusCode.Created);
        Assert.False(created.IsSystem);
        Assert.False((await GetTemplateAsync(key)).IsSystem);
        Assert.Equal(HttpStatusCode.NoContent, (await DeleteAsync(key)).StatusCode);
    }

    [Fact]
    public async Task Create_WithBodyAtTheMaximumLength_IsAccepted()
    {
        var key = UniqueKey();
        var html = new string('a', EmailTemplate.BodyMaxLength);

        var created = await CreateAsync(key, Body(key, html: html, text: html));

        Assert.Equal(EmailTemplate.BodyMaxLength, (await GetTemplateAsync(created.Key)).HtmlBody.Length);
    }

    [Fact]
    public async Task Create_WithDuplicateKey_Returns409AndKeepsTheOriginal()
    {
        var key = UniqueKey();
        var original = await CreateAsync(key);

        var response = await PostAsync(BaseUrl, Body(key, name: "Other"));

        await AssertProblemAsync(response, HttpStatusCode.Conflict, "Templates.KeyTaken");
        Assert.Equal("Custom template", (await GetTemplateAsync(key)).Name);
        Assert.Single(await AuditRowsAsync(original.Id));
    }

    [Fact]
    public async Task Create_WithTheKeyOfASeededSystemTemplate_Returns409()
    {
        var response = await PostAsync(BaseUrl, Body("welcome-user"));

        await AssertProblemAsync(response, HttpStatusCode.Conflict, "Templates.KeyTaken");
    }

    [Fact]
    public async Task Create_WithConcurrentDuplicateRequests_ReturnsOneCreatedAndConflictsNeverServerErrors()
    {
        var key = UniqueKey();

        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => PostAsync(BaseUrl, Body(key))));

        Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.Created));
        var conflicts = responses.Where(response => response.StatusCode == HttpStatusCode.Conflict).ToList();
        Assert.Equal(4, conflicts.Count);
        foreach (var conflict in conflicts)
        {
            Assert.Equal("Templates.KeyTaken", (await ReadProblemAsync(conflict)).Extensions["errorCode"]?.ToString());
        }

        var stored = await GetTemplateAsync(key);
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM audit_logs WHERE action = 'TEMPLATE_CREATED' AND new_values->>'key' = @key",
            connection);
        command.Parameters.AddWithValue("key", key);
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
        Assert.Single(await AuditRowsAsync(stored.Id));
    }

    [Theory]
    [MemberData(nameof(InvalidCreateBodies))]
    public async Task Create_WithInvalidContent_Returns400WithTheErrorCode(string json, string errorCode)
    {
        var response = await PostRawAsync(BaseUrl, json);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, errorCode);
    }

    [Fact]
    public async Task Create_WithBodiesOverTheMaximumLength_Returns400ContentTooLarge()
    {
        var oversized = new string('a', EmailTemplate.BodyMaxLength + 1);
        var key = UniqueKey();

        var htmlResponse = await PostAsync(BaseUrl, Body(key, html: oversized));
        var textResponse = await PostAsync(BaseUrl, Body(key, text: oversized));

        await AssertProblemAsync(htmlResponse, HttpStatusCode.BadRequest, "Templates.ContentTooLarge");
        await AssertProblemAsync(textResponse, HttpStatusCode.BadRequest, "Templates.ContentTooLarge");
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync($"{BaseUrl}/{key}")).StatusCode);
    }

    [Fact]
    public async Task Update_WithBodyOverTheMaximumLength_Returns400ContentTooLarge()
    {
        var created = await CreateAsync();

        var response = await PutAsync(created.Key, UpdateBody(html: new string('a', EmailTemplate.BodyMaxLength + 1)));

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "Templates.ContentTooLarge");
    }

    [Fact]
    public async Task Create_WithoutABody_Returns400()
    {
        var response = await PostRawAsync(BaseUrl, "null");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Update_OfCustomTemplate_ReplacesContentAndKeepsTheKey()
    {
        var created = await CreateAsync();

        var response = await PutAsync(created.Key, UpdateBody());

        var updated = await ReadTemplateAsync(response);
        Assert.Equal(created.Id, updated.Id);
        Assert.Equal(created.Key, updated.Key);
        Assert.Equal("Renamed template", updated.Name);
        Assert.Equal("Changed description", updated.Description);
        Assert.Equal("Bye {{Name}}", updated.Subject);
        Assert.Equal("<p>Bye {{Name}}</p>", updated.HtmlBody);
        Assert.Equal("Bye {{Name}}", updated.TextBody);
        Assert.False(updated.IsSystem);
        Assert.Equal(created.CreatedAtUtc, updated.CreatedAtUtc);
        Assert.True(updated.UpdatedAtUtc > created.UpdatedAtUtc);

        var fetched = await GetTemplateAsync(created.Key);
        Assert.Equal("Renamed template", fetched.Name);
        Assert.True((updated.UpdatedAtUtc - fetched.UpdatedAtUtc).Duration() < TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task Update_WithAKeyInTheBody_IgnoresIt()
    {
        var created = await CreateAsync();
        var otherKey = UniqueKey();
        var json = $$"""{"key":"{{otherKey}}","name":"Renamed","subject":"S","htmlBody":"<p>x</p>","isSystem":true}""";

        var response = await _client.PutAsync(
            $"{BaseUrl}/{created.Key}",
            new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        var updated = await ReadTemplateAsync(response);
        Assert.Equal(created.Key, updated.Key);
        Assert.Equal("Renamed", updated.Name);
        Assert.False(updated.IsSystem);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync($"{BaseUrl}/{otherKey}")).StatusCode);
    }

    [Theory]
    [InlineData("does-not-exist")]
    [InlineData("bad_key")]
    [InlineData("WELCOME-USER")]
    public async Task Update_WithUnknownOrInvalidKey_Returns404(string key)
    {
        var response = await PutAsync(key, UpdateBody());

        await AssertProblemAsync(response, HttpStatusCode.NotFound, "Templates.NotFound");
    }

    [Fact]
    public async Task Update_WithInvalidContent_Returns400AndLeavesTheTemplateUntouched()
    {
        var created = await CreateAsync();

        var response = await PutAsync(created.Key, UpdateBody(subject: "   "));

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "Templates.InvalidSubject");
        var fetched = await GetTemplateAsync(created.Key);
        Assert.Equal(created.Name, fetched.Name);
        Assert.Equal(created.Subject, fetched.Subject);
        Assert.Equal(created.UpdatedAtUtc, fetched.UpdatedAtUtc);
        Assert.Single(await AuditRowsAsync(created.Id));
    }

    [Fact]
    public async Task Update_OfSystemTemplateThatDropsARequiredPlaceholder_Returns400ListingItAndChangesNothing()
    {
        var original = await GetTemplateAsync("verify-email");
        var auditBefore = (await AuditRowsAsync(original.Id)).Count;

        var response = await PutAsync(
            "verify-email",
            UpdateBody(
                subject: "Verify {{AppName}}",
                html: "<p>{{UserName}} {{AppName}} {{ExpiresInHours}}</p>",
                text: "{{UserName}} {{AppName}} {{ExpiresInHours}}"));

        var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest, "Templates.MissingRequiredPlaceholders");
        Assert.Contains("VerificationUrl", problem.Detail);
        Assert.DoesNotContain("UserName", problem.Detail);
        var after = await GetTemplateAsync("verify-email");
        Assert.Equal(original.HtmlBody, after.HtmlBody);
        Assert.Equal(original.UpdatedAtUtc, after.UpdatedAtUtc);
        Assert.Equal(auditBefore, (await AuditRowsAsync(original.Id)).Count);
    }

    [Fact]
    public async Task Update_OfSystemTemplateThatAddsAnUnknownPlaceholder_Returns400ListingItAndChangesNothing()
    {
        var original = await GetTemplateAsync("reset-password");
        var auditBefore = (await AuditRowsAsync(original.Id)).Count;

        var response = await PutAsync(
            "reset-password",
            UpdateBody(
                subject: "Reset {{AppName}}",
                html: "<p>{{UserName}} {{AppName}} {{ResetUrl}} {{ExpiresInMinutes}} {{Surprise}}</p>",
                text: null));

        var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest, "Templates.UnknownPlaceholders");
        Assert.Contains("Surprise", problem.Detail);
        var after = await GetTemplateAsync("reset-password");
        Assert.Equal(original.HtmlBody, after.HtmlBody);
        Assert.Equal(original.UpdatedAtUtc, after.UpdatedAtUtc);
        Assert.Equal(auditBefore, (await AuditRowsAsync(original.Id)).Count);
    }

    [Fact]
    public async Task Update_OfSystemTemplate_WithInvalidContent_ReportsTheContentErrorFirst()
    {
        var response = await PutAsync("welcome-user", UpdateBody(subject: "   ", html: "<p>no placeholders</p>"));

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "Templates.InvalidSubject");
    }

    [Fact]
    public async Task Update_OfSystemTemplate_KeepingItsPlaceholdersAcrossAnyPartIsAcceptedAndRestorable()
    {
        const string key = "welcome-user";
        var original = await GetTemplateAsync(key);

        try
        {
            var response = await PutAsync(
                key,
                UpdateBody(
                    name: "Edited welcome",
                    description: null,
                    subject: "Bienvenido a {{appname}}",
                    html: "<p>Hola {{ UserName }}, bienvenido a {{AppName}}</p>",
                    text: null));

            var updated = await ReadTemplateAsync(response);
            Assert.True(updated.IsSystem);
            Assert.Equal(key, updated.Key);
            Assert.Equal("Edited welcome", updated.Name);
            Assert.Null(updated.TextBody);
            Assert.Equal("Bienvenido a {{appname}}", (await GetTemplateAsync(key)).Subject);

            var preview = await PostAsync(
                $"{BaseUrl}/{key}/preview",
                new { variables = new Dictionary<string, string> { ["UserName"] = "Ana", ["AppName"] = "Href" } });
            var rendered = await preview.Content.ReadFromJsonAsync<EmailTemplatePreviewResponse>(TestContext.Current.CancellationToken);
            Assert.Equal("Bienvenido a Href", rendered!.Subject);
        }
        finally
        {
            var restore = await PutAsync(
                key,
                UpdateBody(original.Name, original.Description, original.Subject, original.HtmlBody, original.TextBody));
            Assert.Equal(HttpStatusCode.OK, restore.StatusCode);
        }

        var restored = await GetTemplateAsync(key);
        Assert.Equal(original.Name, restored.Name);
        Assert.Equal(original.Description, restored.Description);
        Assert.Equal(original.Subject, restored.Subject);
        Assert.Equal(original.HtmlBody, restored.HtmlBody);
        Assert.Equal(original.TextBody, restored.TextBody);
        Assert.True(restored.IsSystem);
    }

    [Fact]
    public async Task Delete_OfCustomTemplate_Returns204AndTheTemplateIsGone()
    {
        var created = await CreateAsync();

        var response = await DeleteAsync(created.Key);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await AssertProblemAsync(await GetAsync($"{BaseUrl}/{created.Key}"), HttpStatusCode.NotFound, "Templates.NotFound");
        await AssertProblemAsync(await DeleteAsync(created.Key), HttpStatusCode.NotFound, "Templates.NotFound");
        Assert.DoesNotContain(
            JsonDocument.Parse(await (await GetAsync(BaseUrl)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
                .RootElement.EnumerateArray(),
            item => item.GetProperty("key").GetString() == created.Key);
    }

    [Fact]
    public async Task Delete_OfSystemTemplate_Returns409AndKeepsItWithoutAuditRow()
    {
        var original = await GetTemplateAsync("delivery-failed-alert");
        var auditBefore = (await AuditRowsAsync(original.Id)).Count;

        var response = await DeleteAsync("delivery-failed-alert");

        await AssertProblemAsync(response, HttpStatusCode.Conflict, "Templates.SystemTemplate");
        Assert.Equal(original.Id, (await GetTemplateAsync("delivery-failed-alert")).Id);
        Assert.Equal(auditBefore, (await AuditRowsAsync(original.Id)).Count);
    }

    [Theory]
    [InlineData("does-not-exist")]
    [InlineData("bad_key")]
    [InlineData("WELCOME-USER")]
    public async Task Delete_WithUnknownOrInvalidKey_Returns404(string key)
    {
        var response = await DeleteAsync(key);

        await AssertProblemAsync(response, HttpStatusCode.NotFound, "Templates.NotFound");
    }

    [Fact]
    public async Task Delete_KeepsTheTemplateKeyOfExistingNotifications()
    {
        var key = UniqueKey();
        await CreateAsync(key, Body(key, subject: "Fixed", html: "<p>Fixed</p>", text: null));
        var appKey = $"tpl-{Guid.NewGuid():N}"[..20];
        var appResponse = await PostAsync("/api/apps", new CreateApplicationRequest(appKey, "Template Client", null, false));
        var app = await appResponse.Content.ReadFromJsonAsync<ApplicationCreatedResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(app);

        using var queue = new HttpRequestMessage(HttpMethod.Post, "/api/notifications/email")
        {
            Content = JsonContent.Create(new { to = new { email = "user@example.com" }, templateKey = key }),
        };
        queue.Headers.Add(AppKeyAuthentication.AppKeyHeaderName, appKey);
        queue.Headers.Add(AppKeyAuthentication.AppSecretHeaderName, app.ClientSecret);
        var queued = await _client.SendAsync(queue, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        var queuedBody = await queued.Content.ReadFromJsonAsync<QueuedNotificationResponse>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, (await DeleteAsync(key)).StatusCode);

        using var read = new HttpRequestMessage(HttpMethod.Get, $"/api/notifications/{queuedBody!.Id}");
        read.Headers.Add(AppKeyAuthentication.AppKeyHeaderName, appKey);
        read.Headers.Add(AppKeyAuthentication.AppSecretHeaderName, app.ClientSecret);
        var notification = await _client.SendAsync(read, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, notification.StatusCode);
        var notificationJson = Parse(await notification.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(key, notificationJson.GetProperty("templateKey").GetString());
    }

    [Fact]
    public async Task Preview_RendersWithEncodedHtmlAndStoresNothing()
    {
        var key = UniqueKey();
        var created = await CreateAsync(key);
        var auditBefore = (await AuditRowsAsync(created.Id)).Count;

        var response = await PostAsync(
            $"{BaseUrl}/{key}/preview",
            new { variables = new Dictionary<string, string> { ["Name"] = "<b>Ana</b> & Co" } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("Hello <b>Ana</b> & Co", json.GetProperty("subject").GetString());
        Assert.Equal("<p>Hello &lt;b&gt;Ana&lt;/b&gt; &amp; Co</p>", json.GetProperty("html").GetString());
        Assert.Equal("Hello <b>Ana</b> & Co", json.GetProperty("text").GetString());
        var after = await GetTemplateAsync(key);
        Assert.Equal(created.UpdatedAtUtc, after.UpdatedAtUtc);
        Assert.Equal("Hello {{Name}}", after.Subject);
        Assert.Equal(auditBefore, (await AuditRowsAsync(created.Id)).Count);
    }

    [Fact]
    public async Task Preview_OfTemplateWithoutTextBody_ReturnsNullText()
    {
        var key = UniqueKey();
        await CreateAsync(key, Body(key, subject: "S", html: "<p>x</p>", text: null));

        var response = await PostAsync($"{BaseUrl}/{key}/preview", new { variables = new Dictionary<string, string>() });

        var json = Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("text").ValueKind);
    }

    [Theory]
    [InlineData("""{"variables":{}}""")]
    [InlineData("""{"variables":{"Other":"x"}}""")]
    [InlineData("{}")]
    public async Task Preview_WithMissingVariables_Returns400(string json)
    {
        var key = UniqueKey();
        await CreateAsync(key);

        var response = await PostRawAsync($"{BaseUrl}/{key}/preview", json);

        var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest, "Templates.MissingVariables");
        Assert.Contains("Name", problem.Detail);
    }

    [Fact]
    public async Task Preview_WithCaseDifferingDuplicateVariables_Returns400()
    {
        var key = UniqueKey();
        await CreateAsync(key);

        var response = await PostRawAsync($"{BaseUrl}/{key}/preview", """{"variables":{"Name":"a","name":"b"}}""");

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "Templates.DuplicateVariables");
    }

    [Theory]
    [InlineData("does-not-exist")]
    [InlineData("bad_key")]
    [InlineData("WELCOME-USER")]
    public async Task Preview_WithUnknownOrInvalidKey_Returns404(string key)
    {
        var response = await PostRawAsync($"{BaseUrl}/{key}/preview", """{"variables":{}}""");

        await AssertProblemAsync(response, HttpStatusCode.NotFound, "Templates.NotFound");
    }

    [Fact]
    public async Task Preview_OfSystemTemplate_RendersAndDoesNotChangeIt()
    {
        var original = await GetTemplateAsync("verify-email");

        var response = await PostAsync(
            $"{BaseUrl}/verify-email/preview",
            new
            {
                variables = new Dictionary<string, string>
                {
                    ["UserName"] = "Ana",
                    ["AppName"] = "Href",
                    ["VerificationUrl"] = "https://example.com/v?a=1&b=2",
                    ["ExpiresInHours"] = "24",
                },
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Contains("https://example.com/v?a=1&amp;b=2", json.GetProperty("html").GetString());
        Assert.Equal(original.UpdatedAtUtc, (await GetTemplateAsync("verify-email")).UpdatedAtUtc);
    }

    [Fact]
    public async Task Audit_Create_RecordsNewValuesWithTheTemplateIdAsEntityId()
    {
        var created = await CreateAsync();

        var rows = await AuditRowsAsync(created.Id);

        var row = Assert.Single(rows);
        Assert.Equal(AuditActions.TemplateCreated, row.Action);
        Assert.Equal("TEMPLATE_CREATED", row.Action);
        Assert.Equal(nameof(EmailTemplate), row.EntityType);
        Assert.Equal(created.Id.ToString(), row.EntityId);
        Assert.Equal(AuditActorType.Anonymous, row.ActorType);
        Assert.Null(row.OldValues);
        var values = Parse(row.NewValues);
        Assert.Equal(created.Key, values.GetProperty("key").GetString());
        Assert.Equal("Custom template", values.GetProperty("name").GetString());
        Assert.Equal("A test template", values.GetProperty("description").GetString());
        Assert.Equal("Hello {{Name}}", values.GetProperty("subject").GetString());
        Assert.Equal("<p>Hello {{Name}}</p>", values.GetProperty("htmlBody").GetString());
        Assert.Equal("Hello {{Name}}", values.GetProperty("textBody").GetString());
        Assert.False(values.GetProperty("isSystem").GetBoolean());
    }

    [Fact]
    public async Task Audit_Update_RecordsOldAndNewValues()
    {
        var created = await CreateAsync();
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(created.Key, UpdateBody())).StatusCode);

        var rows = await AuditRowsAsync(created.Id);

        Assert.Equal([AuditActions.TemplateCreated, AuditActions.TemplateUpdated], rows.Select(row => row.Action));
        var update = rows[1];
        Assert.Equal("TEMPLATE_UPDATED", update.Action);
        Assert.Equal(created.Id.ToString(), update.EntityId);
        var oldValues = Parse(update.OldValues);
        var newValues = Parse(update.NewValues);
        Assert.Equal("Custom template", oldValues.GetProperty("name").GetString());
        Assert.Equal("Hello {{Name}}", oldValues.GetProperty("subject").GetString());
        Assert.Equal("<p>Hello {{Name}}</p>", oldValues.GetProperty("htmlBody").GetString());
        Assert.Equal("Renamed template", newValues.GetProperty("name").GetString());
        Assert.Equal("Bye {{Name}}", newValues.GetProperty("subject").GetString());
        Assert.Equal("<p>Bye {{Name}}</p>", newValues.GetProperty("htmlBody").GetString());
        Assert.Equal(created.Key, newValues.GetProperty("key").GetString());
    }

    [Fact]
    public async Task Audit_Delete_RecordsOldValuesAndNoNewValues()
    {
        var created = await CreateAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await DeleteAsync(created.Key)).StatusCode);

        var rows = await AuditRowsAsync(created.Id);

        Assert.Equal([AuditActions.TemplateCreated, AuditActions.TemplateDeleted], rows.Select(row => row.Action));
        var deleted = rows[1];
        Assert.Equal("TEMPLATE_DELETED", deleted.Action);
        Assert.Equal(nameof(EmailTemplate), deleted.EntityType);
        Assert.Equal(created.Id.ToString(), deleted.EntityId);
        Assert.Null(deleted.NewValues);
        var oldValues = Parse(deleted.OldValues);
        Assert.Equal(created.Key, oldValues.GetProperty("key").GetString());
        Assert.Equal("<p>Hello {{Name}}</p>", oldValues.GetProperty("htmlBody").GetString());
        Assert.False(oldValues.GetProperty("isSystem").GetBoolean());
    }

    [Fact]
    public async Task SystemTemplateRequirements_MatchTheSeededSystemTemplatesExactly()
    {
        var seededKeys = SeededKeyRegex().Matches(ReadSeedScript()).Select(match => match.Groups[1].Value).ToList();
        Assert.Equal(4, seededKeys.Count);
        Assert.Equal(seededKeys.Order(StringComparer.Ordinal), SystemTemplateRequirements.Keys.Order(StringComparer.Ordinal));

        var databaseKeys = await QueryStringsAsync("SELECT key FROM email_templates WHERE is_system AND key NOT LIKE 'tpl-%'");
        Assert.Equal(databaseKeys.Order(StringComparer.Ordinal), SystemTemplateRequirements.Keys.Order(StringComparer.Ordinal));

        var renderer = new TemplateRenderer();

        foreach (var key in seededKeys)
        {
            Assert.True(SystemTemplateRequirements.TryGet(key, out var required));
            using var session = postgres.SessionFactory.OpenSession();
            var template = await session.Query<EmailTemplate>()
                .SingleAsync(candidate => candidate.Key == key, TestContext.Current.CancellationToken);

            var used = renderer.GetPlaceholders(template.Subject)
                .Concat(renderer.GetPlaceholders(template.HtmlBody))
                .Concat(renderer.GetPlaceholders(template.TextBody))
                .ToHashSet(StringComparer.Ordinal);

            Assert.True(template.IsSystem);
            Assert.True(used.IsSupersetOf(required), $"{key} does not satisfy its requirements");
            Assert.True(used.SetEquals(required), $"{key} uses placeholders outside its requirements");
        }
    }

    [Fact]
    public void SystemTemplateRequirements_TryGet_ReturnsFalseForCustomKeys()
    {
        Assert.False(SystemTemplateRequirements.TryGet("my-custom-template", out _));
        Assert.False(SystemTemplateRequirements.TryGet("WELCOME-USER", out _));
    }

    private async Task<List<string>> QueryStringsAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var values = new List<string>();

        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private async Task DeleteRowAsync(string key)
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("DELETE FROM email_templates WHERE key = @key", connection);
        command.Parameters.AddWithValue("key", key);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private async Task InsertAsync(EmailTemplate template)
    {
        using var session = postgres.SessionFactory.OpenSession();
        using var transaction = session.BeginTransaction();
        await session.SaveAsync(template, TestContext.Current.CancellationToken);
        await transaction.CommitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Delete_WithConcurrentRequestsOnTheSameKey_ReturnsOneNoContentAndOneNotFoundNeverServerErrors()
    {
        for (var round = 0; round < 5; round++)
        {
            var created = await CreateAsync();

            var responses = await Task.WhenAll(DeleteAsync(created.Key), DeleteAsync(created.Key));

            Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.NoContent));
            Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.NotFound));
            Assert.Equal(
                [AuditActions.TemplateCreated, AuditActions.TemplateDeleted],
                (await AuditRowsAsync(created.Id)).Select(row => row.Action));
        }
    }

    [Fact]
    public async Task UpdateAndDelete_RunningConcurrentlyNeverReturnServerErrors()
    {
        for (var round = 0; round < 5; round++)
        {
            var created = await CreateAsync();

            var responses = await Task.WhenAll(PutAsync(created.Key, UpdateBody()), DeleteAsync(created.Key));

            Assert.Equal(HttpStatusCode.NoContent, responses[1].StatusCode);
            Assert.Contains(responses[0].StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.NotFound });
            Assert.Equal(HttpStatusCode.NotFound, (await GetAsync($"{BaseUrl}/{created.Key}")).StatusCode);
            Assert.Equal(AuditActions.TemplateDeleted, (await AuditRowsAsync(created.Id)).Last().Action);
        }
    }

    [Fact]
    public async Task Update_WithConcurrentRequests_RecordsEachOldValuesAsThePreviousNewValues()
    {
        for (var round = 0; round < 5; round++)
        {
            var created = await CreateAsync();

            var responses = await Task.WhenAll(
                PutAsync(created.Key, UpdateBody(name: "First writer", subject: "A {{Name}}", html: "<p>A {{Name}}</p>")),
                PutAsync(created.Key, UpdateBody(name: "Second writer", subject: "B {{Name}}", html: "<p>B {{Name}}</p>")));

            Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
            var rows = await AuditRowsAsync(created.Id);
            Assert.Equal(
                [AuditActions.TemplateCreated, AuditActions.TemplateUpdated, AuditActions.TemplateUpdated],
                rows.Select(row => row.Action));
            var first = rows[1];
            var second = rows[2];
            Assert.Equal("Custom template", Parse(first.OldValues).GetProperty("name").GetString());
            Assert.Equal(Parse(first.NewValues).GetRawText(), Parse(second.OldValues).GetRawText());
            Assert.NotEqual(Parse(first.NewValues).GetProperty("name").GetString(), Parse(second.NewValues).GetProperty("name").GetString());
            Assert.Equal(Parse(second.NewValues).GetProperty("name").GetString(), (await GetTemplateAsync(created.Key)).Name);
        }
    }

    [Fact]
    public async Task Update_OfNoOpContent_Returns200WithoutAuditRowOrTimestampChange()
    {
        var created = await CreateAsync();

        var response = await PutAsync(
            created.Key,
            UpdateBody(name: "  Custom template ", description: "A test template", subject: " Hello {{Name}} ", html: "<p>Hello {{Name}}</p>", text: "Hello {{Name}}"));

        var returned = await ReadTemplateAsync(response);
        Assert.Equal(created.Key, returned.Key);
        Assert.Equal("Custom template", returned.Name);
        var fetched = await GetTemplateAsync(created.Key);
        Assert.Equal(created.UpdatedAtUtc, fetched.UpdatedAtUtc);
        Assert.Equal([AuditActions.TemplateCreated], (await AuditRowsAsync(created.Id)).Select(row => row.Action));
    }

    [Fact]
    public async Task Update_OfDeliveryFailedAlert_ThatCannotRenderWithWorstCaseValues_Returns400()
    {
        var original = await GetTemplateAsync("delivery-failed-alert");
        var auditBefore = (await AuditRowsAsync(original.Id)).Count;

        var response = await PutAsync(
            "delivery-failed-alert",
            UpdateBody(
                name: original.Name,
                description: original.Description,
                subject: "Falló: {{OriginalSubject}} ({{LastError}})",
                html: original.HtmlBody,
                text: original.TextBody));

        var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest, "Templates.SystemTemplateNotRenderable");
        Assert.Contains("subject", problem.Detail);
        Assert.Equal(original.Subject, (await GetTemplateAsync("delivery-failed-alert")).Subject);
        Assert.Equal(auditBefore, (await AuditRowsAsync(original.Id)).Count);
    }

    [Fact]
    public async Task Update_OfDeliveryFailedAlert_WithTheSeededContent_PassesTheWorstCaseRender()
    {
        var original = await GetTemplateAsync("delivery-failed-alert");

        var response = await PutAsync(
            "delivery-failed-alert",
            UpdateBody(original.Name, original.Description, original.Subject, original.HtmlBody, original.TextBody));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(SystemTemplateRequirements.TryGetWorstCaseVariables("delivery-failed-alert", out var worstCase));
        Assert.True(new TemplateRenderer().Render(original.Subject, original.HtmlBody, original.TextBody, worstCase).IsSuccess);
        Assert.False(SystemTemplateRequirements.TryGetWorstCaseVariables("welcome-user", out _));
    }

    [Fact]
    public async Task Update_OfSystemTemplateWithoutRequirementsEntry_FailsClosedKeepingItsCurrentPlaceholders()
    {
        var key = UniqueKey();
        var template = EmailTemplate.Create(key, "Unmapped", null, "S {{A}}", "<p>{{A}} {{B}}</p>", null, true).Value;
        await InsertAsync(template);

        try
        {
            await AssertProblemAsync(
                await PutAsync(key, UpdateBody(subject: "S {{A}}", html: "<p>{{A}}</p>", text: null)),
                HttpStatusCode.BadRequest,
                "Templates.MissingRequiredPlaceholders");
            await AssertProblemAsync(
                await PutAsync(key, UpdateBody(subject: "S {{A}}", html: "<p>{{A}} {{B}} {{C}}</p>", text: null)),
                HttpStatusCode.BadRequest,
                "Templates.UnknownPlaceholders");
            await AssertProblemAsync(await DeleteAsync(key), HttpStatusCode.Conflict, "Templates.SystemTemplate");

            var accepted = await ReadTemplateAsync(await PutAsync(key, UpdateBody(subject: "New {{a}}", html: "<h1>{{B}}</h1>{{A}}", text: null)));
            Assert.Equal("<h1>{{B}}</h1>{{A}}", accepted.HtmlBody);
            Assert.True(accepted.IsSystem);
        }
        finally
        {
            await DeleteRowAsync(key);
        }
    }

    [Fact]
    public async Task IsKeyUniqueViolation_RecognizesTheExceptionRaisedByADuplicateInsert()
    {
        var key = UniqueKey();
        await InsertAsync(EmailTemplate.Create(key, "First", null, "S", "<p>x</p>", null, false).Value);

        try
        {
            using var session = postgres.SessionFactory.OpenSession();
            using var transaction = session.BeginTransaction();
            await session.SaveAsync(
                EmailTemplate.Create(key, "Second", null, "S", "<p>x</p>", null, false).Value,
                TestContext.Current.CancellationToken);

            var exception = await Assert.ThrowsAnyAsync<Exception>(
                () => transaction.CommitAsync(TestContext.Current.CancellationToken));

            Assert.True(EmailTemplateService.IsKeyUniqueViolation(exception));
            Assert.False(EmailTemplateService.IsKeyUniqueViolation(new InvalidOperationException("other")));
        }
        finally
        {
            await DeleteRowAsync(key);
        }
    }
}
