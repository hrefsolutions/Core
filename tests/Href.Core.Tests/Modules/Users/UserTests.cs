using Href.Core.Api.Modules.Users;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Href.Core.Tests.Modules.Users;

public sealed class UserTests
{
    private const string ValidPassword = "correct horse battery";

    private static readonly DateTime Now = new(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc);

    private static readonly IPasswordHasher<User> FastHasher = CreateHasher(iterationCount: 1_000);

    [Fact]
    public void Create_WithValidInputs_CreatesActiveUnverifiedUserWithVerifiablePassword()
    {
        var result = User.Create("ana@example.com", "Ana Pérez", ValidPassword, FastHasher);

        Assert.True(result.IsSuccess);
        var user = result.Value;
        Assert.NotEqual(Guid.Empty, user.Id);
        Assert.Equal("ana@example.com", user.Email);
        Assert.Equal("Ana Pérez", user.FullName);
        Assert.Equal(UserStatus.Active, user.Status);
        Assert.Null(user.EmailVerifiedAtUtc);
        Assert.Null(user.LockedAtUtc);
        Assert.Null(user.LockReason);
        Assert.Null(user.LastLoginAtUtc);
        Assert.NotEqual(Guid.Empty, user.SecurityStamp);
        Assert.NotEqual(ValidPassword, user.PasswordHash);
        Assert.DoesNotContain(ValidPassword, user.PasswordHash, StringComparison.Ordinal);
        Assert.True(user.VerifyPassword(ValidPassword, FastHasher));
        Assert.False(user.VerifyPassword("wrong horse battery", FastHasher));
    }

    [Theory]
    [InlineData("  Ana@Example.COM  ", "ana@example.com")]
    [InlineData("ANA.PEREZ+TAG@EXAMPLE.COM", "ana.perez+tag@example.com")]
    public void Create_NormalizesEmailByTrimmingAndLowerCasing(string email, string expected)
    {
        var user = User.Create(email, "Ana", ValidPassword, FastHasher).Value;

        Assert.Equal(expected, user.Email);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-email")]
    [InlineData("Ana <ana@example.com>")]
    [InlineData("ana@example.com, bob@example.com")]
    [InlineData("ana@exam\nple.com")]
    public void Create_WithInvalidEmail_ReturnsInvalidEmail(string? email)
    {
        var result = User.Create(email, "Ana", ValidPassword, FastHasher);

        Assert.True(result.IsFailure);
        Assert.Equal(UserErrors.InvalidEmail, result.Error);
    }

    [Fact]
    public void Create_WithEmailLongerThan254Characters_ReturnsInvalidEmail()
    {
        var email = new string('a', 64) + "@" + new string('b', 63) + "." + new string('c', 63) + "." + new string('d', 63) + ".com";

        Assert.True(email.Length > User.EmailMaxLength);
        var result = User.Create(email, "Ana", ValidPassword, FastHasher);

        Assert.True(result.IsFailure);
        Assert.Equal(UserErrors.InvalidEmail, result.Error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ana\nPérez")]
    [InlineData("Ana\U00002028Pérez")]
    public void Create_WithInvalidFullName_ReturnsInvalidFullName(string? fullName)
    {
        var result = User.Create("ana@example.com", fullName, ValidPassword, FastHasher);

        Assert.True(result.IsFailure);
        Assert.Equal(UserErrors.InvalidFullName, result.Error);
    }

    [Fact]
    public void Create_TrimsFullNameAndAcceptsMaximumLength()
    {
        var maximum = new string('n', User.FullNameMaxLength);

        Assert.Equal("Ana Pérez", User.Create("ana@example.com", "  Ana Pérez  ", ValidPassword, FastHasher).Value.FullName);
        Assert.True(User.Create("ana@example.com", maximum, ValidPassword, FastHasher).IsSuccess);
        Assert.Equal(
            UserErrors.InvalidFullName,
            User.Create("ana@example.com", maximum + "n", ValidPassword, FastHasher).Error);
    }

    [Fact]
    public void Create_WithWeakPassword_ReturnsWeakPassword()
    {
        var result = User.Create("ana@example.com", "Ana", "short", FastHasher);

        Assert.True(result.IsFailure);
        Assert.Equal(UserErrors.WeakPassword, result.Error);
    }

    [Fact]
    public void Create_GeneratesDistinctSecurityStampsAndSaltedHashes()
    {
        var first = User.Create("ana@example.com", "Ana", ValidPassword, FastHasher).Value;
        var second = User.Create("bob@example.com", "Bob", ValidPassword, FastHasher).Value;

        Assert.NotEqual(first.SecurityStamp, second.SecurityStamp);
        Assert.NotEqual(first.PasswordHash, second.PasswordHash);
    }

    [Fact]
    public void SetPassword_WithValidPassword_ReplacesHashAndRegeneratesSecurityStamp()
    {
        var user = CreateUser();
        var previousHash = user.PasswordHash;
        var previousStamp = user.SecurityStamp;

        var result = user.SetPassword("another strong password", FastHasher);

        Assert.True(result.IsSuccess);
        Assert.NotEqual(previousHash, user.PasswordHash);
        Assert.NotEqual(previousStamp, user.SecurityStamp);
        Assert.True(user.VerifyPassword("another strong password", FastHasher));
        Assert.False(user.VerifyPassword(ValidPassword, FastHasher));
    }

    [Fact]
    public void SetPassword_WithWeakPassword_KeepsHashAndSecurityStamp()
    {
        var user = CreateUser();
        var previousHash = user.PasswordHash;
        var previousStamp = user.SecurityStamp;

        var result = user.SetPassword("tiny", FastHasher);

        Assert.True(result.IsFailure);
        Assert.Equal(UserErrors.WeakPassword, result.Error);
        Assert.Equal(previousHash, user.PasswordHash);
        Assert.Equal(previousStamp, user.SecurityStamp);
        Assert.True(user.VerifyPassword(ValidPassword, FastHasher));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void VerifyPassword_WithMissingPassword_ReturnsFalse(string? password)
    {
        var user = CreateUser();

        Assert.False(user.VerifyPassword(password, FastHasher));
    }

    [Fact]
    public void VerifyPassword_WhenHashNeedsRehash_RehashesWithCurrentSettingsAndKeepsSecurityStamp()
    {
        var currentHasher = CreateHasher(iterationCount: 5_000);
        var user = CreateUser();
        var legacyHash = user.PasswordHash;
        var stamp = user.SecurityStamp;

        Assert.Equal(
            PasswordVerificationResult.SuccessRehashNeeded,
            currentHasher.VerifyHashedPassword(user, legacyHash, ValidPassword));

        Assert.True(user.VerifyPassword(ValidPassword, currentHasher));

        Assert.NotEqual(legacyHash, user.PasswordHash);
        Assert.Equal(stamp, user.SecurityStamp);
        Assert.Equal(
            PasswordVerificationResult.Success,
            currentHasher.VerifyHashedPassword(user, user.PasswordHash, ValidPassword));
    }

    [Fact]
    public void VerifyPassword_WithWrongPassword_DoesNotRehash()
    {
        var currentHasher = CreateHasher(iterationCount: 5_000);
        var user = CreateUser();
        var legacyHash = user.PasswordHash;

        Assert.False(user.VerifyPassword("wrong horse battery", currentHasher));
        Assert.Equal(legacyHash, user.PasswordHash);
    }

    [Fact]
    public void UpdateProfile_WithValidName_ChangesNameOnly()
    {
        var user = CreateUser();
        var stamp = user.SecurityStamp;

        var result = user.UpdateProfile("  Ana María  ");

        Assert.True(result.IsSuccess);
        Assert.Equal("Ana María", user.FullName);
        Assert.Equal(stamp, user.SecurityStamp);
    }

    [Fact]
    public void UpdateProfile_WithInvalidName_KeepsPreviousName()
    {
        var user = CreateUser();

        var result = user.UpdateProfile(" ");

        Assert.Equal(UserErrors.InvalidFullName, result.Error);
        Assert.Equal("Ana", user.FullName);
    }

    [Fact]
    public void ChangeEmail_WithNewEmail_NormalizesClearsVerificationAndRegeneratesSecurityStamp()
    {
        var user = CreateUser();
        user.MarkEmailVerified(Now);
        var stamp = user.SecurityStamp;

        var result = user.ChangeEmail("  New.Address@Example.com ");

        Assert.True(result.IsSuccess);
        Assert.Equal("new.address@example.com", user.Email);
        Assert.Null(user.EmailVerifiedAtUtc);
        Assert.NotEqual(stamp, user.SecurityStamp);
    }

    [Fact]
    public void ChangeEmail_WithSameEmailInOtherCase_KeepsVerificationAndSecurityStamp()
    {
        var user = CreateUser();
        user.MarkEmailVerified(Now);
        var stamp = user.SecurityStamp;

        var result = user.ChangeEmail("ANA@EXAMPLE.COM");

        Assert.True(result.IsSuccess);
        Assert.Equal("ana@example.com", user.Email);
        Assert.Equal(Now, user.EmailVerifiedAtUtc);
        Assert.Equal(stamp, user.SecurityStamp);
    }

    [Fact]
    public void ChangeEmail_WithInvalidEmail_KeepsEverything()
    {
        var user = CreateUser();
        user.MarkEmailVerified(Now);

        var result = user.ChangeEmail("broken");

        Assert.Equal(UserErrors.InvalidEmail, result.Error);
        Assert.Equal("ana@example.com", user.Email);
        Assert.Equal(Now, user.EmailVerifiedAtUtc);
    }

    [Fact]
    public void MarkEmailVerified_SetsTimestampOnceAndRejectsSecondCall()
    {
        var user = CreateUser();

        Assert.True(user.MarkEmailVerified(Now).IsSuccess);
        Assert.Equal(Now, user.EmailVerifiedAtUtc);

        var second = user.MarkEmailVerified(Now.AddMinutes(5));

        Assert.Equal(UserErrors.EmailAlreadyVerified, second.Error);
        Assert.Equal(Now, user.EmailVerifiedAtUtc);
    }

    [Fact]
    public void Lock_WhenActive_LocksWithReasonAndRegeneratesSecurityStamp()
    {
        var user = CreateUser();
        var stamp = user.SecurityStamp;

        var result = user.Lock("  Suspicious activity  ", Now);

        Assert.True(result.IsSuccess);
        Assert.Equal(UserStatus.Locked, user.Status);
        Assert.Equal(Now, user.LockedAtUtc);
        Assert.Equal("Suspicious activity", user.LockReason);
        Assert.NotEqual(stamp, user.SecurityStamp);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Lock_WithoutReason_StoresNullReason(string? reason)
    {
        var user = CreateUser();

        Assert.True(user.Lock(reason, Now).IsSuccess);
        Assert.Null(user.LockReason);
    }

    [Fact]
    public void Lock_WithInvalidReason_ReturnsInvalidLockReasonAndStaysActive()
    {
        var user = CreateUser();
        var stamp = user.SecurityStamp;

        Assert.Equal(UserErrors.InvalidLockReason, user.Lock(new string('r', User.LockReasonMaxLength + 1), Now).Error);
        Assert.Equal(UserErrors.InvalidLockReason, user.Lock("bad\0reason", Now).Error);
        Assert.Equal(UserStatus.Active, user.Status);
        Assert.Equal(stamp, user.SecurityStamp);
    }

    [Fact]
    public void Lock_WhenAlreadyLocked_ReturnsAlreadyLockedAndKeepsState()
    {
        var user = CreateUser();
        user.Lock("first", Now);
        var stamp = user.SecurityStamp;

        var result = user.Lock("second", Now.AddHours(1));

        Assert.Equal(UserErrors.AlreadyLocked, result.Error);
        Assert.Equal("first", user.LockReason);
        Assert.Equal(Now, user.LockedAtUtc);
        Assert.Equal(stamp, user.SecurityStamp);
    }

    [Fact]
    public void Unlock_WhenLocked_ClearsLockAndRegeneratesSecurityStamp()
    {
        var user = CreateUser();
        user.Lock("reason", Now);
        var stamp = user.SecurityStamp;

        var result = user.Unlock();

        Assert.True(result.IsSuccess);
        Assert.Equal(UserStatus.Active, user.Status);
        Assert.Null(user.LockedAtUtc);
        Assert.Null(user.LockReason);
        Assert.NotEqual(stamp, user.SecurityStamp);
    }

    [Fact]
    public void Unlock_WhenActive_ReturnsNotLockedAndKeepsSecurityStamp()
    {
        var user = CreateUser();
        var stamp = user.SecurityStamp;

        Assert.Equal(UserErrors.NotLocked, user.Unlock().Error);
        Assert.Equal(stamp, user.SecurityStamp);
    }

    [Fact]
    public void RegisterLogin_SetsLastLoginWithoutChangingSecurityStamp()
    {
        var user = CreateUser();
        var stamp = user.SecurityStamp;

        user.RegisterLogin(Now);

        Assert.Equal(Now, user.LastLoginAtUtc);
        Assert.Equal(stamp, user.SecurityStamp);
    }

    [Fact]
    public void TimestampMethods_WithNonUtcDateTime_Throw()
    {
        var user = CreateUser();
        var local = new DateTime(2026, 11, 19, 12, 0, 0, DateTimeKind.Local);
        var unspecified = new DateTime(2026, 11, 19, 12, 0, 0, DateTimeKind.Unspecified);

        Assert.Throws<ArgumentException>(() => user.Lock("reason", local));
        Assert.Throws<ArgumentException>(() => user.MarkEmailVerified(unspecified));
        Assert.Throws<ArgumentException>(() => user.RegisterLogin(local));
        Assert.Equal(UserStatus.Active, user.Status);
    }

    private static User CreateUser() => User.Create("ana@example.com", "Ana", ValidPassword, FastHasher).Value;

    private static PasswordHasher<User> CreateHasher(int iterationCount) =>
        new(Options.Create(new PasswordHasherOptions { IterationCount = iterationCount }));
}
