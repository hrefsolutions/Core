using Href.Core.Api.Modules.Auth;
using Microsoft.Extensions.Time.Testing;

namespace Href.Core.Tests.Modules.Auth;

public sealed class LoginAttemptThrottleTests
{
    private const string Email = "ana@example.com";

    [Fact]
    public void TryAcquire_AllowsTheLimitThenReturnsTheRestOfTheWindow()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 24, 10, 0, 0, TimeSpan.Zero));
        var throttle = new LoginAttemptThrottle(time);

        for (var attempt = 1; attempt <= LoginAttemptThrottle.AttemptLimit; attempt++)
        {
            Assert.Null(throttle.TryAcquire(Email));
        }

        time.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(LoginAttemptThrottle.Window - TimeSpan.FromMinutes(5), throttle.TryAcquire(Email));
    }

    [Fact]
    public void TryAcquire_AfterTheWindow_StartsANewWindow()
    {
        var time = new FakeTimeProvider();
        var throttle = new LoginAttemptThrottle(time);
        Exhaust(throttle, Email);

        time.Advance(LoginAttemptThrottle.Window);

        Assert.Null(throttle.TryAcquire(Email));
    }

    [Fact]
    public void TryAcquire_CountsEachAccountSeparately()
    {
        var throttle = new LoginAttemptThrottle(new FakeTimeProvider());
        Exhaust(throttle, Email);

        Assert.NotNull(throttle.TryAcquire(Email));
        Assert.Null(throttle.TryAcquire("bea@example.com"));
    }

    [Fact]
    public void Reset_ReleasesTheAccount()
    {
        var throttle = new LoginAttemptThrottle(new FakeTimeProvider());
        Exhaust(throttle, Email);

        throttle.Reset(Email);

        Assert.Null(throttle.TryAcquire(Email));
        throttle.Reset("never-seen@example.com");
    }

    [Fact]
    public async Task TryAcquire_Concurrently_GrantsExactlyTheLimit()
    {
        var throttle = new LoginAttemptThrottle(new FakeTimeProvider());

        var results = await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => Task.Run(() => throttle.TryAcquire(Email))));

        Assert.Equal(LoginAttemptThrottle.AttemptLimit, results.Count(result => result is null));
    }

    [Fact]
    public void TryAcquire_OverThePruneThreshold_DropsExpiredWindowsOnly()
    {
        var time = new FakeTimeProvider();
        var throttle = new LoginAttemptThrottle(time);

        for (var index = 0; index <= LoginAttemptThrottle.PruneThreshold; index++)
        {
            throttle.TryAcquire($"old-{index}@example.com");
        }

        time.Advance(LoginAttemptThrottle.Window);
        Exhaust(throttle, Email);

        Assert.Equal(1, throttle.TrackedAccounts);
        Assert.NotNull(throttle.TryAcquire(Email));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void TryAcquire_WithoutEmail_Throws(string? email)
    {
        var throttle = new LoginAttemptThrottle(new FakeTimeProvider());

        Assert.ThrowsAny<ArgumentException>(() => throttle.TryAcquire(email!));
    }

    private static void Exhaust(LoginAttemptThrottle throttle, string email)
    {
        for (var attempt = 1; attempt <= LoginAttemptThrottle.AttemptLimit; attempt++)
        {
            throttle.TryAcquire(email);
        }
    }
}
