using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Jellyfin.Plugin.MailConcierge.Data;
using Xunit;

namespace Jellyfin.Plugin.MailConcierge.Tests.Data;

public sealed class MailConciergeStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mailconcierge-test-" + Guid.NewGuid().ToString("N"));

    public MailConciergeStoreTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        Directory.Delete(_dir, true);
    }

    [Fact]
    public async Task Store_PersistsAndReloads()
    {
        var id = Guid.NewGuid();
        var first = new MailConciergeStore(_dir);
        await first.WriteAsync(d => d.Emails.Add(new UserEmail(id, "a@example.org", null, "fr", null)));

        var second = new MailConciergeStore(_dir);
        var email = await second.ReadAsync(d => d.Emails.Single());

        Assert.Equal(id, email.UserId);
        Assert.Equal("a@example.org", email.Email);
        Assert.Equal("fr", email.Language);
    }

    [Fact]
    public async Task Read_InvitationWrittenBefore1241_HasNullLiveTv()
    {
        var now = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
        var id = Guid.NewGuid();
        using (var writer = new MailConciergeStore(_dir))
        {
            await writer.WriteAsync(d => d.Invitations.Add(new Invitation(id, "bob", null, [], "en", now.AddDays(7), InvitationState.Pending, now, true)));
        }

        // The file as an older version wrote it: the key is not there.
        var file = Path.Combine(_dir, "mailconcierge.json");
        var root = JsonNode.Parse(await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken))!;
        var invitation = root["Invitations"]!.AsArray().Single()!.AsObject();
        Assert.True(invitation.ContainsKey("LiveTv"));
        Assert.True(invitation.Remove("LiveTv"));
        Assert.False(invitation.ContainsKey("LiveTv"));
        await File.WriteAllTextAsync(file, root.ToJsonString(), TestContext.Current.CancellationToken);

        using var reader = new MailConciergeStore(_dir);
        var inv = await reader.ReadAsync(d => d.Invitations.Single());

        Assert.Equal(id, inv.Id);
        Assert.Equal("bob", inv.ProposedUsername);
        Assert.Null(inv.LiveTv);
    }

    [Fact]
    public async Task Read_InvitationWithoutUserIdAndAcceptedAt_HasNulls()
    {
        var now = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
        var id = Guid.NewGuid();
        using (var writer = new MailConciergeStore(_dir))
        {
            await writer.WriteAsync(d => d.Invitations.Add(new Invitation(id, "bob", null, [], "en", now.AddDays(7), InvitationState.Accepted, now, true, Guid.NewGuid(), now)));
        }

        // The file as 12.4.0.0 wrote it: neither key is there.
        var file = Path.Combine(_dir, "mailconcierge.json");
        var root = JsonNode.Parse(await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken))!;
        var invitation = root["Invitations"]!.AsArray().Single()!.AsObject();
        Assert.True(invitation.Remove("UserId"));
        Assert.True(invitation.Remove("AcceptedAt"));
        Assert.False(invitation.ContainsKey("UserId"));
        Assert.False(invitation.ContainsKey("AcceptedAt"));
        await File.WriteAllTextAsync(file, root.ToJsonString(), TestContext.Current.CancellationToken);

        using var reader = new MailConciergeStore(_dir);
        var inv = await reader.ReadAsync(d => d.Invitations.Single());

        Assert.Equal(id, inv.Id);
        Assert.Equal(InvitationState.Accepted, inv.State);
        Assert.Null(inv.UserId);
        Assert.Null(inv.AcceptedAt);
    }

    [Fact]
    public async Task Store_WriteIsAtomic()
    {
        var store = new MailConciergeStore(_dir);
        await store.WriteAsync(d => d.Emails.Add(new UserEmail(Guid.NewGuid(), null, null, "en", null)));
        await store.WriteAsync(d => d.Emails.Add(new UserEmail(Guid.NewGuid(), null, null, "en", null)));

        Assert.True(File.Exists(Path.Combine(_dir, "mailconcierge.json")));
        Assert.False(File.Exists(Path.Combine(_dir, "mailconcierge.json.tmp")));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(Path.Combine(_dir, "mailconcierge.json")));
        }
    }

    [Fact]
    public async Task Store_ConcurrentWrites_AllApplied()
    {
        var store = new MailConciergeStore(_dir);
        await Task.WhenAll(Enumerable.Range(0, 50).Select(_ =>
            Task.Run(() => store.WriteAsync(d => d.Emails.Add(new UserEmail(Guid.NewGuid(), null, null, "en", null))))));

        Assert.Equal(50, await store.ReadAsync(d => d.Emails.Count));
        Assert.Equal(50, await new MailConciergeStore(_dir).ReadAsync(d => d.Emails.Count));
    }

    [Fact]
    public async Task PurgeExpired_RemovesExpiredTokensAndMarksInvitations()
    {
        var now = new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);
        var store = new MailConciergeStore(_dir);
        await store.WriteAsync(d =>
        {
            d.Tokens.Add(new TokenRecord("old", TokenPurpose.ResetPin, null, null, null, now.AddMinutes(-1), 0));
            d.Tokens.Add(new TokenRecord("new", TokenPurpose.ResetPin, null, null, null, now.AddMinutes(1), 0));
            d.Invitations.Add(new Invitation(Guid.NewGuid(), "bob", null, [], "en", now.AddDays(-1), InvitationState.Pending, now.AddDays(-8)));
            d.Invitations.Add(new Invitation(Guid.NewGuid(), "amy", null, [], "en", now.AddDays(1), InvitationState.Pending, now));
        });

        await store.PurgeExpiredAsync(now);

        Assert.Equal(["new"], await store.ReadAsync(d => d.Tokens.Select(t => t.Hash).ToArray()));
        var states = await store.ReadAsync(d => d.Invitations.ToDictionary(i => i.ProposedUsername, i => i.State));
        Assert.Equal(InvitationState.Expired, states["bob"]);
        Assert.Equal(InvitationState.Pending, states["amy"]);
    }

    [Fact]
    public async Task Store_FailedWrite_DoesNotLeaveMutationInCache()
    {
        var store = new MailConciergeStore(_dir);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.WriteAsync(d =>
        {
            d.Emails.Add(new UserEmail(Guid.NewGuid(), null, null, "en", null));
            throw new InvalidOperationException("boom");
        }));

        Assert.Equal(0, await store.ReadAsync(d => d.Emails.Count));
    }

    [Fact]
    public async Task Store_StaleTempFile_IsReplaced()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "mailconcierge.json.tmp"), "junk", TestContext.Current.CancellationToken);
        var store = new MailConciergeStore(_dir);
        await store.WriteAsync(d => d.Emails.Add(new UserEmail(Guid.NewGuid(), null, null, "en", null)));
        Assert.Equal(1, await new MailConciergeStore(_dir).ReadAsync(d => d.Emails.Count));
        Assert.False(File.Exists(Path.Combine(_dir, "mailconcierge.json.tmp")));
    }
}
