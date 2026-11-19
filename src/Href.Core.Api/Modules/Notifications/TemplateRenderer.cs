using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Href.Core.Api.Common.Results;

namespace Href.Core.Api.Modules.Notifications;

/// <summary>
/// Stateless renderer for <c>{{Key}}</c> placeholders. Rendering is a single pass: values are inserted literally and never re-rendered.
/// Anything that does not match the placeholder syntax stays untouched. The renderer does not trim values.
/// Rendering stops as soon as a part exceeds its length limit, so allocation stays bounded to roughly the limit plus one value.
/// </summary>
public sealed partial class TemplateRenderer : ITemplateRenderer
{
    /// <summary>
    /// Maximum rendered length, in characters, of the HTML body and of the text body.
    /// </summary>
    public const int MaxRenderedBodyLength = 512 * 1024;

    /// <summary>
    /// Maximum rendered length, in characters, of the subject.
    /// </summary>
    public const int MaxRenderedSubjectLength = Notification.SubjectMaxLength;

    private const int MaxReportedDuplicates = 10;
    private const int MaxReportedKeyLength = 100;

    [GeneratedRegex(@"\{\{\s*([A-Za-z0-9_.]+)\s*\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderRegex();

    [GeneratedRegex(@"\A[A-Za-z0-9_.]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex ValidKeyRegex();

    /// <inheritdoc />
    public Result<RenderedEmail> Render(EmailTemplate template, IReadOnlyDictionary<string, string>? variables)
    {
        ArgumentNullException.ThrowIfNull(template);

        return Render(template.Subject, template.HtmlBody, template.TextBody, variables);
    }

    /// <inheritdoc />
    public Result<RenderedEmail> Render(string subject, string htmlBody, string? textBody, IReadOnlyDictionary<string, string>? variables)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(htmlBody);

        var lookupResult = BuildLookup(variables);
        if (lookupResult.IsFailure)
        {
            return lookupResult.Error;
        }

        var lookup = lookupResult.Value;
        var missing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        CollectMissing(subject, lookup, missing);
        CollectMissing(htmlBody, lookup, missing);
        CollectMissing(textBody, lookup, missing);

        if (missing.Count > 0)
        {
            return EmailTemplateErrors.MissingVariables(missing.Values);
        }

        var renderedSubject = Replace(subject, lookup, htmlEncode: false, singleLine: true, MaxRenderedSubjectLength, "subject");
        if (renderedSubject.IsFailure)
        {
            return renderedSubject.Error;
        }

        var renderedHtml = Replace(htmlBody, lookup, htmlEncode: true, singleLine: false, MaxRenderedBodyLength, "HTML body");
        if (renderedHtml.IsFailure)
        {
            return renderedHtml.Error;
        }

        string? renderedText = null;
        if (textBody is not null)
        {
            var textResult = Replace(textBody, lookup, htmlEncode: false, singleLine: false, MaxRenderedBodyLength, "text body");
            if (textResult.IsFailure)
            {
                return textResult.Error;
            }

            renderedText = textResult.Value;
        }

        return new RenderedEmail(renderedSubject.Value, renderedHtml.Value, renderedText);
    }

    /// <inheritdoc />
    public Result<string> Render(
        string text,
        IReadOnlyDictionary<string, string>? variables,
        bool htmlEncode,
        int maxLength = MaxRenderedBodyLength)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfNegative(maxLength);

        var lookupResult = BuildLookup(variables);
        if (lookupResult.IsFailure)
        {
            return lookupResult.Error;
        }

        var lookup = lookupResult.Value;
        var missing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        CollectMissing(text, lookup, missing);

        if (missing.Count > 0)
        {
            return EmailTemplateErrors.MissingVariables(missing.Values);
        }

        return Replace(text, lookup, htmlEncode, singleLine: false, maxLength, "text");
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetPlaceholders(string? text)
    {
        var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        CollectKeys(text, keys);

        return keys.Values.ToList();
    }

    private static Result<Dictionary<string, string?>> BuildLookup(IReadOnlyDictionary<string, string>? variables)
    {
        var lookup = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (variables is null)
        {
            return lookup;
        }

        var spellings = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in variables)
        {
            if (key is null || !ValidKeyRegex().IsMatch(key))
            {
                continue;
            }

            if (!spellings.TryGetValue(key, out var list))
            {
                list = [];
                spellings[key] = list;
                lookup[key] = value;
            }

            list.Add(key);
        }

        var conflicting = spellings.Values
            .Where(list => list.Count > 1)
            .SelectMany(list => list)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        if (conflicting.Count > 0)
        {
            return EmailTemplateErrors.DuplicateVariables(
                conflicting.Take(MaxReportedDuplicates).Select(key => Truncate(key, MaxReportedKeyLength)));
        }

        return lookup;
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private static void CollectKeys(string? text, Dictionary<string, string> keys)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        foreach (Match match in PlaceholderRegex().Matches(text))
        {
            var key = match.Groups[1].Value;
            keys.TryAdd(key, key);
        }
    }

    private static void CollectMissing(string? text, Dictionary<string, string?> lookup, Dictionary<string, string> missing)
    {
        var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        CollectKeys(text, keys);

        foreach (var key in keys.Values)
        {
            if (!lookup.TryGetValue(key, out var value) || value is null)
            {
                missing.TryAdd(key, key);
            }
        }
    }

    private static Result<string> Replace(
        string text,
        Dictionary<string, string?> lookup,
        bool htmlEncode,
        bool singleLine,
        int maxLength,
        string partName)
    {
        var builder = new StringBuilder();
        var position = 0;

        for (var match = PlaceholderRegex().Match(text); match.Success; match = match.NextMatch())
        {
            builder.Append(text, position, match.Index - position);
            position = match.Index + match.Length;

            if (builder.Length > maxLength)
            {
                return EmailTemplateErrors.RenderedContentTooLarge(partName, maxLength);
            }

            var value = lookup[match.Groups[1].Value]!;

            if (value.Length > maxLength - builder.Length)
            {
                return EmailTemplateErrors.RenderedContentTooLarge(partName, maxLength);
            }

            if (singleLine)
            {
                value = CollapseToSingleLine(value);
            }

            builder.Append(htmlEncode ? WebUtility.HtmlEncode(value) : value);

            if (builder.Length > maxLength)
            {
                return EmailTemplateErrors.RenderedContentTooLarge(partName, maxLength);
            }
        }

        builder.Append(text, position, text.Length - position);

        if (builder.Length > maxLength)
        {
            return EmailTemplateErrors.RenderedContentTooLarge(partName, maxLength);
        }

        return builder.ToString();
    }

    private static string CollapseToSingleLine(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            var isLineBreaking = char.IsControl(character)
                || char.GetUnicodeCategory(character) is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;
            builder.Append(isLineBreaking ? ' ' : character);
        }

        return builder.ToString();
    }
}
