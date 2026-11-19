using System.Globalization;
using System.Net.Mail;

namespace Href.Core.Api.Common.Domain;

internal static class TextRules
{
    public static bool HasControlOrLineSeparator(string value)
    {
        foreach (var character in value)
        {
            if (char.IsControl(character)
                || char.GetUnicodeCategory(character) is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
            {
                return true;
            }
        }

        return false;
    }

    public static bool HasNul(string? value) => value is not null && value.Contains('\0');

    public static bool IsSingleEmailAddress(string value, int maxLength) =>
        value.Length > 0
        && value.Length <= maxLength
        && !HasControlOrLineSeparator(value)
        && MailAddress.TryCreate(value, out var parsed)
        && string.Equals(parsed.Address, value, StringComparison.Ordinal);
}
