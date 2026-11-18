namespace Href.Core.Api.Modules.Audit;

public static class AuditActions
{
    public const string ApplicationCreated = "APPLICATION_CREATED";
    public const string ApplicationUpdated = "APPLICATION_UPDATED";
    public const string ApplicationStatusChanged = "APPLICATION_STATUS_CHANGED";
    public const string ApplicationSecretRotated = "APPLICATION_SECRET_ROTATED";
    public const string UserCreated = "USER_CREATED";
    public const string UserUpdated = "USER_UPDATED";
    public const string UserLockChanged = "USER_LOCK_CHANGED";
    public const string RoleCreated = "ROLE_CREATED";
    public const string RoleUpdated = "ROLE_UPDATED";
    public const string PermissionAssigned = "PERMISSION_ASSIGNED";
    public const string LoginFailed = "LOGIN_FAILED";
    public const string NotificationEnqueued = "NOTIFICATION_ENQUEUED";
    public const string TemplateCreated = "TEMPLATE_CREATED";
    public const string TemplateUpdated = "TEMPLATE_UPDATED";
}
