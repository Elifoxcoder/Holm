using System;
using WinNotch.Sharing;

namespace WinNotch.Tests;

/// <summary>Notes between the devices of one account (the phone app, other PCs), which share one identity and inbox.</summary>
public class OwnDevicesTests
{
    static readonly DateTime T0 = new(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);
    static readonly string Pc = OwnDevice.NewId(), Phone = OwnDevice.NewId();

    [Fact]
    public void A_login_code_from_the_phone_is_digits_only()
    {
        var code = PhoneStatus.From(PhoneStatus.Code, "Google", "482913", 1, true);
        Assert.NotNull(code);
        Assert.Equal("482913", code!.Detail);
        Assert.Null(PhoneStatus.From(PhoneStatus.Code, "Google", "48-29", 1, true));
        Assert.Null(PhoneStatus.From(PhoneStatus.Code, "Google", "your code is 482913", 1, true));
        Assert.Null(PhoneStatus.From(PhoneStatus.Code, "Google", null, 1, true));
        Assert.True(new AppSettings().PhoneCodes);
    }

    [Fact]
    public void A_message_written_on_the_phone_shows_in_the_PCs_chat_as_ours()
    {
        FriendsService.ProtectKey = (data, _) => data.ToArray();
        FriendsService.UnprotectKey = (data, _) => data.ToArray();
        using var lena = FriendIdentity.Create("Lena", null);
        var settings = new AppSettings { Beam = false };
        settings.Friends.Add(lena.AsFriend());
        var pc = new FriendsService(settings, a => { a(); return System.Threading.Tasks.Task.CompletedTask; });
        var me = pc.EnsureMe();
        (string Key, string Id, string Text)? synced = null;
        pc.OwnChatSynced += (friend, id, text, _, _) => synced = (friend.Key, id, text);
        string msgId = Guid.NewGuid().ToString("N");
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        // What the phone app posts to the account's own inbox after sending to Lena (app_state.dart _deliver).
        string echo = FriendNotes.Seal(me, me.AsFriend(), new FriendNote(FriendNoteKind.Chat, "Pixel 9", now, Code: lena.PublicKey,
                                                                         Text: "from my phone", MessageId: msgId, Device: Phone));
        pc.Handle("phone-echo", now, echo);
        Assert.Equal((lena.PublicKey, msgId, "from my phone"), synced);
    }

    [Fact]
    public void A_device_note_sealed_for_ourselves_opens_with_its_id_and_question()
    {
        using var me = FriendIdentity.Create("Elias", null);
        string text = FriendNotes.Seal(me, me.AsFriend(), new FriendNote(FriendNoteKind.Device, "Pixel 9", 10, Device: Phone, Query: true, IsPhone: true));
        var note = FriendNotes.Open(me, text, out string sender);
        Assert.NotNull(note);
        Assert.Equal(FriendNoteKind.Device, note!.Kind);
        Assert.Equal("Pixel 9", note.From);
        Assert.Equal(Phone, note.Device);
        Assert.True(note.Query);
        Assert.True(note.IsPhone);
        Assert.Equal(me.PublicKey, sender); // sealed by our own identity: one of our devices
    }

    [Fact]
    public void The_phone_tells_the_pc_what_happens_on_it()
    {
        using var me = FriendIdentity.Create("Elias", null);
        var status = new PhoneStatus(PhoneStatus.Notification, "Lena", "WhatsApp · See you at 8", 0, true);
        string text = FriendNotes.Seal(me, me.AsFriend(), new FriendNote(FriendNoteKind.Phone, "Pixel 9", 10, Device: Phone, Phone: status));
        var note = FriendNotes.Open(me, text, out _);
        Assert.Equal(FriendNoteKind.Phone, note!.Kind);
        Assert.Equal(Phone, note.Device);
        Assert.Equal(status, note.Phone);

        // Music that paused says so; a kind this version doesn't know, or no title, leaves the note unopened.
        var paused = new PhoneStatus(PhoneStatus.Music, "Song", "Artist", 0, false);
        text = FriendNotes.Seal(me, me.AsFriend(), new FriendNote(FriendNoteKind.Phone, "Pixel 9", 10, Device: Phone, Phone: paused));
        Assert.False(FriendNotes.Open(me, text, out _)!.Phone!.On);
        Assert.Null(PhoneStatus.From("weather", "Sunny", null, 0, true));
        Assert.Equal(PhoneStatus.Battery, PhoneStatus.From("battery", "Charged", "100%", 0, true)!.Kind);
        // Now and then the phone's state, for the phone tool: the battery, charging, the song.
        var state = PhoneStatus.From("status", "87%", "Golden Hour · Holm Demo", 0, false)!;
        Assert.Equal(("87%", "Golden Hour · Holm Demo", false), (state.Title, state.Detail, state.On));

        // Its connections travel along as JSON and read as words; anything else in there is left out.
        var full = new PhoneStatus(PhoneStatus.Status, "87%", null, 0, true, "{\"net\":\"wifi\",\"vpn\":true,\"bt\":\"AirPods 80%\",\"dnd\":true}");
        text = FriendNotes.Seal(me, me.AsFriend(), new FriendNote(FriendNoteKind.Phone, "Pixel 9", 10, Device: Phone, Phone: full));
        var back = FriendNotes.Open(me, text, out _)!.Phone!;
        Assert.Equal(new[] { "Wi-Fi", "VPN", "AirPods 80%", "Do Not Disturb" }, back.Connections());
        Assert.Empty(new PhoneStatus(PhoneStatus.Status, "87%", null, 0, true, "not json").Connections());

        // A notification carries its key (the PC can close it on the phone); a PC's "close" is never read as a status.
        var message = new PhoneStatus(PhoneStatus.Notification, "Lena", "WhatsApp · Hi", 0, true, Key: "0|com.whatsapp|1|null|10");
        text = FriendNotes.Seal(me, me.AsFriend(), new FriendNote(FriendNoteKind.Phone, "Pixel 9", 10, Device: Phone, Phone: message));
        Assert.Equal("0|com.whatsapp|1|null|10", FriendNotes.Open(me, text, out _)!.Phone!.Key);
        var close = new PhoneStatus(PhoneStatus.Dismiss, "dismiss", null, 0, true, Key: "0|com.whatsapp|1|null|10");
        text = FriendNotes.Seal(me, me.AsFriend(), new FriendNote(FriendNoteKind.Phone, "PC", 10, Device: Pc, ForDevice: Phone, Phone: close));
        Assert.Null(FriendNotes.Open(me, text, out _));
        var closeAll = new PhoneStatus(PhoneStatus.DismissAll, "dismiss-all", null, 0, true);
        text = FriendNotes.Seal(me, me.AsFriend(), new FriendNote(FriendNoteKind.Phone, "PC", 10, Device: Pc, ForDevice: Phone, Phone: closeAll));
        Assert.Null(FriendNotes.Open(me, text, out _));
        Assert.Null(PhoneStatus.CleanKey(new string('k', 400)));
        Assert.Null(PhoneStatus.From(PhoneStatus.Call, "  ", null, 0, true));
        Assert.Equal(80, PhoneStatus.From(PhoneStatus.Call, new string('a', 200), null, 0, true)!.Title.Length);
    }

    [Fact]
    public void A_device_note_without_a_valid_id_is_left_unopened()
    {
        using var me = FriendIdentity.Create("Elias", null);
        string text = FriendNotes.Seal(me, me.AsFriend(), new FriendNote(FriendNoteKind.Device, "Pixel 9", 10, Device: "not-an-id"));
        Assert.Null(FriendNotes.Open(me, text, out _));
    }

    [Fact]
    public void Files_for_one_device_say_which_and_from_which()
    {
        using var me = FriendIdentity.Create("Elias", null);
        var secret = ShareCode.NewSecret();
        string text = FriendNotes.Seal(me, me.AsFriend(), new FriendNote(FriendNoteKind.Files, "Pixel 9", 10, Secret: secret, Device: Phone, ForDevice: Pc));
        var note = FriendNotes.Open(me, text, out _)!;
        Assert.Equal(FriendNoteKind.Files, note.Kind);
        Assert.Equal(secret, note.Secret);
        Assert.Equal(Phone, note.Device);
        Assert.Equal(Pc, note.ForDevice);
    }

    [Fact]
    public void Notes_between_friends_carry_no_device()
    {
        using var me = FriendIdentity.Create("Elias", null);
        using var lena = FriendIdentity.Create("Lena", null);
        string text = FriendNotes.Seal(me, lena.AsFriend(), new FriendNote(FriendNoteKind.Files, "Elias", 10, Secret: ShareCode.NewSecret()));
        var note = FriendNotes.Open(lena, text, out _)!;
        Assert.Null(note.Device);
        Assert.Null(note.ForDevice);
    }

    [Fact]
    public void Devices_count_while_they_spoke_lately()
    {
        var devices = new OwnDevices();
        Assert.True(devices.Saw(Phone, "Pixel 9", T0));
        Assert.False(devices.Saw(Phone, "Pixel 9", T0.AddMinutes(1))); // nothing new to show
        Assert.True(devices.Saw(Phone, "Lena's Pixel", T0.AddMinutes(2))); // a new name is
        Assert.Single(devices.Recent(T0.AddMinutes(5)));
        Assert.Empty(devices.Recent(T0.AddMinutes(2) + OwnDevices.Fresh));
        Assert.False(devices.Saw("nope", "Pixel 9", T0));
    }

    [Fact]
    public void The_same_device_under_a_new_id_replaces_the_old_one()
    {
        // The phone app after an update: same name, new id. The old one would stay as a device that never answers.
        var devices = new OwnDevices();
        devices.Saw(Phone, "Pixel 9", T0);
        devices.Saw(Pc, "Office PC", T0);
        string updated = Guid.NewGuid().ToString("N");
        Assert.True(devices.Saw(updated, "pixel 9", T0.AddMinutes(1)));
        var now = devices.Recent(T0.AddMinutes(2));
        Assert.Equal(2, now.Count);
        Assert.Contains(now, d => d.Id == updated);
        Assert.DoesNotContain(now, d => d.Id == Phone);
    }

    [Fact]
    public void Names_are_cleaned_and_a_failed_send_forgets_the_device()
    {
        var devices = new OwnDevices();
        devices.Saw(Pc, "  Office\u0007PC  ", T0);
        Assert.Equal("OfficePC", devices.Find(Pc)!.Name);
        devices.Saw(Phone, "\u0001", T0);
        Assert.Equal("Holm", devices.Find(Phone)!.Name);
        devices.Forget(Pc);
        Assert.Null(devices.Find(Pc));
    }

    [Fact]
    public void Own_clipboard_and_chat_sync_notes_round_trip()
    {
        using var me = FriendIdentity.Create("Elias", null);
        using var friend = FriendIdentity.Create("Lena", null);
        var own = me.AsFriend();

        string clipSealed = FriendNotes.Seal(me, own, new FriendNote(FriendNoteKind.Clipboard, "Pixel 9", 10, Text: "hello clipboard", Device: Phone));
        var clipNote = FriendNotes.Open(me, clipSealed, out string clipSender);
        Assert.NotNull(clipNote);
        Assert.Equal(me.PublicKey, clipSender);
        Assert.Equal(FriendNoteKind.Clipboard, clipNote!.Kind);
        Assert.Equal("hello clipboard", clipNote.Text);
        Assert.Equal(Phone, clipNote.Device);

        string msgId = Guid.NewGuid().ToString("N");
        string chatSealed = FriendNotes.Seal(me, own, new FriendNote(FriendNoteKind.Chat, "Pixel 9", 20, Code: friend.PublicKey, Text: "synced hello", MessageId: msgId, Device: Phone));
        var chatNote = FriendNotes.Open(me, chatSealed, out _);
        Assert.NotNull(chatNote);
        Assert.Equal(FriendNoteKind.Chat, chatNote!.Kind);
        Assert.Equal(friend.PublicKey, chatNote.Code);
        Assert.Equal("synced hello", chatNote.Text);
        Assert.Equal(msgId, chatNote.MessageId);
        Assert.Equal(Phone, chatNote.Device);

        string reactSealed = FriendNotes.Seal(me, own, new FriendNote(FriendNoteKind.Reaction, "Pixel 9", 30, Code: friend.PublicKey, Text: "❤️", MessageId: msgId, Device: Phone));
        var reactNote = FriendNotes.Open(me, reactSealed, out _);
        Assert.NotNull(reactNote);
        Assert.Equal(FriendNoteKind.Reaction, reactNote!.Kind);
        Assert.Equal(friend.PublicKey, reactNote.Code);
        Assert.Equal("❤️", reactNote.Text);
        Assert.Equal(msgId, reactNote.MessageId);
        Assert.Equal(Phone, reactNote.Device);
    }
}
