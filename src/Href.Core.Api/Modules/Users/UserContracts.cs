namespace Href.Core.Api.Modules.Users;

public sealed record CreateUserRequest(string? Email, string? FullName, string? Password);

public sealed record UpdateUserRequest(string? Email, string? FullName);

public sealed record LockUserRequest(string? Reason);

/// <summary>
/// Filters and paging of the user list. <c>Search</c> matches part of the email or the full name, ignoring case.
/// </summary>
public sealed record ListUsersQuery(string? Search, string? Status, int Page, int PageSize);

/// <summary>
/// A user as returned by the admin API. It never includes the password hash or the security stamp.
/// </summary>
public sealed record UserResponse(
    Guid Id,
    string Email,
    string FullName,
    string Status,
    DateTime? EmailVerifiedAtUtc,
    DateTime? LockedAtUtc,
    string? LockReason,
    DateTime? LastLoginAtUtc,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc)
{
    public static UserResponse From(User user) =>
        new(
            user.Id,
            user.Email,
            user.FullName,
            user.Status.ToString(),
            user.EmailVerifiedAtUtc,
            user.LockedAtUtc,
            user.LockReason,
            user.LastLoginAtUtc,
            user.CreatedAtUtc,
            user.UpdatedAtUtc);
}

public sealed record UserDetailResponse(
    Guid Id,
    string Email,
    string FullName,
    string Status,
    DateTime? EmailVerifiedAtUtc,
    DateTime? LockedAtUtc,
    string? LockReason,
    DateTime? LastLoginAtUtc,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    IReadOnlyList<UserApplicationResponse> Applications)
{
    public static UserDetailResponse From(User user, IReadOnlyList<UserApplicationResponse> applications) =>
        new(
            user.Id,
            user.Email,
            user.FullName,
            user.Status.ToString(),
            user.EmailVerifiedAtUtc,
            user.LockedAtUtc,
            user.LockReason,
            user.LastLoginAtUtc,
            user.CreatedAtUtc,
            user.UpdatedAtUtc,
            applications);
}

/// <summary>
/// An application the user can access, with the names of the user's roles in it (ordinal order).
/// </summary>
public sealed record UserApplicationResponse(Guid ApplicationId, string AppKey, string Name, IReadOnlyList<string> Roles);

public sealed record UserPageResponse(IReadOnlyList<UserResponse> Items, int Page, int PageSize, long TotalCount);
