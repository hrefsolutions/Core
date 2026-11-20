namespace Href.Core.Api.Modules.Users;

/// <summary>
/// Audit snapshot of a user. It never includes the password hash or the security stamp;
/// the last login time is left out so that audit diffs only show administrative changes.
/// </summary>
public sealed record UserAuditSnapshot(
    Guid Id,
    string Email,
    string FullName,
    string Status,
    DateTime? EmailVerifiedAtUtc,
    DateTime? LockedAtUtc,
    string? LockReason)
{
    /// <summary>
    /// Copies the current values of <paramref name="user"/>.
    /// </summary>
    public static UserAuditSnapshot From(User user)
    {
        ArgumentNullException.ThrowIfNull(user);

        return new UserAuditSnapshot(
            user.Id,
            user.Email,
            user.FullName,
            user.Status.ToString(),
            user.EmailVerifiedAtUtc,
            user.LockedAtUtc,
            user.LockReason);
    }
}
