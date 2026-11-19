using Href.Core.Api.Modules.Notifications;

namespace Href.Core.Tests.Modules.Notifications;

public sealed class EmailTemplateTests
{
    private static Href.Core.Api.Common.Results.Result<EmailTemplate> CreateValid(
        string? key = "welcome-email",
        string? name = "Welcome",
        string? description = "Sent on sign-up",
        string? subject = "Hello {{Name}}",
        string? htmlBody = "<p>Hello {{Name}}</p>",
        string? textBody = "Hello {{Name}}",
        bool isSystem = true) =>
        EmailTemplate.Create(key, name, description, subject, htmlBody, textBody, isSystem);

    [Fact]
    public void Create_WithValidInputs_CreatesTemplate()
    {
        var result = CreateValid(key: " welcome-email ", name: " Welcome ", description: " Sent on sign-up ", subject: " Hello {{Name}} ");

        Assert.True(result.IsSuccess);
        var template = result.Value;
        Assert.Equal("welcome-email", template.Key);
        Assert.Equal("Welcome", template.Name);
        Assert.Equal("Sent on sign-up", template.Description);
        Assert.Equal("Hello {{Name}}", template.Subject);
        Assert.Equal("<p>Hello {{Name}}</p>", template.HtmlBody);
        Assert.Equal("Hello {{Name}}", template.TextBody);
        Assert.True(template.IsSystem);
    }

    [Fact]
    public void Create_WithBlankOptionals_LeavesThemNull()
    {
        var result = CreateValid(description: "  ", textBody: "", isSystem: false);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value.Description);
        Assert.Null(result.Value.TextBody);
        Assert.False(result.Value.IsSystem);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ab")]
    [InlineData("UpperCase")]
    [InlineData("-leading")]
    [InlineData("trailing-")]
    [InlineData("double--hyphen")]
    [InlineData("has_underscore")]
    [InlineData("has space")]
    [InlineData("new\nline")]
    [InlineData("a\0b-c")]
    [InlineData("abc\U00002028def")]
    [InlineData("abc\tdef")]
    public void Create_WithInvalidKey_ReturnsInvalidKeyError(string? key)
    {
        var result = CreateValid(key: key);

        Assert.True(result.IsFailure);
        Assert.Equal(EmailTemplateErrors.InvalidKey, result.Error);
    }

    [Fact]
    public void Create_WithTooLongKey_ReturnsInvalidKeyError()
    {
        var result = CreateValid(key: new string('k', 101));

        Assert.True(result.IsFailure);
        Assert.Equal(EmailTemplateErrors.InvalidKey, result.Error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithBlankName_ReturnsInvalidNameError(string? name)
    {
        var result = CreateValid(name: name);

        Assert.True(result.IsFailure);
        Assert.Equal(EmailTemplateErrors.InvalidName, result.Error);
    }

    [Fact]
    public void Create_WithTooLongName_ReturnsInvalidNameError()
    {
        var result = CreateValid(name: new string('n', 101));

        Assert.True(result.IsFailure);
        Assert.Equal(EmailTemplateErrors.InvalidName, result.Error);
    }

    [Fact]
    public void Create_WithTooLongDescription_ReturnsInvalidDescriptionError()
    {
        var result = CreateValid(description: new string('d', 501));

        Assert.True(result.IsFailure);
        Assert.Equal(EmailTemplateErrors.InvalidDescription, result.Error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("line1\nline2")]
    [InlineData("line1\rline2")]
    [InlineData("line1\r\nBcc: x@y.co")]
    [InlineData("a\0b")]
    [InlineData("\0")]
    [InlineData("a\u0085b")]
    [InlineData("a\U00002028b")]
    [InlineData("a\U00002029b")]
    [InlineData("a\tb")]
    public void Create_WithInvalidSubject_ReturnsInvalidSubjectError(string? subject)
    {
        var result = CreateValid(subject: subject);

        Assert.True(result.IsFailure);
        Assert.Equal(EmailTemplateErrors.InvalidSubject, result.Error);
    }

    [Fact]
    public void Create_WithTooLongSubject_ReturnsInvalidSubjectError()
    {
        var result = CreateValid(subject: new string('s', 501));

        Assert.True(result.IsFailure);
        Assert.Equal(EmailTemplateErrors.InvalidSubject, result.Error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithBlankHtmlBody_ReturnsInvalidHtmlBodyError(string? htmlBody)
    {
        var result = CreateValid(htmlBody: htmlBody);

        Assert.True(result.IsFailure);
        Assert.Equal(EmailTemplateErrors.InvalidHtmlBody, result.Error);
    }

    [Theory]
    [InlineData("<p>a\0b</p>")]
    [InlineData("\0")]
    public void Create_WithNulInHtmlBody_ReturnsInvalidHtmlBodyError(string htmlBody)
    {
        var result = CreateValid(htmlBody: htmlBody);

        Assert.True(result.IsFailure);
        Assert.Equal(EmailTemplateErrors.InvalidHtmlBody, result.Error);
    }

    [Theory]
    [InlineData("te\0xt")]
    [InlineData("\0")]
    public void Create_WithNulInTextBody_ReturnsInvalidTextBodyError(string textBody)
    {
        var result = CreateValid(textBody: textBody);

        Assert.True(result.IsFailure);
        Assert.Equal(EmailTemplateErrors.InvalidTextBody, result.Error);
    }

    [Theory]
    [InlineData("Na\0me")]
    [InlineData("Na\U00002028me")]
    public void Create_WithControlCharsInName_ReturnsInvalidNameError(string name)
    {
        var result = CreateValid(name: name);

        Assert.True(result.IsFailure);
        Assert.Equal(EmailTemplateErrors.InvalidName, result.Error);
    }

    [Fact]
    public void Create_WithNulInDescription_ReturnsInvalidDescriptionError()
    {
        var result = CreateValid(description: "de\0sc");

        Assert.True(result.IsFailure);
        Assert.Equal(EmailTemplateErrors.InvalidDescription, result.Error);
    }

    [Fact]
    public void UpdateContent_WithNulInAnyField_ReturnsErrorAndKeepsState()
    {
        var template = CreateValid().Value;

        Assert.Equal(EmailTemplateErrors.InvalidName, template.UpdateContent("N\0", null, "S", "<p>x</p>", null).Error);
        Assert.Equal(EmailTemplateErrors.InvalidSubject, template.UpdateContent("N", null, "S\0", "<p>x</p>", null).Error);
        Assert.Equal(EmailTemplateErrors.InvalidHtmlBody, template.UpdateContent("N", null, "S", "<p>\0</p>", null).Error);
        Assert.Equal(EmailTemplateErrors.InvalidTextBody, template.UpdateContent("N", null, "S", "<p>x</p>", "\0").Error);

        Assert.Equal("Welcome", template.Name);
        Assert.Equal("<p>Hello {{Name}}</p>", template.HtmlBody);
    }

    [Fact]
    public void UpdateContent_WithValidInputs_ChangesContentButNotKeyOrIsSystem()
    {
        var template = CreateValid(isSystem: true).Value;

        var result = template.UpdateContent(" New name ", null, "New subject", "<p>New</p>", "");

        Assert.True(result.IsSuccess);
        Assert.Equal("welcome-email", template.Key);
        Assert.True(template.IsSystem);
        Assert.Equal("New name", template.Name);
        Assert.Null(template.Description);
        Assert.Equal("New subject", template.Subject);
        Assert.Equal("<p>New</p>", template.HtmlBody);
        Assert.Null(template.TextBody);
    }

    [Fact]
    public void UpdateContent_WithInvalidInput_ReturnsErrorAndKeepsState()
    {
        var template = CreateValid().Value;

        Assert.Equal(EmailTemplateErrors.InvalidName, template.UpdateContent(" ", null, "S", "<p>x</p>", null).Error);
        Assert.Equal(EmailTemplateErrors.InvalidDescription, template.UpdateContent("N", new string('d', 501), "S", "<p>x</p>", null).Error);
        Assert.Equal(EmailTemplateErrors.InvalidSubject, template.UpdateContent("N", null, "a\nb", "<p>x</p>", null).Error);
        Assert.Equal(EmailTemplateErrors.InvalidHtmlBody, template.UpdateContent("N", null, "S", " ", null).Error);

        Assert.Equal("Welcome", template.Name);
        Assert.Equal("Sent on sign-up", template.Description);
        Assert.Equal("Hello {{Name}}", template.Subject);
        Assert.Equal("<p>Hello {{Name}}</p>", template.HtmlBody);
        Assert.Equal("Hello {{Name}}", template.TextBody);
    }

    [Theory]
    [InlineData("welcome-user", true)]
    [InlineData("abc", true)]
    [InlineData("ab", false)]
    [InlineData("Welcome-User", false)]
    [InlineData("welcome_user", false)]
    [InlineData(" welcome-user", false)]
    [InlineData("welcome-user\n", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValidKey_AcceptsOnlyExactLowercaseSlugs(string? key, bool expected)
    {
        Assert.Equal(expected, EmailTemplate.IsValidKey(key));
    }

    [Fact]
    public void IsValidKey_RejectsKeysOverTheMaximumLength()
    {
        Assert.True(EmailTemplate.IsValidKey(new string('a', EmailTemplate.KeyMaxLength)));
        Assert.False(EmailTemplate.IsValidKey(new string('a', EmailTemplate.KeyMaxLength + 1)));
    }

    [Fact]
    public void Create_AndUpdateContent_RejectBodiesOverTheMaximumLength()
    {
        var atLimit = new string('a', EmailTemplate.BodyMaxLength);
        var overLimit = new string('a', EmailTemplate.BodyMaxLength + 1);
        var template = CreateValid(htmlBody: atLimit, textBody: atLimit, isSystem: false).Value;

        Assert.Equal(EmailTemplateErrors.ContentTooLarge, CreateValid(htmlBody: overLimit).Error);
        Assert.Equal(EmailTemplateErrors.ContentTooLarge, CreateValid(textBody: overLimit).Error);
        Assert.Equal(EmailTemplateErrors.ContentTooLarge, template.UpdateContent("N", null, "S", overLimit, null).Error);
        Assert.Equal(EmailTemplateErrors.ContentTooLarge, template.UpdateContent("N", null, "S", "<p>x</p>", overLimit).Error);
        Assert.Equal(atLimit, template.HtmlBody);
    }

    [Fact]
    public void ValidateContent_AppliesTheUpdateRulesWithoutChangingAnything()
    {
        Assert.True(EmailTemplate.ValidateContent("N", null, "S", "<p>x</p>", null).IsSuccess);
        Assert.Equal(EmailTemplateErrors.InvalidName, EmailTemplate.ValidateContent("  ", null, "S", "<p>x</p>", null).Error);
        Assert.Equal(EmailTemplateErrors.InvalidSubject, EmailTemplate.ValidateContent("N", null, "S\nT", "<p>x</p>", null).Error);
        Assert.Equal(EmailTemplateErrors.InvalidHtmlBody, EmailTemplate.ValidateContent("N", null, "S", " ", null).Error);
    }
}
