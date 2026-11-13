using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MailConcierge.Security;
using Xunit;

namespace Jellyfin.Plugin.MailConcierge.Tests.Security;

public class KeyedLockTests
{
    [Fact]
    public async Task SameKey_RunsOneAtATime()
    {
        var gate = new KeyedLock<string>();
        var first = await gate.AcquireAsync("a", TestContext.Current.CancellationToken);
        var second = gate.AcquireAsync("a", TestContext.Current.CancellationToken);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(second.IsCompleted);

        first.Dispose();
        var handle = await second.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        handle.Dispose();
    }

    [Fact]
    public async Task DifferentKeys_DoNotWait()
    {
        var gate = new KeyedLock<string>();
        using var first = await gate.AcquireAsync("a", TestContext.Current.CancellationToken);
        using var second = await gate.AcquireAsync("b", TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(2, gate.Count);
    }

    [Fact]
    public async Task Idle_KeepsNoEntry()
    {
        var gate = new KeyedLock<string>();
        var first = await gate.AcquireAsync("a", TestContext.Current.CancellationToken);
        var second = gate.AcquireAsync("a", TestContext.Current.CancellationToken);
        Assert.Equal(1, gate.Count);

        first.Dispose();
        (await second.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).Dispose();

        Assert.Equal(0, gate.Count);
    }

    [Fact]
    public async Task DoubleDispose_ReleasesOnce()
    {
        var gate = new KeyedLock<string>();
        var first = await gate.AcquireAsync("a", TestContext.Current.CancellationToken);
        var second = gate.AcquireAsync("a", TestContext.Current.CancellationToken);
        var third = gate.AcquireAsync("a", TestContext.Current.CancellationToken);

        first.Dispose();
        var secondHandle = await second.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        first.Dispose();

        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(third.IsCompleted);

        secondHandle.Dispose();
        (await third.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).Dispose();
        Assert.Equal(0, gate.Count);
    }

    [Fact]
    public async Task Cancelled_Wait_LeavesNoEntry()
    {
        var gate = new KeyedLock<string>();
        var first = await gate.AcquireAsync("a", TestContext.Current.CancellationToken);
        using var cts = new CancellationTokenSource();
        var waiting = gate.AcquireAsync("a", cts.Token);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(1, gate.Count);

        first.Dispose();
        Assert.Equal(0, gate.Count);
    }

    [Fact]
    public async Task HandOff_ThenANewCaller_Acquires()
    {
        var gate = new KeyedLock<string>();
        var a = await gate.AcquireAsync("k", TestContext.Current.CancellationToken);
        var b = gate.AcquireAsync("k", TestContext.Current.CancellationToken);
        Assert.False(b.IsCompleted);

        a.Dispose();
        var bHandle = await b.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var c = gate.AcquireAsync("k", TestContext.Current.CancellationToken);
        Assert.False(c.IsCompleted);
        Assert.Equal(1, gate.Count);

        bHandle.Dispose();
        (await c.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).Dispose();
        Assert.Equal(0, gate.Count);
    }

    [Fact]
    public async Task AlreadyCancelledToken_OnAFreeKey()
    {
        // The wait is cancelled before it starts, even though the key is free: nothing is taken and no entry is left.
        var gate = new KeyedLock<string>();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.AcquireAsync("k", cts.Token));
        Assert.Equal(0, gate.Count);

        var handle = gate.AcquireAsync("k", TestContext.Current.CancellationToken);
        Assert.True(handle.IsCompleted);
        (await handle).Dispose();
        Assert.Equal(0, gate.Count);
    }
}
