using DesktopPlus.Companion;
using Xunit;

namespace DesktopPlus.Tests;

public class CompanionRateLimiterTests
{
    [Fact]
    public void NotLockedOut_Initially()
    {
        var rl = new CompanionRateLimiter();
        Assert.False(rl.IsLockedOut("1.2.3.4"));
    }

    [Fact]
    public void LocksOut_OnlyAfterEighthFailure()
    {
        var rl = new CompanionRateLimiter();
        const string key = "10.0.0.9";

        for (int i = 0; i < 7; i++) { rl.RegisterFailure(key); }
        Assert.False(rl.IsLockedOut(key));   // 7 strikes: still allowed

        rl.RegisterFailure(key);             // 8th strike
        Assert.True(rl.IsLockedOut(key));
    }

    [Fact]
    public void Reset_ClearsLockout()
    {
        var rl = new CompanionRateLimiter();
        const string key = "10.0.0.10";

        for (int i = 0; i < 8; i++) { rl.RegisterFailure(key); }
        Assert.True(rl.IsLockedOut(key));

        rl.Reset(key);
        Assert.False(rl.IsLockedOut(key));
    }

    [Fact]
    public void Lockout_IsPerKey()
    {
        var rl = new CompanionRateLimiter();
        for (int i = 0; i < 8; i++) { rl.RegisterFailure("attacker"); }

        Assert.True(rl.IsLockedOut("attacker"));
        Assert.False(rl.IsLockedOut("other-device"));
    }
}
