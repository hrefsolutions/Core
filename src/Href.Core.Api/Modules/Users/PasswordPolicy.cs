using Href.Core.Api.Common.Results;

namespace Href.Core.Api.Modules.Users;

/// <summary>
/// Password rules based on NIST SP 800-63B: length counted in Unicode code points,
/// no composition rules, no truncation and no whitespace trimming.
/// </summary>
public static class PasswordPolicy
{
    public const int MinLength = 10;
    public const int MaxLength = 128;

    public static Result Validate(string? password)
    {
        if (string.IsNullOrWhiteSpace(password)
            || password.Length > MaxLength * 2
            || password.Contains('\0'))
        {
            return UserErrors.WeakPassword;
        }

        var codePoints = password.EnumerateRunes().Count();

        return codePoints is >= MinLength and <= MaxLength
            ? Result.Success()
            : UserErrors.WeakPassword;
    }
}
