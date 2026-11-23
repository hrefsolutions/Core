using Href.Core.Api.Modules.Applications;
using Href.Core.Api.Modules.Audit;
using Href.Core.Api.Modules.Authorization;
using Href.Core.Api.Modules.Users;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using NHibernate.Linq;
using ISession = NHibernate.ISession;

namespace Href.Core.Api.Modules.Auth;

/// <summary>
/// First-deploy bootstrap, run once at startup (after the migrations) in one transaction serialized across instances by
/// an advisory lock. With <c>Bootstrap:CoreClientSecret</c> it sets the secret of the reserved <c>core</c> application.
/// With <c>Bootstrap:AdminEmail</c> and <c>Bootstrap:AdminPassword</c>, when no active and verified user holds
/// <c>core.admin</c> (the same conditions login checks), it creates that user (email verified) or reuses the existing
/// one, and gives it access to <c>core</c> with the <c>admin</c> role. A verified existing user keeps its password; an
/// unverified one gets the bootstrap password (which regenerates its security stamp) before it is verified, so whoever
/// registered that email cannot take over the admin account. An existing user is changed under the per-user refresh
/// token lock and the user row lock, in that order. Every change is audited with actor <c>System</c>; a warning reminds to remove
/// the variables.
/// </summary>
public sealed partial class AdminBootstrapper(
    IServiceScopeFactory scopeFactory,
    IOptions<AdminBootstrapOptions> options,
    ILogger<AdminBootstrapper> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var bootstrap = options.Value;

        if (!bootstrap.HasAdmin && !bootstrap.HasCoreClientSecret)
        {
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var session = services.GetRequiredService<ISession>();
        var auditService = services.GetRequiredService<IAuditService>();
        var timeProvider = services.GetRequiredService<TimeProvider>();

        using var transaction = session.BeginTransaction();

        await session.CreateSQLQuery("SELECT 1 FROM pg_advisory_xact_lock(hashtextextended('core-admin-bootstrap', 0))")
            .UniqueResultAsync(cancellationToken);

        if (bootstrap.HasCoreClientSecret)
        {
            await SetCoreClientSecretAsync(session, auditService, timeProvider, bootstrap.CoreClientSecret!, cancellationToken);
        }

        if (bootstrap.HasAdmin)
        {
            await EnsureAdminAsync(
                session,
                auditService,
                services.GetRequiredService<IPasswordHasher<User>>(),
                timeProvider,
                bootstrap,
                cancellationToken);
        }

        await session.FlushAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task SetCoreClientSecretAsync(
        ISession session,
        IAuditService auditService,
        TimeProvider timeProvider,
        string secret,
        CancellationToken cancellationToken)
    {
        var application = await session.GetAsync<Application>(CoreApplication.Id, NHibernate.LockMode.Upgrade, cancellationToken)
            ?? throw new InvalidOperationException("The reserved core application does not exist; migration 0008_seed_core_admin.sql was not applied.");

        if (application.SetClientSecret(secret, timeProvider))
        {
            await auditService.RecordAsync(
                new AuditEntry(
                    AuditActions.ApplicationSecretRotated,
                    EntityType: nameof(Application),
                    EntityId: application.Id.ToString(),
                    NewValues: new CoreSecretBootstrapAuditSnapshot(CoreApplication.AppKey, "Bootstrap:CoreClientSecret"),
                    ActorTypeOverride: AuditActorType.System,
                    AppKeyOverride: CoreApplication.AppKey),
                cancellationToken);
            LogCoreSecretSet(logger);
        }

        LogRemoveCoreSecret(logger);
    }

    private async Task EnsureAdminAsync(
        ISession session,
        IAuditService auditService,
        IPasswordHasher<User> passwordHasher,
        TimeProvider timeProvider,
        AdminBootstrapOptions bootstrap,
        CancellationToken cancellationToken)
    {
        var adminUserIds = await session.Query<UserApplicationAccess>()
            .Where(access => access.ApplicationId == CoreApplication.Id
                && access.Roles.Any(role => role.Permissions.Any(permission => permission.Id == CoreApplication.AdminPermissionId)))
            .Select(access => access.UserId)
            .ToListAsync(cancellationToken);
        var hasActiveAdmin = adminUserIds.Count > 0
            && await session.Query<User>()
                .AnyAsync(
                    user => adminUserIds.Contains(user.Id)
                        && user.Status == UserStatus.Active
                        && user.EmailVerifiedAtUtc != null,
                    cancellationToken);

        if (hasActiveAdmin)
        {
            LogAdminAlreadyExists(logger);
            return;
        }

        var adminRole = await session.GetAsync<Role>(CoreApplication.AdminRoleId, cancellationToken)
            ?? throw new InvalidOperationException("The reserved admin role does not exist; migration 0008_seed_core_admin.sql was not applied.");
        var email = User.NormalizeEmail(bootstrap.AdminEmail).Value;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var existingId = await session.Query<User>()
            .Where(existing => existing.Email == email)
            .Select(existing => (Guid?)existing.Id)
            .SingleOrDefaultAsync(cancellationToken);
        User user;

        if (existingId is null)
        {
            var created = User.Create(
                email,
                bootstrap.AdminFullName?.Trim() ?? AdminBootstrapOptions.DefaultAdminFullName,
                bootstrap.AdminPassword,
                passwordHasher);

            if (created.IsFailure)
            {
                throw new InvalidOperationException($"The bootstrap admin could not be created: {created.Error.Code}.");
            }

            user = created.Value;
            user.MarkEmailVerified(now);
            await session.SaveAsync(user, cancellationToken);
            await auditService.RecordUserChangeAsync(AuditActions.UserCreated, null, UserAuditSnapshot.From(user), cancellationToken);
        }
        else
        {
            await RefreshTokenWrites.LockUserAsync(session, existingId.Value, cancellationToken);
            user = await session.GetAsync<User>(existingId.Value, NHibernate.LockMode.Upgrade, cancellationToken);

            if (user.Status == UserStatus.Locked)
            {
                LogExistingUserLocked(logger, user.Id);
                return;
            }

            if (user.EmailVerifiedAtUtc is null)
            {
                var before = UserAuditSnapshot.From(user);
                var passwordResult = user.SetPassword(bootstrap.AdminPassword, passwordHasher);

                if (passwordResult.IsFailure)
                {
                    throw new InvalidOperationException($"The bootstrap admin password could not be set: {passwordResult.Error.Code}.");
                }

                user.MarkEmailVerified(now);
                await auditService.RecordUserChangeAsync(AuditActions.UserUpdated, before, UserAuditSnapshot.From(user), cancellationToken);
                LogUnverifiedUserTakenOver(logger, user.Id);
            }
        }

        var access = await session.Query<UserApplicationAccess>()
            .SingleOrDefaultAsync(
                candidate => candidate.UserId == user.Id && candidate.ApplicationId == CoreApplication.Id,
                cancellationToken);

        if (access is null)
        {
            access = UserApplicationAccess.Grant(user.Id, CoreApplication.Id, [adminRole]).Value;
            await session.SaveAsync(access, cancellationToken);
            await auditService.RecordAccessChangeAsync(
                AuditActions.AccessGranted,
                CoreApplication.AppKey,
                null,
                AccessAuditSnapshot.From(access),
                cancellationToken);
        }
        else
        {
            var before = AccessAuditSnapshot.From(access);
            var assigned = access.AssignRole(adminRole);

            if (assigned.IsFailure)
            {
                throw new InvalidOperationException($"The bootstrap admin role could not be assigned: {assigned.Error.Code}.");
            }

            await auditService.RecordAccessChangeAsync(
                AuditActions.AccessRolesChanged,
                CoreApplication.AppKey,
                before,
                AccessAuditSnapshot.From(access),
                cancellationToken);
        }

        LogAdminBootstrapped(logger, user.Id);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Bootstrap set the client secret of the core application")]
    private static partial void LogCoreSecretSet(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Remove Bootstrap__CoreClientSecret: while it is set, every restart resets the core application secret and undoes any rotation")]
    private static partial void LogRemoveCoreSecret(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "An active core admin already exists; remove Bootstrap__AdminEmail and Bootstrap__AdminPassword")]
    private static partial void LogAdminAlreadyExists(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Bootstrap admin not created: the existing user {UserId} with that email is locked")]
    private static partial void LogExistingUserLocked(ILogger logger, Guid userId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Bootstrap set the password of the unverified user {UserId} and verified it, so whoever registered that email cannot use the admin account")]
    private static partial void LogUnverifiedUserTakenOver(ILogger logger, Guid userId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Bootstrap gave core admin access to user {UserId}; remove Bootstrap__AdminEmail and Bootstrap__AdminPassword")]
    private static partial void LogAdminBootstrapped(ILogger logger, Guid userId);
}

/// <summary>
/// Audit snapshot of the core secret set by the bootstrap; never contains the secret or its hash.
/// </summary>
public sealed record CoreSecretBootstrapAuditSnapshot(string AppKey, string Source);
