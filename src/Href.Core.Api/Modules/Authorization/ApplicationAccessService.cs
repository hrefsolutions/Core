using Href.Core.Api.Common.Persistence;
using Href.Core.Api.Common.Results;
using Href.Core.Api.Modules.Applications;
using Href.Core.Api.Modules.Audit;
using Href.Core.Api.Modules.Auth;
using Href.Core.Api.Modules.Users;
using NHibernate.Linq;
using ISession = NHibernate.ISession;

namespace Href.Core.Api.Modules.Authorization;

/// <summary>
/// Admin use cases for the users that can access one application. Writes lock the application row, run in one explicit
/// transaction and record their audit entries (with the application's <c>AppKey</c>) in it.
/// </summary>
public sealed class ApplicationAccessService(ISession session, IAuditService auditService)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    /// <summary>
    /// Lists the users with access to the application, ordered by email, with their roles there.
    /// </summary>
    public async Task<Result<ApplicationUserPageResponse>> ListAsync(
        string appKey,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        if (page < 1)
        {
            return AuthorizationErrors.InvalidPage;
        }

        if (pageSize is < 1 or > MaxPageSize)
        {
            return AuthorizationErrors.InvalidPageSize;
        }

        var application = await AuthorizationAdminQueries.FindApplicationAsync(session, appKey, lockForUpdate: false, cancellationToken);

        if (application is null)
        {
            return ApplicationErrors.NotFound;
        }

        var applicationId = application.Id;
        var usersQuery = session.Query<User>()
            .Where(user => session.Query<UserApplicationAccess>()
                .Any(access => access.UserId == user.Id && access.ApplicationId == applicationId));

        var totalCount = await usersQuery.LongCountAsync(cancellationToken);
        var offset = (long)(page - 1) * pageSize;

        if (offset >= totalCount)
        {
            return new ApplicationUserPageResponse([], page, pageSize, totalCount);
        }

        var users = await usersQuery
            .OrderBy(user => user.Email)
            .ThenBy(user => user.Id)
            .Skip((int)offset)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var userIds = users.Select(user => user.Id).ToList();
        var accesses = await session.Query<UserApplicationAccess>()
            .Where(access => access.ApplicationId == applicationId && userIds.Contains(access.UserId))
            .ToListAsync(cancellationToken);
        var accessesByUserId = accesses.ToDictionary(access => access.UserId);

        var items = users
            .Where(user => accessesByUserId.ContainsKey(user.Id))
            .Select(user => ToResponse(user, accessesByUserId[user.Id]))
            .ToList();

        return new ApplicationUserPageResponse(items, page, pageSize, totalCount);
    }

    /// <summary>
    /// Sets the user's roles in the application. A user without access gets it (<c>ACCESS_GRANTED</c>); otherwise the roles are
    /// replaced (<c>ACCESS_ROLES_CHANGED</c>); the same roles record nothing.
    /// </summary>
    public async Task<Result<ApplicationUserResponse>> ReplaceRolesAsync(
        string appKey,
        Guid userId,
        ReplaceUserRolesRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var transaction = session.BeginTransaction();

        var hadActiveAdmin = await CoreAdminGuard.BeginForApplicationAsync(session, appKey, cancellationToken);
        var application = await AuthorizationAdminQueries.FindApplicationAsync(session, appKey, lockForUpdate: true, cancellationToken);

        if (application is null)
        {
            return ApplicationErrors.NotFound;
        }

        var user = await session.GetAsync<User>(userId, cancellationToken);

        if (user is null)
        {
            return UserErrors.NotFound;
        }

        var rolesResult = await AuthorizationAdminQueries.ResolveRolesAsync(session, application.Id, request.RoleIds, cancellationToken);

        if (rolesResult.IsFailure)
        {
            return rolesResult.Error;
        }

        var access = await FindAccessAsync(application.Id, userId, cancellationToken);

        try
        {
            if (access is null)
            {
                access = UserApplicationAccess.Grant(userId, application.Id, rolesResult.Value).Value;
                await session.SaveAsync(access, cancellationToken);
                await auditService.RecordAccessChangeAsync(
                    AuditActions.AccessGranted,
                    application.AppKey,
                    null,
                    AccessAuditSnapshot.From(access),
                    cancellationToken);
            }
            else
            {
                var before = AccessAuditSnapshot.From(access);
                access.ReplaceRoles(rolesResult.Value);
                var after = AccessAuditSnapshot.From(access);

                if (!AuthorizationAdminQueries.SameAccess(before, after))
                {
                    await auditService.RecordAccessChangeAsync(AuditActions.AccessRolesChanged, application.AppKey, before, after, cancellationToken);
                }
            }

            await session.FlushAsync(cancellationToken);

            if (await CoreAdminGuard.WouldLeaveNoAdminAsync(session, hadActiveAdmin, cancellationToken))
            {
                return AuthorizationErrors.LastAdmin;
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception exception) when (PostgresErrors.IsUniqueViolation(exception, UserApplicationAccess.UserApplicationUniqueIndexName))
        {
            return AuthorizationErrors.AccessAlreadyGranted;
        }

        return ToResponse(user, access);
    }

    /// <summary>
    /// Revokes the access (the row is deleted), recorded as <c>ACCESS_REVOKED</c>.
    /// </summary>
    public async Task<Result> RevokeAsync(string appKey, Guid userId, CancellationToken cancellationToken)
    {
        using var transaction = session.BeginTransaction();

        var hadActiveAdmin = await CoreAdminGuard.BeginForApplicationAsync(session, appKey, cancellationToken);
        var application = await AuthorizationAdminQueries.FindApplicationAsync(session, appKey, lockForUpdate: true, cancellationToken);

        if (application is null)
        {
            return ApplicationErrors.NotFound;
        }

        var access = await FindAccessAsync(application.Id, userId, cancellationToken);

        if (access is null)
        {
            return AuthorizationErrors.AccessNotFound;
        }

        var before = AccessAuditSnapshot.From(access);
        await session.DeleteAsync(access, cancellationToken);
        await auditService.RecordAccessChangeAsync(AuditActions.AccessRevoked, application.AppKey, before, null, cancellationToken);
        await session.FlushAsync(cancellationToken);

        if (await CoreAdminGuard.WouldLeaveNoAdminAsync(session, hadActiveAdmin, cancellationToken))
        {
            return AuthorizationErrors.LastAdmin;
        }

        await transaction.CommitAsync(cancellationToken);

        return Result.Success();
    }
    private static ApplicationUserResponse ToResponse(User user, UserApplicationAccess access) =>
        new(
            user.Id,
            user.Email,
            user.FullName,
            user.Status.ToString(),
            access.Roles
                .Select(role => new AccessRoleResponse(role.Id, role.Name))
                .OrderBy(role => role.Name, StringComparer.Ordinal)
                .ThenBy(role => role.Id)
                .ToList(),
            access.CreatedAtUtc);

    private async Task<UserApplicationAccess?> FindAccessAsync(Guid applicationId, Guid userId, CancellationToken cancellationToken)
    {
        var rows = await session.Query<UserApplicationAccess>()
            .Where(access => access.ApplicationId == applicationId && access.UserId == userId)
            .ToListAsync(cancellationToken);

        return rows.FirstOrDefault();
    }
}
