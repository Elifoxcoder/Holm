using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using WinNotch.Sharing;

namespace WinNotch.Tests;

// Friend notes on Holm's relay (relay/) with ntfy beside it: where notes go, what happens when one server refuses them,
// and how an inbox moves to the relay without losing friends on older Holm versions. Both servers are played by
// FakeInboxServer here; RelayWorkerTests runs the same against the real Worker (wrangler dev).

[CollectionDefinition("Inbox servers", DisableParallelization = true)]
public class InboxServersCollection { }

[Collection("Inbox servers")]
public sealed class RelayInboxTests : IDisposable
{
    readonly FakeInboxServer _relay = new(), _ntfy = new();
    readonly string _wasDefault = FriendIdentity.DefaultServer, _wasRelay = FriendIdentity.RelayServer;
    readonly string? _wasHolm = FriendIdentity.HolmRelayServer;

    public RelayInboxTests()
    {
        FriendIdentity.DefaultServer = _ntfy.Url;
        FriendIdentity.RelayServer = _relay.Url;
        FriendIdentity.HolmRelayServer = null; // these tests never call the real Holm relay
        FriendsService.ProtectKey = (data, _) => data.ToArray();
        FriendsService.UnprotectKey = (data, _) => data.ToArray();
    }

    public void Dispose()
    {
        FriendIdentity.DefaultServer = _wasDefault;
        FriendIdentity.RelayServer = _wasRelay;
        FriendIdentity.HolmRelayServer = _wasHolm;
        _relay.Dispose();
        _ntfy.Dispose();
    }

    static Friend OnRelay(FriendIdentity who) => new() { Name = who.Name, Topic = who.Topic, Key = who.PublicKey, Server = FriendIdentity.RelayServer };

    [Fact]
    public async Task A_note_the_relay_refuses_goes_to_ntfy()
    {
        using var lena = FriendIdentity.Create("Lena", null);
        _relay.Status = 503;
        await FriendInbox.PostAsync(OnRelay(lena), "wnf1 x", Net.Timeout());
        Assert.Equal(lena.Topic, Assert.Single(_ntfy.Posts).Topic);
        Assert.Empty(_relay.Posts);
    }

    [Fact]
    public async Task A_note_for_ntfy_doesnt_go_to_the_relay()
    {
        // Older Holm only follows ntfy: a note it would never read is worse than a clear error.
        using var lena = FriendIdentity.Create("Lena", null);
        _ntfy.Status = 429;
        await Assert.ThrowsAsync<IOException>(() => FriendInbox.PostAsync(lena.AsFriend(), "wnf1 x", Net.Timeout()));
        Assert.Empty(_relay.Posts);
    }

    [Fact]
    public async Task Presence_is_passed_on_but_not_kept()
    {
        using var lena = FriendIdentity.Create("Lena", null);
        await FriendInbox.PostAsync(OnRelay(lena), "wnf1 ping", Net.Timeout(), keep: false);
        await FriendInbox.PostAsync(OnRelay(lena), "wnf1 chat", Net.Timeout());
        Assert.Equal([false, true], _relay.Posts.Select(p => p.Keep));
    }

    [Fact]
    public async Task An_invite_code_works_while_ntfy_is_over_its_daily_quota()
    {
        using var lena = FriendIdentity.Create("Lena", null);
        string code = FriendInvite.New();
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _ntfy.Status = 429; // "daily message quota reached": what broke short friend codes
        await FriendInbox.PublishInviteAsync(FriendInvite.Topic(code), FriendInvite.Seal(code, lena.FriendCode, now + 600), Net.Timeout());
        _ntfy.Status = 200;
        var cards = await FriendInbox.FetchInviteAsync(FriendInvite.Topic(code), Net.Timeout());
        Assert.Equal(lena.PublicKey, cards.Select(c => FriendInvite.Open(code, c, now)).Single(f => f != null)!.Key);
    }

    [Fact]
    public async Task A_new_invite_is_not_mirrored_to_ntfy_but_an_old_ntfy_only_code_still_opens()
    {
        using var lena = FriendIdentity.Create("Lena", null);
        string code = FriendInvite.New(), old = FriendInvite.New();
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await FriendInbox.PublishInviteAsync(FriendInvite.Topic(code), FriendInvite.Seal(code, lena.FriendCode, now + 600), Net.Timeout());
        Assert.Single(_relay.Posts);
        Assert.Empty(_ntfy.Posts); // no new third-party copy of the unstretched invite derivation

        _ntfy.Keep(FriendInvite.Topic(old), FriendInvite.Seal(old, lena.FriendCode, now + 600)); // as Holm 1.17 leaves it
        _relay.Status = 503;
        var cards = await FriendInbox.FetchInviteAsync(FriendInvite.Topic(old), Net.Timeout());
        Assert.NotNull(FriendInvite.Open(old, Assert.Single(cards), now));
    }

    [Fact]
    public async Task An_invite_can_be_revoked_and_renewed_as_single_use()
    {
        long now = Now();
        var old = new OwnInvite { Code = FriendInvite.New(), Proof = "", Expires = now + 3600 };
        old.Proof = FriendInvite.Proof(old.Code);
        var settings = new AppSettings { Beam = false, FriendInvites = [old] };
        var lena = new FriendsService(settings, Ui);
        Assert.Same(old, Assert.Single(lena.Invites));
        Assert.True(lena.RevokeInvite(old));
        Assert.Empty(lena.Invites);

        var fresh = await lena.InviteAsync(fresh: true, Net.Timeout());
        Assert.True(fresh.SingleUse);
        Assert.True(old.Revoked);
        Assert.Same(fresh, Assert.Single(lena.Invites));
        Assert.Single(_relay.Posts);
        Assert.Empty(_ntfy.Posts);
    }

    [Fact]
    public async Task An_invite_needs_one_server()
    {
        _relay.Status = _ntfy.Status = 503;
        var ex = await Assert.ThrowsAsync<IOException>(() => FriendInbox.PublishInviteAsync(FriendInvite.Topic(FriendInvite.New()), "hinv1 x", Net.Timeout()));
        Assert.Contains("503", ex.Message);
        Assert.Empty(_ntfy.Posts); // publishing an invite never falls back to the third-party mirror
    }

    static Task Ui(Action a)
    {
        lock (typeof(RelayInboxTests)) a();
        return Task.CompletedTask;
    }

    static async Task Until(Func<bool> done, string what)
    {
        for (int i = 0; i < 100 && !done(); i++) await Task.Delay(100);
        Assert.True(done(), what);
    }

    [Fact]
    public async Task An_inbox_moves_to_the_relay_and_friends_on_older_Holm_keep_reaching_it()
    {
        // Noah is a friend on Holm 1.17: he only knows ntfy, and Lena's old card says ntfy.
        using var noah = FriendIdentity.Create("Noah", null);
        var settings = new AppSettings { Beam = true, ProfileName = "Lena" };
        settings.Friends.Add(noah.AsFriend());
        var lena = new FriendsService(settings, Ui);
        var chats = new ConcurrentQueue<string>();
        lena.ChatReceived += (_, _, text, _) => chats.Enqueue(text);
        try
        {
            var me = lena.EnsureMe(); // follows ntfy and the relay
            var oldCard = me.AsFriend();
            Assert.Null(oldCard.Server);

            // The relay let her in: her code names it now, and Noah was told.
            await Until(() => settings.FriendsRelay, "the inbox moved to the relay");
            Assert.Equal(_relay.Url, FriendIdentity.ParseFriendCode(lena.Me!.FriendCode)!.Server);
            // Her profile goes to him as well; the move is the "added you" note with her new code.
            List<FriendNote> Told() => _ntfy.Posts.Where(p => p.Topic == noah.Topic).Select(p => FriendNotes.Open(noah, p.Text, out _))
                                                  .Where(n => n?.Kind == FriendNoteKind.AddedYou).ToList()!;
            await Until(() => Told().Count > 0, "Noah heard of the move");
            var told = Assert.Single(Told());
            var newCard = FriendIdentity.ParseFriendCode(told.Code)!;
            Assert.Equal(_relay.Url, newCard.Server); // Holm 1.17 replaces a friend's card with this one (FriendsService.Handle)

            // Noah's notes reach her through both: the old card on ntfy, the new one on the relay.
            var lenaAsFriend = lena.Me!.AsFriend();
            await FriendInbox.PostAsync(oldCard, Chat(noah, lenaAsFriend, "via ntfy"), Net.Timeout());
            await FriendInbox.PostAsync(newCard, Chat(noah, lenaAsFriend, "via the relay"), Net.Timeout());
            await Until(() => chats.Count == 2, "both chats arrived");
            Assert.Equal(["via ntfy", "via the relay"], chats.Order());

            // Holm closed; a note waits on the relay; the next start catches up and handles it once.
            lena.Stop();
            await FriendInbox.PostAsync(newCard, Chat(noah, lenaAsFriend, "while closed"), Net.Timeout());
            lena.Restart();
            await Until(() => chats.Count == 3, "the note left while Holm was closed arrived");
            await Task.Delay(500);
            Assert.Equal(3, chats.Count);

            // Moving happens once.
            lena.Restart();
            await Task.Delay(1000);
            Assert.Single(Told());
        }
        finally { lena.Stop(); }
    }

    [Fact]
    public async Task An_inbox_on_a_server_of_ones_own_stays_there()
    {
        using var custom = new FakeInboxServer();
        var settings = new AppSettings { Beam = true, FriendsServer = custom.Url };
        var lena = new FriendsService(settings, Ui);
        try
        {
            lena.EnsureMe();
            await Task.Delay(1000);
            Assert.False(settings.FriendsRelay);
            Assert.Equal(custom.Url, FriendIdentity.ParseFriendCode(lena.Me!.FriendCode)!.Server);
            Assert.Equal(0, _relay.Followers);
        }
        finally { lena.Stop(); }
    }

    static string Chat(FriendIdentity from, Friend to, string text)
        => FriendNotes.Seal(from, to, new FriendNote(FriendNoteKind.Chat, from.Name, DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                                                     Text: text, MessageId: Guid.NewGuid().ToString("N")));

    static string Request(FriendIdentity from, Friend to, string proof, long sent)
        => FriendNotes.Seal(from, to, new FriendNote(FriendNoteKind.FriendRequest, from.Name, sent, Code: from.FriendCode,
                                                      RequestId: Guid.NewGuid().ToString("N"), Invite: proof));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_mirrored_note_is_replayed_once_even_after_settings_are_reloaded(bool legacy)
    {
        using var noah = FriendIdentity.Create("Noah", null);
        var settings = new AppSettings { Beam = false, FriendsServer = _ntfy.Url };
        settings.Friends.Add(noah.AsFriend());
        var lena = new FriendsService(settings, Ui);
        var target = lena.EnsureMe().AsFriend();
        var note = new FriendNote(FriendNoteKind.Chat, noah.Name, Now(), Text: "only once", MessageId: Guid.NewGuid().ToString("N"));
        string text = legacy ? FriendNotes.SealWithoutNoteId(noah, target, note) : FriendNotes.Seal(noah, target, note);
        int chats = 0;
        lena.ChatReceived += (_, _, _, _) => chats++;
        long arrived = Now();
        lena.Handle("relay-copy", arrived, text);
        lena.Handle("ntfy-copy", arrived, text);
        Assert.Equal(1, chats);
        Assert.Contains(settings.FriendsSeen, id => id.StartsWith(legacy ? "hash:" : "note:", StringComparison.Ordinal));
        Assert.Equal(settings.FriendsSeen.Count, settings.FriendsSeenTimes.Count);

        var reloaded = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        var again = new FriendsService(reloaded, Ui);
        again.ChatReceived += (_, _, _, _) => chats++;
        again.EnsureMe();
        again.Handle("another-copy", arrived, text);
        Assert.Equal(1, chats);
    }

    static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    [Theory]
    [InlineData(301)]
    [InlineData(-605200)] // safely beyond the seven-day + 300-second window, including test execution time
    public void An_authenticated_note_outside_the_sender_time_bounds_is_ignored(int delta)
    {
        using var noah = FriendIdentity.Create("Noah", null);
        var settings = new AppSettings { Beam = false, FriendsServer = _ntfy.Url };
        settings.Friends.Add(noah.AsFriend());
        var lena = new FriendsService(settings, Ui);
        var target = lena.EnsureMe().AsFriend();
        int chats = 0;
        lena.ChatReceived += (_, _, _, _) => chats++;
        long now = Now();
        Assert.False(FriendNotes.IsSentTimePlausible(now - 605101, now)); // exact boundary, independent of elapsed test time
        string text = FriendNotes.Seal(noah, target, new FriendNote(FriendNoteKind.Chat, noah.Name, now + delta,
                                                                    Text: "bad clock", MessageId: Guid.NewGuid().ToString("N")));
        lena.Handle("bad-clock", now, text);
        Assert.Equal(0, chats);
    }

    [Theory]
    [InlineData(FriendNoteKind.Ping)]
    [InlineData(FriendNoteKind.Here)]
    public void A_fresh_server_timestamp_cannot_make_an_old_authenticated_presence_note_fresh(FriendNoteKind kind)
    {
        using var noah = FriendIdentity.Create("Noah", null);
        var settings = new AppSettings { Beam = false, FriendsServer = _ntfy.Url };
        settings.Friends.Add(noah.AsFriend());
        var lena = new FriendsService(settings, Ui);
        var target = lena.EnsureMe().AsFriend();
        int online = 0;
        lena.FriendOnline += _ => online++;
        long now = Now();
        string text = FriendNotes.Seal(noah, target, new FriendNote(kind, noah.Name, now - 31));
        lena.Handle("fresh-server-time", now, text);
        Assert.Equal(0, online);
        Assert.Empty(_ntfy.Posts);
    }

    [Fact]
    public void A_fresh_server_timestamp_cannot_make_an_old_own_device_phone_note_fresh()
    {
        var settings = new AppSettings { Beam = false };
        var service = new FriendsService(settings, Ui);
        var own = service.EnsureMe();
        int received = 0;
        service.PhoneStatusReceived += (_, _) => received++;
        long now = Now();
        var status = new PhoneStatus(PhoneStatus.Notification, "Lena", "old notification", now, true);
        string text = FriendNotes.Seal(own, own.AsFriend(), new FriendNote(FriendNoteKind.Phone, "My phone", now - 61,
                                                                            Device: OwnDevice.NewId(), Phone: status));
        service.Handle("fresh-server-time", now, text);
        Assert.Equal(0, received);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_fresh_server_timestamp_cannot_make_an_old_authenticated_friend_request_fresh(bool alreadyFriend)
    {
        using var noah = FriendIdentity.Create("Noah", null);
        var settings = new AppSettings { Beam = false, FriendsServer = _ntfy.Url };
        if (alreadyFriend) settings.Friends.Add(noah.AsFriend());
        var lena = new FriendsService(settings, Ui);
        var target = lena.EnsureMe().AsFriend();
        int asked = 0;
        lena.FriendRequested += (_, _) => asked++;
        long now = Now();
        string text = FriendNotes.Seal(noah, target, new FriendNote(FriendNoteKind.FriendRequest, noah.Name,
            now - 2 * 24 * 60 * 60 - 1, Code: noah.FriendCode, RequestId: Guid.NewGuid().ToString("N")));
        lena.Handle("fresh-server-time", now, text);
        Assert.Equal(0, asked);
        Assert.Empty(lena.Incoming);
        await Task.Delay(100); // a fire-and-forget repair response would reach the fake inbox by now
        Assert.Empty(_ntfy.Posts);
    }

    [Fact]
    public async Task A_backdated_invite_request_that_arrived_after_the_grace_period_is_not_autoaccepted()
    {
        using var noah = FriendIdentity.Create("Noah", null);
        string code = FriendInvite.New();
        long now = Now();
        var settings = new AppSettings
        {
            Beam = false, FriendsServer = _ntfy.Url,
            FriendInvites = [new OwnInvite { Code = code, Proof = FriendInvite.Proof(code), Expires = now - 600 }],
        };
        var lena = new FriendsService(settings, Ui);
        var target = lena.EnsureMe().AsFriend();
        int asked = 0;
        lena.FriendRequested += (_, _) => asked++;
        // dto.T would pass the old expires + 300 check. The server arrival time must not.
        lena.Handle("late", now, Request(noah, target, FriendInvite.Proof(code), now - 3600));
        Assert.Equal(1, asked);
        Assert.Single(lena.Incoming);
        Assert.Null(lena.Find(noah.PublicKey));
        Assert.Empty(_ntfy.Posts);
    }

    [Theory]
    [InlineData(300, true)]
    [InlineData(301, false)]
    public async Task Invite_expiry_keeps_the_300_second_server_arrival_allowance(int offset, bool accepted)
    {
        using var noah = FriendIdentity.Create("Noah", null);
        string code = FriendInvite.New();
        string proof = FriendInvite.Proof(code);
        long now = Now(), expires = now - 600, arrived = expires + offset;
        var settings = new AppSettings
        {
            Beam = false, FriendsServer = _ntfy.Url,
            FriendInvites = [new OwnInvite { Code = code, Proof = proof, Expires = expires }],
        };
        var lena = new FriendsService(settings, Ui);
        var target = lena.EnsureMe().AsFriend();
        int asked = 0, acceptedEvents = 0;
        lena.FriendRequested += (_, _) => asked++;
        lena.InviteAccepted += _ => acceptedEvents++;
        lena.Handle("boundary", arrived, Request(noah, target, proof, now - 3600));
        if (accepted) await Until(() => lena.Find(noah.PublicKey) != null && acceptedEvents == 1, "the boundary request was accepted");
        else
        {
            Assert.Equal(1, asked);
            Assert.Null(lena.Find(noah.PublicKey));
            Assert.Single(lena.Incoming);
        }
    }

    [Fact]
    public async Task A_single_use_invite_uses_server_arrival_time_and_cannot_spend_twice()
    {
        using var first = FriendIdentity.Create("First", null);
        using var second = FriendIdentity.Create("Second", null);
        string code = FriendInvite.New();
        string proof = FriendInvite.Proof(code);
        long now = Now();
        var invite = new OwnInvite { Code = code, Proof = proof, Expires = now - 600, SingleUse = true };
        var settings = new AppSettings { Beam = false, FriendsServer = _ntfy.Url, FriendInvites = [invite] };
        var lena = new FriendsService(settings, Ui);
        var target = lena.EnsureMe().AsFriend();
        int asked = 0, accepted = 0;
        lena.FriendRequested += (_, _) => asked++;
        lena.InviteAccepted += _ => accepted++;

        // Both reached the server before expires + 300, while Holm was offline. Reconnecting now must not move
        // that arrival time forward. The first is also deliberately backdated; dto.T is not the expiry clock.
        long arrived = now - 1000;
        lena.Handle("first", arrived, Request(first, target, proof, now - 3600));
        lena.Handle("second", arrived, Request(second, target, proof, now));
        await Until(() => lena.Find(first.PublicKey) != null && accepted == 1, "the first request was accepted");
        Assert.Equal(1, accepted);
        Assert.Equal(1, asked);
        Assert.True(invite.Revoked);
        Assert.Equal(first.PublicKey, invite.UsedBy);
        Assert.Empty(lena.Invites);
        Assert.Null(lena.Find(second.PublicKey));
        Assert.Single(lena.Incoming);
    }

    [Fact]
    public void Seen_note_retention_is_deduplicated_and_bounded()
    {
        var settings = new AppSettings { Beam = false };
        settings.FriendsSeen.AddRange(Enumerable.Range(0, FriendsService.SeenLimit + 20).Select(i => $"id-{i}"));
        settings.FriendsSeen.Add(settings.FriendsSeen[^1]);
        _ = new FriendsService(settings, Ui);
        Assert.Equal(FriendsService.SeenLimit, settings.FriendsSeen.Count);
        Assert.Equal(settings.FriendsSeen.Count, settings.FriendsSeen.Distinct().Count());
        Assert.Equal(settings.FriendsSeen.Count, settings.FriendsSeenTimes.Count);
        Assert.All(settings.FriendsSeenTimes, at => Assert.True(at > 0));
        Assert.DoesNotContain("id-0", settings.FriendsSeen);
        Assert.DoesNotContain("id-19", settings.FriendsSeen);
        Assert.Contains("id-20", settings.FriendsSeen);
        Assert.Contains($"id-{FriendsService.SeenLimit + 19}", settings.FriendsSeen);
    }

    [Fact]
    public void Seen_entries_are_pruned_by_time_while_a_recent_replay_stays_blocked()
    {
        using var noah = FriendIdentity.Create("Noah", null);
        var settings = new AppSettings { Beam = false, FriendsServer = _ntfy.Url };
        settings.Friends.Add(noah.AsFriend());
        var first = new FriendsService(settings, Ui);
        var target = first.EnsureMe().AsFriend();
        var note = new FriendNote(FriendNoteKind.Chat, noah.Name, Now(), Text: "recent replay", MessageId: Guid.NewGuid().ToString("N"),
                                  NoteId: FriendNotes.NewNoteId());
        string text = FriendNotes.Seal(noah, target, note);
        string replayId = FriendNotes.ReplayId(text, note);
        long now = Now();
        settings.FriendsSeen.Add(replayId);
        settings.FriendsSeenTimes.Add(now - 60);
        settings.FriendsSeen.Add("old-transport-id");
        settings.FriendsSeenTimes.Add(now - FriendsService.SeenRetentionSeconds - 1);

        var reloaded = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        var again = new FriendsService(reloaded, Ui);
        int chats = 0;
        again.ChatReceived += (_, _, _, _) => chats++;
        again.EnsureMe();
        Assert.DoesNotContain("old-transport-id", reloaded.FriendsSeen);
        Assert.Contains(replayId, reloaded.FriendsSeen);
        Assert.Equal(reloaded.FriendsSeen.Count, reloaded.FriendsSeenTimes.Count);
        Assert.Single(reloaded.FriendsSeenTimes);

        again.Handle("another-transport-id", now, text);
        Assert.Equal(0, chats);
    }

    [Fact]
    public void Legacy_seen_entries_without_times_are_retained_as_unknown_for_one_window()
    {
        var settings = new AppSettings { Beam = false };
        settings.FriendsSeen.Add("legacy-transport-id");
        long before = Now();
        _ = new FriendsService(settings, Ui);
        long after = Now();
        Assert.Contains("legacy-transport-id", settings.FriendsSeen);
        Assert.InRange(Assert.Single(settings.FriendsSeenTimes), before, after);
    }

    [Fact]
    public async Task Resending_a_request_keeps_the_original_accept_button_working()
    {
        var sender = new FriendsService(new AppSettings { Beam = true, FriendsServer = _ntfy.Url }, Ui);
        var receiver = new FriendsService(new AppSettings { Beam = true, FriendsServer = _ntfy.Url }, Ui);
        try
        {
            var target = receiver.EnsureMe().AsFriend();
            sender.EnsureMe();
            await Until(() => _ntfy.Followers == 2, "both inboxes connected");
            Assert.Equal(FriendRequestResult.NoResponse,
                await sender.RequestAddAsync(target, TimeSpan.FromMilliseconds(100), Net.Timeout()));
            await Until(() => receiver.Incoming.Count == 1, "the request arrived");
            var original = Assert.Single(receiver.Incoming);

            Assert.Equal(FriendRequestResult.NoResponse,
                await sender.RequestAddAsync(target, TimeSpan.FromMilliseconds(100), Net.Timeout()));
            Assert.Equal(original.RequestId, Assert.Single(sender.Pending).RequestId);
            Assert.Equal(original.RequestId, Assert.Single(receiver.Incoming).RequestId);

            await receiver.RespondToFriendRequestAsync(original.Card, original.RequestId, true, Net.Timeout());
            await Until(() => sender.Find(target.Key) != null, "the sender received the acceptance");
            Assert.NotNull(receiver.Find(sender.Me!.PublicKey));
            Assert.Empty(receiver.Incoming);
        }
        finally { sender.Stop(); receiver.Stop(); }
    }

    [Fact]
    public async Task An_unanswered_request_survives_a_settings_reload_and_can_be_withdrawn()
    {
        var settings = new AppSettings { Beam = true, FriendsServer = _ntfy.Url };
        var sender = new FriendsService(new AppSettings { Beam = true, FriendsServer = _ntfy.Url }, Ui);
        var receiver = new FriendsService(settings, Ui);
        FriendsService? restarted = null;
        try
        {
            var target = receiver.EnsureMe().AsFriend();
            sender.EnsureMe();
            await Until(() => _ntfy.Subscriptions == 2, "both inboxes connected");
            await sender.RequestAddAsync(target, TimeSpan.FromMilliseconds(100), Net.Timeout());
            await Until(() => receiver.Incoming.Count == 1, "the request arrived");
            receiver.Stop();
            var reloaded = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
            restarted = new FriendsService(reloaded, Ui);
            Assert.Equal(Assert.Single(sender.Pending).RequestId, Assert.Single(restarted.Incoming).RequestId);
            Assert.Equal(target.Key, restarted.EnsureMe().PublicKey);
            restarted.Restart();
            await Until(() => _ntfy.Subscriptions == 3, "the receiver reconnected");
            await sender.CancelRequestAsync(Assert.Single(sender.Pending));
            await Until(() => restarted.Incoming.Count == 0, "the withdrawal removed the saved request");
        }
        finally { sender.Stop(); receiver.Stop(); restarted?.Stop(); }
    }
}

/// <summary>
/// The relay (relay/src/index.js) run locally with `npm run dev` in relay/: set HOLM_RELAY_URL=http://127.0.0.1:8787
/// and run the Local tests. Checks Holm's real client against the real Worker.
/// </summary>
[Collection("Inbox servers")]
[Trait("Category", "Local")]
public sealed class RelayWorkerTests : IDisposable
{
    readonly string? _url = Environment.GetEnvironmentVariable("HOLM_RELAY_URL");
    readonly string _wasDefault = FriendIdentity.DefaultServer, _wasRelay = FriendIdentity.RelayServer;
    readonly string? _wasHolm = FriendIdentity.HolmRelayServer;

    public RelayWorkerTests()
    {
        if (_url == null) return;
        FriendIdentity.RelayServer = _url.TrimEnd('/');
        FriendIdentity.DefaultServer = "http://127.0.0.1:9"; // no ntfy: everything must work on the relay alone
        FriendIdentity.HolmRelayServer = null;
    }

    public void Dispose()
    {
        FriendIdentity.DefaultServer = _wasDefault;
        FriendIdentity.RelayServer = _wasRelay;
        FriendIdentity.HolmRelayServer = _wasHolm;
    }

    [Fact]
    public async Task Notes_invites_presence_and_catching_up_work_on_the_Worker()
    {
        if (_url == null) return; // needs `npm run dev` in relay/
        using var lena = FriendIdentity.Create("Lena", FriendIdentity.RelayServer);
        using var noah = FriendIdentity.Create("Noah", null);
        var toLena = lena.AsFriend();
        Assert.Equal(FriendIdentity.RelayServer, toLena.Server);

        string Note(FriendNoteKind kind, string? text = null)
            => FriendNotes.Seal(noah, toLena, new FriendNote(kind, "Noah", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Text: text,
                                                             MessageId: text == null ? null : Guid.NewGuid().ToString("N")));

        // Left before Lena follows: kept, and found when she catches up.
        long since = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 5;
        await FriendInbox.PostAsync(toLena, Note(FriendNoteKind.Chat, "kept"), Net.Timeout());
        await FriendInbox.PostAsync(toLena, Note(FriendNoteKind.Ping), Net.Timeout(), keep: false); // gone: nobody listened

        var got = new ConcurrentQueue<FriendNote>();
        long last = 0;
        bool connected = false;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var follow = FriendInbox.FollowRelayAsync(lena.Topic, since, (_, _, text) =>
        {
            if (FriendNotes.Open(lena, text, out _) is { } n) got.Enqueue(n);
            return Task.CompletedTask;
        }, t => last = t, () => connected = true, null, stop.Token);

        for (int i = 0; i < 100 && got.IsEmpty; i++) await Task.Delay(100);
        Assert.True(connected);
        Assert.Equal("kept", Assert.Single(got).Text);

        await FriendInbox.PostAsync(toLena, Note(FriendNoteKind.Ping), Net.Timeout(), keep: false);
        await FriendInbox.PostAsync(toLena, Note(FriendNoteKind.Chat, "live"), Net.Timeout());
        for (int i = 0; i < 100 && got.Count < 3; i++) await Task.Delay(100);
        Assert.Equal([FriendNoteKind.Chat, FriendNoteKind.Ping, FriendNoteKind.Chat], got.Select(n => n.Kind));
        Assert.True(last >= since);
        stop.Cancel();
        await follow;

        // An invite code, on the relay alone (ntfy is unreachable here).
        string code = FriendInvite.New();
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await FriendInbox.PublishInviteAsync(FriendInvite.Topic(code), FriendInvite.Seal(code, lena.FriendCode, now + 600), Net.Timeout());
        var cards = await FriendInbox.FetchInviteAsync(FriendInvite.Topic(code), Net.Timeout());
        Assert.Equal(lena.PublicKey, FriendInvite.Open(code, Assert.Single(cards), now)!.Key);

        // A quiet line stays open: WebSocket pings (every 30 s, 30 s for the pong) keep it, without reconnecting.
        got.Clear();
        int connects = 0;
        using var quiet = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var quietFollow = FriendInbox.FollowRelayAsync(lena.Topic, null, (_, _, text) =>
        {
            if (FriendNotes.Open(lena, text, out _) is { } n) got.Enqueue(n);
            return Task.CompletedTask;
        }, _ => { }, () => Interlocked.Increment(ref connects), null, quiet.Token);
        await Task.Delay(TimeSpan.FromSeconds(75));
        await FriendInbox.PostAsync(toLena, Note(FriendNoteKind.Chat, "after a quiet minute"), Net.Timeout());
        for (int i = 0; i < 50 && got.IsEmpty; i++) await Task.Delay(100);
        Assert.Equal("after a quiet minute", Assert.Single(got).Text);
        Assert.Equal(1, connects);
        quiet.Cancel();
        await quietFollow;

        // What isn't a Holm note is refused.
        await Assert.ThrowsAsync<IOException>(() => FriendInbox.PostAsync(new Friend { Topic = lena.Topic, Server = FriendIdentity.RelayServer }, "hello", Net.Timeout()));
    }
}

/// <summary>
/// An inbox server for tests, speaking what Holm uses of ntfy (POST, polling, the JSON stream) and the relay's WebSocket.
/// <see cref="Status"/> other than 200 refuses every note and poll, as a server over its quota or down.
/// </summary>
sealed class FakeInboxServer : IDisposable
{
    readonly HttpListener _listener = new();
    readonly List<(string Id, long Time, string Topic, string Text)> _kept = new();
    readonly List<(string Topic, Func<string, Task> Send)> _followers = new();
    readonly CancellationTokenSource _stop = new();
    int _subscriptions;
    public string Url { get; }
    public volatile int Status = 200;
    public ConcurrentQueue<(string Topic, string Text, bool Keep)> Posts { get; } = new();
    public int Followers { get { lock (_followers) return _followers.Count; } }
    public int Subscriptions => Volatile.Read(ref _subscriptions);

    public FakeInboxServer()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        Url = $"http://localhost:{port}";
        _listener.Prefixes.Add(Url + "/");
        _listener.Start();
        _ = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync(); } catch { return; }
                _ = Task.Run(() => HandleAsync(context));
            }
        });
    }

    public void Keep(string topic, string text)
    {
        lock (_kept) _kept.Add((Guid.NewGuid().ToString("N")[..12], DateTimeOffset.UtcNow.ToUnixTimeSeconds(), topic, text));
    }

    static string Line(string ev, string id, long time, string topic, string? text)
        => JsonSerializer.Serialize(new { id, time, @event = ev, topic, message = text });

    List<string> KeptSince(string topic, long? since)
    {
        lock (_kept) return since == null ? [] : _kept.Where(k => k.Topic == topic && k.Time >= since).Select(k => Line("message", k.Id, k.Time, topic, k.Text)).ToList();
    }

    async Task HandleAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        string[] parts = request.Url!.AbsolutePath.Trim('/').Split('/');
        string topic = parts[0];
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long? since = request.QueryString["since"] switch { null => null, "12h" => now - 43200, var s => long.Parse(s) };
        try
        {
            if (Status != 200)
            {
                response.StatusCode = Status;
                response.Close();
                return;
            }
            if (request.HttpMethod == "POST")
            {
                string text = await new StreamReader(request.InputStream).ReadToEndAsync();
                bool keep = request.Headers["Cache"] != "no";
                Posts.Enqueue((topic, text, keep));
                string id = Guid.NewGuid().ToString("N")[..12];
                if (keep) lock (_kept) _kept.Add((id, now, topic, text));
                List<Func<string, Task>> sends;
                lock (_followers) sends = _followers.Where(f => f.Topic == topic).Select(f => f.Send).ToList();
                foreach (var send in sends)
                {
                    try { await send(Line("message", id, now, topic, text)); } catch { }
                }
                await Write(response, Line("message", id, now, topic, text) + "\n");
                response.Close();
                return;
            }
            if (parts.Length > 1 && parts[1] == "json" && request.QueryString["poll"] == "1")
            {
                await Write(response, string.Concat(KeptSince(topic, since).Select(l => l + "\n")));
                response.Close();
                return;
            }
            if (parts.Length > 1 && parts[1] == "ws" && request.IsWebSocketRequest)
            {
                var ws = (await context.AcceptWebSocketAsync(null)).WebSocket;
                var gate = new SemaphoreSlim(1);
                async Task Send(string line)
                {
                    await gate.WaitAsync();
                    try { await ws.SendAsync(Encoding.UTF8.GetBytes(line), WebSocketMessageType.Text, true, CancellationToken.None); }
                    finally { gate.Release(); }
                }
                await Send(Line("open", "o", now, topic, null));
                foreach (string line in KeptSince(topic, since)) await Send(line);
                var follower = (topic, (Func<string, Task>)Send);
                lock (_followers) _followers.Add(follower);
                Interlocked.Increment(ref _subscriptions);
                try { await ws.ReceiveAsync(new byte[16], _stop.Token); } catch { }
                lock (_followers) _followers.Remove(follower);
                return;
            }
            if (parts.Length > 1 && parts[1] == "json")
            {
                response.SendChunked = true;
                var gate = new SemaphoreSlim(1);
                async Task Send(string line)
                {
                    await gate.WaitAsync();
                    try
                    {
                        byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
                        await response.OutputStream.WriteAsync(bytes);
                        await response.OutputStream.FlushAsync();
                    }
                    finally { gate.Release(); }
                }
                await Send(Line("open", "o", now, topic, null));
                foreach (string line in KeptSince(topic, since)) await Send(line);
                var follower = (topic, (Func<string, Task>)Send);
                lock (_followers) _followers.Add(follower);
                Interlocked.Increment(ref _subscriptions);
                try { await Task.Delay(Timeout.Infinite, _stop.Token); } catch { }
                lock (_followers) _followers.Remove(follower);
                return;
            }
            response.StatusCode = 404;
            response.Close();
        }
        catch { try { response.Abort(); } catch { } }
    }

    static async Task Write(HttpListenerResponse response, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _listener.Stop(); _listener.Close(); } catch { }
    }
}
