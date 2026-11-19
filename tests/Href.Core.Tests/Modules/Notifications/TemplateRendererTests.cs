using Href.Core.Api.Modules.Notifications;

namespace Href.Core.Tests.Modules.Notifications;

public sealed class TemplateRendererTests
{
    private readonly TemplateRenderer _renderer = new();

    private static Dictionary<string, string> Vars(params (string Key, string Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value);

    [Fact]
    public void Render_ReplacesPlaceholders()
    {
        var result = _renderer.Render("Hello {{Name}}, see {{Url}}", Vars(("Name", "Ana"), ("Url", "https://x.test")), htmlEncode: false);

        Assert.True(result.IsSuccess);
        Assert.Equal("Hello Ana, see https://x.test", result.Value);
    }

    [Fact]
    public void Render_AllowsWhitespaceInsideBraces()
    {
        var result = _renderer.Render("{{ Name }}|{{  Name}}|{{Name  }}|{{\tName\n}}", Vars(("Name", "Ana")), htmlEncode: false);

        Assert.Equal("Ana|Ana|Ana|Ana", result.Value);
    }

    [Fact]
    public void Render_LooksUpKeysCaseInsensitively()
    {
        var result = _renderer.Render("{{NAME}} {{name}} {{Name}}", Vars(("nAmE", "Ana")), htmlEncode: false);

        Assert.Equal("Ana Ana Ana", result.Value);
    }

    [Fact]
    public void Render_SupportsDotsAndUnderscoresInKeys()
    {
        var result = _renderer.Render("{{a.b_c}} {{user.first_name}}", Vars(("a.b_c", "1"), ("user.first_name", "Ana")), htmlEncode: false);

        Assert.Equal("1 Ana", result.Value);
    }

    [Fact]
    public void Render_RepeatedPlaceholder_IsReplacedEverywhere()
    {
        var text = string.Concat(Enumerable.Repeat("{{N}},", 1000));

        var result = _renderer.Render(text, Vars(("N", "x")), htmlEncode: false);

        Assert.Equal(string.Concat(Enumerable.Repeat("x,", 1000)), result.Value);
    }

    [Fact]
    public void Render_TextWithoutPlaceholders_IsUnchanged()
    {
        const string text = "Plain text with { braces } and {single}";

        var plain = _renderer.Render(text, Vars(("Name", "Ana")), htmlEncode: false);
        var html = _renderer.Render(text, null, htmlEncode: true);

        Assert.Equal(text, plain.Value);
        Assert.Equal(text, html.Value);
    }

    [Fact]
    public void Render_ExtraVariables_AreIgnored()
    {
        var result = _renderer.Render("Hi {{Name}}", Vars(("Name", "Ana"), ("Unused", "x"), ("Other", "y")), htmlEncode: false);

        Assert.True(result.IsSuccess);
        Assert.Equal("Hi Ana", result.Value);
    }

    [Theory]
    [InlineData("{{ bad key }}")]
    [InlineData("{{}}")]
    [InlineData("{{ }}")]
    [InlineData("{Name}")]
    [InlineData("{{Na-me}}")]
    [InlineData("{{{{")]
    [InlineData("{{Name")]
    public void Render_InvalidPlaceholderSyntax_StaysLiteral(string text)
    {
        var result = _renderer.Render(text, Vars(("Name", "Ana"), ("Na-me", "x")), htmlEncode: false);

        Assert.True(result.IsSuccess);
        Assert.Equal(text, result.Value);
    }

    [Fact]
    public void Render_SinglePass_DoesNotRenderValuesRecursively()
    {
        var result = _renderer.Render("{{A}}", Vars(("A", "{{B}}"), ("B", "boom")), htmlEncode: false);

        Assert.Equal("{{B}}", result.Value);
    }

    [Fact]
    public void Render_ValueEqualToItsOwnPlaceholder_IsInsertedLiterally()
    {
        var plain = _renderer.Render("x {{Name}} y", Vars(("Name", "{{Name}}")), htmlEncode: false);
        var html = _renderer.Render("x {{Name}} y", Vars(("Name", "{{Name}}")), htmlEncode: true);

        Assert.Equal("x {{Name}} y", plain.Value);
        Assert.Equal("x {{Name}} y", html.Value);
    }

    [Fact]
    public void Render_ValueWithPlaceholderForAnotherVariable_IsNotRenderedInHtml()
    {
        var result = _renderer.Render("<p>{{A}}</p>", Vars(("A", "{{B}}"), ("B", "<b>")), htmlEncode: true);

        Assert.Equal("<p>{{B}}</p>", result.Value);
    }

    [Fact]
    public void Render_HtmlEncode_EncodesValuesButNotTemplateMarkup()
    {
        var result = _renderer.Render("<p>{{V}}</p>", Vars(("V", "<script>alert(\"x\") & 'y'</script>")), htmlEncode: true);

        Assert.Equal("<p>&lt;script&gt;alert(&quot;x&quot;) &amp; &#39;y&#39;&lt;/script&gt;</p>", result.Value);
    }

    [Fact]
    public void Render_WithoutHtmlEncode_KeepsValueAsIs()
    {
        var result = _renderer.Render("{{V}}", Vars(("V", "<b>&\"'")), htmlEncode: false);

        Assert.Equal("<b>&\"'", result.Value);
    }

    [Fact]
    public void Render_Email_EncodesOnlyInHtmlBody()
    {
        const string value = "<script>x</script> & \"q\"";

        var result = _renderer.Render("Hi {{V}}", "<p>{{V}}</p>", "Text {{V}}", Vars(("V", value)));

        Assert.True(result.IsSuccess);
        Assert.Equal("Hi " + value, result.Value.Subject);
        Assert.Equal("<p>&lt;script&gt;x&lt;/script&gt; &amp; &quot;q&quot;</p>", result.Value.HtmlBody);
        Assert.Equal("Text " + value, result.Value.TextBody);
    }

    [Fact]
    public void Render_Template_DelegatesToPartsOverload()
    {
        var template = EmailTemplate.Create(
            "welcome-email", "Welcome", null, "Hi {{Name}}", "<p>{{Name}}</p>", "Text {{Name}}", false).Value;

        var result = _renderer.Render(template, Vars(("Name", "A&B")));

        Assert.True(result.IsSuccess);
        Assert.Equal("Hi A&B", result.Value.Subject);
        Assert.Equal("<p>A&amp;B</p>", result.Value.HtmlBody);
        Assert.Equal("Text A&B", result.Value.TextBody);
    }

    [Fact]
    public void Render_Email_NullTextBody_RendersNullTextBody()
    {
        var result = _renderer.Render("Hi {{Name}}", "<p>{{Name}}</p>", null, Vars(("Name", "Ana")));

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value.TextBody);
    }

    [Fact]
    public void Render_Email_MissingVariables_AreListedDeduplicatedInOrder()
    {
        var result = _renderer.Render(
            "{{Zeta}} {{Alpha}}",
            "{{alpha}} {{Url}} {{Present}} {{Name}}",
            "{{URL}} {{Last}} {{zeta}}",
            Vars(("Present", "x")));

        Assert.True(result.IsFailure);
        Assert.Equal("Templates.MissingVariables", result.Error.Code);
        Assert.Equal("Missing variables: Zeta, Alpha, Url, Name, Last", result.Error.Message);
    }

    [Fact]
    public void Render_MissingVariables_ReturnsValidationError()
    {
        var result = _renderer.Render("{{Name}} {{Url}}", Vars(), htmlEncode: false);

        Assert.True(result.IsFailure);
        Assert.Equal(Href.Core.Api.Common.Results.ErrorType.Validation, result.Error.Type);
        Assert.Equal("Missing variables: Name, Url", result.Error.Message);
    }

    [Fact]
    public void Render_NullVariables_ActAsEmpty()
    {
        var withoutPlaceholders = _renderer.Render("Hello", null, htmlEncode: false);
        var withPlaceholders = _renderer.Render("Hello {{Name}}", null, htmlEncode: false);

        Assert.Equal("Hello", withoutPlaceholders.Value);
        Assert.Equal("Templates.MissingVariables", withPlaceholders.Error.Code);
    }

    [Fact]
    public void Render_NullValue_CountsAsMissing()
    {
        var variables = new Dictionary<string, string> { ["Name"] = null! };

        var result = _renderer.Render("Hi {{Name}}", variables, htmlEncode: false);

        Assert.True(result.IsFailure);
        Assert.Equal("Missing variables: Name", result.Error.Message);
    }

    [Fact]
    public void Render_EmptyValue_IsNotMissing()
    {
        var result = _renderer.Render("[{{Name}}]", Vars(("Name", "")), htmlEncode: false);

        Assert.Equal("[]", result.Value);
    }

    [Fact]
    public void Render_KeysDifferingOnlyByCase_ReturnDuplicateVariablesError()
    {
        var result = _renderer.Render("{{Name}}", Vars(("name", "a"), ("Name", "b")), htmlEncode: false);

        Assert.True(result.IsFailure);
        Assert.Equal("Templates.DuplicateVariables", result.Error.Code);
        Assert.Equal("Duplicate variables (case-insensitive): Name, name", result.Error.Message);
    }

    [Fact]
    public void Render_DuplicateVariables_AreSortedOrdinallyAndCappedAtTen()
    {
        var pairs = Enumerable.Range(0, 12)
            .SelectMany(i => new[] { ("k" + i.ToString("D2"), "a"), ("K" + i.ToString("D2"), "b") })
            .OrderByDescending(pair => pair.Item1, StringComparer.Ordinal)
            .ToArray();

        var result = _renderer.Render("x", Vars(pairs), htmlEncode: false);

        Assert.Equal("Templates.DuplicateVariables", result.Error.Code);
        var expected = string.Join(", ", Enumerable.Range(0, 10).Select(i => "K" + i.ToString("D2")));
        Assert.Equal("Duplicate variables (case-insensitive): " + expected, result.Error.Message);
    }

    [Fact]
    public void Render_DuplicateVariables_TruncatesEachKeyTo100Characters()
    {
        var lower = new string('a', 150);
        var upper = new string('A', 150);

        var result = _renderer.Render("x", Vars((lower, "1"), (upper, "2")), htmlEncode: false);

        Assert.Equal(
            "Duplicate variables (case-insensitive): " + new string('A', 100) + ", " + new string('a', 100),
            result.Error.Message);
    }

    [Fact]
    public void Render_InvalidVariableKeys_AreIgnoredLikeExtras()
    {
        var result = _renderer.Render("{{Name}}", Vars((" Name", "Ana"), ("", "x"), ("Na me", "y"), ("Name\n", "z")), htmlEncode: false);

        Assert.True(result.IsFailure);
        Assert.Equal("Missing variables: Name", result.Error.Message);
    }

    [Fact]
    public void Render_PaddedKeysDifferingByCase_AreIgnoredInsteadOfReportedAsDuplicates()
    {
        var result = _renderer.Render("{{Name}}", Vars(("name ", "a"), ("NAME ", "b"), ("Name", "Ana")), htmlEncode: false);

        Assert.True(result.IsSuccess);
        Assert.Equal("Ana", result.Value);
    }

    [Fact]
    public void Render_WithNegativeMaxLength_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _renderer.Render("x", Vars(), htmlEncode: false, maxLength: -1));
    }

    [Fact]
    public void Render_InvalidVariableKeysDifferingByCase_AreNotDuplicates()
    {
        var result = _renderer.Render("{{Name}}", Vars(("Na me", "a"), ("NA ME", "b"), ("Name", "Ana")), htmlEncode: false);

        Assert.True(result.IsSuccess);
        Assert.Equal("Ana", result.Value);
    }

    [Fact]
    public void Render_TripleBraces_RenderInnerPlaceholderOnly()
    {
        var result = _renderer.Render("{{{Name}}}", Vars(("Name", "Ana")), htmlEncode: false);

        Assert.Equal("{Ana}", result.Value);
    }

    [Fact]
    public void Render_Emoji_StaysRawInSubjectAndTextAndIsEncodedInHtml()
    {
        const string emoji = "\U0001F600";

        var result = _renderer.Render("{{V}}", "<p>{{V}}</p>", "{{V}}", Vars(("V", emoji)));

        Assert.Equal(emoji, result.Value.Subject);
        Assert.Equal(emoji, result.Value.TextBody);
        Assert.Equal("<p>&#128512;</p>", result.Value.HtmlBody);
        Assert.Contains("&#", result.Value.HtmlBody);
        Assert.DoesNotContain(emoji, result.Value.HtmlBody);
    }

    [Fact]
    public void Render_NullSubjectOrHtmlBody_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => _renderer.Render(null!, "<p>x</p>", null, Vars()));
        Assert.Throws<ArgumentNullException>(() => _renderer.Render("s", null!, null, Vars()));
        Assert.Throws<ArgumentNullException>(() => _renderer.Render(null!, Vars(), htmlEncode: false));
    }

    [Fact]
    public void Render_HugeValueRepeated_ReturnsRenderedContentTooLarge()
    {
        var value = new string('a', 100_000);

        var result = _renderer.Render("s", string.Concat(Enumerable.Repeat("{{V}}", 6)), null, Vars(("V", value)));

        Assert.True(result.IsFailure);
        Assert.Equal("Templates.RenderedContentTooLarge", result.Error.Code);
        Assert.Equal("Rendered HTML body exceeds 524288 characters.", result.Error.Message);
    }

    [Fact]
    public void Render_HtmlEncodingThatExpandsPastTheLimit_ReturnsRenderedContentTooLarge()
    {
        var value = new string('&', 200_000);

        var result = _renderer.Render("s", "{{V}}", null, Vars(("V", value)));

        Assert.Equal("Templates.RenderedContentTooLarge", result.Error.Code);
    }

    [Fact]
    public void Render_SingleValueLargerThanLimit_ReturnsRenderedContentTooLarge()
    {
        var value = new string('a', TemplateRenderer.MaxRenderedBodyLength + 1);

        var result = _renderer.Render("{{V}}", Vars(("V", value)), htmlEncode: false);

        Assert.Equal("Templates.RenderedContentTooLarge", result.Error.Code);
    }

    [Fact]
    public void Render_TextBodyExceedingLimit_ReturnsRenderedContentTooLarge()
    {
        var value = new string('a', 300_000);

        var result = _renderer.Render("s", "<p>x</p>", "{{V}}{{V}}", Vars(("V", value)));

        Assert.Equal("Rendered text body exceeds 524288 characters.", result.Error.Message);
    }

    [Fact]
    public void Render_BodyExactlyAtLimit_Succeeds()
    {
        var text = new string('a', TemplateRenderer.MaxRenderedBodyLength - 5) + "{{V}}";

        var result = _renderer.Render(text, Vars(("V", "12345")), htmlEncode: false);

        Assert.True(result.IsSuccess);
        Assert.Equal(TemplateRenderer.MaxRenderedBodyLength, result.Value.Length);
    }

    [Fact]
    public void Render_BodyOneOverLimit_Fails()
    {
        var text = new string('a', TemplateRenderer.MaxRenderedBodyLength - 5) + "{{V}}";

        var result = _renderer.Render(text, Vars(("V", "123456")), htmlEncode: false);

        Assert.Equal("Templates.RenderedContentTooLarge", result.Error.Code);
    }

    [Fact]
    public void Render_CustomMaxLength_IsHonored()
    {
        var result = _renderer.Render("{{V}}", Vars(("V", "abcdef")), htmlEncode: false, maxLength: 5);

        Assert.Equal("Rendered text exceeds 5 characters.", result.Error.Message);
    }

    [Fact]
    public void Render_SubjectOf500Characters_Succeeds()
    {
        var result = _renderer.Render("{{V}}", "<p>x</p>", null, Vars(("V", new string('a', 500))));

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void Render_SubjectOf501Characters_ReturnsRenderedContentTooLarge()
    {
        var result = _renderer.Render("{{V}}", "<p>x</p>", null, Vars(("V", new string('a', 501))));

        Assert.True(result.IsFailure);
        Assert.Equal("Templates.RenderedContentTooLarge", result.Error.Code);
        Assert.Equal("Rendered subject exceeds 500 characters.", result.Error.Message);
    }

    [Fact]
    public void Render_MissingVariables_TakePrecedenceOverSizeLimit()
    {
        var value = new string('a', TemplateRenderer.MaxRenderedBodyLength + 1);

        var result = _renderer.Render("{{V}}", "{{Missing}}", null, Vars(("V", value)));

        Assert.Equal("Templates.MissingVariables", result.Error.Code);
    }

    [Fact]
    public void Render_HostileSubjectValue_ProducesSubjectAcceptedByNotificationQueue()
    {
        var hostile = "a\rb\nc\0d\U00000085e\U00002028f";

        var rendered = _renderer.Render("Hi {{V}}", "<p>x</p>", null, Vars(("V", hostile)));
        var queued = Notification.Queue(
            "app", "a@b.co", null, rendered.Value.Subject, rendered.Value.HtmlBody, null, null, null, null, TimeProvider.System);

        Assert.True(queued.IsSuccess);
        Assert.Equal("Hi a b c d e f", queued.Value.Subject);
    }

    [Fact]
    public void Render_Email_Subject_CollapsesLineBreakingCharactersToSpaces()
    {
        var value = "a\rb\nc\td\0e\U00002028f\U00002029g\U00000085h\U0000007Fi";

        var result = _renderer.Render("[{{V}}]", "<p>x</p>", null, Vars(("V", value)));

        Assert.True(result.IsSuccess);
        Assert.Equal("[a b c d e f g h i]", result.Value.Subject);
    }

    [Fact]
    public void Render_Email_Bodies_KeepNewlines()
    {
        const string value = "line1\r\nline2\nline3";

        var result = _renderer.Render("s", "<p>{{V}}</p>", "{{V}}", Vars(("V", value)));

        Assert.Equal("<p>line1\r\nline2\nline3</p>", result.Value.HtmlBody);
        Assert.Equal(value, result.Value.TextBody);
    }

    [Fact]
    public void Render_Email_SubjectLiteralTemplateText_IsNotCollapsed()
    {
        var result = _renderer.Render("a\tb {{V}}", "<p>x</p>", null, Vars(("V", "v")));

        Assert.Equal("a\tb v", result.Value.Subject);
    }

    [Fact]
    public void GetPlaceholders_ReturnsDistinctKeysInOrderOfFirstAppearance()
    {
        var keys = _renderer.GetPlaceholders("{{B}} {{ a }} {{b}} {{C.d}} {{A}} {{ bad key }}");

        Assert.Equal(new[] { "B", "a", "C.d" }, keys);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no placeholders")]
    public void GetPlaceholders_WithoutPlaceholders_ReturnsEmpty(string? text)
    {
        Assert.Empty(_renderer.GetPlaceholders(text));
    }
}
