using System;
using System.Net;
using Jellyfin.Plugin.MailConcierge.Security;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Jellyfin.Plugin.MailConcierge.Tests.Security;

public class RateLimiterTests
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    [Fact]
    public void TryAcquire_AllowsLimitThenRefuses()
    {
        var limiter = new RateLimiter(new FakeTimeProvider());
        for (var i = 0; i < 5; i++)
        {
            Assert.True(limiter.TryAcquire(RateLimitBuckets.ForgotIp, "1.2.3.4", 5, Window));
        }

        Assert.False(limiter.TryAcquire(RateLimitBuckets.ForgotIp, "1.2.3.4", 5, Window));
        Assert.True(limiter.TryAcquire(RateLimitBuckets.ForgotIp, "5.6.7.8", 5, Window));
    }

    [Fact]
    public void TryAcquire_AllowsAgainAfterWindow()
    {
        var time = new FakeTimeProvider();
        var limiter = new RateLimiter(time);
        for (var i = 0; i < 5; i++)
        {
            limiter.TryAcquire("b", "k", 5, Window);
        }

        Assert.False(limiter.TryAcquire("b", "k", 5, Window));
        time.Advance(TimeSpan.FromMinutes(15));
        Assert.True(limiter.TryAcquire("b", "k", 5, Window));
    }

    [Fact]
    public void Penalize_BlocksUntilExpiry()
    {
        var time = new FakeTimeProvider();
        var limiter = new RateLimiter(time);
        limiter.Penalize("b", "k", TimeSpan.FromMinutes(10));
        Assert.True(limiter.IsBlocked("b", "k"));
        Assert.False(limiter.TryAcquire("b", "k", 5, Window));
        time.Advance(TimeSpan.FromMinutes(9));
        Assert.True(limiter.IsBlocked("b", "k"));
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.False(limiter.IsBlocked("b", "k"));
        Assert.True(limiter.TryAcquire("b", "k", 5, Window));
    }

    [Fact]
    public void ExpiredEntries_ArePruned()
    {
        var time = new FakeTimeProvider();
        var limiter = new RateLimiter(time);
        for (var i = 0; i < 2000; i++)
        {
            limiter.TryAcquire("b", "k" + i, 5, Window);
        }

        time.Advance(TimeSpan.FromMinutes(20));
        limiter.TryAcquire("b", "fresh", 5, Window);
        Assert.Equal(1, limiter.Count);
    }

    [Fact]
    public void Sweep_KeepsBlockedAndInWindowEntries()
    {
        var time = new FakeTimeProvider();
        var limiter = new RateLimiter(time);
        limiter.Penalize("b", "blocked", TimeSpan.FromMinutes(30));
        for (var i = 0; i < 5; i++)
        {
            limiter.TryAcquire("b", "busy", 5, Window);
        }

        for (var i = 0; i < 2000; i++)
        {
            limiter.TryAcquire("b", "junk" + i, 1, TimeSpan.FromSeconds(1));
        }

        time.Advance(TimeSpan.FromMinutes(2));
        limiter.TryAcquire("b", "trigger", 5, Window);
        Assert.True(limiter.SweepCount > 0);
        Assert.Equal(3, limiter.Count);
        Assert.True(limiter.IsBlocked("b", "blocked"));
        Assert.False(limiter.TryAcquire("b", "busy", 5, Window));
    }

    [Fact]
    public void Flood_OfLiveKeys_DoesNotSweepOnEveryCall()
    {
        var limiter = new RateLimiter(new FakeTimeProvider());
        for (var i = 0; i < 3000; i++)
        {
            limiter.TryAcquire("b", "k" + i, 5, Window);
        }

        Assert.InRange(limiter.SweepCount, 1, 5);
    }

    [Fact]
    public void Entries_AreCapped_OldestEvicted()
    {
        var time = new FakeTimeProvider();
        var limiter = new RateLimiter(time, 100);
        for (var i = 0; i < 1000; i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(1));
            limiter.TryAcquire("b", "k" + i, 5, Window);
        }

        Assert.InRange(limiter.Count, 1, 100);

        // The newest key is still tracked (its first hit counts), the oldest was evicted.
        for (var i = 0; i < 4; i++)
        {
            Assert.True(limiter.TryAcquire("b", "k999", 5, Window));
        }

        Assert.False(limiter.TryAcquire("b", "k999", 5, Window));
    }

    [Fact]
    public void Cap_PrefersEvictingUnblockedEntries()
    {
        var time = new FakeTimeProvider();
        var limiter = new RateLimiter(time, 100);
        limiter.Penalize("b", "blocked", TimeSpan.FromHours(1));
        for (var i = 0; i < 500; i++)
        {
            limiter.TryAcquire("b", "k" + i, 5, Window);
        }

        Assert.True(limiter.IsBlocked("b", "blocked"));
    }

    [Fact]
    public void Cap_EvictsPerClientAddressEntriesFirst_SoTheEmailBudgetsSurviveAFlood()
    {
        var time = new FakeTimeProvider();
        var limiter = new RateLimiter(time, 100);
        var hour = TimeSpan.FromHours(1);
        var account = Guid.NewGuid().ToString("N");

        // The server-wide budget and the per-account budget are created first and used up.
        Assert.True(limiter.TryAcquire(RateLimitBuckets.RecoveryEmail, "global", 2, hour));
        Assert.True(limiter.TryAcquire(RateLimitBuckets.RecoveryEmail, "global", 2, hour));
        Assert.True(limiter.TryAcquire(RateLimitBuckets.ForgotAccount, account, 1, hour));

        // A flood of distinct client addresses on the page-view bucket, well beyond the cap.
        for (var i = 0; i < 1000; i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(1));
            limiter.TryAcquire(RateLimitBuckets.PageViewIp, "203.0.113." + i, 30, TimeSpan.FromMinutes(5));
        }

        Assert.InRange(limiter.Count, 1, 100);
        Assert.False(limiter.TryAcquire(RateLimitBuckets.RecoveryEmail, "global", 2, hour));
        Assert.False(limiter.TryAcquire(RateLimitBuckets.ForgotAccount, account, 1, hour));
    }

    [Fact]
    public void Cap_WithOnlyPerClientAddressEntries_KeepsBlockedThenNewest()
    {
        var time = new FakeTimeProvider();
        var limiter = new RateLimiter(time, 100);
        limiter.Penalize(RateLimitBuckets.TokenFailIp, "198.51.100.1", TimeSpan.FromHours(1));
        for (var i = 0; i < 1000; i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(1));
            limiter.TryAcquire(RateLimitBuckets.PageViewIp, "k" + i, 5, Window);
        }

        Assert.InRange(limiter.Count, 1, 100);
        Assert.True(limiter.IsBlocked(RateLimitBuckets.TokenFailIp, "198.51.100.1"));

        // The newest key is still tracked (its first hit counts); the oldest were evicted first.
        for (var i = 0; i < 4; i++)
        {
            Assert.True(limiter.TryAcquire(RateLimitBuckets.PageViewIp, "k999", 5, Window));
        }

        Assert.False(limiter.TryAcquire(RateLimitBuckets.PageViewIp, "k999", 5, Window));
    }

    [Fact]
    public void PerClientAddressBuckets_AreExactlyTheOnesKeyedByAClientAddress()
    {
        Assert.True(RateLimitBuckets.IsPerClientAddress(RateLimitBuckets.ForgotIp));
        Assert.True(RateLimitBuckets.IsPerClientAddress(RateLimitBuckets.PinFailIp));
        Assert.True(RateLimitBuckets.IsPerClientAddress(RateLimitBuckets.TokenFailIp));
        Assert.True(RateLimitBuckets.IsPerClientAddress(RateLimitBuckets.PageViewIp));

        Assert.False(RateLimitBuckets.IsPerClientAddress(RateLimitBuckets.ForgotAccount));
        Assert.False(RateLimitBuckets.IsPerClientAddress(RateLimitBuckets.RecoveryEmail));
        Assert.False(RateLimitBuckets.IsPerClientAddress(RateLimitBuckets.InvitationEmail));
        Assert.False(RateLimitBuckets.IsPerClientAddress(RateLimitBuckets.AccountEmail));
        Assert.False(RateLimitBuckets.IsPerClientAddress(RateLimitBuckets.UsernameCheckToken));
        Assert.False(RateLimitBuckets.IsPerClientAddress(RateLimitBuckets.EmailChangeUser));
        Assert.False(RateLimitBuckets.IsPerClientAddress(RateLimitBuckets.PasswordFailUser));
        Assert.False(RateLimitBuckets.IsPerClientAddress("b"));
    }

    [Fact]
    public void DefaultCap_Is50000() => Assert.Equal(50_000, RateLimiter.DefaultMaxEntries);

    [Fact]
    public void Penalize_KeepsLongerBlock()
    {
        var time = new FakeTimeProvider();
        var limiter = new RateLimiter(time);
        limiter.Penalize("b", "k", TimeSpan.FromMinutes(30));
        limiter.Penalize("b", "k", TimeSpan.FromMinutes(1));
        time.Advance(TimeSpan.FromMinutes(10));
        Assert.True(limiter.IsBlocked("b", "k"));
    }

    [Fact]
    public void Key_Ipv6_GroupsBy64()
    {
        var a = ClientIp.Key(IPAddress.Parse("2001:db8:1:2:a::1"));
        var b = ClientIp.Key(IPAddress.Parse("2001:db8:1:2:b::9"));
        Assert.Equal(a, b);
        Assert.Equal("2001:db8:1:2::/64", a);
        Assert.NotEqual(a, ClientIp.Key(IPAddress.Parse("2001:db8:1:3::1")));
    }

    [Fact]
    public void Key_Ipv4_Unchanged() => Assert.Equal("192.0.2.7", ClientIp.Key(IPAddress.Parse("192.0.2.7")));

    [Fact]
    public void Key_MappedIpv4_Unwrapped() =>
        Assert.Equal("192.0.2.7", ClientIp.Key(IPAddress.Parse("::ffff:192.0.2.7")));
}
