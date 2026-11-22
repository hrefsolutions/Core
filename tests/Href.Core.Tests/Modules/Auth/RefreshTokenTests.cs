using Href.Core.Api.Common.Results;
using Href.Core.Api.Modules.Applications;
using Href.Core.Api.Modules.Auth;

namespace Href.Core.Tests.Modules.Auth;

public sealed class RefreshTokenTests
{
    private static readonly DateTime Now = new(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);
    private static readonly Guid UserId = Guid.CreateVersion7();
    private static readonly Guid ApplicationId = Guid.CreateVersion7();

    [Fact]
    public void Issue_StartsANewFamilyWithAHashedTokenAndExpiry()
    {
        var issued = RefreshToken.Issue(UserId, ApplicationId, Now, Lifetime, "203.0.113.7", "Satellite/1.0");

        var token = issued.Token;
        Assert.NotEqual(Guid.Empty, token.Id);
        Assert.Equal(UserId, token.UserId);
        Assert.Equal(ApplicationId, token.ApplicationId);
        Assert.NotEqual(Guid.Empty, token.FamilyId);
        Assert.NotEqual(token.Id, token.FamilyId);
        Assert.Equal(43, issued.PlainToken.Length);
        Assert.Equal(64, token.TokenHash.Length);
        Assert.NotEqual(issued.PlainToken, token.TokenHash);
        Assert.Equal(ClientSecretGenerator.Hash(issued.PlainToken), token.TokenHash);
        Assert.Equal(RefreshToken.HashToken(issued.PlainToken), token.TokenHash);
        Assert.Equal(Now, token.CreatedAtUtc);
        Assert.Equal(Now + Lifetime, token.ExpiresAtUtc);
        Assert.Null(token.RevokedAtUtc);
        Assert.Null(token.RevokedReason);
        Assert.Null(token.ReplacedByTokenId);
        Assert.Equal("203.0.113.7", token.CreatedByIp);
        Assert.Equal("Satellite/1.0", token.UserAgent);
        Assert.True(token.IsActive(Now));
    }

    [Fact]
    public void Issue_TwiceForTheSameUser_StartsDifferentFamiliesAndTokens()
    {
        var first = RefreshToken.Issue(UserId, ApplicationId, Now, Lifetime, null, null);
        var second = RefreshToken.Issue(UserId, ApplicationId, Now, Lifetime, null, null);

        Assert.NotEqual(first.Token.FamilyId, second.Token.FamilyId);
        Assert.NotEqual(first.PlainToken, second.PlainToken);
        Assert.NotEqual(first.Token.TokenHash, second.Token.TokenHash);
    }

    [Fact]
    public void Rotate_RevokesAsRotatedAndReturnsASuccessorInTheSameFamily()
    {
        var current = RefreshToken.Issue(UserId, ApplicationId, Now, Lifetime, "203.0.113.7", "Satellite/1.0").Token;
        var later = Now.AddDays(10);

        var result = current.Rotate(later, Lifetime, "198.51.100.4", "Satellite/2.0");

        Assert.True(result.IsSuccess);
        var successor = result.Value.Token;
        Assert.Equal(later, current.RevokedAtUtc);
        Assert.Equal(RefreshTokenRevocationReason.Rotated, current.RevokedReason);
        Assert.Equal(successor.Id, current.ReplacedByTokenId);
        Assert.False(current.IsActive(later));
        Assert.Equal(current.FamilyId, successor.FamilyId);
        Assert.Equal(UserId, successor.UserId);
        Assert.Equal(ApplicationId, successor.ApplicationId);
        Assert.NotEqual(current.TokenHash, successor.TokenHash);
        Assert.Equal(RefreshToken.HashToken(result.Value.PlainToken), successor.TokenHash);
        Assert.Equal(later, successor.CreatedAtUtc);
        Assert.Equal(later + Lifetime, successor.ExpiresAtUtc);
        Assert.Equal("198.51.100.4", successor.CreatedByIp);
        Assert.Equal("Satellite/2.0", successor.UserAgent);
        Assert.True(successor.IsActive(later));
    }

    [Fact]
    public void Rotate_WhenAlreadyRotated_ReturnsRefreshTokenReusedAndChangesNothing()
    {
        var current = RefreshToken.Issue(UserId, ApplicationId, Now, Lifetime, null, null).Token;
        var successorId = current.Rotate(Now.AddMinutes(1), Lifetime, null, null).Value.Token.Id;

        var result = current.Rotate(Now.AddMinutes(2), Lifetime, null, null);

        Assert.True(result.IsFailure);
        Assert.Equal(AuthErrors.RefreshTokenReused, result.Error);
        Assert.Equal(Now.AddMinutes(1), current.RevokedAtUtc);
        Assert.Equal(successorId, current.ReplacedByTokenId);
    }

    [Fact]
    public void Rotate_WhenRotatedAndAlsoExpired_ReportsReuse()
    {
        var current = RefreshToken.Issue(UserId, ApplicationId, Now, Lifetime, null, null).Token;
        current.Rotate(Now.AddMinutes(1), Lifetime, null, null);

        var result = current.Rotate(Now + Lifetime + TimeSpan.FromDays(1), Lifetime, null, null);

        Assert.Equal(AuthErrors.RefreshTokenReused, result.Error);
    }

    [Theory]
    [InlineData(RefreshTokenRevocationReason.Logout)]
    [InlineData(RefreshTokenRevocationReason.ReuseDetected)]
    [InlineData(RefreshTokenRevocationReason.PasswordReset)]
    [InlineData(RefreshTokenRevocationReason.UserLocked)]
    [InlineData(RefreshTokenRevocationReason.AdminRevoked)]
    public void Rotate_WhenRevokedForAnotherReason_ReturnsRefreshTokenRevoked(RefreshTokenRevocationReason reason)
    {
        var current = RefreshToken.Issue(UserId, ApplicationId, Now, Lifetime, null, null).Token;
        current.Revoke(Now.AddMinutes(1), reason);

        var result = current.Rotate(Now.AddMinutes(2), Lifetime, null, null);

        Assert.Equal(AuthErrors.RefreshTokenRevoked, result.Error);
        Assert.Equal(reason, current.RevokedReason);
        Assert.Null(current.ReplacedByTokenId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Rotate_AtOrAfterExpiry_ReturnsRefreshTokenExpiredAndChangesNothing(int secondsAfterExpiry)
    {
        var current = RefreshToken.Issue(UserId, ApplicationId, Now, Lifetime, null, null).Token;
        var at = current.ExpiresAtUtc.AddSeconds(secondsAfterExpiry);

        var result = current.Rotate(at, Lifetime, null, null);

        Assert.Equal(AuthErrors.RefreshTokenExpired, result.Error);
        Assert.Null(current.RevokedAtUtc);
        Assert.Null(current.RevokedReason);
        Assert.False(current.IsActive(at));
    }

    [Fact]
    public void Rotate_OneTickBeforeExpiry_Succeeds()
    {
        var current = RefreshToken.Issue(UserId, ApplicationId, Now, Lifetime, null, null).Token;

        var result = current.Rotate(current.ExpiresAtUtc.AddTicks(-1), Lifetime, null, null);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void AuthErrors_AreUnauthorized()
    {
        Assert.Equal(ErrorType.Unauthorized, AuthErrors.RefreshTokenReused.Type);
        Assert.Equal(ErrorType.Unauthorized, AuthErrors.RefreshTokenRevoked.Type);
        Assert.Equal(ErrorType.Unauthorized, AuthErrors.RefreshTokenExpired.Type);
        Assert.Equal("Auth.RefreshTokenReused", AuthErrors.RefreshTokenReused.Code);
        Assert.Equal("Auth.RefreshTokenRevoked", AuthErrors.RefreshTokenRevoked.Code);
        Assert.Equal("Auth.RefreshTokenExpired", AuthErrors.RefreshTokenExpired.Code);
    }

    [Fact]
    public void Revoke_ActiveToken_RevokesAndReturnsTrue()
    {
        var token = RefreshToken.Issue(UserId, ApplicationId, Now, Lifetime, null, null).Token;

        var changed = token.Revoke(Now.AddMinutes(5), RefreshTokenRevocationReason.Logout);

        Assert.True(changed);
        Assert.Equal(Now.AddMinutes(5), token.RevokedAtUtc);
        Assert.Equal(RefreshTokenRevocationReason.Logout, token.RevokedReason);
        Assert.Null(token.ReplacedByTokenId);
    }

    [Fact]
    public void Revoke_AlreadyRevokedToken_KeepsTheFirstRevocationAndReturnsFalse()
    {
        var token = RefreshToken.Issue(UserId, ApplicationId, Now, Lifetime, null, null).Token;
        token.Revoke(Now.AddMinutes(5), RefreshTokenRevocationReason.Logout);

        var changed = token.Revoke(Now.AddMinutes(6), RefreshTokenRevocationReason.AdminRevoked);

        Assert.False(changed);
        Assert.Equal(Now.AddMinutes(5), token.RevokedAtUtc);
        Assert.Equal(RefreshTokenRevocationReason.Logout, token.RevokedReason);
    }

    [Fact]
    public void Revoke_ExpiredToken_RecordsTheRevocation()
    {
        var token = RefreshToken.Issue(UserId, ApplicationId, Now, Lifetime, null, null).Token;
        var afterExpiry = token.ExpiresAtUtc.AddDays(1);

        var changed = token.Revoke(afterExpiry, RefreshTokenRevocationReason.UserLocked);

        Assert.True(changed);
        Assert.Equal(RefreshTokenRevocationReason.UserLocked, token.RevokedReason);
    }

    [Fact]
    public void Revoke_WithRotatedReason_Throws()
    {
        var token = RefreshToken.Issue(UserId, ApplicationId, Now, Lifetime, null, null).Token;

        Assert.Throws<ArgumentException>(() => token.Revoke(Now, RefreshTokenRevocationReason.Rotated));
        Assert.Null(token.RevokedAtUtc);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(99)]
    public void Revoke_WithUndefinedReason_Throws(int reason)
    {
        var token = RefreshToken.Issue(UserId, ApplicationId, Now, Lifetime, null, null).Token;

        Assert.Throws<ArgumentOutOfRangeException>(() => token.Revoke(Now, (RefreshTokenRevocationReason)reason));
    }

    [Fact]
    public void RevokeFamily_AfterReuse_RevokesActiveTokensOnlyAndKeepsEarlierReasons()
    {
        var first = RefreshToken.Issue(UserId, ApplicationId, Now, Lifetime, null, null).Token;
        var second = first.Rotate(Now.AddMinutes(1), Lifetime, null, null).Value.Token;
        var third = second.Rotate(Now.AddMinutes(2), Lifetime, null, null).Value.Token;
        var reuse = first.Rotate(Now.AddMinutes(3), Lifetime, null, null);
        Assert.Equal(AuthErrors.RefreshTokenReused, reuse.Error);

        var revoked = RefreshToken.RevokeFamily([first, second, third], Now.AddMinutes(3), RefreshTokenRevocationReason.ReuseDetected);

        Assert.Equal(1, revoked);
        Assert.Equal(RefreshTokenRevocationReason.Rotated, first.RevokedReason);
        Assert.Equal(RefreshTokenRevocationReason.Rotated, second.RevokedReason);
        Assert.Equal(RefreshTokenRevocationReason.ReuseDetected, third.RevokedReason);
        Assert.Equal(Now.AddMinutes(3), third.RevokedAtUtc);
        Assert.False(third.IsActive(Now.AddMinutes(3)));
        Assert.Equal(AuthErrors.RefreshTokenRevoked, third.Rotate(Now.AddMinutes(4), Lifetime, null, null).Error);
    }

    [Fact]
    public void RevokeFamily_WithTokensOfDifferentFamilies_ThrowsAndChangesNothing()
    {
        var first = RefreshToken.Issue(UserId, ApplicationId, Now, Lifetime, null, null).Token;
        var other = RefreshToken.Issue(UserId, ApplicationId, Now, Lifetime, null, null).Token;

        Assert.Throws<ArgumentException>(() =>
            RefreshToken.RevokeFamily([first, other], Now, RefreshTokenRevocationReason.ReuseDetected));
        Assert.Null(first.RevokedAtUtc);
        Assert.Null(other.RevokedAtUtc);
    }

    [Fact]
    public void RevokeFamily_WithNoTokens_ReturnsZero()
    {
        Assert.Equal(0, RefreshToken.RevokeFamily([], Now, RefreshTokenRevocationReason.ReuseDetected));
    }

    [Fact]
    public void TimestampsThatAreNotUtc_Throw()
    {
        var local = new DateTime(2026, 11, 22, 12, 0, 0, DateTimeKind.Local);
        var unspecified = new DateTime(2026, 11, 22, 12, 0, 0, DateTimeKind.Unspecified);
        var token = RefreshToken.Issue(UserId, ApplicationId, Now, Lifetime, null, null).Token;

        Assert.Throws<ArgumentException>(() => RefreshToken.Issue(UserId, ApplicationId, local, Lifetime, null, null));
        Assert.Throws<ArgumentException>(() => token.Rotate(unspecified, Lifetime, null, null));
        Assert.Throws<ArgumentException>(() => token.Revoke(local, RefreshTokenRevocationReason.Logout));
        Assert.Throws<ArgumentException>(() => token.IsActive(unspecified));
        Assert.Throws<ArgumentException>(() => RefreshToken.RevokeFamily([token], local, RefreshTokenRevocationReason.Logout));
        Assert.Null(token.RevokedAtUtc);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveLifetime_Throws(int seconds)
    {
        var lifetime = TimeSpan.FromSeconds(seconds);
        var token = RefreshToken.Issue(UserId, ApplicationId, Now, Lifetime, null, null).Token;

        Assert.Throws<ArgumentOutOfRangeException>(() => RefreshToken.Issue(UserId, ApplicationId, Now, lifetime, null, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => token.Rotate(Now, lifetime, null, null));
        Assert.Null(token.RevokedAtUtc);
    }

    [Fact]
    public void EmptyUserOrApplicationId_Throws()
    {
        Assert.Throws<ArgumentException>(() => RefreshToken.Issue(Guid.Empty, ApplicationId, Now, Lifetime, null, null));
        Assert.Throws<ArgumentException>(() => RefreshToken.Issue(UserId, Guid.Empty, Now, Lifetime, null, null));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(" 203.0.113.7 ", "203.0.113.7")]
    [InlineData("2001:0db8:0000:0000:0000:ff00:0042:8329", "2001:0db8:0000:0000:0000:ff00:0042:8329")]
    [InlineData("0000:0000:0000:0000:0000:ffff:203.000.113.007x", null)]
    [InlineData("203.0.113.7\0", null)]
    public void CreatedByIp_IsTrimmedAndDroppedWhenBlankTooLongOrWithControlCharacters(string? ip, string? expected)
    {
        var token = RefreshToken.Issue(UserId, ApplicationId, Now, Lifetime, ip, null).Token;

        Assert.Equal(expected, token.CreatedByIp);
    }

    [Fact]
    public void UserAgent_IsTrimmedTruncatedTo512AndStrippedOfNul()
    {
        var longAgent = "  Agent\0/" + new string('x', 600);

        var token = RefreshToken.Issue(UserId, ApplicationId, Now, Lifetime, null, longAgent).Token;

        Assert.Equal(RefreshToken.UserAgentMaxLength, token.UserAgent!.Length);
        Assert.StartsWith("Agent/xxx", token.UserAgent, StringComparison.Ordinal);
        Assert.DoesNotContain('\0', token.UserAgent);
    }

    [Fact]
    public void UserAgent_TruncationNeverSplitsASurrogatePair()
    {
        var agent = new string('a', RefreshToken.UserAgentMaxLength - 1) + "\U0001F600";

        var token = RefreshToken.Issue(UserId, ApplicationId, Now, Lifetime, null, agent).Token;

        Assert.Equal(RefreshToken.UserAgentMaxLength - 1, token.UserAgent!.Length);
        Assert.False(char.IsHighSurrogate(token.UserAgent[^1]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("\0\0")]
    public void UserAgent_BlankBecomesNull(string? agent)
    {
        var token = RefreshToken.Issue(UserId, ApplicationId, Now, Lifetime, null, agent).Token;

        Assert.Null(token.UserAgent);
    }
}
