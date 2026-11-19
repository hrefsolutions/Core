using Href.Core.Api.Modules.Users;

namespace Href.Core.Tests.Modules.Users;

public sealed class PasswordPolicyTests
{
    [Theory]
    [InlineData(10)]
    [InlineData(64)]
    [InlineData(128)]
    public void Validate_WithLengthWithinLimits_Succeeds(int length)
    {
        var result = PasswordPolicy.Validate(new string('a', length));

        Assert.True(result.IsSuccess);
    }

    [Theory]
    [InlineData(9)]
    [InlineData(129)]
    [InlineData(1000)]
    public void Validate_WithLengthOutsideLimits_ReturnsWeakPassword(int length)
    {
        var result = PasswordPolicy.Validate(new string('a', length));

        Assert.True(result.IsFailure);
        Assert.Equal(UserErrors.WeakPassword, result.Error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("            ")]
    [InlineData("\t\t\t\t\t\n\n\n\n\n\n")]
    public void Validate_WithMissingOrWhitespaceOnlyPassword_ReturnsWeakPassword(string? password)
    {
        var result = PasswordPolicy.Validate(password);

        Assert.True(result.IsFailure);
        Assert.Equal(UserErrors.WeakPassword, result.Error);
    }

    [Fact]
    public void Validate_WithNulCharacter_ReturnsWeakPassword()
    {
        var result = PasswordPolicy.Validate("abcdefghij\0klm");

        Assert.True(result.IsFailure);
        Assert.Equal(UserErrors.WeakPassword, result.Error);
    }

    [Fact]
    public void Validate_WithSpacesAndNoCompositionRules_Succeeds()
    {
        var result = PasswordPolicy.Validate("correct horse battery staple");

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void Validate_CountsCodePointsInsteadOfUtf16Units()
    {
        var fiveEmojis = string.Concat(Enumerable.Repeat("\U0001F600", 5));
        var tenEmojis = string.Concat(Enumerable.Repeat("\U0001F600", 10));
        var hundredTwentyEightEmojis = string.Concat(Enumerable.Repeat("\U0001F600", 128));

        Assert.Equal(10, fiveEmojis.Length);
        Assert.True(PasswordPolicy.Validate(fiveEmojis).IsFailure);
        Assert.True(PasswordPolicy.Validate(tenEmojis).IsSuccess);
        Assert.True(PasswordPolicy.Validate(hundredTwentyEightEmojis).IsSuccess);
    }
}
