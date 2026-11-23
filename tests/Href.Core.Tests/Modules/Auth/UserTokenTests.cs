using Href.Core.Api.Modules.Auth;

namespace Href.Core.Tests.Modules.Auth;

public sealed class UserTokenTests
{
    private static readonly DateTime Now = new(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid UserId = Guid.CreateVersion7();
    private static readonly Guid ApplicationId = Guid.CreateVersion7();

    [Fact]
    public void Issue_CreatesAHashedUsableTokenWithItsExpiry()
    {
        var issued = UserToken.Issue(UserId, ApplicationId, UserTokenPurpose.EmailVerification, Now, UserToken.EmailVerificationLifetime);

        var token = issued.Token;
        Assert.Equal(UserId, token.UserId);
        Assert.Equal(ApplicationId, token.ApplicationId);
        Assert.Equal(UserTokenPurpose.EmailVerification, token.Purpose);
        Assert.Equal(43, issued.PlainToken.Length);
        Assert.Equal(UserToken.HashToken(issued.PlainToken), token.TokenHash);
        Assert.NotEqual(issued.PlainToken, token.TokenHash);
        Assert.Equal(Now, token.CreatedAtUtc);
        Assert.Equal(Now.AddHours(24), token.ExpiresAtUtc);
        Assert.Null(token.ConsumedAtUtc);
        Assert.Null(token.RevokedAtUtc);
        Assert.True(token.IsUsable(Now));
    }

    [Fact]
    public void Lifetimes_Are24HoursForVerificationAnd30MinutesForReset()
    {
        Assert.Equal(TimeSpan.FromHours(24), UserToken.EmailVerificationLifetime);
        Assert.Equal(TimeSpan.FromMinutes(30), UserToken.PasswordResetLifetime);
    }

    [Fact]
    public void Consume_UsableToken_SetsConsumedAt()
    {
        var token = NewToken();

        var result = token.Consume(Now.AddMinutes(1));

        Assert.True(result.IsSuccess);
        Assert.Equal(Now.AddMinutes(1), token.ConsumedAtUtc);
        Assert.False(token.IsUsable(Now.AddMinutes(1)));
    }

    [Fact]
    public void Consume_Twice_FailsAndKeepsTheFirstConsumption()
    {
        var token = NewToken();
        token.Consume(Now.AddMinutes(1));

        var result = token.Consume(Now.AddMinutes(2));

        Assert.Equal(AuthErrors.InvalidOneTimeToken, result.Error);
        Assert.Equal(Now.AddMinutes(1), token.ConsumedAtUtc);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Consume_AtOrAfterExpiry_FailsAndChangesNothing(int secondsAfterExpiry)
    {
        var token = NewToken();

        var result = token.Consume(token.ExpiresAtUtc.AddSeconds(secondsAfterExpiry));

        Assert.Equal(AuthErrors.InvalidOneTimeToken, result.Error);
        Assert.Null(token.ConsumedAtUtc);
    }

    [Fact]
    public void Consume_OneTickBeforeExpiry_Succeeds()
    {
        var token = NewToken();

        Assert.True(token.Consume(token.ExpiresAtUtc.AddTicks(-1)).IsSuccess);
    }

    [Fact]
    public void Revoke_UsableToken_ReturnsTrueAndBlocksConsumption()
    {
        var token = NewToken();

        Assert.True(token.Revoke(Now.AddMinutes(1)));
        Assert.Equal(Now.AddMinutes(1), token.RevokedAtUtc);
        Assert.Equal(AuthErrors.InvalidOneTimeToken, token.Consume(Now.AddMinutes(2)).Error);
    }

    [Fact]
    public void Revoke_ConsumedOrRevokedToken_ReturnsFalseAndChangesNothing()
    {
        var consumed = NewToken();
        consumed.Consume(Now.AddMinutes(1));
        var revoked = NewToken();
        revoked.Revoke(Now.AddMinutes(1));

        Assert.False(consumed.Revoke(Now.AddMinutes(2)));
        Assert.Null(consumed.RevokedAtUtc);
        Assert.False(revoked.Revoke(Now.AddMinutes(2)));
        Assert.Equal(Now.AddMinutes(1), revoked.RevokedAtUtc);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void HashToken_ForAMissingOrBlankToken_ReturnsNull(string? plainToken)
    {
        Assert.Null(UserToken.HashToken(plainToken));
    }

    [Fact]
    public void InvalidArguments_Throw()
    {
        var local = new DateTime(2026, 11, 23, 12, 0, 0, DateTimeKind.Local);
        var token = NewToken();

        Assert.Throws<ArgumentException>(() => UserToken.Issue(Guid.Empty, ApplicationId, UserTokenPurpose.EmailVerification, Now, TimeSpan.FromHours(1)));
        Assert.Throws<ArgumentException>(() => UserToken.Issue(UserId, Guid.Empty, UserTokenPurpose.EmailVerification, Now, TimeSpan.FromHours(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => UserToken.Issue(UserId, ApplicationId, (UserTokenPurpose)99, Now, TimeSpan.FromHours(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => UserToken.Issue(UserId, ApplicationId, UserTokenPurpose.EmailVerification, Now, TimeSpan.Zero));
        Assert.Throws<ArgumentException>(() => UserToken.Issue(UserId, ApplicationId, UserTokenPurpose.EmailVerification, local, TimeSpan.FromHours(1)));
        Assert.Throws<ArgumentException>(() => token.Consume(local));
        Assert.Throws<ArgumentException>(() => token.Revoke(local));
        Assert.Null(token.ConsumedAtUtc);
        Assert.Null(token.RevokedAtUtc);
    }

    private static UserToken NewToken() =>
        UserToken.Issue(UserId, ApplicationId, UserTokenPurpose.EmailVerification, Now, UserToken.EmailVerificationLifetime).Token;
}
