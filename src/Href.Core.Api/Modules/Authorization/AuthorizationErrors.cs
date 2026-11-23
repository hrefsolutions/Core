using Href.Core.Api.Common.Results;

namespace Href.Core.Api.Modules.Authorization;

public static class AuthorizationErrors
{
    public static readonly Error Reserved = Error.Conflict(
        "Authorization.Reserved",
        "The core.admin permission and the admin role of the core application cannot be deleted, and the admin role must keep core.admin.");

    public static readonly Error InvalidPermissionKey = Error.Validation(
        "Authorization.InvalidPermissionKey",
        "The permission key must be lowercase letters and digits separated by '.', '_', ':' or '-' (up to 100 characters).");

    public static readonly Error InvalidRoleName = Error.Validation(
        "Authorization.InvalidRoleName",
        "The role name is required, must be a single line and must be between 1 and 100 characters.");

    public static readonly Error InvalidDescription = Error.Validation(
        "Authorization.InvalidDescription",
        "The description cannot exceed 500 characters or contain NUL characters.");

    public static readonly Error CrossApplicationPermission = Error.Validation(
        "Authorization.CrossApplicationPermission",
        "A role can only hold permissions of its own application.");

    public static readonly Error CrossApplicationRole = Error.Validation(
        "Authorization.CrossApplicationRole",
        "An access or a default role change can only involve roles of the same application.");

    public static readonly Error PermissionKeyTaken = Error.Conflict(
        "Authorization.PermissionKeyTaken",
        "The application already has a permission with the specified key.");

    public static readonly Error RoleNameTaken = Error.Conflict(
        "Authorization.RoleNameTaken",
        "The application already has a role with the specified name.");

    public static readonly Error AccessAlreadyGranted = Error.Conflict(
        "Authorization.AccessAlreadyGranted",
        "The user already has access to the application.");

    public static readonly Error PermissionNotFound = Error.NotFound(
        "Authorization.PermissionNotFound",
        "The application has no permission with the specified key.");

    public static readonly Error RoleNotFound = Error.NotFound(
        "Authorization.RoleNotFound",
        "The application has no role with the specified id.");

    public static readonly Error AccessNotFound = Error.NotFound(
        "Authorization.AccessNotFound",
        "The user has no access to the application.");

    public static readonly Error InvalidPermissionKeys = Error.Validation(
        "Authorization.InvalidPermissionKeys",
        "The permission keys must be a list of up to 200 non-empty keys.");

    public static readonly Error InvalidRoleIds = Error.Validation(
        "Authorization.InvalidRoleIds",
        "The role ids must be a list of up to 200 ids.");

    public static readonly Error InvalidPage = Error.Validation(
        "Authorization.InvalidPage",
        "The page must be 1 or greater.");

    public static readonly Error InvalidPageSize = Error.Validation(
        "Authorization.InvalidPageSize",
        "The page size must be between 1 and 200.");

    public const string UnknownPermissionsCode = "Authorization.UnknownPermissions";

    public const string UnknownRolesCode = "Authorization.UnknownRoles";

    /// <summary>
    /// Permission keys that the application does not define (unknown, or defined by another application). Lists up to 10 keys.
    /// </summary>
    public static Error UnknownPermissions(IEnumerable<string> keys) =>
        Error.Validation(UnknownPermissionsCode, $"Unknown permission keys for this application: {Describe(keys)}.");

    /// <summary>
    /// Role ids that the application does not define (unknown, or roles of another application). Lists up to 10 ids.
    /// </summary>
    public static Error UnknownRoles(IEnumerable<Guid> ids) =>
        Error.Validation(UnknownRolesCode, $"Unknown role ids for this application: {Describe(ids.Select(id => id.ToString()))}.");

    private static string Describe(IEnumerable<string> values)
    {
        var sorted = values.Order(StringComparer.Ordinal).ToList();
        var shown = string.Join(", ", sorted.Take(10));
        return sorted.Count > 10 ? $"{shown} (+{sorted.Count - 10} more)" : shown;
    }
}
