using Href.Core.Api.Common.Persistence;
using Href.Core.Api.Common.Results;
using Href.Core.Api.Modules.Applications;
using Href.Core.Api.Modules.Audit;
using Href.Core.Api.Modules.Auth;
using NHibernate.Linq;
using ISession = NHibernate.ISession;

namespace Href.Core.Api.Modules.Authorization;

/// <summary>
/// Admin use cases for the roles of one application. Writes lock the application row, run in one explicit transaction and
/// record their audit entries (with the application's <c>AppKey</c>) in it. When the default flag moves, the previous default
/// is cleared and flushed first, so the partial unique index never sees two defaults.
/// </summary>
public sealed class RoleAdminService(ISession session, IAuditService auditService)
{
    public async Task<Result<IReadOnlyList<RoleResponse>>> ListAsync(string appKey, CancellationToken cancellationToken)
    {
        var application = await AuthorizationAdminQueries.FindApplicationAsync(session, appKey, lockForUpdate: false, cancellationToken);

        if (application is null)
        {
            return ApplicationErrors.NotFound;
        }

        var roles = await session.Query<Role>()
            .Where(role => role.ApplicationId == application.Id)
            .ToListAsync(cancellationToken);

        IReadOnlyList<RoleResponse> items = roles
            .OrderBy(role => role.Name, StringComparer.Ordinal)
            .Select(RoleResponse.From)
            .ToList();

        return Result.Success(items);
    }

    public async Task<Result<RoleResponse>> GetAsync(string appKey, Guid roleId, CancellationToken cancellationToken)
    {
        var application = await AuthorizationAdminQueries.FindApplicationAsync(session, appKey, lockForUpdate: false, cancellationToken);

        if (application is null)
        {
            return ApplicationErrors.NotFound;
        }

        var role = await AuthorizationAdminQueries.FindRoleAsync(session, application.Id, roleId, cancellationToken);

        if (role is null)
        {
            return AuthorizationErrors.RoleNotFound;
        }

        return RoleResponse.From(role);
    }

    public async Task<Result<RoleResponse>> CreateAsync(string appKey, CreateRoleRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var transaction = session.BeginTransaction();

        var application = await AuthorizationAdminQueries.FindApplicationAsync(session, appKey, lockForUpdate: true, cancellationToken);

        if (application is null)
        {
            return ApplicationErrors.NotFound;
        }

        var createResult = Role.Create(application.Id, request.Name, request.Description);

        if (createResult.IsFailure)
        {
            return createResult.Error;
        }

        var role = createResult.Value;
        var permissionsResult = await AuthorizationAdminQueries.ResolvePermissionsAsync(
            session,
            application.Id,
            request.PermissionKeys,
            allowNullList: true,
            cancellationToken);

        if (permissionsResult.IsFailure)
        {
            return permissionsResult.Error;
        }

        if (await NameTakenAsync(application.Id, role.Name, excludedRoleId: null, cancellationToken))
        {
            return AuthorizationErrors.RoleNameTaken;
        }

        role.ReplacePermissions(permissionsResult.Value);

        try
        {
            if (request.IsDefault)
            {
                await ClearCurrentDefaultAsync(application, exceptRoleId: role.Id, cancellationToken);
                role.MakeDefault(null);
            }

            await session.SaveAsync(role, cancellationToken);
            await auditService.RecordRoleChangeAsync(AuditActions.RoleCreated, application.AppKey, null, RoleAuditSnapshot.From(role), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception exception) when (PostgresErrors.IsUniqueViolation(exception, Role.NameUniqueIndexName))
        {
            return AuthorizationErrors.RoleNameTaken;
        }

        return RoleResponse.From(role);
    }

    /// <summary>
    /// Replaces the name, the description and the default flag. No change records nothing.
    /// </summary>
    public async Task<Result<RoleResponse>> UpdateAsync(
        string appKey,
        Guid roleId,
        UpdateRoleRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var transaction = session.BeginTransaction();

        var application = await AuthorizationAdminQueries.FindApplicationAsync(session, appKey, lockForUpdate: true, cancellationToken);

        if (application is null)
        {
            return ApplicationErrors.NotFound;
        }

        var role = await AuthorizationAdminQueries.FindRoleAsync(session, application.Id, roleId, cancellationToken);

        if (role is null)
        {
            return AuthorizationErrors.RoleNotFound;
        }

        var before = RoleAuditSnapshot.From(role);
        var trimmedName = request.Name?.Trim() ?? string.Empty;

        if (!string.Equals(trimmedName, role.Name, StringComparison.OrdinalIgnoreCase)
            && await NameTakenAsync(application.Id, trimmedName, role.Id, cancellationToken))
        {
            return AuthorizationErrors.RoleNameTaken;
        }

        var updateResult = role.Update(request.Name, request.Description);

        if (updateResult.IsFailure)
        {
            return updateResult.Error;
        }

        try
        {
            if (request.IsDefault && !role.IsDefault)
            {
                await ClearCurrentDefaultAsync(application, exceptRoleId: role.Id, cancellationToken);
                role.MakeDefault(null);
            }
            else if (!request.IsDefault && role.IsDefault)
            {
                role.ClearDefault();
            }

            var after = RoleAuditSnapshot.From(role);

            if (!AuthorizationAdminQueries.SameRole(before, after))
            {
                await auditService.RecordRoleChangeAsync(AuditActions.RoleUpdated, application.AppKey, before, after, cancellationToken);
            }

            await session.FlushAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception exception) when (PostgresErrors.IsUniqueViolation(exception, Role.NameUniqueIndexName))
        {
            return AuthorizationErrors.RoleNameTaken;
        }

        return RoleResponse.From(role);
    }

    /// <summary>
    /// Replaces every permission of the role. No change records nothing.
    /// </summary>
    public async Task<Result<RoleResponse>> ReplacePermissionsAsync(
        string appKey,
        Guid roleId,
        ReplaceRolePermissionsRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var transaction = session.BeginTransaction();

        var application = await AuthorizationAdminQueries.FindApplicationAsync(session, appKey, lockForUpdate: true, cancellationToken);

        if (application is null)
        {
            return ApplicationErrors.NotFound;
        }

        var role = await AuthorizationAdminQueries.FindRoleAsync(session, application.Id, roleId, cancellationToken);

        if (role is null)
        {
            return AuthorizationErrors.RoleNotFound;
        }

        var permissionsResult = await AuthorizationAdminQueries.ResolvePermissionsAsync(
            session,
            application.Id,
            request.PermissionKeys,
            allowNullList: false,
            cancellationToken);

        if (permissionsResult.IsFailure)
        {
            return permissionsResult.Error;
        }

        if (role.Id == CoreApplication.AdminRoleId
            && !permissionsResult.Value.Any(permission => permission.Id == CoreApplication.AdminPermissionId))
        {
            return AuthorizationErrors.Reserved;
        }

        var before = RoleAuditSnapshot.From(role);
        var replaceResult = role.ReplacePermissions(permissionsResult.Value);

        if (replaceResult.IsFailure)
        {
            return replaceResult.Error;
        }

        var after = RoleAuditSnapshot.From(role);

        if (AuthorizationAdminQueries.SameRole(before, after))
        {
            return RoleResponse.From(role);
        }

        await auditService.RecordRoleChangeAsync(AuditActions.RolePermissionsChanged, application.AppKey, before, after, cancellationToken);
        await session.FlushAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return RoleResponse.From(role);
    }

    /// <summary>
    /// Deletes the role. Each access that held it first loses it, recorded as <c>ACCESS_ROLES_CHANGED</c>,
    /// so the database cascade never removes a link without an audit entry.
    /// </summary>
    public async Task<Result> DeleteAsync(string appKey, Guid roleId, CancellationToken cancellationToken)
    {
        using var transaction = session.BeginTransaction();

        var application = await AuthorizationAdminQueries.FindApplicationAsync(session, appKey, lockForUpdate: true, cancellationToken);

        if (application is null)
        {
            return ApplicationErrors.NotFound;
        }

        var role = await AuthorizationAdminQueries.FindRoleAsync(session, application.Id, roleId, cancellationToken);

        if (role is null)
        {
            return AuthorizationErrors.RoleNotFound;
        }

        if (role.Id == CoreApplication.AdminRoleId)
        {
            return AuthorizationErrors.Reserved;
        }

        var accesses = await session.Query<UserApplicationAccess>()
            .Where(access => access.ApplicationId == application.Id && access.Roles.Any(held => held.Id == roleId))
            .ToListAsync(cancellationToken);

        foreach (var access in accesses.OrderBy(access => access.Id))
        {
            var accessBefore = AccessAuditSnapshot.From(access);
            access.RemoveRole(role);
            await auditService.RecordAccessChangeAsync(
                AuditActions.AccessRolesChanged,
                application.AppKey,
                accessBefore,
                AccessAuditSnapshot.From(access),
                cancellationToken);
        }

        var before = RoleAuditSnapshot.From(role);
        await session.DeleteAsync(role, cancellationToken);
        await auditService.RecordRoleChangeAsync(AuditActions.RoleDeleted, application.AppKey, before, null, cancellationToken);
        await session.FlushAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return Result.Success();
    }

    private async Task ClearCurrentDefaultAsync(Application application, Guid exceptRoleId, CancellationToken cancellationToken)
    {
        var currentDefault = await AuthorizationAdminQueries.FindDefaultRoleAsync(session, application.Id, cancellationToken);

        if (currentDefault is null || currentDefault.Id == exceptRoleId)
        {
            return;
        }

        var before = RoleAuditSnapshot.From(currentDefault);
        currentDefault.ClearDefault();
        await auditService.RecordRoleChangeAsync(AuditActions.RoleUpdated, application.AppKey, before, RoleAuditSnapshot.From(currentDefault), cancellationToken);
        await session.FlushAsync(cancellationToken);
    }

    private Task<bool> NameTakenAsync(Guid applicationId, string name, Guid? excludedRoleId, CancellationToken cancellationToken)
    {
        var lowerName = name.ToLowerInvariant();

        return excludedRoleId is null
            ? session.Query<Role>().AnyAsync(
                role => role.ApplicationId == applicationId && role.Name.ToLower() == lowerName,
                cancellationToken)
            : session.Query<Role>().AnyAsync(
                role => role.ApplicationId == applicationId && role.Id != excludedRoleId.Value && role.Name.ToLower() == lowerName,
                cancellationToken);
    }
}
