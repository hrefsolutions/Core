using System.Globalization;
using Href.Core.Api.Common.Persistence;
using Href.Core.Api.Common.Results;
using Href.Core.Api.Modules.Applications;
using Href.Core.Api.Modules.Audit;
using Href.Core.Api.Modules.Auth;
using Href.Core.Api.Modules.Authorization;
using Href.Core.Api.Modules.Notifications;
using Microsoft.AspNetCore.Identity;
using NHibernate;
using NHibernate.Linq;
using ISession = NHibernate.ISession;

namespace Href.Core.Api.Modules.Users;

/// <summary>
/// Admin invitations: the admin creates the user without choosing a password and Core emails a one-time link
/// (<c>user-invitation</c>, <see cref="UserToken.InvitationLifetime"/>) to the page of one application, which calls
/// <c>POST /api/auth/accept-invitation</c> so the user picks the password and the email is verified. The invited user
/// gets a random password nobody knows, so it cannot log in before accepting. Each write locks the application row
/// (like every access write) and runs in one transaction with its audit entries and the queued email.
/// </summary>
public sealed class InvitationService(
    ISession session,
    IPasswordHasher<User> passwordHasher,
    NotificationService notificationService,
    IAuditService auditService,
    TimeProvider timeProvider)
{
    public const string InvitationTemplateKey = "user-invitation";

    /// <summary>
    /// Creates the unverified user, grants it access to the application (default role when <c>RoleIds</c> is omitted),
    /// issues the invitation token and queues the email. Records <c>USER_INVITED</c> and <c>ACCESS_GRANTED</c>.
    /// </summary>
    public async Task<Result<UserResponse>> InviteAsync(
        InviteUserRequest request,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!CallbackUrl.TryParse(request.AcceptInvitationUrl, out var acceptUrl))
        {
            return AuthErrors.InvalidCallbackUrl;
        }

        var created = User.Create(request.Email, request.FullName, ClientSecretGenerator.Generate(), passwordHasher);

        if (created.IsFailure)
        {
            return created.Error;
        }

        var user = created.Value;

        try
        {
            using var transaction = session.BeginTransaction();

            var application = await AuthorizationAdminQueries.FindApplicationAsync(session, request.AppKey ?? string.Empty, lockForUpdate: true, cancellationToken);

            if (application is null)
            {
                return ApplicationErrors.NotFound;
            }

            var rolesResult = request.RoleIds is null
                ? await DefaultRolesAsync(application.Id, cancellationToken)
                : await AuthorizationAdminQueries.ResolveRolesAsync(session, application.Id, request.RoleIds, cancellationToken);

            if (rolesResult.IsFailure)
            {
                return rolesResult.Error;
            }

            if (await session.Query<User>().AnyAsync(existing => existing.Email == user.Email, cancellationToken))
            {
                return UserErrors.EmailTaken;
            }

            var access = UserApplicationAccess.Grant(user.Id, application.Id, rolesResult.Value).Value;
            var issued = UserToken.Issue(user.Id, application.Id, UserTokenPurpose.Invitation, timeProvider.GetUtcNow().UtcDateTime, UserToken.InvitationLifetime);

            await session.SaveAsync(user, cancellationToken);
            await session.SaveAsync(access, cancellationToken);
            await session.SaveAsync(issued.Token, cancellationToken);
            await auditService.RecordUserChangeAsync(AuditActions.UserInvited, null, UserAuditSnapshot.From(user), cancellationToken);
            await auditService.RecordAccessChangeAsync(AuditActions.AccessGranted, application.AppKey, null, AccessAuditSnapshot.From(access), cancellationToken);

            var queued = await QueueInvitationAsync(application, user, acceptUrl, issued.PlainToken, correlationId, cancellationToken);

            if (queued.IsFailure)
            {
                return queued.Error;
            }

            await session.FlushAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception exception) when (PostgresErrors.IsUniqueViolation(exception, User.EmailUniqueIndexName))
        {
            return UserErrors.EmailTaken;
        }

        return UserResponse.From(user);
    }

    /// <summary>
    /// Revokes the user's open invitation links (of every application) and emails a new one for an application the user
    /// already has access to. Only for an active user whose email is not verified yet. Records <c>USER_INVITATION_RESENT</c>.
    /// Lock order: the application row, then the user row, then the token rows.
    /// </summary>
    public async Task<Result> ResendAsync(
        Guid userId,
        ResendInvitationRequest request,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!CallbackUrl.TryParse(request.AcceptInvitationUrl, out var acceptUrl))
        {
            return AuthErrors.InvalidCallbackUrl;
        }

        using var transaction = session.BeginTransaction();

        var application = await AuthorizationAdminQueries.FindApplicationAsync(session, request.AppKey ?? string.Empty, lockForUpdate: true, cancellationToken);

        if (application is null)
        {
            return ApplicationErrors.NotFound;
        }

        var user = await session.GetAsync<User>(userId, LockMode.Upgrade, cancellationToken);

        if (user is null)
        {
            return UserErrors.NotFound;
        }

        if (user.Status != UserStatus.Active)
        {
            return UserErrors.AlreadyLocked;
        }

        if (user.EmailVerifiedAtUtc is not null)
        {
            return UserErrors.EmailAlreadyVerified;
        }

        var hasAccess = await session.Query<UserApplicationAccess>()
            .AnyAsync(access => access.UserId == user.Id && access.ApplicationId == application.Id, cancellationToken);

        if (!hasAccess)
        {
            return AuthorizationErrors.AccessNotFound;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        await UserTokenWrites.RevokeOpenAsync(session, user.Id, UserTokenPurpose.Invitation, exceptTokenId: null, now, cancellationToken);

        var issued = UserToken.Issue(user.Id, application.Id, UserTokenPurpose.Invitation, now, UserToken.InvitationLifetime);
        await session.SaveAsync(issued.Token, cancellationToken);
        await auditService.RecordUserChangeAsync(AuditActions.UserInvitationResent, null, UserAuditSnapshot.From(user), cancellationToken);

        var queued = await QueueInvitationAsync(application, user, acceptUrl, issued.PlainToken, correlationId, cancellationToken);

        if (queued.IsFailure)
        {
            return queued.Error;
        }

        await session.FlushAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Result.Success();
    }

    private async Task<Result<List<Role>>> DefaultRolesAsync(Guid applicationId, CancellationToken cancellationToken)
    {
        var defaultRole = await AuthorizationAdminQueries.FindDefaultRoleAsync(session, applicationId, cancellationToken);
        return defaultRole is null ? new List<Role>() : new List<Role> { defaultRole };
    }

    private Task<Result<Notification>> QueueInvitationAsync(
        Application application,
        User user,
        Uri acceptUrl,
        string plainToken,
        string? correlationId,
        CancellationToken cancellationToken) =>
        notificationService.QueueTemplateAsync(
            application.AppKey,
            new EmailRecipientRequest(user.Email, user.FullName),
            InvitationTemplateKey,
            new Dictionary<string, string>
            {
                ["UserName"] = user.FullName,
                ["AppName"] = application.Name,
                ["InvitationUrl"] = CallbackUrl.WithToken(acceptUrl, plainToken),
                ["ExpiresInHours"] = ((int)UserToken.InvitationLifetime.TotalHours).ToString(CultureInfo.InvariantCulture),
            },
            notifyOnFailureTo: null,
            correlationId,
            cancellationToken);
}
