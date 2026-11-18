using System.Globalization;

namespace Href.Core.Api.Modules.Notifications;

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
}
