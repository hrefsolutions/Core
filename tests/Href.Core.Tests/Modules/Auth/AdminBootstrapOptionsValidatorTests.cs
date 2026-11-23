using Href.Core.Api.Modules.Auth;

namespace Href.Core.Tests.Modules.Auth;

public sealed class AdminBootstrapOptionsValidatorTests
{
    private const string Password = "correct horse battery staple";
    private static readonly string Secret = new('s', AdminBootstrapOptions.CoreClientSecretMinLength);

    [Fact]
    public void Validate_WithNothingSet_Succeeds()
    {
        Assert.True(Validate(new AdminBootstrapOptions()).Succeeded);
    }

    [Fact]
    public void Validate_WithEverythingValid_Succeeds()
    {
        var result = Validate(new AdminBootstrapOptions
        {
            AdminEmail = "root@example.test",
            AdminPassword = Password,
            AdminFullName = "Root Admin",
            CoreClientSecret = Secret,
        });

        Assert.True(result.Succeeded, string.Join(" | ", result.Failures ?? []));
    }

    [Theory]
    [InlineData("root@example.test", null)]
    [InlineData(null, Password)]
    [InlineData("   ", Password)]
    public void Validate_WithOnlyTheEmailOrThePassword_Fails(string? email, string? password)
    {
        Assert.True(Validate(new AdminBootstrapOptions { AdminEmail = email, AdminPassword = password }).Failed);
    }

    [Fact]
    public void Validate_WithAnInvalidEmail_Fails()
    {
        Assert.True(Validate(new AdminBootstrapOptions { AdminEmail = "not an email", AdminPassword = Password }).Failed);
    }

    [Fact]
    public void Validate_WithAWeakPassword_FailsWithoutEchoingIt()
    {
        var result = Validate(new AdminBootstrapOptions { AdminEmail = "root@example.test", AdminPassword = "short" });

        Assert.True(result.Failed);
        Assert.DoesNotContain(result.Failures!, failure => failure.Contains("short", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Line\nBreak")]
    public void Validate_WithAnInvalidFullName_Fails(string fullName)
    {
        Assert.True(Validate(new AdminBootstrapOptions { AdminEmail = "root@example.test", AdminPassword = Password, AdminFullName = fullName }).Failed);
    }

    [Theory]
    [InlineData("short-secret")]
    [InlineData("secret with spaces that is long enough to pass")]
    public void Validate_WithAnInvalidCoreClientSecret_FailsWithoutEchoingIt(string secret)
    {
        var result = Validate(new AdminBootstrapOptions { CoreClientSecret = secret });

        Assert.True(result.Failed);
        Assert.DoesNotContain(result.Failures!, failure => failure.Contains(secret, StringComparison.Ordinal));
    }

    private static Microsoft.Extensions.Options.ValidateOptionsResult Validate(AdminBootstrapOptions options) =>
        new AdminBootstrapOptionsValidator().Validate(null, options);
}
