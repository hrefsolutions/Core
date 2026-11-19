using Href.Core.Api.Common.Results;

namespace Href.Core.Api.Modules.Authorization;

public static class AuthorizationErrors
{
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
}
