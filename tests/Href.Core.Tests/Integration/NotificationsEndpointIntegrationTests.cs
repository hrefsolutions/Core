using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Href.Core.Api.Modules.Applications;
using Href.Core.Api.Modules.Audit;
using Href.Core.Api.Modules.Notifications;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Time.Testing;
using NHibernate.Linq;
using ISession = NHibernate.ISession;

namespace Href.Core.Tests.Integration;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class NotificationsEndpointIntegrationTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const int BodyLimit = 512 * 1024;

    private ApiFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new ApiFactory(postgres.ConnectionString);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    public static TheoryData<string> InvalidContentBodies => new()
    {
        """{"to":{"email":"user@example.com"},"templateKey":"welcome-user","html":"<p>x</p>","subject":"S"}""",
        """{"to":{"email":"user@example.com"}}""",
        """{"to":{"email":"user@example.com"},"html":"<p>x</p>"}""",
        """{"to":{"email":"user@example.com"},"subject":"S"}""",
        """{"to":{"email":"user@example.com"},"html":"<p>x</p>","subject":"S","variables":{"UserName":"Ana"}}""",
        """{"to":{"email":"user@example.com"},"html":"<p>x</p>","subject":"S","templateKey":""}""",
        """{"to":{"email":"user@example.com"},"templateKey":"welcome-user","subject":"S"}""",
        """{"to":{"email":"user@example.com"},"templateKey":"welcome-user","text":"t"}""",
        """{"to":{"email":"user@example.com"},"templateKey":"   "}""",
        """{"to":{"email":"user@example.com"},"html":"   ","subject":"S"}""",
    };

    public static TheoryData<string, string> InvalidFieldBodies => new()
    {
        { """{"html":"<p>x</p>","subject":"S"}""", "Notifications.InvalidRecipient" },
        { """{"to":null,"html":"<p>x</p>","subject":"S"}""", "Notifications.InvalidRecipient" },
        { """{"to":{},"html":"<p>x</p>","subject":"S"}""", "Notifications.InvalidRecipient" },
        { """{"to":{"email":null},"html":"<p>x</p>","subject":"S"}""", "Notifications.InvalidRecipient" },
        { """{"to":{"email":"not-an-email"},"html":"<p>x</p>","subject":"S"}""", "Notifications.InvalidRecipient" },
        { """{"to":{"email":"a@example.com, b@example.com"},"html":"<p>x</p>","subject":"S"}""", "Notifications.InvalidRecipient" },
        { """{"to":{"email":"user@example.com","name":"Ana\r\nBcc: x@example.com"},"html":"<p>x</p>","subject":"S"}""", "Notifications.InvalidRecipientName" },
        { """{"to":{"email":"user@example.com"},"html":"<p>x</p>","subject":"Hi\r\nBcc: x@example.com"}""", "Notifications.InvalidSubject" },
        { "{\"to\":{\"email\":\"user@example.com\"},\"html\":\"<p>x</p>\",\"subject\":\"Hi\U00002028there\"}", "Notifications.InvalidSubject" },
        { """{"to":{"email":"user@example.com"},"html":"<p>x</p>","subject":"S","notifyOnFailureTo":"ops@example.com\r\nBcc: x@example.com"}""", "Notifications.InvalidNotifyOnFailureTo" },
    };

    private static string UniqueKey(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..20];

    private async Task<TestApp> CreateAppAsync()
    {
        using var client = _factory.CreateClient();
        var appKey = UniqueKey("notif");
        var response = await client.PostAsJsonAsync(
            "/api/apps",
            new CreateApplicationRequest(appKey, "Notification Client", null, false),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<ApplicationCreatedResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(created);
        return new TestApp(appKey, created.ClientSecret);
    }

    private async Task<HttpResponseMessage> SendAsync(
        TestApp? app,
        HttpMethod method,
        string url,
        string? json = null,
        string? correlationId = null)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(method, url);

        if (app is not null)
        {
            request.Headers.Add(AppKeyAuthentication.AppKeyHeaderName, app.AppKey);
            request.Headers.Add(AppKeyAuthentication.AppSecretHeaderName, app.Secret);
        }

        if (correlationId is not null)
        {
            request.Headers.Add("X-Correlation-Id", correlationId);
        }

        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private Task<HttpResponseMessage> PostAsync(TestApp? app, string json, string? correlationId = null) =>
        SendAsync(app, HttpMethod.Post, "/api/notifications/email", json, correlationId);

    private Task<HttpResponseMessage> GetAsync(TestApp? app, string url) =>
        SendAsync(app, HttpMethod.Get, url);

    private static string HtmlBodyJson(string html, string? text = null) =>
        JsonSerializer.Serialize(new
        {
            to = new { email = "user@example.com" },
            subject = "Hello",
            html,
            text,
        });

    private static async Task<ProblemDetails> ReadProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(TestContext.Current.CancellationToken);
        Assert.NotNull(problem);
        return problem;
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string errorCode)
    {
        Assert.Equal(status, response.StatusCode);
        var problem = await ReadProblemAsync(response);
        Assert.Equal(errorCode, problem.Extensions["errorCode"]?.ToString());
    }

    private static async Task<Guid> ReadQueuedIdAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var queued = await response.Content.ReadFromJsonAsync<QueuedNotificationResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(queued);
        Assert.Equal("Pending", queued.Status);
        return queued.Id;
    }

    private async Task<Notification?> LoadNotificationAsync(Guid id)
    {
        using var session = postgres.SessionFactory.OpenSession();
        return await session.GetAsync<Notification>(id, TestContext.Current.CancellationToken);
    }

    private async Task<List<Guid>> SeedAsync(string appKey, params (DateTimeOffset At, bool Sent)[] rows)
    {
        var clock = new FakeTimeProvider(rows.Length > 0 ? rows[0].At : DateTimeOffset.UtcNow);
        using var sessionFactory = postgres.BuildSessionFactory(clock);
        using var session = sessionFactory.OpenSession();
        var ids = new List<Guid>();
        var index = 0;

        foreach (var (at, sent) in rows)
        {
            index++;
            clock.SetUtcNow(at);
            var notification = Notification.Queue(
                appKey,
                "user@example.com",
                "Ana",
                $"Subject {index}",
                "<p>secret html body</p>",
                "secret text body",
                null,
                "ops@example.com",
                "corr-seed",
                clock).Value;

            using var transaction = session.BeginTransaction();
            await session.SaveAsync(notification, TestContext.Current.CancellationToken);
            await transaction.CommitAsync(TestContext.Current.CancellationToken);

            if (sent)
            {
                using var sentTransaction = session.BeginTransaction();
                Assert.True(notification.MarkProcessing(at.UtcDateTime).IsSuccess);
                Assert.True(notification.MarkSent(at.UtcDateTime).IsSuccess);
                await sentTransaction.CommitAsync(TestContext.Current.CancellationToken);
            }

            ids.Add(notification.Id);
        }

        return ids;
    }

    private static readonly DateTimeOffset BaseTime = new(2026, 3, 10, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Post_WithTemplate_Returns202WithLocationAndPersistsRenderedContent()
    {
        var app = await CreateAppAsync();
        var correlationId = $"corr-{Guid.NewGuid():N}";

        var response = await PostAsync(
            app,
            """
            {"to":{"email":"ana@example.com","name":"Ana"},"templateKey":"welcome-user",
             "variables":{"UserName":"Ana <b>","AppName":"Portal"},"notifyOnFailureTo":"ops@example.com"}
            """,
            correlationId);

        var id = await ReadQueuedIdAsync(response);
        Assert.EndsWith($"/api/notifications/{id}", response.Headers.Location?.ToString(), StringComparison.Ordinal);
        Assert.Equal(correlationId, response.Headers.GetValues("X-Correlation-Id").Single());

        var notification = await LoadNotificationAsync(id);
        Assert.NotNull(notification);
        Assert.Equal(app.AppKey, notification.AppKey);
        Assert.Equal("ana@example.com", notification.ToEmail);
        Assert.Equal("Ana", notification.ToName);
        Assert.Equal("welcome-user", notification.TemplateKey);
        Assert.Equal(NotificationStatus.Pending, notification.Status);
        Assert.Equal("ops@example.com", notification.NotifyOnFailureTo);
        Assert.Equal(correlationId, notification.CorrelationId);
        Assert.DoesNotContain("{{", notification.Subject, StringComparison.Ordinal);
        Assert.DoesNotContain("{{", notification.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("Portal", notification.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("Ana &lt;b&gt;", notification.HtmlBody, StringComparison.Ordinal);
        Assert.NotNull(notification.TextBody);
        Assert.DoesNotContain("{{", notification.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Post_WithHtml_Returns202AndPersistsContentWithoutTemplateKey()
    {
        var app = await CreateAppAsync();

        var response = await PostAsync(
            app,
            """{"to":{"email":"ana@example.com"},"subject":"Invoice","html":"<p>Total 10</p>","text":"Total 10"}""");

        var id = await ReadQueuedIdAsync(response);
        var notification = await LoadNotificationAsync(id);
        Assert.NotNull(notification);
        Assert.Equal(app.AppKey, notification.AppKey);
        Assert.Equal("Invoice", notification.Subject);
        Assert.Equal("<p>Total 10</p>", notification.HtmlBody);
        Assert.Equal("Total 10", notification.TextBody);
        Assert.Null(notification.TemplateKey);
        Assert.Null(notification.ToName);
        Assert.Null(notification.NotifyOnFailureTo);
        Assert.False(string.IsNullOrWhiteSpace(notification.CorrelationId));
    }

    [Fact]
    public async Task Post_IgnoresAppKeyInTheBodyAndUsesTheAuthenticatedApplication()
    {
        var app = await CreateAppAsync();

        var response = await PostAsync(
            app,
            """{"appKey":"someone-else","unknownField":1,"to":{"email":"ana@example.com","extra":true},"subject":"S","html":"<p>x</p>"}""");

        var id = await ReadQueuedIdAsync(response);
        var notification = await LoadNotificationAsync(id);
        Assert.NotNull(notification);
        Assert.Equal(app.AppKey, notification.AppKey);
    }

    [Theory]
    [MemberData(nameof(InvalidContentBodies))]
    public async Task Post_WithInvalidContentCombination_Returns400InvalidContent(string json)
    {
        var app = await CreateAppAsync();

        var response = await PostAsync(app, json);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, NotificationErrors.InvalidContent.Code);
    }

    [Theory]
    [MemberData(nameof(InvalidFieldBodies))]
    public async Task Post_WithInvalidRecipientOrHeaderFields_Returns400WithTheFieldErrorCode(string json, string errorCode)
    {
        var app = await CreateAppAsync();

        var response = await PostAsync(app, json);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, errorCode);
    }

    [Fact]
    public async Task Post_WithEmptyVariablesInHtmlMode_Returns202()
    {
        var app = await CreateAppAsync();

        var response = await PostAsync(
            app,
            """{"to":{"email":"ana@example.com"},"subject":"S","html":"<p>x</p>","variables":{}}""");

        await ReadQueuedIdAsync(response);
    }

    [Fact]
    public async Task Post_WithMissingVariables_Returns400ListingThem()
    {
        var app = await CreateAppAsync();

        var response = await PostAsync(
            app,
            """{"to":{"email":"ana@example.com"},"templateKey":"verify-email","variables":{"UserName":"Ana"}}""");

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "Templates.MissingVariables");
        using var json = JsonDocument.Parse(await GetBodyAsync(response));
        var detail = json.RootElement.GetProperty("detail").GetString();
        Assert.Contains("AppName", detail, StringComparison.Ordinal);
        Assert.Contains("VerificationUrl", detail, StringComparison.Ordinal);
        Assert.Contains("ExpiresInHours", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("UserName", detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Post_WithNoVariablesForATemplateThatNeedsThem_Returns400MissingVariables()
    {
        var app = await CreateAppAsync();

        var response = await PostAsync(app, """{"to":{"email":"ana@example.com"},"templateKey":"welcome-user"}""");

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "Templates.MissingVariables");
    }

    [Fact]
    public async Task Post_WithCaseInsensitiveDuplicateVariables_Returns400DuplicateVariables()
    {
        var app = await CreateAppAsync();

        var response = await PostAsync(
            app,
            """{"to":{"email":"ana@example.com"},"templateKey":"welcome-user","variables":{"UserName":"A","username":"B","AppName":"P"}}""");

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "Templates.DuplicateVariables");
    }

    [Theory]
    [InlineData("no-such-template")]
    [InlineData("Welcome-User")]
    public async Task Post_WithUnknownOrDifferentlyCasedTemplate_Returns404TemplatesNotFound(string templateKey)
    {
        var app = await CreateAppAsync();

        var response = await PostAsync(
            app,
            $$"""{"to":{"email":"ana@example.com"},"variables":{"UserName":"A","AppName":"P"},"templateKey":"{{templateKey}}"}""");

        await AssertProblemAsync(response, HttpStatusCode.NotFound, "Templates.NotFound");
    }

    [Fact]
    public async Task Post_WithManyExtraVariables_IgnoresThemAndReturns202()
    {
        var app = await CreateAppAsync();
        var variables = Enumerable.Range(0, 5000).ToDictionary(index => $"Extra{index}", index => $"value-{index}");
        variables["UserName"] = "Ana";
        variables["AppName"] = "Portal";
        var json = JsonSerializer.Serialize(new
        {
            to = new { email = "ana@example.com" },
            templateKey = "welcome-user",
            variables,
        });

        var response = await PostAsync(app, json);

        await ReadQueuedIdAsync(response);
    }

    [Fact]
    public async Task Post_WithNonStringVariableValue_Returns400ProblemDetails()
    {
        var app = await CreateAppAsync();

        var response = await PostAsync(
            app,
            """{"to":{"email":"ana@example.com"},"templateKey":"welcome-user","variables":{"UserName":123,"AppName":"P"}}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await ReadProblemAsync(response);
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("")]
    [InlineData("[]")]
    public async Task Post_WithMalformedBody_Returns400ProblemDetails(string json)
    {
        var app = await CreateAppAsync();

        var response = await PostAsync(app, json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await ReadProblemAsync(response);
    }

    [Fact]
    public async Task Post_WithHtmlExactlyAtTheLimit_Returns202()
    {
        var app = await CreateAppAsync();

        var response = await PostAsync(app, HtmlBodyJson(new string('a', BodyLimit), new string('b', BodyLimit)));

        var id = await ReadQueuedIdAsync(response);
        var notification = await LoadNotificationAsync(id);
        Assert.NotNull(notification);
        Assert.Equal(BodyLimit, notification.HtmlBody.Length);
        Assert.Equal(BodyLimit, notification.TextBody?.Length);
    }

    [Fact]
    public async Task Post_WithHtmlOverTheLimit_Returns400ContentTooLarge()
    {
        var app = await CreateAppAsync();

        var response = await PostAsync(app, HtmlBodyJson(new string('a', BodyLimit + 1)));

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, NotificationErrors.ContentTooLarge.Code);
    }

    [Fact]
    public async Task Post_WithTextOverTheLimit_Returns400ContentTooLarge()
    {
        var app = await CreateAppAsync();

        var response = await PostAsync(app, HtmlBodyJson("<p>x</p>", new string('b', BodyLimit + 1)));

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, NotificationErrors.ContentTooLarge.Code);
    }

    [Fact]
    public async Task Post_WithHtmlWhoseUtf8BytesExceedTheLimitButNotItsCharacters_Returns400ContentTooLarge()
    {
        var app = await CreateAppAsync();
        var html = new string('é', (BodyLimit / 2) + 1);
        Assert.True(html.Length < BodyLimit);

        var response = await PostAsync(app, HtmlBodyJson(html));

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, NotificationErrors.ContentTooLarge.Code);
    }

    [Fact]
    public async Task Post_WithEscapedHtmlAndTextAtTheLimit_IsAcceptedWithinTheRequestSizeLimit()
    {
        var app = await CreateAppAsync();
        var json = JsonSerializer.Serialize(new
        {
            to = new { email = "user@example.com" },
            subject = "Hello",
            html = new string('<', BodyLimit),
            text = new string('>', BodyLimit),
        });
        Assert.True(Encoding.UTF8.GetByteCount(json) > 6 * 1024 * 1024);
        Assert.True(Encoding.UTF8.GetByteCount(json) <= NotificationsController.MaxRequestBodyBytes);

        var response = await PostAsync(app, json);

        await ReadQueuedIdAsync(response);
    }

    [Fact]
    public void QueueEmailAction_DeclaresARequestSizeLimitLargeEnoughForEscapedMaximumBodies()
    {
        var action = typeof(NotificationsController).GetMethod(nameof(NotificationsController.QueueEmail));

        var attribute = action?.GetCustomAttributes(typeof(RequestSizeLimitAttribute), false).Cast<RequestSizeLimitAttribute>().SingleOrDefault();

        Assert.NotNull(attribute);
        var metadata = Assert.IsAssignableFrom<IRequestSizeLimitMetadata>(attribute);
        Assert.Equal(NotificationsController.MaxRequestBodyBytes, metadata.MaxRequestBodySize);
        Assert.True(metadata.MaxRequestBodySize >= (2L * BodyLimit * 6) + 4096);
    }

    [Fact]
    public async Task QueueHtmlAsync_InsideAnActiveTransaction_JoinsItAndRollsBackWithTheCaller()
    {
        var appKey = UniqueKey("outer-rb");
        Guid id;

        using (var session = postgres.SessionFactory.OpenSession())
        {
            var service = new NotificationService(
                session,
                new TemplateRenderer(),
                new AuditService(session, new NullHttpContextAccessor(), TimeProvider.System),
                TimeProvider.System);
            using var outer = session.BeginTransaction();

            var result = await service.QueueHtmlAsync(
                appKey,
                new EmailRecipientRequest("ana@example.com", null),
                "Subject",
                "<p>x</p>",
                null,
                null,
                null,
                TestContext.Current.CancellationToken);

            Assert.True(result.IsSuccess);
            id = result.Value.Id;
            Assert.True(outer.IsActive);
            await session.FlushAsync(TestContext.Current.CancellationToken);
            await outer.RollbackAsync(TestContext.Current.CancellationToken);
        }

        using var verify = postgres.SessionFactory.OpenSession();
        Assert.Null(await verify.GetAsync<Notification>(id, TestContext.Current.CancellationToken));
        var entityId = id.ToString();
        Assert.Empty(await verify.Query<AuditLog>().Where(log => log.EntityId == entityId).ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task QueueHtmlAsync_InsideAnActiveTransaction_CommitsOnlyWhenTheCallerCommits()
    {
        var appKey = UniqueKey("outer-ok");
        Guid id;

        using (var session = postgres.SessionFactory.OpenSession())
        {
            var service = new NotificationService(
                session,
                new TemplateRenderer(),
                new AuditService(session, new NullHttpContextAccessor(), TimeProvider.System),
                TimeProvider.System);
            using var outer = session.BeginTransaction();

            var result = await service.QueueHtmlAsync(
                appKey,
                new EmailRecipientRequest("ana@example.com", null),
                "Subject",
                "<p>x</p>",
                null,
                null,
                null,
                TestContext.Current.CancellationToken);

            Assert.True(result.IsSuccess);
            id = result.Value.Id;
            Assert.True(outer.IsActive);

            await session.FlushAsync(TestContext.Current.CancellationToken);

            using (var uncommitted = postgres.SessionFactory.OpenSession())
            {
                Assert.Null(await uncommitted.GetAsync<Notification>(id, TestContext.Current.CancellationToken));
            }

            await outer.CommitAsync(TestContext.Current.CancellationToken);
        }

        using var verify = postgres.SessionFactory.OpenSession();
        var saved = await verify.GetAsync<Notification>(id, TestContext.Current.CancellationToken);
        Assert.NotNull(saved);
        Assert.Equal(appKey, saved.AppKey);
        var entityId = id.ToString();
        var audit = await verify.Query<AuditLog>().Where(log => log.EntityId == entityId).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(AuditActions.NotificationQueued, Assert.Single(audit).Action);
    }

    [Fact]
    public async Task List_WithEmptyStatus_AppliesNoFilter()
    {
        var app = await CreateAppAsync();
        var ids = await SeedAsync(app.AppKey, (BaseTime, false), (BaseTime.AddHours(1), true));

        var page = await ReadPageAsync(app, "/api/notifications?status=");

        Assert.Equal([ids[1], ids[0]], page.Ids);
        Assert.Equal(2, page.TotalCount);
    }

    [Fact]
    public async Task Post_WithTemplateSubjectContainingVariableValues_OmitsTheSubjectFromTheAudit()
    {
        var app = await CreateAppAsync();
        var templateKey = UniqueKey("code");
        var template = EmailTemplate.Create(
            templateKey,
            "Code",
            null,
            "Your code is {{Code}}",
            "<p>{{Code}}</p>",
            null,
            false).Value;

        using (var session = postgres.SessionFactory.OpenSession())
        using (var transaction = session.BeginTransaction())
        {
            await session.SaveAsync(template, TestContext.Current.CancellationToken);
            await transaction.CommitAsync(TestContext.Current.CancellationToken);
        }

        var response = await PostAsync(
            app,
            $$"""{"to":{"email":"ana@example.com"},"variables":{"Code":"SUBJECT-SECRET-482913"},"templateKey":"{{templateKey}}"}""");
        var id = await ReadQueuedIdAsync(response);

        var notification = await LoadNotificationAsync(id);
        Assert.NotNull(notification);
        Assert.Equal("Your code is SUBJECT-SECRET-482913", notification.Subject);

        using var verify = postgres.SessionFactory.OpenSession();
        var audit = await FindAuditAsync(verify, id);
        Assert.NotNull(audit.NewValues);
        Assert.DoesNotContain("SUBJECT-SECRET-482913", audit.NewValues, StringComparison.Ordinal);
        using var snapshot = JsonDocument.Parse(audit.NewValues);
        Assert.Equal(templateKey, snapshot.RootElement.GetProperty("templateKey").GetString());
        Assert.Equal(JsonValueKind.Null, snapshot.RootElement.GetProperty("subject").ValueKind);
    }

    [Fact]
    public async Task Post_WritesAuditRecordWithoutBodiesOrVariables()
    {
        var app = await CreateAppAsync();
        var correlationId = $"corr-{Guid.NewGuid():N}";

        var templateResponse = await PostAsync(
            app,
            """
            {"to":{"email":"ana@example.com"},"templateKey":"verify-email",
             "variables":{"UserName":"Ana","AppName":"Portal","VerificationUrl":"https://x.example/verify?token=SECRET-TOKEN-XYZ","ExpiresInHours":"24"},
             "notifyOnFailureTo":"ops@example.com"}
            """,
            correlationId);
        var templateId = await ReadQueuedIdAsync(templateResponse);

        var htmlResponse = await PostAsync(
            app,
            """{"to":{"email":"bob@example.com"},"subject":"Plain","html":"<p>BODY-SENTINEL</p>","text":"TEXT-SENTINEL"}""");
        var htmlId = await ReadQueuedIdAsync(htmlResponse);

        using var session = postgres.SessionFactory.OpenSession();
        var templateAudit = await FindAuditAsync(session, templateId);
        var htmlAudit = await FindAuditAsync(session, htmlId);

        Assert.Equal(AuditActions.NotificationQueued, templateAudit.Action);
        Assert.Equal("NOTIFICATION_QUEUED", templateAudit.Action);
        Assert.Equal("Notification", templateAudit.EntityType);
        Assert.Equal(templateId.ToString(), templateAudit.EntityId);
        Assert.Equal(app.AppKey, templateAudit.AppKey);
        Assert.Equal(AuditActorType.Application, templateAudit.ActorType);
        Assert.Equal(app.AppKey, templateAudit.ActorId);
        Assert.Equal(correlationId, templateAudit.CorrelationId);
        Assert.Null(templateAudit.OldValues);
        Assert.NotNull(templateAudit.NewValues);

        using var snapshot = JsonDocument.Parse(templateAudit.NewValues);
        var root = snapshot.RootElement;
        Assert.Equal(templateId.ToString(), root.GetProperty("id").GetString());
        Assert.Equal("ana@example.com", root.GetProperty("toEmail").GetString());
        Assert.Equal("verify-email", root.GetProperty("templateKey").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("subject").ValueKind);
        Assert.Equal("Pending", root.GetProperty("status").GetString());
        Assert.Equal("ops@example.com", root.GetProperty("notifyOnFailureTo").GetString());
        Assert.Equal(correlationId, root.GetProperty("correlationId").GetString());
        Assert.False(root.TryGetProperty("htmlBody", out _));
        Assert.False(root.TryGetProperty("textBody", out _));
        Assert.False(root.TryGetProperty("variables", out _));
        Assert.DoesNotContain("SECRET-TOKEN-XYZ", templateAudit.NewValues, StringComparison.Ordinal);

        Assert.NotNull(htmlAudit.NewValues);
        Assert.DoesNotContain("BODY-SENTINEL", htmlAudit.NewValues, StringComparison.Ordinal);
        using var htmlSnapshot = JsonDocument.Parse(htmlAudit.NewValues);
        Assert.Equal("Plain", htmlSnapshot.RootElement.GetProperty("subject").GetString());
        Assert.DoesNotContain("TEXT-SENTINEL", htmlAudit.NewValues, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Post_WhenValidationFails_WritesNeitherNotificationNorAudit()
    {
        var app = await CreateAppAsync();

        var response = await PostAsync(app, """{"to":{"email":"nope"},"subject":"S","html":"<p>x</p>"}""");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var session = postgres.SessionFactory.OpenSession();
        Assert.Empty(await session.Query<Notification>().Where(n => n.AppKey == app.AppKey).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await session.Query<AuditLog>().Where(a => a.AppKey == app.AppKey && a.Action == AuditActions.NotificationQueued).ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task QueueHtmlAsync_WhenTheAuditRecordFails_RollsBackTheNotification()
    {
        var appKey = UniqueKey("atomic");
        Guid? notificationId = null;

        using (var session = postgres.SessionFactory.OpenSession())
        {
            var audit = new FlushingFailingAuditService(session, id => notificationId = id);
            var service = new NotificationService(session, new TemplateRenderer(), audit, TimeProvider.System);

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.QueueHtmlAsync(
                appKey,
                new EmailRecipientRequest("ana@example.com", null),
                "Subject",
                "<p>x</p>",
                null,
                null,
                null,
                TestContext.Current.CancellationToken));
        }

        Assert.NotNull(notificationId);
        using var verifySession = postgres.SessionFactory.OpenSession();
        Assert.Null(await verifySession.GetAsync<Notification>(notificationId.Value, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetById_ForOwnNotification_Returns200WithoutBodies()
    {
        var app = await CreateAppAsync();
        var queued = await PostAsync(
            app,
            """{"to":{"email":"ana@example.com","name":"Ana"},"subject":"Invoice","html":"<p>BODY-SENTINEL</p>","text":"TEXT-SENTINEL","notifyOnFailureTo":"ops@example.com"}""",
            "corr-get-by-id");
        var id = await ReadQueuedIdAsync(queued);

        var response = await GetAsync(app, $"/api/notifications/{id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await GetBodyAsync(response);
        Assert.DoesNotContain("BODY-SENTINEL", body, StringComparison.Ordinal);
        Assert.DoesNotContain("TEXT-SENTINEL", body, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        Assert.False(root.TryGetProperty("htmlBody", out _));
        Assert.False(root.TryGetProperty("textBody", out _));
        Assert.Equal(id, root.GetProperty("id").GetGuid());
        Assert.Equal("ana@example.com", root.GetProperty("toEmail").GetString());
        Assert.Equal("Ana", root.GetProperty("toName").GetString());
        Assert.Equal("Invoice", root.GetProperty("subject").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("templateKey").ValueKind);
        Assert.Equal("Pending", root.GetProperty("status").GetString());
        Assert.Equal(0, root.GetProperty("attempts").GetInt32());
        Assert.Equal(3, root.GetProperty("maxAttempts").GetInt32());
        Assert.Equal(JsonValueKind.String, root.GetProperty("nextAttemptAtUtc").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("sentAtUtc").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("lastError").ValueKind);
        Assert.Equal("ops@example.com", root.GetProperty("notifyOnFailureTo").GetString());
        Assert.Equal("corr-get-by-id", root.GetProperty("correlationId").GetString());
        Assert.EndsWith("Z", root.GetProperty("createdAtUtc").GetString(), StringComparison.Ordinal);
        Assert.EndsWith("Z", root.GetProperty("updatedAtUtc").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetById_ForAnotherApplicationsOrUnknownNotification_Returns404WithIdenticalProblem()
    {
        var owner = await CreateAppAsync();
        var other = await CreateAppAsync();
        var queued = await PostAsync(owner, """{"to":{"email":"ana@example.com"},"subject":"S","html":"<p>x</p>"}""");
        var id = await ReadQueuedIdAsync(queued);

        var foreign = await GetAsync(other, $"/api/notifications/{id}");
        var unknown = await GetAsync(other, $"/api/notifications/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        var foreignProblem = await ReadProblemAsync(foreign);
        var unknownProblem = await ReadProblemAsync(unknown);
        Assert.Equal(NotificationErrors.NotFound.Code, foreignProblem.Extensions["errorCode"]?.ToString());
        Assert.Equal(foreignProblem.Extensions["errorCode"]?.ToString(), unknownProblem.Extensions["errorCode"]?.ToString());
        Assert.Equal(foreignProblem.Title, unknownProblem.Title);
        Assert.Equal(foreignProblem.Detail, unknownProblem.Detail);
    }

    [Fact]
    public async Task GetById_WithNonGuidId_Returns404()
    {
        var app = await CreateAppAsync();

        var response = await GetAsync(app, "/api/notifications/not-a-guid");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task List_ReturnsOnlyOwnNotificationsNewestFirstWithoutBodies()
    {
        var app = await CreateAppAsync();
        var other = await CreateAppAsync();
        var ids = await SeedAsync(
            app.AppKey,
            (BaseTime, false),
            (BaseTime.AddHours(1), true),
            (BaseTime.AddHours(2), false));
        await SeedAsync(other.AppKey, (BaseTime.AddHours(3), false));

        var response = await GetAsync(app, "/api/notifications");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await GetBodyAsync(response);
        Assert.DoesNotContain("secret html body", body, StringComparison.Ordinal);
        Assert.DoesNotContain("secret text body", body, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        Assert.Equal(1, root.GetProperty("page").GetInt32());
        Assert.Equal(50, root.GetProperty("pageSize").GetInt32());
        Assert.Equal(3, root.GetProperty("totalCount").GetInt32());
        var items = root.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal([ids[2], ids[1], ids[0]], items.Select(item => item.GetProperty("id").GetGuid()).ToList());

        foreach (var item in items)
        {
            Assert.False(item.TryGetProperty("htmlBody", out _));
            Assert.False(item.TryGetProperty("textBody", out _));
            Assert.Equal("user@example.com", item.GetProperty("toEmail").GetString());
            Assert.Equal("Ana", item.GetProperty("toName").GetString());
            Assert.Equal("ops@example.com", item.GetProperty("notifyOnFailureTo").GetString());
            Assert.Equal("corr-seed", item.GetProperty("correlationId").GetString());
        }

        Assert.Equal("Sent", items[1].GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.String, items[1].GetProperty("sentAtUtc").ValueKind);
        Assert.Equal("Pending", items[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task List_ForApplicationWithoutNotifications_ReturnsEmptyPage()
    {
        var app = await CreateAppAsync();

        var response = await GetAsync(app, "/api/notifications");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await GetBodyAsync(response));
        Assert.Equal(0, json.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Empty(json.RootElement.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task List_Paginates_WithStableOrderAndTotalCount()
    {
        var app = await CreateAppAsync();
        var ids = await SeedAsync(
            app.AppKey,
            (BaseTime, false),
            (BaseTime.AddHours(1), false),
            (BaseTime.AddHours(2), false),
            (BaseTime.AddHours(3), false),
            (BaseTime.AddHours(4), false));

        var first = await ReadPageAsync(app, "/api/notifications?page=1&pageSize=2");
        var second = await ReadPageAsync(app, "/api/notifications?page=2&pageSize=2");
        var third = await ReadPageAsync(app, "/api/notifications?page=3&pageSize=2");
        var beyond = await ReadPageAsync(app, "/api/notifications?page=4&pageSize=2");
        var huge = await ReadPageAsync(app, $"/api/notifications?page={int.MaxValue}&pageSize=200");

        Assert.Equal([ids[4], ids[3]], first.Ids);
        Assert.Equal([ids[2], ids[1]], second.Ids);
        Assert.Equal([ids[0]], third.Ids);
        Assert.Empty(beyond.Ids);
        Assert.Empty(huge.Ids);
        Assert.All(new[] { first, second, third, beyond, huge }, page => Assert.Equal(5, page.TotalCount));
        Assert.Equal(2, second.Page);
        Assert.Equal(2, second.PageSize);
        Assert.Equal(4, beyond.Page);
    }

    [Fact]
    public async Task List_WithSameCreatedAtUtc_OrdersByIdDescending()
    {
        var app = await CreateAppAsync();
        var ids = await SeedAsync(app.AppKey, (BaseTime, false), (BaseTime, false), (BaseTime, false));

        var page = await ReadPageAsync(app, "/api/notifications");

        Assert.Equal(ids.OrderByDescending(id => id).ToList(), page.Ids);
    }

    [Fact]
    public async Task List_FiltersByStatus()
    {
        var app = await CreateAppAsync();
        var ids = await SeedAsync(
            app.AppKey,
            (BaseTime, false),
            (BaseTime.AddHours(1), true),
            (BaseTime.AddHours(2), true));

        var sent = await ReadPageAsync(app, "/api/notifications?status=Sent");
        var pending = await ReadPageAsync(app, "/api/notifications?status=Pending");
        var failed = await ReadPageAsync(app, "/api/notifications?status=Failed");

        Assert.Equal([ids[2], ids[1]], sent.Ids);
        Assert.Equal(2, sent.TotalCount);
        Assert.Equal([ids[0]], pending.Ids);
        Assert.Empty(failed.Ids);
        Assert.Equal(0, failed.TotalCount);
    }

    [Fact]
    public async Task List_FiltersByDateRange_WithInclusiveFromAndExclusiveTo()
    {
        var app = await CreateAppAsync();
        var ids = await SeedAsync(
            app.AppKey,
            (BaseTime, false),
            (BaseTime.AddHours(1), false),
            (BaseTime.AddHours(2), false),
            (BaseTime.AddHours(3), false));
        var from = Uri.EscapeDataString(BaseTime.AddHours(1).ToString("O"));
        var to = Uri.EscapeDataString(BaseTime.AddHours(3).ToString("O"));

        var fromOnly = await ReadPageAsync(app, $"/api/notifications?from={from}");
        var toOnly = await ReadPageAsync(app, $"/api/notifications?to={to}");
        var both = await ReadPageAsync(app, $"/api/notifications?from={from}&to={to}");
        var offsetTo = await ReadPageAsync(
            app,
            $"/api/notifications?to={Uri.EscapeDataString(BaseTime.AddHours(3).ToOffset(TimeSpan.FromHours(-3)).ToString("O"))}");

        Assert.Equal([ids[3], ids[2], ids[1]], fromOnly.Ids);
        Assert.Equal([ids[2], ids[1], ids[0]], toOnly.Ids);
        Assert.Equal([ids[2], ids[1]], both.Ids);
        Assert.Equal(2, both.TotalCount);
        Assert.Equal([ids[2], ids[1], ids[0]], offsetTo.Ids);
    }

    [Fact]
    public async Task List_CombinesStatusAndDateFilters()
    {
        var app = await CreateAppAsync();
        var ids = await SeedAsync(
            app.AppKey,
            (BaseTime, true),
            (BaseTime.AddHours(1), false),
            (BaseTime.AddHours(2), true));
        var from = Uri.EscapeDataString(BaseTime.AddHours(1).ToString("O"));

        var page = await ReadPageAsync(app, $"/api/notifications?status=Sent&from={from}");

        Assert.Equal([ids[2]], page.Ids);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("pending")]
    [InlineData("PENDING")]
    [InlineData("Pending,Sent")]
    [InlineData("Bogus")]
    [InlineData("%20Pending")]
    public async Task List_WithInvalidStatus_Returns400InvalidStatus(string status)
    {
        var app = await CreateAppAsync();

        var response = await GetAsync(app, $"/api/notifications?status={status}");

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, NotificationErrors.InvalidStatus.Code);
    }

    [Theory]
    [InlineData("page=0", "Notifications.InvalidPage")]
    [InlineData("page=-1", "Notifications.InvalidPage")]
    [InlineData("pageSize=0", "Notifications.InvalidPageSize")]
    [InlineData("pageSize=-5", "Notifications.InvalidPageSize")]
    [InlineData("pageSize=201", "Notifications.InvalidPageSize")]
    [InlineData("from=2026-03-11T00:00:00Z&to=2026-03-10T00:00:00Z", "Notifications.InvalidDateRange")]
    public async Task List_WithInvalidPagingOrRange_Returns400WithTheirCodes(string query, string errorCode)
    {
        var app = await CreateAppAsync();

        var response = await GetAsync(app, $"/api/notifications?{query}");

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, errorCode);
    }

    [Theory]
    [InlineData("from=garbage")]
    [InlineData("to=2026-13-45")]
    [InlineData("page=abc")]
    [InlineData("pageSize=1.5")]
    public async Task List_WithUnparsableParameters_Returns400ProblemDetails(string query)
    {
        var app = await CreateAppAsync();

        var response = await GetAsync(app, $"/api/notifications?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await ReadProblemAsync(response);
    }

    [Fact]
    public async Task List_WithPageSizeAtTheMaximum_Returns200()
    {
        var app = await CreateAppAsync();

        var page = await ReadPageAsync(app, "/api/notifications?pageSize=200");

        Assert.Equal(200, page.PageSize);
    }

    [Theory]
    [InlineData("POST", "/api/notifications/email")]
    [InlineData("GET", "/api/notifications")]
    [InlineData("GET", "/api/notifications/3f2504e0-4f89-41d3-9a0c-0305e82c3301")]
    public async Task Endpoints_WithoutCredentials_Return401(string method, string url)
    {
        var json = method == "POST" ? """{"to":{"email":"a@example.com"},"subject":"S","html":"<p>x</p>"}""" : null;

        var response = await SendAsync(null, new HttpMethod(method), url, json);

        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, AppKeyAuthentication.InvalidCredentialsError.Code);
    }

    [Fact]
    public async Task Endpoints_WithWrongSecret_Return401AndQueueNothing()
    {
        var app = await CreateAppAsync();
        var wrong = app with { Secret = "wrong-secret-value" };

        var post = await PostAsync(wrong, """{"to":{"email":"a@example.com"},"subject":"S","html":"<p>x</p>"}""");
        var list = await GetAsync(wrong, "/api/notifications");

        Assert.Equal(HttpStatusCode.Unauthorized, post.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, list.StatusCode);
        using var session = postgres.SessionFactory.OpenSession();
        Assert.Empty(await session.Query<Notification>().Where(n => n.AppKey == app.AppKey).ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Endpoints_ForSuspendedApplication_Return403()
    {
        var app = await CreateAppAsync();
        using (var client = _factory.CreateClient())
        {
            var suspend = await client.PatchAsJsonAsync(
                $"/api/apps/{app.AppKey}/status",
                new UpdateApplicationStatusRequest("Suspended"),
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, suspend.StatusCode);
        }

        var post = await PostAsync(app, """{"to":{"email":"a@example.com"},"subject":"S","html":"<p>x</p>"}""");
        var list = await GetAsync(app, "/api/notifications");

        await AssertProblemAsync(post, HttpStatusCode.Forbidden, AppKeyAuthentication.ApplicationSuspendedError.Code);
        await AssertProblemAsync(list, HttpStatusCode.Forbidden, AppKeyAuthentication.ApplicationSuspendedError.Code);
        using var session = postgres.SessionFactory.OpenSession();
        Assert.Empty(await session.Query<Notification>().Where(n => n.AppKey == app.AppKey).ToListAsync(TestContext.Current.CancellationToken));
    }

    private static async Task<string> GetBodyAsync(HttpResponseMessage response) =>
        await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

    private static async Task<AuditLog> FindAuditAsync(ISession session, Guid notificationId)
    {
        var entityId = notificationId.ToString();
        var logs = await session.Query<AuditLog>()
            .Where(log => log.EntityType == "Notification" && log.EntityId == entityId)
            .ToListAsync(TestContext.Current.CancellationToken);
        return Assert.Single(logs);
    }

    private async Task<PageResult> ReadPageAsync(TestApp app, string url)
    {
        var response = await GetAsync(app, url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await GetBodyAsync(response));
        var root = json.RootElement;
        return new PageResult(
            root.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ToList(),
            root.GetProperty("page").GetInt32(),
            root.GetProperty("pageSize").GetInt32(),
            root.GetProperty("totalCount").GetInt64());
    }

    private sealed class NullHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    private sealed record TestApp(string AppKey, string Secret);

    private sealed record PageResult(List<Guid> Ids, int Page, int PageSize, long TotalCount);

    private sealed class FlushingFailingAuditService(ISession session, Action<Guid> onRecord) : IAuditService
    {
        public async Task<AuditLog> RecordAsync(AuditEntry entry, CancellationToken cancellationToken = default)
        {
            onRecord(Guid.Parse(entry.EntityId!));
            await session.FlushAsync(cancellationToken);
            throw new InvalidOperationException("The audit record failed.");
        }
    }
}
