using System.Text.RegularExpressions;
using Href.Core.Api.Common.Domain;
using Href.Core.Api.Common.Results;

namespace Href.Core.Api.Modules.Authorization;

/// <summary>
/// A permission defined by one application (for example <c>orders.read</c>). The key is unique per application and never changes.
/// </summary>
public partial class Permission : Entity, IAuditableEntity
{
    public const int KeyMaxLength = 100;
    public const int DescriptionMaxLength = AuthorizationText.DescriptionMaxLength;
    public const string PermissionKeyUniqueIndexName = "ux_permissions_application_id_key";

    protected Permission()
    {
    }

    public virtual Guid ApplicationId { get; protected set; }

    public virtual string Key { get; protected set; } = string.Empty;

    public virtual string? Description { get; protected set; }

    public virtual DateTime CreatedAtUtc { get; protected set; }

    public virtual DateTime UpdatedAtUtc { get; protected set; }

    public static Result<Permission> Create(Guid applicationId, string? key, string? description)
    {
        AuthorizationText.EnsureIdentifier(applicationId, nameof(applicationId));

        var normalizedKey = key?.Trim() ?? string.Empty;

        if (normalizedKey.Length is 0 or > KeyMaxLength || !KeyRegex().IsMatch(normalizedKey))
        {
            return AuthorizationErrors.InvalidPermissionKey;
        }

        var descriptionResult = AuthorizationText.NormalizeDescription(description);

        if (descriptionResult.IsFailure)
        {
            return descriptionResult.Error;
        }

        return new Permission
        {
            ApplicationId = applicationId,
            Key = normalizedKey,
            Description = descriptionResult.Value,
        };
    }

    public virtual Result UpdateDescription(string? description)
    {
        var descriptionResult = AuthorizationText.NormalizeDescription(description);

        if (descriptionResult.IsFailure)
        {
            return descriptionResult.Error;
        }

        Description = descriptionResult.Value;
        return Result.Success();
    }

    [GeneratedRegex("^[a-z0-9]+([._:-][a-z0-9]+)*$")]
    private static partial Regex KeyRegex();
}
