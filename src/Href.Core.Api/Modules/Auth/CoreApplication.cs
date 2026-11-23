namespace Href.Core.Api.Modules.Auth;

/// <summary>
/// The reserved <c>core</c> application (the identity of the admin frontend's backend), its <c>core.admin</c> permission
/// and its <c>admin</c> role, seeded by migration <c>0008_seed_core_admin.sql</c> with these fixed ids. Admin endpoints
/// require an access token issued for this application (audience <c>core</c>) that carries <c>core.admin</c>.
/// </summary>
public static class CoreApplication
{
    public const string AppKey = "core";
    public const string AdminPermissionKey = "core.admin";
    public const string AdminRoleName = "admin";

    public static readonly Guid Id = Guid.Parse("01937a00-0000-7000-8000-000000000001");
    public static readonly Guid AdminPermissionId = Guid.Parse("01937a00-0000-7000-8000-000000000002");
    public static readonly Guid AdminRoleId = Guid.Parse("01937a00-0000-7000-8000-000000000003");
}
