namespace Href.Core.Api.Modules.Authorization;

/// <summary>
/// Audit snapshot of a permission.
/// </summary>
public sealed record PermissionAuditSnapshot(Guid Id, Guid ApplicationId, string Key, string? Description)
{
    /// <summary>
    /// Copies the current values of <paramref name="permission"/>.
    /// </summary>
    public static PermissionAuditSnapshot From(Permission permission)
    {
        ArgumentNullException.ThrowIfNull(permission);
        return new PermissionAuditSnapshot(permission.Id, permission.ApplicationId, permission.Key, permission.Description);
    }
}

/// <summary>
/// Audit snapshot of a role with its permission keys in ordinal order.
/// </summary>
public sealed record RoleAuditSnapshot(
    Guid Id,
    Guid ApplicationId,
    string Name,
    string? Description,
    bool IsDefault,
    IReadOnlyList<string> PermissionKeys)
{
    /// <summary>
    /// Copies the current values of <paramref name="role"/>.
    /// </summary>
    public static RoleAuditSnapshot From(Role role)
    {
        ArgumentNullException.ThrowIfNull(role);

        return new RoleAuditSnapshot(
            role.Id,
            role.ApplicationId,
            role.Name,
            role.Description,
            role.IsDefault,
            role.GetPermissionKeys());
    }
}

/// <summary>
/// Audit snapshot of a user's access to an application with its roles ordered by name.
/// </summary>
public sealed record AccessAuditSnapshot(
    Guid Id,
    Guid UserId,
    Guid ApplicationId,
    IReadOnlyList<AccessRoleAuditSnapshot> Roles)
{
    /// <summary>
    /// Copies the current values of <paramref name="access"/>.
    /// </summary>
    public static AccessAuditSnapshot From(UserApplicationAccess access)
    {
        ArgumentNullException.ThrowIfNull(access);

        var roles = access.Roles
            .Select(role => new AccessRoleAuditSnapshot(role.Id, role.Name))
            .OrderBy(role => role.Name, StringComparer.Ordinal)
            .ThenBy(role => role.Id)
            .ToList();

        return new AccessAuditSnapshot(access.Id, access.UserId, access.ApplicationId, roles);
    }
}

/// <summary>
/// A role inside an <see cref="AccessAuditSnapshot"/>.
/// </summary>
public sealed record AccessRoleAuditSnapshot(Guid Id, string Name);
