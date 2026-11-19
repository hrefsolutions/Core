using Href.Core.Api.Common.Domain;
using Href.Core.Api.Common.Results;
using Microsoft.AspNetCore.Identity;

namespace Href.Core.Api.Modules.Users;

/// <summary>
/// Global user account, shared by every application. Access to each application is granted separately.
/// The email is stored trimmed and in lower case, so its unique index is case-insensitive in practice.
/// </summary>
public class User : Entity, IAuditableEntity
{
    public const int EmailMaxLength = 254;
    public const int FullNameMaxLength = 150;
    public const int PasswordHashMaxLength = 500;
    public const int LockReasonMaxLength = 500;
    public const string EmailUniqueIndexName = "ux_users_email";

    protected User()
    {
    }

    public virtual string Email { get; protected set; } = string.Empty;

    public virtual string FullName { get; protected set; } = string.Empty;

    public virtual string PasswordHash { get; protected set; } = string.Empty;

    public virtual DateTime? EmailVerifiedAtUtc { get; protected set; }

    public virtual UserStatus Status { get; protected set; } = UserStatus.Active;

    public virtual DateTime? LockedAtUtc { get; protected set; }

    public virtual string? LockReason { get; protected set; }

    /// <summary>
    /// Random value regenerated when the credentials or the access state change (password, email, lock, unlock),
    /// so tokens issued with a previous value can be rejected.
    /// </summary>
    public virtual Guid SecurityStamp { get; protected set; }

    public virtual DateTime? LastLoginAtUtc { get; protected set; }

    public virtual DateTime CreatedAtUtc { get; protected set; }

    public virtual DateTime UpdatedAtUtc { get; protected set; }

    public static Result<User> Create(
        string? email,
        string? fullName,
        string? password,
        IPasswordHasher<User> passwordHasher)
    {
        ArgumentNullException.ThrowIfNull(passwordHasher);

        var emailResult = NormalizeEmail(email);

        if (emailResult.IsFailure)
        {
            return emailResult.Error;
        }

        var fullNameResult = NormalizeFullName(fullName);

        if (fullNameResult.IsFailure)
        {
            return fullNameResult.Error;
        }

        var user = new User
        {
            Email = emailResult.Value,
            FullName = fullNameResult.Value,
            Status = UserStatus.Active,
        };

        var passwordResult = user.SetPassword(password, passwordHasher);

        if (passwordResult.IsFailure)
        {
            return passwordResult.Error;
        }

        return user;
    }

    public virtual Result SetPassword(string? password, IPasswordHasher<User> passwordHasher)
    {
        ArgumentNullException.ThrowIfNull(passwordHasher);

        var policyResult = PasswordPolicy.Validate(password);

        if (policyResult.IsFailure)
        {
            return policyResult;
        }

        PasswordHash = passwordHasher.HashPassword(this, password!);
        RegenerateSecurityStamp();

        return Result.Success();
    }

    /// <summary>
    /// Verifies a password. When the stored hash uses outdated settings, it is replaced with a new hash
    /// of the same password (the entity becomes dirty and is saved on commit); the security stamp does not change.
    /// </summary>
    public virtual bool VerifyPassword(string? password, IPasswordHasher<User> passwordHasher)
    {
        ArgumentNullException.ThrowIfNull(passwordHasher);

        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(PasswordHash))
        {
            return false;
        }

        var verification = passwordHasher.VerifyHashedPassword(this, PasswordHash, password);

        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
        {
            PasswordHash = passwordHasher.HashPassword(this, password);
            return true;
        }

        return verification == PasswordVerificationResult.Success;
    }

    public virtual Result UpdateProfile(string? fullName)
    {
        var fullNameResult = NormalizeFullName(fullName);

        if (fullNameResult.IsFailure)
        {
            return fullNameResult.Error;
        }

        FullName = fullNameResult.Value;
        return Result.Success();
    }

    /// <summary>
    /// Changes the email. A different address clears the verification and regenerates the security stamp;
    /// the same address (after normalization) changes nothing.
    /// </summary>
    public virtual Result ChangeEmail(string? email)
    {
        var emailResult = NormalizeEmail(email);

        if (emailResult.IsFailure)
        {
            return emailResult.Error;
        }

        if (string.Equals(emailResult.Value, Email, StringComparison.Ordinal))
        {
            return Result.Success();
        }

        Email = emailResult.Value;
        EmailVerifiedAtUtc = null;
        RegenerateSecurityStamp();

        return Result.Success();
    }

    public virtual Result MarkEmailVerified(DateTime utcNow)
    {
        EnsureUtc(utcNow);

        if (EmailVerifiedAtUtc.HasValue)
        {
            return UserErrors.EmailAlreadyVerified;
        }

        EmailVerifiedAtUtc = utcNow;
        return Result.Success();
    }

    public virtual Result Lock(string? reason, DateTime utcNow)
    {
        EnsureUtc(utcNow);

        if (Status == UserStatus.Locked)
        {
            return UserErrors.AlreadyLocked;
        }

        var normalizedReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();

        if (normalizedReason is not null
            && (normalizedReason.Length > LockReasonMaxLength || TextRules.HasNul(normalizedReason)))
        {
            return UserErrors.InvalidLockReason;
        }

        Status = UserStatus.Locked;
        LockedAtUtc = utcNow;
        LockReason = normalizedReason;
        RegenerateSecurityStamp();

        return Result.Success();
    }

    public virtual Result Unlock()
    {
        if (Status != UserStatus.Locked)
        {
            return UserErrors.NotLocked;
        }

        Status = UserStatus.Active;
        LockedAtUtc = null;
        LockReason = null;
        RegenerateSecurityStamp();

        return Result.Success();
    }

    public virtual void RegisterLogin(DateTime utcNow)
    {
        EnsureUtc(utcNow);
        LastLoginAtUtc = utcNow;
    }

    private static Result<string> NormalizeEmail(string? email)
    {
        var normalized = email?.Trim().ToLowerInvariant() ?? string.Empty;

        if (!TextRules.IsSingleEmailAddress(normalized, EmailMaxLength))
        {
            return UserErrors.InvalidEmail;
        }

        return normalized;
    }

    private static Result<string> NormalizeFullName(string? fullName)
    {
        var normalized = fullName?.Trim() ?? string.Empty;

        if (normalized.Length is 0 or > FullNameMaxLength || TextRules.HasControlOrLineSeparator(normalized))
        {
            return UserErrors.InvalidFullName;
        }

        return normalized;
    }

    private static void EnsureUtc(DateTime utcNow)
    {
        if (utcNow.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("The timestamp must have DateTimeKind.Utc.", nameof(utcNow));
        }
    }

    private void RegenerateSecurityStamp() => SecurityStamp = Guid.NewGuid();
}
