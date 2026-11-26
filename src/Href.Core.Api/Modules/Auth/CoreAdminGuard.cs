using Href.Core.Api.Modules.Authorization;
using Href.Core.Api.Modules.Users;
using NHibernate.Linq;
using ISession = NHibernate.ISession;

namespace Href.Core.Api.Modules.Auth;

/// <summary>
/// Keeps Core administrable: a write that could remove the last active admin (an active user with a verified email whose
/// <c>core</c> access holds <c>core.admin</c>, the same conditions login checks) runs between <see cref="BeginAsync"/> and
/// <see cref="WouldLeaveNoAdminAsync"/> in its transaction. <see cref="BeginAsync"/> takes a transaction-scoped advisory lock
/// before any other lock of the write, so two such writes cannot each remove a different admin and leave none. When there
/// was no admin before the write (for example a fresh database waiting for the bootstrap), nothing is blocked.
/// </summary>
internal static class CoreAdminGuard
{
    /// <summary>
    /// Takes the guard lock and returns whether an active admin exists before the write.
    /// </summary>
    public static async Task<bool> BeginAsync(ISession session, CancellationToken cancellationToken)
    {
        await session.CreateSQLQuery("SELECT 1 FROM pg_advisory_xact_lock(hashtextextended('core-admin-guard', 0))")
            .UniqueResultAsync(cancellationToken);

        return await HasActiveAdminAsync(session, cancellationToken);
    }

    /// <summary>
    /// <see cref="BeginAsync"/> for authorization writes: only writes to the reserved <c>core</c> application (exact key)
    /// can remove an admin, so other applications skip the guard and return <c>false</c>.
    /// </summary>
    public static Task<bool> BeginForApplicationAsync(ISession session, string appKey, CancellationToken cancellationToken) =>
        string.Equals(appKey, CoreApplication.AppKey, StringComparison.Ordinal)
            ? BeginAsync(session, cancellationToken)
            : Task.FromResult(false);

    /// <summary>
    /// Flushes the pending changes and returns <c>true</c> when an admin existed before the write and none is left.
    /// The caller then returns <see cref="AuthorizationErrors.LastAdmin"/> without committing.
    /// </summary>
    public static async Task<bool> WouldLeaveNoAdminAsync(ISession session, bool hadActiveAdmin, CancellationToken cancellationToken)
    {
        if (!hadActiveAdmin)
        {
            return false;
        }

        await session.FlushAsync(cancellationToken);
        return !await HasActiveAdminAsync(session, cancellationToken);
    }

    /// <summary>
    /// Whether an active user with a verified email holds <c>core.admin</c> through its <c>core</c> access.
    /// </summary>
    public static async Task<bool> HasActiveAdminAsync(ISession session, CancellationToken cancellationToken)
    {
        var adminUserIds = await session.Query<UserApplicationAccess>()
            .Where(access => access.ApplicationId == CoreApplication.Id
                && access.Roles.Any(role => role.Permissions.Any(permission => permission.Id == CoreApplication.AdminPermissionId)))
            .Select(access => access.UserId)
            .ToListAsync(cancellationToken);

        return adminUserIds.Count > 0
            && await session.Query<User>()
                .AnyAsync(
                    user => adminUserIds.Contains(user.Id)
                        && user.Status == UserStatus.Active
                        && user.EmailVerifiedAtUtc != null,
                    cancellationToken);
    }
}
