using Href.Core.Api.Common.Domain;
using Href.Core.Api.Common.Results;

namespace Href.Core.Api.Modules.Authorization;

/// <summary>
/// A role of one application, holding permissions of that same application. The name is unique per application ignoring case.
/// At most one role per application is the default one (given on self-registration); the partial unique index
/// <see cref="DefaultUniqueIndexName"/> enforces it under concurrency.
/// </summary>
public class Role : Entity, IAuditableEntity
{
    public const int NameMaxLength = 100;
    public const int DescriptionMaxLength = AuthorizationText.DescriptionMaxLength;
    public const string NameUniqueIndexName = "ux_roles_application_id_name";
    public const string DefaultUniqueIndexName = "ux_roles_application_id_default";

    private ISet<Permission> _permissions = new HashSet<Permission>();

    protected Role()
    {
    }

    public virtual Guid ApplicationId { get; protected set; }

    public virtual string Name { get; protected set; } = string.Empty;

    public virtual string? Description { get; protected set; }

    public virtual bool IsDefault { get; protected set; }

    public virtual IEnumerable<Permission> Permissions => _permissions;

    public virtual DateTime CreatedAtUtc { get; protected set; }

    public virtual DateTime UpdatedAtUtc { get; protected set; }

    public static Result<Role> Create(Guid applicationId, string? name, string? description)
    {
        AuthorizationText.EnsureIdentifier(applicationId, nameof(applicationId));

        var role = new Role { ApplicationId = applicationId };
        var updateResult = role.Update(name, description);

        if (updateResult.IsFailure)
        {
            return updateResult.Error;
        }

        return role;
    }

    public virtual Result Update(string? name, string? description)
    {
        var normalizedName = name?.Trim() ?? string.Empty;

        if (normalizedName.Length is 0 or > NameMaxLength || TextRules.HasControlOrLineSeparator(normalizedName))
        {
            return AuthorizationErrors.InvalidRoleName;
        }

        var descriptionResult = AuthorizationText.NormalizeDescription(description);

        if (descriptionResult.IsFailure)
        {
            return descriptionResult.Error;
        }

        Name = normalizedName;
        Description = descriptionResult.Value;
        return Result.Success();
    }

    public virtual Result AddPermission(Permission permission)
    {
        ArgumentNullException.ThrowIfNull(permission);

        if (permission.ApplicationId != ApplicationId)
        {
            return AuthorizationErrors.CrossApplicationPermission;
        }

        _permissions.Add(permission);
        return Result.Success();
    }

    public virtual bool RemovePermission(Permission permission)
    {
        ArgumentNullException.ThrowIfNull(permission);
        return _permissions.Remove(permission);
    }

    /// <summary>
    /// Replaces every permission of the role. Nothing changes when any permission belongs to another application.
    /// </summary>
    public virtual Result ReplacePermissions(IEnumerable<Permission> permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);

        var requested = permissions.ToList();

        if (requested.Any(permission => permission is null))
        {
            throw new ArgumentException("The permissions cannot contain null.", nameof(permissions));
        }

        if (requested.Any(permission => permission.ApplicationId != ApplicationId))
        {
            return AuthorizationErrors.CrossApplicationPermission;
        }

        _permissions.Clear();

        foreach (var permission in requested)
        {
            _permissions.Add(permission);
        }

        return Result.Success();
    }

    /// <summary>
    /// Makes this role the default one of its application and clears the flag of <paramref name="currentDefault"/>,
    /// the role that is currently the default (or <c>null</c> when there is none).
    /// </summary>
    public virtual Result MakeDefault(Role? currentDefault)
    {
        if (currentDefault is not null && currentDefault.ApplicationId != ApplicationId)
        {
            return AuthorizationErrors.CrossApplicationRole;
        }

        if (currentDefault is not null && !Equals(currentDefault, this))
        {
            currentDefault.ClearDefault();
        }

        IsDefault = true;
        return Result.Success();
    }

    public virtual void ClearDefault() => IsDefault = false;

    public virtual IReadOnlyList<string> GetPermissionKeys() =>
        Permissions
            .Select(permission => permission.Key)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
}
