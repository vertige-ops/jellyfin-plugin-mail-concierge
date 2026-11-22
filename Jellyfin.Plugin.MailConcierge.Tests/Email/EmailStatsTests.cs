using System;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.MailConcierge.Email;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Jellyfin.Plugin.MailConcierge.Tests.Email;

public class EmailStatsTests
{
    private static readonly Guid Alice = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Bob = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly DateTimeOffset Start = new(2026, 11, 21, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RecordSent_UpdatesCountAndLastSent()
    {
        var time = new FakeTimeProvider(Start);
        var stats = new EmailStats(time);
        stats.RecordSent("recovery", Alice);
        time.Advance(TimeSpan.FromMinutes(5));
        stats.RecordSent("confirm-email", Alice);

        var snapshot = stats.Snapshot();

        Assert.Equal(Start, snapshot.Since);
        var row = Assert.Single(snapshot.Users);
        Assert.Equal(new AccountEmailStats(Alice, 2, 0, Start.AddMinutes(5)), row);
        Assert.Equal(new EmailTotals(0, 0), snapshot.Invitations);
    }

    [Fact]
    public void RecordFailed_DoesNotSetLastSent()
    {
        var stats = new EmailStats(new FakeTimeProvider(Start));
        stats.RecordFailed("recovery", Alice);

        var row = Assert.Single(stats.Snapshot().Users);
        Assert.Equal(new AccountEmailStats(Alice, 0, 1, null), row);
    }

    [Fact]
    public void RecordFailed_AfterASend_KeepsTheLastSent()
    {
        var time = new FakeTimeProvider(Start);
        var stats = new EmailStats(time);
        stats.RecordSent("recovery", Alice);
        time.Advance(TimeSpan.FromHours(1));
        stats.RecordFailed("recovery", Alice);

        Assert.Equal(new AccountEmailStats(Alice, 1, 1, Start), Assert.Single(stats.Snapshot().Users));
    }

    [Fact]
    public void Invitation_OnlyMovesTotals()
    {
        var stats = new EmailStats(new FakeTimeProvider(Start));
        stats.RecordSent("invitation", null);
        stats.RecordSent("invitation", Alice);
        stats.RecordFailed("invitation", Alice);

        var snapshot = stats.Snapshot();

        Assert.Equal(new EmailTotals(2, 1), snapshot.Invitations);
        Assert.Empty(snapshot.Users);
    }

    [Fact]
    public void NullUserId_OnlyMovesInvitationTotals()
    {
        var stats = new EmailStats(new FakeTimeProvider(Start));
        stats.RecordSent("recovery", null);
        stats.RecordFailed("confirm-email", null);

        var snapshot = stats.Snapshot();

        Assert.Equal(new EmailTotals(1, 1), snapshot.Invitations);
        Assert.Empty(snapshot.Users);
    }

    [Fact]
    public void EvictsLeastRecentlyUpdatedBeyondCap()
    {
        var time = new FakeTimeProvider(Start);
        var stats = new EmailStats(time, 3);
        var ids = Enumerable.Range(1, 4).Select(i => new Guid(i, 0, 0, new byte[8])).ToArray();
        stats.RecordSent("recovery", ids[0]);
        time.Advance(TimeSpan.FromSeconds(1));
        stats.RecordSent("recovery", ids[1]);
        time.Advance(TimeSpan.FromSeconds(1));
        stats.RecordSent("recovery", ids[2]);
        time.Advance(TimeSpan.FromSeconds(1));

        // The oldest row is refreshed by a failure, so the second one is now the least recently updated.
        stats.RecordFailed("recovery", ids[0]);
        stats.RecordSent("recovery", ids[3]);

        var users = stats.Snapshot().Users.Select(u => u.UserId).ToHashSet();
        Assert.Equal(3, users.Count);
        Assert.DoesNotContain(ids[1], users);
        Assert.Contains(ids[0], users);
        Assert.Contains(ids[2], users);
        Assert.Contains(ids[3], users);
    }

    [Fact]
    public void FloodOfDistinctIds_NeverExceedsTheDefaultCap()
    {
        var stats = new EmailStats(new FakeTimeProvider(Start));
        for (var i = 0; i < EmailStats.DefaultMaxAccounts + 50; i++)
        {
            stats.RecordSent("recovery", Guid.NewGuid());
        }

        Assert.Equal(EmailStats.DefaultMaxAccounts, stats.Snapshot().Users.Count);
    }

    [Fact]
    public void Snapshot_IsSortedAndIsACopy()
    {
        var time = new FakeTimeProvider(Start);
        var stats = new EmailStats(time);
        var carol = Guid.Parse("cccccccc-0000-0000-0000-000000000003");
        var dave = Guid.Parse("dddddddd-0000-0000-0000-000000000004");
        stats.RecordFailed("recovery", dave);
        stats.RecordFailed("recovery", carol);
        stats.RecordSent("recovery", Alice);
        time.Advance(TimeSpan.FromMinutes(1));
        stats.RecordSent("recovery", Bob);

        var first = stats.Snapshot();

        Assert.Equal(new[] { Bob, Alice, carol, dave }, first.Users.Select(u => u.UserId).ToArray());

        stats.RecordSent("recovery", Alice);
        stats.RecordSent("invitation", null);

        Assert.Equal(1, first.Users.Single(u => u.UserId == Alice).Sent);
        Assert.Equal(new EmailTotals(0, 0), first.Invitations);
        Assert.Equal(2, stats.Snapshot().Users.Single(u => u.UserId == Alice).Sent);
    }

    [Fact]
    public void ConcurrentRecords_AreCounted()
    {
        var stats = new EmailStats(new FakeTimeProvider(Start));
        Parallel.For(0, 1000, i =>
        {
            if (i % 2 == 0)
            {
                stats.RecordSent("recovery", Alice);
            }
            else
            {
                stats.RecordSent("invitation", null);
            }
        });

        var snapshot = stats.Snapshot();

        Assert.Equal(500, Assert.Single(snapshot.Users).Sent);
        Assert.Equal(500, snapshot.Invitations.Sent);
    }
}
