using Href.Core.Api.Common.Persistence;
using Href.Core.Api.Common.Results;
using Href.Core.Api.Modules.Applications;
using Href.Core.Api.Modules.Audit;
using Href.Core.Api.Modules.Auth;
using NHibernate.Linq;
using ISession = NHibernate.ISession;

namespace Href.Core.Api.Modules.Authorization;

/// <summary>
/// Admin use cases for the permissions of one application. Writes lock the application row, run in one explicit transaction
/// and record their audit entries (with the application's <c>AppKey</c>) in it.
/// </summary>
public sealed class PermissionAdminService(ISession session, IAuditService auditService)
{
    public async Task<Result<IReadOnlyList<PermissionResponse>>> ListAsync(string appKey, CancellationToken cancellationToken)
    {
        var application = await AuthorizationAdminQueries.FindApplicationAsync(session, appKey, lockForUpdate: false, cancellationToken);

        if (application is null)
        {
            return ApplicationErrors.NotFound;
        }

        var permissions = await session.Query<Permission>()
            .Where(permission => permission.ApplicationId == application.Id)
            .ToListAsync(cancellationToken);

        IReadOnlyList<PermissionResponse> items = permissions
            .OrderBy(permission => permission.Key, StringComparer.Ordinal)
            .Select(PermissionResponse.From)
            .ToList();

        return Result.Success(items);
    }

    public async Task<Result<PermissionResponse>> GetAsync(string appKey, string key, CancellationToken cancellationToken)
    {
        var application = await AuthorizationAdminQueries.FindApplicationAsync(session, appKey, lockForUpdate: false, cancellationToken);

        if (application is null)
        {
            return ApplicationErrors.NotFound;
        }

        var permission = await FindPermissionAsync(application.Id, key, cancellationToken);

        if (permission is null)
        {
            return AuthorizationErrors.PermissionNotFound;
        }

        return PermissionResponse.From(permission);
    }

    public async Task<Result<PermissionResponse>> CreateAsync(
        string appKey,
        CreatePermissionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var transaction = session.BeginTransaction();

        var application = await AuthorizationAdminQueries.FindApplicationAsync(session, appKey, lockForUpdate: true, cancellationToken);

        if (application is null)
        {
            return ApplicationErrors.NotFound;
        }

        var createResult = Permission.Create(application.Id, request.Key, request.Description);

        if (createResult.IsFailure)
        {
            return createResult.Error;
        }

        var permission = createResult.Value;

        if (await FindPermissionAsync(application.Id, permission.Key, cancellationToken) is not null)
        {
            return AuthorizationErrors.PermissionKeyTaken;
        }

        try
        {
            await session.SaveAsync(permission, cancellationToken);
            await auditService.RecordPermissionChangeAsync(
                AuditActions.PermissionCreated,
                application.AppKey,
                null,
                PermissionAuditSnapshot.From(permission),
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception exception) when (PostgresErrors.IsUniqueViolation(exception, Permission.PermissionKeyUniqueIndexName))
        {
            return AuthorizationErrors.PermissionKeyTaken;
        }

        return PermissionResponse.From(permission);
    }

    /// <summary>
    /// Replaces the description. The key never changes. No change records nothing.
    /// </summary>
    public async Task<Result<PermissionResponse>> UpdateAsync(
        string appKey,
        string key,
        UpdatePermissionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var transaction = session.BeginTransaction();

        var application = await AuthorizationAdminQueries.FindApplicationAsync(session, appKey, lockForUpdate: true, cancellationToken);

        if (application is null)
        {
            return ApplicationErrors.NotFound;
        }

        var permission = await FindPermissionAsync(application.Id, key, cancellationToken);

        if (permission is null)
        {
            return AuthorizationErrors.PermissionNotFound;
        }

        var before = PermissionAuditSnapshot.From(permission);
        var updateResult = permission.UpdateDescription(request.Description);

        if (updateResult.IsFailure)
        {
            return updateResult.Error;
        }

        var after = PermissionAuditSnapshot.From(permission);

        if (after == before)
        {
            return PermissionResponse.From(permission);
        }

        await auditService.RecordPermissionChangeAsync(AuditActions.PermissionUpdated, application.AppKey, before, after, cancellationToken);
        await session.FlushAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return PermissionResponse.From(permission);
    }

    /// <summary>
    /// Deletes the permission. Each role that held it first loses it, recorded as <c>ROLE_PERMISSIONS_CHANGED</c>,
    /// so the database cascade never removes a link without an audit entry.
    /// </summary>
    public async Task<Result> DeleteAsync(string appKey, string key, CancellationToken cancellationToken)
    {
        using var transaction = session.BeginTransaction();

        var application = await AuthorizationAdminQueries.FindApplicationAsync(session, appKey, lockForUpdate: true, cancellationToken);

        if (application is null)
        {
            return ApplicationErrors.NotFound;
        }

        var permission = await FindPermissionAsync(application.Id, key, cancellationToken);

        if (permission is null)
        {
            return AuthorizationErrors.PermissionNotFound;
        }

        if (permission.Id == CoreApplication.AdminPermissionId)
        {
            return AuthorizationErrors.Reserved;
        }

        var permissionId = permission.Id;
        var roles = await session.Query<Role>()
            .Where(role => role.ApplicationId == application.Id && role.Permissions.Any(held => held.Id == permissionId))
            .ToListAsync(cancellationToken);

        foreach (var role in roles.OrderBy(role => role.Name, StringComparer.Ordinal))
        {
            var roleBefore = RoleAuditSnapshot.From(role);
            role.RemovePermission(permission);
            await auditService.RecordRoleChangeAsync(
                AuditActions.RolePermissionsChanged,
                application.AppKey,
                roleBefore,
                RoleAuditSnapshot.From(role),
                cancellationToken);
        }

        var before = PermissionAuditSnapshot.From(permission);
        await session.DeleteAsync(permission, cancellationToken);
        await auditService.RecordPermissionChangeAsync(AuditActions.PermissionDeleted, application.AppKey, before, null, cancellationToken);
        await session.FlushAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return Result.Success();
    }

    private async Task<Permission?> FindPermissionAsync(Guid applicationId, string key, CancellationToken cancellationToken)
    {
        var rows = await session.Query<Permission>()
            .Where(permission => permission.ApplicationId == applicationId && permission.Key == key)
            .ToListAsync(cancellationToken);

        return rows.FirstOrDefault();
    }
}
