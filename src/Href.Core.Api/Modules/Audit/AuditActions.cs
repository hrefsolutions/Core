namespace Href.Core.Api.Modules.Audit;

public static class AuditActions
{
    public const string ApplicationCreated = "APPLICATION_CREATED";
    public const string ApplicationUpdated = "APPLICATION_UPDATED";
    public const string ApplicationStatusChanged = "APPLICATION_STATUS_CHANGED";
    public const string ApplicationSecretRotated = "APPLICATION_SECRET_ROTATED";
    public const string UserCreated = "USER_CREATED";
    public const string UserUpdated = "USER_UPDATED";
    public const string UserEmailChanged = "USER_EMAIL_CHANGED";
    public const string UserLocked = "USER_LOCKED";
    public const string UserUnlocked = "USER_UNLOCKED";
    public const string RoleCreated = "ROLE_CREATED";
    public const string RoleUpdated = "ROLE_UPDATED";
    public const string RoleDeleted = "ROLE_DELETED";
    public const string RolePermissionsChanged = "ROLE_PERMISSIONS_CHANGED";
    public const string PermissionCreated = "PERMISSION_CREATED";
    public const string PermissionUpdated = "PERMISSION_UPDATED";
    public const string PermissionDeleted = "PERMISSION_DELETED";
    public const string AccessGranted = "ACCESS_GRANTED";
    public const string AccessRolesChanged = "ACCESS_ROLES_CHANGED";
    public const string AccessRevoked = "ACCESS_REVOKED";
    public const string LoginFailed = "LOGIN_FAILED";
    public const string NotificationQueued = "NOTIFICATION_QUEUED";
    public const string TemplateCreated = "TEMPLATE_CREATED";
    public const string TemplateUpdated = "TEMPLATE_UPDATED";
    public const string TemplateDeleted = "TEMPLATE_DELETED";
}
