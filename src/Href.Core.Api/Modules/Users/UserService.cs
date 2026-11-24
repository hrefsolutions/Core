using Href.Core.Api.Common.Domain;
using Href.Core.Api.Common.Persistence;
using Href.Core.Api.Common.Results;
using Href.Core.Api.Modules.Applications;
using Href.Core.Api.Modules.Audit;
using Href.Core.Api.Modules.Auth;
using Href.Core.Api.Modules.Authorization;
using Microsoft.AspNetCore.Identity;
using NHibernate;
using NHibernate.Linq;
using ISession = NHibernate.ISession;

namespace Href.Core.Api.Modules.Users;

/// <summary>
/// Admin use cases for global users over <see cref="ISession"/>. Writes run in one explicit transaction that also records
/// the audit entry. Users are locked, never deleted.
/// </summary>
public sealed class UserService(
    ISession session,
    IPasswordHasher<User> passwordHasher,
    IAuditService auditService,
    TimeProvider timeProvider)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;
    public const int SearchMaxLength = 254;

    /// <summary>
    /// Creates a user with its email already verified (admin-created users skip the verification flow).
    /// </summary>
    public async Task<Result<UserResponse>> CreateAsync(CreateUserRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var createResult = User.Create(request.Email, request.FullName, request.Password, passwordHasher);

        if (createResult.IsFailure)
        {
            return createResult.Error;
        }

        var user = createResult.Value;
        user.MarkEmailVerified(timeProvider.GetUtcNow().UtcDateTime);

        using var transaction = session.BeginTransaction();

        if (await EmailExistsAsync(user.Email, excludedUserId: null, cancellationToken))
        {
            return UserErrors.EmailTaken;
        }

        try
        {
            await session.SaveAsync(user, cancellationToken);
            await auditService.RecordUserChangeAsync(AuditActions.UserCreated, null, UserAuditSnapshot.From(user), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception exception) when (PostgresErrors.IsUniqueViolation(exception, User.EmailUniqueIndexName))
        {
            return UserErrors.EmailTaken;
        }

        return UserResponse.From(user);
    }

    public async Task<Result<UserPageResponse>> ListAsync(ListUsersQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Page < 1)
        {
            return UserErrors.InvalidPage;
        }

        if (request.PageSize is < 1 or > MaxPageSize)
        {
            return UserErrors.InvalidPageSize;
        }

        UserStatus? status = null;

        if (request.Status is not null)
        {
            if (!TryParseStatusName(request.Status, out var parsedStatus))
            {
                return UserErrors.InvalidStatus;
            }

            status = parsedStatus;
        }

        var search = string.IsNullOrWhiteSpace(request.Search) ? null : request.Search.Trim();

        if (search is not null && (search.Length > SearchMaxLength || TextRules.HasControlOrLineSeparator(search)))
        {
            return UserErrors.InvalidSearch;
        }

        var query = session.Query<User>();

        if (status is not null)
        {
            var statusValue = status.Value;
            query = query.Where(user => user.Status == statusValue);
        }

        if (search is not null)
        {
            var pattern = EscapeLikePattern(search.ToLowerInvariant());
            query = query.Where(user => user.Email.Contains(pattern) || user.FullName.ToLower().Contains(pattern));
        }

        var totalCount = await query.LongCountAsync(cancellationToken);
        var offset = (long)(request.Page - 1) * request.PageSize;

        if (offset >= totalCount)
        {
            return new UserPageResponse([], request.Page, request.PageSize, totalCount);
        }

        var users = await query
            .OrderBy(user => user.Email)
            .ThenBy(user => user.Id)
            .Skip((int)offset)
            .Take(request.PageSize)
            .ToListAsync(cancellationToken);

        return new UserPageResponse(users.Select(UserResponse.From).ToList(), request.Page, request.PageSize, totalCount);
    }

    public async Task<Result<UserDetailResponse>> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var user = await session.GetAsync<User>(id, cancellationToken);

        if (user is null)
        {
            return UserErrors.NotFound;
        }

        var accesses = await session.Query<UserApplicationAccess>()
            .Where(access => access.UserId == id)
            .ToListAsync(cancellationToken);

        var applicationIds = accesses.Select(access => access.ApplicationId).ToList();
        List<Application> applications = applicationIds.Count == 0
            ? []
            : await session.Query<Application>()
                .Where(application => applicationIds.Contains(application.Id))
                .ToListAsync(cancellationToken);
        var applicationsById = applications.ToDictionary(application => application.Id);

        var items = accesses
            .Where(access => applicationsById.ContainsKey(access.ApplicationId))
            .Select(access =>
            {
                var application = applicationsById[access.ApplicationId];
                return new UserApplicationResponse(application.Id, application.AppKey, application.Name, access.GetRoleNames());
            })
            .OrderBy(item => item.AppKey, StringComparer.Ordinal)
            .ToList();

        return UserDetailResponse.From(user, items);
    }

    /// <summary>
    /// Replaces the full name and the email. A different email clears the verification, revokes the user's open one-time
    /// tokens (email verification and password reset links sent to the old address) and is audited as
    /// <c>USER_EMAIL_CHANGED</c>; a name-only change as <c>USER_UPDATED</c>; no change records nothing.
    /// </summary>
    public async Task<Result<UserResponse>> UpdateAsync(Guid id, UpdateUserRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var transaction = session.BeginTransaction();

        var user = await session.GetAsync<User>(id, LockMode.Upgrade, cancellationToken);

        if (user is null)
        {
            return UserErrors.NotFound;
        }

        var emailResult = User.NormalizeEmail(request.Email);

        if (emailResult.IsFailure)
        {
            return emailResult.Error;
        }

        var emailChanged = !string.Equals(emailResult.Value, user.Email, StringComparison.Ordinal);

        if (emailChanged && await EmailExistsAsync(emailResult.Value, user.Id, cancellationToken))
        {
            return UserErrors.EmailTaken;
        }

        var before = UserAuditSnapshot.From(user);
        var profileResult = user.UpdateProfile(request.FullName);

        if (profileResult.IsFailure)
        {
            return profileResult.Error;
        }

        user.ChangeEmail(emailResult.Value);

        var after = UserAuditSnapshot.From(user);

        if (after == before)
        {
            return UserResponse.From(user);
        }

        var action = emailChanged ? AuditActions.UserEmailChanged : AuditActions.UserUpdated;

        try
        {
            await auditService.RecordUserChangeAsync(action, before, after, cancellationToken);

            if (emailChanged)
            {
                await UserTokenWrites.RevokeOpenAsync(session, user.Id, purpose: null, exceptTokenId: null, timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
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
    /// Locks the user and, in the same transaction, revokes all its active sessions as <c>UserLocked</c>
    /// (<c>USER_LOCKED</c>, then <c>SESSIONS_REVOKED</c> when at least one was revoked). The per-user refresh token lock
    /// is taken before the row lock, in the same order as the login, so they cannot deadlock.
    /// </summary>
    public Task<Result<UserResponse>> LockAsync(Guid id, LockUserRequest? request, CancellationToken cancellationToken) =>
        ChangeLockStateAsync(
            id,
            user => user.Lock(request?.Reason, timeProvider.GetUtcNow().UtcDateTime),
            AuditActions.UserLocked,
            revokeSessions: true,
            cancellationToken);

    public Task<Result<UserResponse>> UnlockAsync(Guid id, CancellationToken cancellationToken) =>
        ChangeLockStateAsync(id, user => user.Unlock(), AuditActions.UserUnlocked, revokeSessions: false, cancellationToken);

    /// <summary>
    /// Accepts only the exact enum names (<c>Active</c>, <c>Locked</c>); numeric or combined values are rejected.
    /// </summary>
    internal static bool TryParseStatusName(string? value, out UserStatus status)
    {
        foreach (var candidate in Enum.GetValues<UserStatus>())
        {
            if (string.Equals(candidate.ToString(), value, StringComparison.Ordinal))
            {
                status = candidate;
                return true;
            }
        }

        status = default;
        return false;
    }

    /// <summary>
    /// Escapes the PostgreSQL <c>LIKE</c> wildcards so the search text matches literally (backslash is PostgreSQL's default escape).
    /// </summary>
    internal static string EscapeLikePattern(string value) =>
        value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private async Task<Result<UserResponse>> ChangeLockStateAsync(
        Guid id,
        Func<User, Result> change,
        string action,
        bool revokeSessions,
        CancellationToken cancellationToken)
    {
        using var transaction = session.BeginTransaction();

        if (revokeSessions)
        {
            await RefreshTokenWrites.LockUserAsync(session, id, cancellationToken);
        }

        var user = await session.GetAsync<User>(id, LockMode.Upgrade, cancellationToken);

        if (user is null)
        {
            return UserErrors.NotFound;
        }

        var before = UserAuditSnapshot.From(user);
        var changeResult = change(user);

        if (changeResult.IsFailure)
        {
            return changeResult.Error;
        }

        await auditService.RecordUserChangeAsync(action, before, UserAuditSnapshot.From(user), cancellationToken);

        if (revokeSessions)
        {
            var revoked = await RefreshTokenWrites.RevokeActiveSessionsAsync(
                session,
                user.Id,
                applicationId: null,
                timeProvider.GetUtcNow().UtcDateTime,
                RefreshTokenRevocationReason.UserLocked,
                cancellationToken);

            if (revoked > 0)
            {
                await auditService.RecordSessionsRevokedAsync(
                    user.Id,
                    appKey: null,
                    RefreshTokenRevocationReason.UserLocked,
                    revoked,
                    cancellationToken);
            }
        }

        await session.FlushAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return UserResponse.From(user);
    }

    private Task<bool> EmailExistsAsync(string email, Guid? excludedUserId, CancellationToken cancellationToken) =>
        excludedUserId is null
            ? session.Query<User>().AnyAsync(user => user.Email == email, cancellationToken)
            : session.Query<User>().AnyAsync(user => user.Email == email && user.Id != excludedUserId.Value, cancellationToken);
}
