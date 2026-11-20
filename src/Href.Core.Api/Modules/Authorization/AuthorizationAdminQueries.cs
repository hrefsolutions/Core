using Href.Core.Api.Common.Results;
using Href.Core.Api.Modules.Applications;
using NHibernate;
using NHibernate.Linq;
using ISession = NHibernate.ISession;

namespace Href.Core.Api.Modules.Authorization;

/// <summary>
/// Lookups shared by the authorization admin services.
/// </summary>
internal static class AuthorizationAdminQueries
{
    public const int MaxListItems = 200;

    /// <summary>
    /// Finds an application by its exact key. With <paramref name="lockForUpdate"/> the row is read with
    /// <c>SELECT ... FOR UPDATE</c>, which serializes every authorization write of that application.
    /// </summary>
    public static async Task<Application?> FindApplicationAsync(
        ISession session,
        string appKey,
        bool lockForUpdate,
        CancellationToken cancellationToken)
    {
        var query = session.Query<Application>();

        if (lockForUpdate)
        {
            query = query.WithLock(LockMode.Upgrade);
        }

        var rows = await query
            .Where(application => application.AppKey == appKey)
            .ToListAsync(cancellationToken);

        return rows.FirstOrDefault();
    }

    /// <summary>
    /// Resolves permission keys of one application. A <c>null</c> list is an empty list only when <paramref name="allowNullList"/>;
    /// blank or <c>null</c> items and more than 200 items are invalid; duplicates are ignored; keys the application does not define
    /// return <c>Authorization.UnknownPermissions</c>.
    /// </summary>
    public static async Task<Result<List<Permission>>> ResolvePermissionsAsync(
        ISession session,
        Guid applicationId,
        IReadOnlyList<string?>? keys,
        bool allowNullList,
        CancellationToken cancellationToken)
    {
        if (keys is null)
        {
            if (!allowNullList)
            {
                return AuthorizationErrors.InvalidPermissionKeys;
            }

            return new List<Permission>();
        }

        if (keys.Count > MaxListItems || keys.Any(string.IsNullOrWhiteSpace))
        {
            return AuthorizationErrors.InvalidPermissionKeys;
        }

        var requested = keys.Select(key => key!.Trim()).Distinct(StringComparer.Ordinal).ToList();

        if (requested.Count == 0)
        {
            return new List<Permission>();
        }

        var permissions = await session.Query<Permission>()
            .Where(permission => permission.ApplicationId == applicationId && requested.Contains(permission.Key))
            .ToListAsync(cancellationToken);

        var missing = requested.Except(permissions.Select(permission => permission.Key), StringComparer.Ordinal).ToList();

        if (missing.Count > 0)
        {
            return AuthorizationErrors.UnknownPermissions(missing);
        }

        return permissions;
    }

    /// <summary>
    /// Resolves role ids of one application. A <c>null</c> list or more than 200 items are invalid; duplicates are ignored;
    /// unknown ids and roles of other applications return <c>Authorization.UnknownRoles</c>.
    /// </summary>
    public static async Task<Result<List<Role>>> ResolveRolesAsync(
        ISession session,
        Guid applicationId,
        IReadOnlyList<Guid>? roleIds,
        CancellationToken cancellationToken)
    {
        if (roleIds is null || roleIds.Count > MaxListItems)
        {
            return AuthorizationErrors.InvalidRoleIds;
        }

        var requested = roleIds.Distinct().ToList();

        if (requested.Count == 0)
        {
            return new List<Role>();
        }

        var roles = await session.Query<Role>()
            .Where(role => role.ApplicationId == applicationId && requested.Contains(role.Id))
            .ToListAsync(cancellationToken);

        var missing = requested.Except(roles.Select(role => role.Id)).ToList();

        if (missing.Count > 0)
        {
            return AuthorizationErrors.UnknownRoles(missing);
        }

        return roles;
    }

    public static async Task<Role?> FindRoleAsync(
        ISession session,
        Guid applicationId,
        Guid roleId,
        CancellationToken cancellationToken)
    {
        var rows = await session.Query<Role>()
            .Where(role => role.Id == roleId && role.ApplicationId == applicationId)
            .ToListAsync(cancellationToken);

        return rows.FirstOrDefault();
    }

    public static async Task<Role?> FindDefaultRoleAsync(
        ISession session,
        Guid applicationId,
        CancellationToken cancellationToken)
    {
        var rows = await session.Query<Role>()
            .Where(role => role.ApplicationId == applicationId && role.IsDefault)
            .ToListAsync(cancellationToken);

        return rows.FirstOrDefault();
    }

    /// <summary>
    /// Compares two role snapshots by value, including the permission keys.
    /// </summary>
    public static bool SameRole(RoleAuditSnapshot left, RoleAuditSnapshot right) =>
        left with { PermissionKeys = Array.Empty<string>() } == right with { PermissionKeys = Array.Empty<string>() }
        && left.PermissionKeys.SequenceEqual(right.PermissionKeys, StringComparer.Ordinal);

    /// <summary>
    /// Compares two access snapshots by value, including the roles.
    /// </summary>
    public static bool SameAccess(AccessAuditSnapshot left, AccessAuditSnapshot right) =>
        left with { Roles = Array.Empty<AccessRoleAuditSnapshot>() } == right with { Roles = Array.Empty<AccessRoleAuditSnapshot>() }
        && left.Roles.SequenceEqual(right.Roles);
}
