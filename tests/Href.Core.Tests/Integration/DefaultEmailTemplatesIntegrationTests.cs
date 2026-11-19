using Href.Core.Api.Modules.Notifications;
using Npgsql;

namespace Href.Core.Tests.Integration;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class DefaultEmailTemplatesIntegrationTests(PostgresFixture postgres)
{
    private const string SeedResourceSuffix = "0004_seed_default_email_templates.sql";

    private static readonly IReadOnlyDictionary<string, string[]> DocumentedVariables = new Dictionary<string, string[]>
    {
        ["welcome-user"] = ["UserName", "AppName"],
        ["verify-email"] = ["UserName", "AppName", "VerificationUrl", "ExpiresInHours"],
        ["reset-password"] = ["UserName", "AppName", "ResetUrl", "ExpiresInMinutes"],
        ["delivery-failed-alert"] = ["OriginalTo", "OriginalSubject", "NotificationId", "Attempts", "LastError"],
    };

    private static readonly IReadOnlyDictionary<string, string> HostileValues = new Dictionary<string, string>
    {
        ["UserName"] = "Ana <b>\"Q\"</b> & O'Neil",
        ["AppName"] = "Mi App & <i>Co</i>",
        ["VerificationUrl"] = "https://example.com/verify?token=abc&user=1&x=\"y\"",
        ["ExpiresInHours"] = "24",
        ["ResetUrl"] = "https://example.com/reset?token=abc&user=1&x=\"y\"",
        ["ExpiresInMinutes"] = "30",
        ["OriginalTo"] = "dest@example.com",
        ["OriginalSubject"] = "Oferta \"única\" & <especial>",
        ["NotificationId"] = "0190a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b",
        ["Attempts"] = "3",
        ["LastError"] = "<script>alert(1)</script>\r\nSMTP 550: mailbox & \"unavailable\"",
    };

    public static TheoryData<string> TemplateKeys() => new(DocumentedVariables.Keys);

    private static Dictionary<string, string> VariablesFor(string key) =>
        DocumentedVariables[key].ToDictionary(name => name, name => HostileValues[name]);

    private static HashSet<string> Documented(string key) => DocumentedVariables[key].ToHashSet(StringComparer.Ordinal);

    private static string ReadSeedScript()
    {
        var assembly = typeof(EmailTemplate).Assembly;
        var resourceName = assembly.GetManifestResourceNames().Single(name => name.EndsWith(SeedResourceSuffix, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
    }

    private async Task<EmailTemplate> LoadAsync(string key)
    {
        using var session = postgres.SessionFactory.OpenSession();
        var template = await session.QueryOver<EmailTemplate>().Where(x => x.Key == key).SingleOrDefaultAsync();

        Assert.NotNull(template);

        return template;
    }

    [Fact]
    public async Task SystemTemplates_IncludeTheFourDefaultKeysExactlyOnce()
    {
        using var session = postgres.SessionFactory.OpenSession();
        var defaultKeys = DocumentedVariables.Keys.ToList();
        var seeded = await session.QueryOver<EmailTemplate>()
            .Where(x => x.IsSystem)
            .WhereRestrictionOn(x => x.Key).IsIn(defaultKeys)
            .ListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(defaultKeys.Order(StringComparer.Ordinal), seeded.Select(x => x.Key).Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(TemplateKeys))]
    public async Task SeededTemplate_ExistsAsSystemTemplateInSpanish(string key)
    {
        var template = await LoadAsync(key);

        Assert.True(template.IsSystem);
        Assert.Contains("<html lang=\"es\">", template.HtmlBody);
        Assert.Contains("<meta charset=\"utf-8\">", template.HtmlBody);
        Assert.False(string.IsNullOrWhiteSpace(template.TextBody));
        Assert.Matches("[áéíóúñÁÉÍÓÚÑ]", template.HtmlBody + template.TextBody);
        Assert.DoesNotContain("Ã", template.Name + template.Description + template.Subject + template.HtmlBody + template.TextBody);
        Assert.DoesNotContain('�', template.Name + template.Description + template.Subject + template.HtmlBody + template.TextBody);
    }

    [Theory]
    [MemberData(nameof(TemplateKeys))]
    public async Task SeededTemplate_PlaceholdersAreExactlyTheDocumentedVariables(string key)
    {
        var template = await LoadAsync(key);
        var renderer = new TemplateRenderer();

        var all = renderer.GetPlaceholders(template.Subject)
            .Concat(renderer.GetPlaceholders(template.HtmlBody))
            .Concat(renderer.GetPlaceholders(template.TextBody))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(Documented(key), all);
        Assert.Equal(Documented(key), renderer.GetPlaceholders(template.HtmlBody).ToHashSet(StringComparer.Ordinal));
        Assert.Equal(Documented(key), renderer.GetPlaceholders(template.TextBody).ToHashSet(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(TemplateKeys))]
    public async Task SeededTemplate_RendersWithHostileValuesAndPassesNotificationQueue(string key)
    {
        var template = await LoadAsync(key);

        var result = new TemplateRenderer().Render(template, VariablesFor(key));

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Code : null);
        var rendered = result.Value;
        Assert.DoesNotContain("{{", rendered.Subject + rendered.HtmlBody + rendered.TextBody);
        Assert.DoesNotContain("<script>", rendered.HtmlBody);

        var queued = Notification.Queue(
            "test-app",
            "user@example.com",
            null,
            rendered.Subject,
            rendered.HtmlBody,
            rendered.TextBody,
            key,
            null,
            null,
            TimeProvider.System);

        Assert.True(queued.IsSuccess, queued.IsFailure ? queued.Error.Code : null);
    }

    [Fact]
    public async Task SeededTemplates_EncodeHostileValuesInHtmlAndKeepThemRawInText()
    {
        var renderer = new TemplateRenderer();

        var alert = renderer.Render(await LoadAsync("delivery-failed-alert"), VariablesFor("delivery-failed-alert")).Value;
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", alert.HtmlBody);
        Assert.Contains("&quot;&#250;nica&quot; &amp; &lt;especial&gt;", alert.HtmlBody);
        Assert.Contains("<script>alert(1)</script>", alert.TextBody);

        var verify = renderer.Render(await LoadAsync("verify-email"), VariablesFor("verify-email")).Value;
        Assert.Contains("href=\"https://example.com/verify?token=abc&amp;user=1&amp;x=&quot;y&quot;\"", verify.HtmlBody);

        var reset = renderer.Render(await LoadAsync("reset-password"), VariablesFor("reset-password")).Value;
        Assert.Contains("href=\"https://example.com/reset?token=abc&amp;user=1&amp;x=&quot;y&quot;\"", reset.HtmlBody);

        var welcome = renderer.Render(await LoadAsync("welcome-user"), VariablesFor("welcome-user")).Value;
        Assert.Equal("Te damos la bienvenida a Mi App & <i>Co</i>", welcome.Subject);
        Assert.Contains("Mi App &amp; &lt;i&gt;Co&lt;/i&gt;", welcome.HtmlBody);
    }

    [Theory]
    [MemberData(nameof(TemplateKeys))]
    public async Task SeededTemplate_PassesEmailTemplateCreateValidation(string key)
    {
        var template = await LoadAsync(key);

        var result = EmailTemplate.Create(
            template.Key,
            template.Name,
            template.Description,
            template.Subject,
            template.HtmlBody,
            template.TextBody,
            template.IsSystem);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Code : null);
    }

    [Fact]
    public async Task SeedScript_DoesNotOverwriteEditsWhenExecutedAgain()
    {
        const string key = "welcome-user";
        const string editedName = "Edited by an administrator";
        var seedSql = ReadSeedScript();

        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await using (var update = new NpgsqlCommand("UPDATE email_templates SET name = @name WHERE key = @key", connection, transaction))
        {
            update.Parameters.AddWithValue("name", editedName);
            update.Parameters.AddWithValue("key", key);
            Assert.Equal(1, await update.ExecuteNonQueryAsync());
        }

        await using (var seed = new NpgsqlCommand(seedSql, connection, transaction))
        {
            await seed.ExecuteNonQueryAsync();
        }

        await using (var select = new NpgsqlCommand("SELECT name FROM email_templates WHERE key = @key", connection, transaction))
        {
            select.Parameters.AddWithValue("key", key);
            Assert.Equal(editedName, (string?)await select.ExecuteScalarAsync());
        }

        await using (var count = new NpgsqlCommand("SELECT count(*) FROM email_templates WHERE key = ANY(@keys)", connection, transaction))
        {
            count.Parameters.AddWithValue("keys", DocumentedVariables.Keys.ToArray());
            Assert.Equal((long)DocumentedVariables.Count, (long)(await count.ExecuteScalarAsync())!);
        }

        await transaction.RollbackAsync();
    }
}
