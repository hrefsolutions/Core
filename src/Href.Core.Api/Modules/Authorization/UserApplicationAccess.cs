using Href.Core.Api.Common.Domain;
using Href.Core.Api.Common.Results;

namespace Href.Core.Api.Modules.Authorization;

/// <summary>
/// Access of one user to one application, with roles of that same application. There is at most one access per user and
/// application. Revoking the access deletes the row; the audit log keeps the history.
/// </summary>
public class UserApplicationAccess : Entity, IAuditableEntity
{
    public const string UserApplicationUniqueIndexName = "ux_user_application_accesses_user_id_application_id";

    /// <summary>
    /// Foreign key to <c>users</c>. A grant that races with the deletion of its user fails on it.
    /// </summary>
    public const string UserForeignKeyName = "fk_user_application_accesses_user_id";

    private ISet<Role> _roles = new HashSet<Role>();

    protected UserApplicationAccess()
    {
    }

    public virtual Guid UserId { get; protected set; }

    public virtual Guid ApplicationId { get; protected set; }

    public virtual IEnumerable<Role> Roles => _roles;

    public virtual DateTime CreatedAtUtc { get; protected set; }

    public virtual DateTime UpdatedAtUtc { get; protected set; }

    public static Result<UserApplicationAccess> Grant(Guid userId, Guid applicationId, IEnumerable<Role> roles)
    {
        AuthorizationText.EnsureIdentifier(userId, nameof(userId));
        AuthorizationText.EnsureIdentifier(applicationId, nameof(applicationId));

        var access = new UserApplicationAccess
        {
            UserId = userId,
            ApplicationId = applicationId,
        };

        var rolesResult = access.ReplaceRoles(roles);

        if (rolesResult.IsFailure)
        {
            return rolesResult.Error;
        }

        return access;
    }

    public virtual Result AssignRole(Role role)
    {
        ArgumentNullException.ThrowIfNull(role);

        if (role.ApplicationId != ApplicationId)
        {
            return AuthorizationErrors.CrossApplicationRole;
        }

        _roles.Add(role);
        return Result.Success();
    }

    public virtual bool RemoveRole(Role role)
    {
        ArgumentNullException.ThrowIfNull(role);
        return _roles.Remove(role);
    }

    /// <summary>
    /// Replaces every role of the access. Nothing changes when any role belongs to another application.
    /// </summary>
    public virtual Result ReplaceRoles(IEnumerable<Role> roles)
    {
        ArgumentNullException.ThrowIfNull(roles);

        var requested = roles.ToList();

        if (requested.Any(role => role is null))
        {
            throw new ArgumentException("The roles cannot contain null.", nameof(roles));
        }

        if (requested.Any(role => role.ApplicationId != ApplicationId))
        {
            return AuthorizationErrors.CrossApplicationRole;
        }

        _roles.Clear();

        foreach (var role in requested)
        {
            _roles.Add(role);
        }

        return Result.Success();
    }

    public virtual IReadOnlyList<string> GetRoleNames() =>
        Roles
            .Select(role => role.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Union of the permission keys of every role of the access, without duplicates and in ordinal order.
    /// </summary>
    public virtual IReadOnlyList<string> GetPermissionKeys() =>
        Roles
            .SelectMany(role => role.Permissions)
            .Select(permission => permission.Key)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
}
