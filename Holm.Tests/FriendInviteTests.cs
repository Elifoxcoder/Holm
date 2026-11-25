using System;
using System.Text.Json;
using WinNotch.Sharing;

namespace WinNotch.Tests;

public class FriendInviteTests
{
    static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    [Fact]
    public void A_new_code_is_short_and_reads_back_as_itself()
    {
        for (int i = 0; i < 200; i++)
        {
            string code = FriendInvite.New();
            Assert.Matches("^HOLM-[0-9A-HJKMNP-TV-Z]{5}-[0-9A-HJKMNP-TV-Z]{5}$", code);
            Assert.Equal(code, FriendInvite.Find(code));
        }
        Assert.NotEqual(FriendInvite.New(), FriendInvite.New());
    }

    [Theory]
    [InlineData("HOLM-7KQ2X-9XF4M")]
    [InlineData("holm-7kq2x-9xf4m")]
    [InlineData("  HOLM 7KQ2X 9XF4M ")]
    [InlineData("7KQ2X-9XF4M")]
    [InlineData("7kq2x9xf4m")]
    [InlineData("Add me as a friend on Holm: HOLM-7KQ2X-9XF4M (get Holm at github.com/ademerci/Holm)")]
    public void A_code_is_found_however_it_was_typed_or_pasted(string text) => Assert.Equal("HOLM-7KQ2X-9XF4M", FriendInvite.Find(text));

    [Fact]
    public void Letters_people_mix_up_are_read_as_what_they_look_like()
    {
        Assert.Equal("HOLM-11K2X-9XF4M", FriendInvite.Find("HOLM-ILK2X-9XF4M"));
        Assert.Equal("HOLM-00K2X-9XF4M", FriendInvite.Find("holm-oOk2x-9xf4m"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("HOLM-7KQ2X")]
    [InlineData("HOLM-7KQ2X-9XF4MM")]
    [InlineData("HOLM-UUUUU-9XF4M")] // U isn't in the alphabet
    [InlineData("hi there")]
    public void Anything_else_is_no_code(string? text) => Assert.Null(FriendInvite.Find(text));

    [Fact]
    public void Copied_text_only_counts_with_the_holm_prefix()
    {
        Assert.Null(FriendInvite.FindInMessage("7KQ2X9XF4M"));
        Assert.Null(FriendInvite.FindInMessage("password12"));
        Assert.Equal("HOLM-7KQ2X-9XF4M", FriendInvite.FindInMessage("my code is HOLM-7KQ2X-9XF4M!"));
    }

    [Fact]
    public void Each_code_has_its_own_inbox_and_proof()
    {
        string a = FriendInvite.New(), b = FriendInvite.New();
        Assert.Equal(FriendInvite.Topic(a), FriendInvite.Topic(a));
        Assert.NotEqual(FriendInvite.Topic(a), FriendInvite.Topic(b));
        Assert.Matches("^holm-inv-[a-z2-7]{20}$", FriendInvite.Topic(a)); // a valid ntfy topic
        Assert.True(FriendInvite.IsProof(FriendInvite.Proof(a)));
        Assert.NotEqual(FriendInvite.Proof(a), FriendInvite.Proof(b));
        Assert.DoesNotContain(FriendInvite.Proof(a), FriendInvite.Topic(a));
    }

    [Fact]
    public void The_card_opens_with_its_code_only()
    {
        using var lena = FriendIdentity.Create("Lena", null);
        string code = FriendInvite.New();
        string card = FriendInvite.Seal(code, lena.FriendCode, Now + 3600);

        var friend = FriendInvite.Open(code, card, Now);
        Assert.NotNull(friend);
        Assert.Equal("Lena", friend!.Name);
        Assert.Equal(lena.PublicKey, friend.Key);
        Assert.Equal(lena.Topic, friend.Topic);

        Assert.Null(FriendInvite.Open(FriendInvite.New(), card, Now));           // another code
        Assert.Null(FriendInvite.Open(code, card, Now + 7200));                   // after it ran out
        Assert.Null(FriendInvite.Open(code, card[..^4] + "AAAA", Now));           // tampered
        Assert.Null(FriendInvite.Open(code, "hinv1 nonsense", Now));
        Assert.Null(FriendInvite.Open(code, null, Now));
    }

    [Fact]
    public void The_card_keeps_a_friends_own_server()
    {
        using var lena = FriendIdentity.Create("Lena", "https://ntfy.example.org");
        string code = FriendInvite.New();
        var friend = FriendInvite.Open(code, FriendInvite.Seal(code, lena.FriendCode, Now + 60), Now);
        Assert.Equal("https://ntfy.example.org", friend!.Server);
    }

    [Fact]
    public void New_notes_have_authenticated_ids_and_old_notes_use_a_stable_sealed_hash()
    {
        using var noah = FriendIdentity.Create("Noah", null);
        using var lena = FriendIdentity.Create("Lena", null);
        var note = new FriendNote(FriendNoteKind.Chat, "Noah", Now, Text: "hi", MessageId: Guid.NewGuid().ToString("N"),
                                  NoteId: FriendNotes.NewNoteId());
        string first = FriendNotes.Seal(noah, lena.AsFriend(), note);
        string again = FriendNotes.Seal(noah, lena.AsFriend(), note);
        var opened = FriendNotes.Open(lena, first, out _)!;
        var repeated = FriendNotes.Open(lena, again, out _)!;

        Assert.StartsWith("wnf1 ", first);
        Assert.Equal(note.NoteId, opened.NoteId);
        Assert.Equal(opened.NoteId, repeated.NoteId);
        Assert.Equal(FriendNotes.ReplayId(first, opened), FriendNotes.ReplayId(again, repeated));
        Assert.NotEqual(FriendNotes.NewNoteId(), FriendNotes.NewNoteId());

        string legacy = FriendNotes.SealWithoutNoteId(noah, lena.AsFriend(), note);
        var old = FriendNotes.Open(lena, legacy, out _)!;
        Assert.Null(old.NoteId);
        Assert.StartsWith("hash:", FriendNotes.ReplayId(legacy, old));
        Assert.Equal(FriendNotes.ReplayId(legacy, old), FriendNotes.ReplayId(legacy, old));
        string[] outer = legacy.Split(' ');
        string paddedKey = outer[1] + new string('=', (4 - outer[1].Length % 4) % 4);
        Assert.Equal(FriendIdentity.FromBase64Url(outer[1]), FriendIdentity.FromBase64Url(paddedKey));
        using (var imported = FriendIdentity.ImportP256PublicKey(paddedKey)) Assert.Equal(256, imported.KeySize);
        string padded = outer[0] + " " + paddedKey + " " + outer[2];
        Assert.NotEqual(legacy, padded);
        var paddedNote = FriendNotes.Open(lena, padded, out _)!;
        Assert.Equal(FriendNotes.ReplayId(legacy, old), FriendNotes.ReplayId(padded, paddedNote));
        Assert.NotEqual(FriendNotes.ReplayId(first, opened), FriendNotes.ReplayId(legacy, old));
    }

    [Fact]
    public void Sender_time_has_a_bounded_future_skew_and_retention_bounded_past()
    {
        long now = Now;
        Assert.True(FriendNotes.IsSentTimePlausible(now, now));
        Assert.True(FriendNotes.IsSentTimePlausible(now + 300, now));
        Assert.True(FriendNotes.IsSentTimePlausible(now - 7 * 24 * 60 * 60 - 300, now));
        Assert.False(FriendNotes.IsSentTimePlausible(now + 301, now));
        Assert.False(FriendNotes.IsSentTimePlausible(now - 7 * 24 * 60 * 60 - 301, now));
    }

    [Fact]
    public void Invite_settings_from_before_single_use_keep_their_old_behavior()
    {
        var old = JsonSerializer.Deserialize<OwnInvite>("{\"Code\":\"HOLM-7KQ2X-9XF4M\",\"Proof\":\"8712e671199feb7ee20ad6dd346d5ef8\",\"Expires\":9999999999}")!;
        Assert.False(old.SingleUse);
        Assert.False(old.Revoked);
        var current = new OwnInvite { SingleUse = true, Revoked = true, UsedBy = "friend", RevokedAt = 123 };
        var back = JsonSerializer.Deserialize<OwnInvite>(JsonSerializer.Serialize(current))!;
        Assert.True(back.SingleUse);
        Assert.True(back.Revoked);
        Assert.Equal("friend", back.UsedBy);
        Assert.Equal(123, back.RevokedAt);
    }

    [Fact]
    public void A_request_made_with_a_code_carries_its_proof()
    {
        using var noah = FriendIdentity.Create("Noah", null);
        using var lena = FriendIdentity.Create("Lena", null);
        string code = FriendInvite.New();
        string requestId = Guid.NewGuid().ToString("N");
        var request = new FriendNote(FriendNoteKind.FriendRequest, "Noah", Now, Code: noah.FriendCode, RequestId: requestId,
                                     Invite: FriendInvite.Proof(code));

        var opened = FriendNotes.Open(lena, FriendNotes.Seal(noah, lena.AsFriend(), request), out string sender);
        Assert.NotNull(opened);
        Assert.Equal(noah.PublicKey, sender);
        Assert.Equal(FriendInvite.Proof(code), opened!.Invite);

        // Without a code, and with something that isn't a proof, there's none.
        var plain = FriendNotes.Open(lena, FriendNotes.Seal(noah, lena.AsFriend(), request with { Invite = null }), out _);
        Assert.Null(plain!.Invite);
        var junk = FriendNotes.Open(lena, FriendNotes.Seal(noah, lena.AsFriend(), request with { Invite = "not a proof" }), out _);
        Assert.Null(junk!.Invite);
    }
}

[Trait("Category", "Online")]
public class FriendInviteOnlineTests
{
    [Fact]
    public async Task A_friend_finds_the_card_behind_a_code_on_the_server()
    {
        using var lena = FriendIdentity.Create("Lena", null);
        string code = FriendInvite.New();
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await FriendInbox.PublishInviteAsync(FriendInvite.Topic(code), FriendInvite.Seal(code, lena.FriendCode, now + 600), Net.Timeout());

        Friend? friend = null;
        for (int attempt = 0; attempt < 10 && friend == null; attempt++)
        {
            await Task.Delay(1000); // the server stores a new message a moment later
            var cards = await FriendInbox.FetchInviteAsync(FriendInvite.Topic(code), Net.Timeout());
            friend = cards.Select(c => FriendInvite.Open(code, c, now)).FirstOrDefault(f => f != null);
        }
        Assert.NotNull(friend);
        Assert.Equal(lena.PublicKey, friend!.Key);

        // Nothing waits behind a code nobody made.
        Assert.Empty(await FriendInbox.FetchInviteAsync(FriendInvite.Topic(FriendInvite.New()), Net.Timeout()));
    }
}
