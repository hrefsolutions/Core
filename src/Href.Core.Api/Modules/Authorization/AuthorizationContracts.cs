namespace Href.Core.Api.Modules.Authorization;

public sealed record CreatePermissionRequest(string? Key, string? Description);

public sealed record UpdatePermissionRequest(string? Description);

public sealed record PermissionResponse(
    Guid Id,
    string Key,
    string? Description,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc)
{
    public static PermissionResponse From(Permission permission) =>
        new(permission.Id, permission.Key, permission.Description, permission.CreatedAtUtc, permission.UpdatedAtUtc);
}

/// <summary>
/// Creates a role. <c>PermissionKeys</c> may be omitted (no permissions); <c>IsDefault</c> moves the default flag to the new role.
/// </summary>
public sealed record CreateRoleRequest(
    string? Name,
    string? Description,
    bool IsDefault,
    IReadOnlyList<string?>? PermissionKeys);

/// <summary>
/// Replaces the name and the description. <c>IsDefault = true</c> moves the default flag to this role;
/// <c>false</c> on the current default leaves the application without a default role.
/// </summary>
public sealed record UpdateRoleRequest(string? Name, string? Description, bool IsDefault);

public sealed record ReplaceRolePermissionsRequest(IReadOnlyList<string?>? PermissionKeys);

public sealed record RoleResponse(
    Guid Id,
    string Name,
    string? Description,
    bool IsDefault,
    IReadOnlyList<string> PermissionKeys,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc)
{
    public static RoleResponse From(Role role) =>
        new(
            role.Id,
            role.Name,
            role.Description,
            role.IsDefault,
            role.GetPermissionKeys(),
            role.CreatedAtUtc,
            role.UpdatedAtUtc);
}

public sealed record ReplaceUserRolesRequest(IReadOnlyList<Guid>? RoleIds);

public sealed record AccessRoleResponse(Guid Id, string Name);

/// <summary>
/// A user with access to the application and the roles it holds there (ordered by name).
/// </summary>
public sealed record ApplicationUserResponse(
    Guid UserId,
    string Email,
    string FullName,
    string Status,
    IReadOnlyList<AccessRoleResponse> Roles,
    DateTime GrantedAtUtc);

public sealed record ApplicationUserPageResponse(
    IReadOnlyList<ApplicationUserResponse> Items,
    int Page,
    int PageSize,
    long TotalCount);
