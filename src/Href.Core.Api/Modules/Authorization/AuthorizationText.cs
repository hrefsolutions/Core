using Href.Core.Api.Common.Domain;
using Href.Core.Api.Common.Results;

namespace Href.Core.Api.Modules.Authorization;

internal static class AuthorizationText
{
    public const int DescriptionMaxLength = 500;

    public static Result<string?> NormalizeDescription(string? description)
    {
        var normalized = string.IsNullOrWhiteSpace(description) ? null : description.Trim();

        if (normalized is not null && (normalized.Length > DescriptionMaxLength || TextRules.HasNul(normalized)))
        {
            return AuthorizationErrors.InvalidDescription;
        }

        return normalized;
    }

    public static void EnsureIdentifier(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("The identifier cannot be empty.", parameterName);
        }
    }
}
