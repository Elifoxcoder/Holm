using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using WinNotch.Sharing;

namespace WinNotch;

internal enum FriendRequestResult { Accepted, Declined, NoResponse }

/// <summary>What came of using a friend's invite code.</summary>
internal enum InviteResult { Added, Sent, AlreadyFriends, OwnCode, NotFound }

/// <summary>
/// Friends on this PC: our identity (the private key is encrypted for this Windows account), the friends list,
/// and our inbox, followed while Beam is on and we have a friend code. Settings are only touched on the UI thread.
/// </summary>
internal sealed partial class FriendsService
{
    static readonly byte[] KeyEntropy = "WinNotch friends key"u8.ToArray(); // former name: keys saved before the rename must still open

    /// <summary>How long the sender waits for the friend's notch to answer; older invitations are stale.</summary>
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(45);
    /// <summary>
    /// How long a sender waits on the relay for the friend's notch. Longer than <see cref="Patience"/>, which the receiver
    /// holds the note's age to: the note's trip through the inbox and their connection to the relay come on top of it.
    /// </summary>
    public static readonly TimeSpan SendPatience = TimeSpan.FromSeconds(40);
    /// <summary>How long a stream invitation waits for the friend to click Watch.</summary>
    public static readonly TimeSpan StreamPatience = TimeSpan.FromSeconds(60);
    /// <summary>An older "are you there?" or answer is history: it came in while Holm wasn't running.</summary>
    const long FreshSeconds = 30;
    /// <summary>A "something waits for you" note is answered this long after it was left.</summary>
    const long WaitingSeconds = 24 * 60 * 60;

    readonly AppSettings _settings;
    /// <summary>Runs on the UI thread (the WPF app: its Dispatcher, see FriendsService.Wpf.cs).</summary>
    readonly Func<Action, Task> _ui;
    FriendIdentity? _me;
    bool _loaded;
    CancellationTokenSource? _follow;
    readonly ConcurrentDictionary<string, TaskCompletionSource<FriendRequestResult>> _friendRequests = new(StringComparer.Ordinal);
    readonly HashSet<string> _cancelledFriendRequests = new(StringComparer.Ordinal);
    readonly FriendPresence _presence = new();

    /// <summary>A friend wants to send us files; meet them on the relay with the secret (UI thread).</summary>
    public event Action<Friend, byte[]>? FilesOffered;
    /// <summary>Someone added us as a friend; this is their card, to add them back (UI thread).</summary>
    public event Action<Friend>? AddedUs;
    /// <summary>A friend request arrived and needs an explicit answer (UI thread).</summary>
    public event Action<Friend, string>? FriendRequested;
    /// <summary>A pending friend request was cancelled by its sender (UI thread).</summary>
    public event Action<Friend, string>? FriendRequestCancelled;
    /// <summary>A friend removed us from their list (UI thread).</summary>
    public event Action<Friend>? RemovedUs;
    /// <summary>A friend's notch answered or asked: their Holm is running now (UI thread).</summary>
    public event Action<Friend>? FriendOnline;
    /// <summary>A friend waved (UI thread); already limited to one per friend every 30 seconds.</summary>
    public event Action<Friend>? Waved;
    /// <summary>A friend sent a new name, status, color or picture, already saved (UI thread).</summary>
    public event Action<Friend>? ProfileReceived;
    /// <summary>A friend wants to stream their screen to us; meet them on the relay with the secret (UI thread).</summary>
    public event Action<Friend, byte[]>? StreamOffered;
    /// <summary>A friend's notch told us what they're doing, or that they stopped (UI thread).</summary>
    public event Action<Friend>? ActivityChanged;
    /// <summary>What we're doing, for the friends who ask; null when there's nothing or we don't share it.</summary>
    public Func<FriendActivity?>? MyActivity { get; set; }
    /// <summary>How many achievements we have, for the friends who ask; null to keep it to ourselves.</summary>
    public Func<int?>? MyTrophies { get; set; }
    /// <summary>A friend was added or removed (UI thread).</summary>
    public event Action? ListChanged;
    /// <summary>Another device of this account came, left or got a new name (UI thread).</summary>
    public event Action? OwnDevicesChanged;
    /// <summary>Our phone says what's happening on it, for the notch. On the UI thread.</summary>
    public event Action<OwnDevice, PhoneStatus>? PhoneStatusReceived;
    /// <summary>Another device of this account synced a non-private clipboard text (UI thread).</summary>
    public event Action<OwnDevice, string>? OwnClipboardReceived;
    /// <summary>A chat message from a friend: its id, text and when it was sent (Unix time; UI thread).</summary>
    public event Action<Friend, string, string, long>? ChatReceived;

    /// <summary>A GIF from a friend: the message's id, the GIF's link and when it was sent (UI thread).</summary>
    public event Action<Friend, string, string, long>? GifReceived;

    /// <summary>A friend reacted to a chat message: its id and the emoji, or null when they took it away (UI thread).</summary>
    public event Action<Friend, string, string?>? ReactionReceived;

    /// <summary>Another of our own devices sent a chat message (or GIF) to this friend (UI thread).</summary>
    public event Action<Friend, string, string, long, string?>? OwnChatSynced;

    /// <summary>Another of our own devices reacted to a message in this friend's chat (UI thread).</summary>
    public event Action<Friend, string, string?>? OwnReactionSynced;

    /// <summary>An E2E group chat invite/update arrived from a friend (UI thread).</summary>
    public event Action<ChatGroup, string>? GroupInviteReceived;

    /// <summary>An E2E-encrypted group chat message arrived: groupId, senderKey, senderName, messageId, ciphertext, sent, isGif (UI thread).</summary>
    public event Action<string, string, string, string, string, long, bool>? GroupChatReceived;

    /// <summary>A reaction in a group chat arrived: groupId, senderKey, messageId, emoji (UI thread).</summary>
    public event Action<string, string, string, string?>? GroupReactionReceived;

    /// <summary>A member left a group chat: groupId, memberKey, memberName (UI thread).</summary>
    public event Action<string, string, string>? GroupLeaveReceived;

    /// <summary>Another of our own devices sent a group chat message (UI thread).</summary>
    public event Action<string, string, string, long, bool>? OwnGroupChatSynced;

    /// <summary>Our public key, if an identity exists.</summary>
    public string? MyPublicKey => Me?.PublicKey;

    /// <summary>Derives a 32-byte AES-256 key for encrypting local chat history at rest.</summary>
    public byte[]? ChatStorageKey() => Me is { } me ? ChatCrypto.DeriveStorageKey(me) : null;

    /// <summary>Someone used our invite code and is now a friend, without asking (UI thread).</summary>
    public event Action<Friend>? InviteAccepted;
    /// <summary>A request of ours was answered after we stopped waiting for it: yes (true) or no (UI thread).</summary>
    public event Action<Friend, bool>? LateAnswer;
    /// <summary>Our invite codes or our unanswered requests changed (UI thread).</summary>
    public event Action? InvitesChanged;

    readonly WaveLimit _wavesOut = new(), _wavesIn = new();
    readonly FriendProofAttempts _proofAttempts = new();
    readonly HashSet<string> _seenIds = new(StringComparer.Ordinal);
    long _lastSeenPrune;
    /// <summary>What friends told us they're doing, by key; only shown while they count as online.</summary>
    readonly Dictionary<string, FriendActivity?> _activities = new(StringComparer.Ordinal);
    readonly Dictionary<string, int> _trophies = new(StringComparer.Ordinal);
    /// <summary>Profile versions on their way, by friend key: one note each, however often we're asked to sync.</summary>
    readonly Dictionary<string, string> _profilesOut = new(StringComparer.Ordinal);

    /// <summary>
    /// How the identity's private key is kept in the settings file, for this user only, and the account's name.
    /// The WPF app: Windows' DPAPI and the Windows account (FriendsService.Wpf.cs); the cross-platform app sets its own.
    /// </summary>
    internal static Func<byte[], byte[], byte[]> ProtectKey = (_, _) => throw new PlatformNotSupportedException();
    internal static Func<byte[], byte[], byte[]> UnprotectKey = (_, _) => throw new PlatformNotSupportedException();
    internal static Func<string> AccountName = () => Environment.UserName;
    /// <summary>What the account's other devices call this one: the computer's name.</summary>
    internal static Func<string> DeviceName = () => Environment.MachineName;

    readonly OwnDevices _devices = new();

    /// <summary>Tests run inboxes on this computer; Holm itself never has its inbox there.</summary>
    internal static bool AllowLoopbackServers
    {
        get => FriendIdentity.AllowInsecureServers;
        set => FriendIdentity.AllowInsecureServers = value;
    }

    /// <summary>An inbox on this computer (localhost, 127.x, ::1): a test's, never a real one other devices can reach.</summary>
    internal static bool IsLoopbackServer(string? url)
        => url != null && Uri.TryCreate(url, UriKind.Absolute, out var uri)
           && (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase));

    public FriendsService(AppSettings settings, Func<Action, Task> ui)
    {
        _settings = settings;
        _ui = ui;
        // An unsafe or retired saved inbox is never contacted: fall back to HolyNotch's relay.
        if (_settings.FriendsServer != null && (!FriendIdentity.TryNormalizeServer(_settings.FriendsServer, out _) || FriendIdentity.IsRetired(_settings.FriendsServer)))
        {
            _settings.FriendsServer = null;
            _settings.FriendsRelay = true;
            _settings.FriendsRelayServer = FriendIdentity.RelayServer;
            _settings.Save();
            Diag.Log("Friends: the inbox server was not usable; back to HolyNotch's relay");
        }
        int removed = RemoveDuplicateFriendCards(_settings.Friends);
        if (removed > 0)
        {
            _settings.Save();
            Diag.Log($"Friends: removed {removed} duplicate or invalid saved entries");
        }
        if (Prune(Now)) _settings.Save();
        if (_settings.FriendsKey != null && Me is { } me && _settings.Friends.RemoveAll(f => f.Key == me.PublicKey) > 0)
        {
            _settings.Save();
            Diag.Log("Friends: our own card was among the friends; removed");
        }
    }

    static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>Invite codes are kept a day past their end, for requests that wait in our inbox while Holm is closed.</summary>
    const long InviteGrace = 24 * 60 * 60;
    /// <summary>The existing allowance for a request that reached the server just around the code's expiry.</summary>
    const long InviteArrivalGrace = 300;
    /// <summary>An unanswered request is forgotten after this long: the note it waits for is long gone from the server.</summary>
    const long PendingFor = 2 * 24 * 60 * 60;
    /// <summary>Transport ids and authenticated note ids/hashes share the existing persisted list, with a hard bound.</summary>
    internal const int SeenLimit = 20_000;
    /// <summary>Past acceptance window plus slack for a note whose authenticated Sent was at the future-skew boundary.</summary>
    internal const int SeenRetentionSeconds = FriendNotes.OldestSentSeconds + FriendNotes.MaxClockSkewSeconds;
    const int SeenPruneIntervalSeconds = 60;

    bool Prune(long now)
    {
        bool changed = PruneSeen(now);
        changed |= _settings.FriendInvites.RemoveAll(i => i.Expires + InviteGrace < now) > 0;
        changed |= _settings.PendingFriendRequests.RemoveAll(p => p.Sent + PendingFor < now) > 0;
        changed |= _settings.IncomingFriendRequests.RemoveAll(p => p.Sent + PendingFor < now) > 0;
        return changed;
    }

    static bool IsSeenId([NotNullWhen(true)] string? id) => !string.IsNullOrWhiteSpace(id) && id.Length <= 256;

    bool PruneSeen(long now)
    {
        _settings.FriendsSeen ??= new();
        _settings.FriendsSeenTimes ??= new();
        bool changed = false;
        if (_settings.FriendsSeenTimes.Count < _settings.FriendsSeen.Count)
        {
            while (_settings.FriendsSeenTimes.Count < _settings.FriendsSeen.Count) _settings.FriendsSeenTimes.Add(now);
            changed = true;
        }
        else if (_settings.FriendsSeenTimes.Count > _settings.FriendsSeen.Count)
        {
            _settings.FriendsSeenTimes.RemoveRange(_settings.FriendsSeen.Count, _settings.FriendsSeenTimes.Count - _settings.FriendsSeen.Count);
            changed = true;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var keptIds = new List<string>(_settings.FriendsSeen.Count);
        var keptTimes = new List<long>(_settings.FriendsSeenTimes.Count);
        long cutoff = now - SeenRetentionSeconds;
        for (int i = _settings.FriendsSeen.Count - 1; i >= 0; i--)
        {
            string? id = _settings.FriendsSeen[i];
            long at = _settings.FriendsSeenTimes[i];
            if (!IsSeenId(id)) { changed = true; continue; }
            // Missing/zero timestamps came from settings written before the parallel list existed. Treat them as
            // unknown and retain them conservatively for one full window from migration; corrupt future times too.
            if (at <= 0 || at > now + FriendNotes.MaxClockSkewSeconds)
            {
                at = now;
                changed = true;
            }
            if (at < cutoff || !seen.Add(id)) { changed = true; continue; }
            keptIds.Add(id);
            keptTimes.Add(at);
        }
        if (keptIds.Count > SeenLimit)
        {
            // Newest first while walking backwards: drop the excess old end, then restore chronological order.
            keptIds.RemoveRange(SeenLimit, keptIds.Count - SeenLimit);
            keptTimes.RemoveRange(SeenLimit, keptTimes.Count - SeenLimit);
            changed = true;
        }
        keptIds.Reverse();
        keptTimes.Reverse();
        if (changed)
        {
            _settings.FriendsSeen.Clear();
            _settings.FriendsSeen.AddRange(keptIds);
            _settings.FriendsSeenTimes.Clear();
            _settings.FriendsSeenTimes.AddRange(keptTimes);
        }
        _seenIds.Clear();
        foreach (string id in _settings.FriendsSeen) _seenIds.Add(id);
        _lastSeenPrune = now;
        return changed;
    }

    void RemoveSeenAt(int index)
    {
        _settings.FriendsSeen.RemoveAt(index);
        _settings.FriendsSeenTimes.RemoveAt(index);
    }

    bool MaintainSeen(long now)
        => _lastSeenPrune == 0 || now < _lastSeenPrune || now - _lastSeenPrune >= SeenPruneIntervalSeconds
            ? PruneSeen(now)
            : PruneSeenFromFront(now);

    bool PruneSeenFromFront(long now)
    {
        bool changed = false;
        long cutoff = now - SeenRetentionSeconds;
        while (_settings.FriendsSeenTimes.Count > 0 && _settings.FriendsSeenTimes[0] < cutoff)
        {
            string id = _settings.FriendsSeen[0];
            RemoveSeenAt(0);
            _seenIds.Remove(id);
            changed = true;
        }
        return changed;
    }

    bool RememberSeen(string? id, long now)
    {
        MaintainSeen(now);
        if (!IsSeenId(id) || _seenIds.Contains(id)) return false;
        while (_settings.FriendsSeen.Count >= SeenLimit)
        {
            string oldest = _settings.FriendsSeen[0];
            RemoveSeenAt(0);
            _seenIds.Remove(oldest);
        }
        _settings.FriendsSeen.Add(id);
        _settings.FriendsSeenTimes.Add(now);
        _seenIds.Add(id);
        return true;
    }

    /// <summary>Our invite codes that still work, newest last.</summary>
    public IReadOnlyList<OwnInvite> Invites => _settings.FriendInvites.Where(i => !i.Revoked && i.Expires > Now).ToList();

    /// <summary>Requests we sent that haven't been answered yet.</summary>
    public IReadOnlyList<PendingFriendRequest> Pending => _settings.PendingFriendRequests;
    public IReadOnlyList<PendingFriendRequest> Incoming
        => _settings.IncomingFriendRequests.Where(p => p.Sent + PendingFor >= Now && Find(p.Card.Key) == null).ToList();

    /// <summary>
    /// The invite code to give a friend: the current one while it works for at least another hour, else a new one,
    /// whose sealed card is left on the server first.
    /// </summary>
    /// <exception cref="IOException">The server couldn't be reached.</exception>
    public async Task<OwnInvite> InviteAsync(bool fresh, CancellationToken ct)
    {
        var me = EnsureMe();
        long now = Now;
        Prune(now);
        if (!fresh && _settings.FriendInvites.LastOrDefault(i => !i.Revoked && i.Expires - now > 3600) is { } current) return current;
        string code = FriendInvite.New();
        long expires = now + (long)FriendInvite.Lifetime.TotalSeconds - 120; // a little before the server lets the card go
        await FriendInbox.PublishInviteAsync(FriendInvite.Topic(code), FriendInvite.Seal(code, me.FriendCode, expires), ct);
        var invite = new OwnInvite { Code = code, Proof = FriendInvite.Proof(code), Expires = expires, SingleUse = true };
        if (fresh)
            foreach (var old in _settings.FriendInvites.Where(i => !i.Revoked && i.Expires > now))
            {
                old.Revoked = true;
                old.RevokedAt = now;
            }
        _settings.FriendInvites.Add(invite);
        _settings.Save();
        InvitesChanged?.Invoke();
        return invite;
    }

    /// <summary>Stops automatic acceptance here. Copies already fetched by older apps can still ask to be added.</summary>
    public bool RevokeInvite(OwnInvite invite)
    {
        var saved = _settings.FriendInvites.FirstOrDefault(i => i.Code == invite.Code && i.Proof == invite.Proof);
        if (saved == null || saved.Revoked) return false;
        saved.Revoked = true;
        saved.RevokedAt = Now;
        _settings.Save();
        InvitesChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// Uses a friend's invite code: fetches their card and sends a request that carries the code's proof, which their
    /// notch accepts without asking. Waits up to <paramref name="wait"/> for that; after it, the request stays open
    /// and you become friends as soon as their Holm sees it.
    /// </summary>
    /// <exception cref="IOException">The server couldn't be reached.</exception>
    public async Task<(InviteResult Result, Friend? Card)> UseInviteAsync(string code, TimeSpan wait, CancellationToken ct)
    {
        if (_settings.FriendInvites.Any(i => i.Code == code)) return (InviteResult.OwnCode, null);
        Friend? card = null;
        for (int attempt = 0; attempt < 2 && card == null; attempt++)
        {
            // The server stores a new card a moment after it was left: a code made just now may need a second look.
            if (attempt > 0) await Task.Delay(TimeSpan.FromSeconds(2), ct);
            var cards = await FriendInbox.FetchInviteAsync(FriendInvite.Topic(code), ct);
            long now = Now;
            card = cards.Select(c => FriendInvite.Open(code, c, now)).FirstOrDefault(c => c != null);
        }
        if (card == null) return (InviteResult.NotFound, null);
        if (IsMe(card)) return (InviteResult.OwnCode, card);
        if (Find(card.Key) != null) return (InviteResult.AlreadyFriends, card);
        var result = await RequestAddAsync(card, wait, ct, FriendInvite.Proof(code));
        return (result == FriendRequestResult.Accepted ? InviteResult.Added : InviteResult.Sent, card);
    }

    public IReadOnlyList<Friend> Friends => _settings.Friends;

    /// <summary>Deduplicates old saved friend cards, keeping the latest card for each public key.</summary>
    internal static int RemoveDuplicateFriendCards(List<Friend> friends)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int removed = 0;
        for (int i = friends.Count - 1; i >= 0; i--)
        {
            var friend = friends[i];
            if (friend == null || string.IsNullOrWhiteSpace(friend.Key) || !seen.Add(friend.Key))
            {
                friends.RemoveAt(i);
                removed++;
            }
        }
        return removed;
    }

    /// <summary>Who we are, once set up (the first time you copy your friend code or add a friend).</summary>
    public FriendIdentity? Me
    {
        get
        {
            if (!_loaded) { _loaded = true; _me = Load(); }
            return _me;
        }
    }

    FriendIdentity? Load()
    {
        if (_settings.FriendsTopic == null || _settings.FriendsKey == null) return null;
        try
        {
            byte[] pkcs8 = UnprotectKey(Convert.FromBase64String(_settings.FriendsKey), KeyEntropy);
            var key = ECDiffieHellman.Create();
            key.ImportPkcs8PrivateKey(pkcs8, out _);
            CryptographicOperations.ZeroMemory(pkcs8);
            return new FriendIdentity(MyName, _settings.FriendsTopic, OwnServer, key);
        }
        catch (Exception ex)
        {
            // E.g. settings copied from another PC or account: the key can't be decrypted here.
            Diag.Log("Friend identity unreadable, a new one will be made: " + ex.Message);
            return null;
        }
    }

    /// <summary>Where our friend code says our inbox is: the server set in the settings, else the relay (always in production where ntfy.sh is retired).</summary>
    string? OwnServer => _settings.FriendsServer != null && !FriendIdentity.IsRetired(_settings.FriendsServer)
        ? _settings.FriendsServer
        : (_settings.FriendsRelay || FriendIdentity.IsRetired(null) ? FriendIdentity.RelayServer : null);

    /// <summary>Our identity, made on first use. Friends who had an unreadable older one must add us again.</summary>
    public FriendIdentity EnsureMe()
    {
        if (Me is { } me) return me;
        me = FriendIdentity.Create(MyName, OwnServer);
        byte[] pkcs8 = me.Key.ExportPkcs8PrivateKey();
        _settings.FriendsKey = Convert.ToBase64String(ProtectKey(pkcs8, KeyEntropy));
        CryptographicOperations.ZeroMemory(pkcs8);
        _settings.FriendsTopic = me.Topic;
        // Catch up from now on: a friend may write while Holm isn't running.
        _settings.FriendsSince = _settings.FriendsRelaySince = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _settings.FriendsSeen.Clear();
        _settings.FriendsSeenTimes.Clear();
        _seenIds.Clear();
        _settings.IncomingFriendRequests.Clear();
        _settings.Save();
        _me = me;
        Restart();
        return me;
    }

    /// <summary>The name in our profile, or the Windows account's.</summary>
    public string MyName => FriendProfile.CleanText(_settings.ProfileName, FriendProfile.MaxName) ?? AccountName();

    /// <summary>Our profile as friends get it.</summary>
    public FriendProfile MyProfile()
    {
        byte[]? picture = null;
        if (_settings.ProfilePicture != null)
        {
            try { picture = Convert.FromBase64String(_settings.ProfilePicture); } catch (FormatException) { }
        }
        return new FriendProfile(MyName, FriendProfile.CleanText(_settings.ProfileStatus, FriendProfile.MaxStatus),
                                 FriendProfile.CleanColor(_settings.ProfileColor), FriendProfile.IsPicture(picture) ? picture : null);
    }

    /// <summary>The profile was edited: our notes carry the new name, and friends get the new profile.</summary>
    public void ProfileChanged()
    {
        if (_me is { } me && me.Name != MyName)
        {
            _me = me.WithName(MyName); // the same key and inbox
            if (_follow != null) Restart(); // the inbox answers with our name too
        }
        SyncProfiles();
    }

    /// <summary>
    /// Sends our profile to each friend who doesn't have this version yet: after an edit, to a new friend, and to friends
    /// made before profiles existed. Nothing goes out before we have a friend code. A friend on a Holm before 1.8 leaves
    /// the note unopened.
    /// </summary>
    public void SyncProfiles()
    {
        if (!_settings.Beam || Me is not { } me) return;
        var profile = MyProfile();
        string version = profile.Version;
        foreach (var friend in _settings.Friends.ToList())
        {
            if (friend.ProfileSent == version) continue;
            if (_profilesOut.TryGetValue(friend.Key, out var sending) && sending == version) continue;
            _profilesOut[friend.Key] = version;
            string text = FriendNotes.Seal(me, friend, new FriendNote(FriendNoteKind.Profile, profile.Name,
                                                                      DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Profile: profile));
            _ = Task.Run(async () =>
            {
                bool sent = false;
                try { await FriendInbox.PostAsync(friend, text, CancellationToken.None); sent = true; }
                catch (IOException ex) { Diag.Log($"Friends: couldn't send the profile to {friend.Name}: {ex.Message}"); }
                await _ui(() =>
                {
                    if (_profilesOut.TryGetValue(friend.Key, out var v) && v == version) _profilesOut.Remove(friend.Key);
                    if (!sent || Find(friend.Key) is not { } current) return;
                    current.ProfileSent = version;
                    _settings.Save();
                });
            });
        }
    }

    // ───────────── Holm account (Account/AccountService.cs) ─────────────

    /// <summary>Our identity for the account to keep (sealed): the inbox and the private key. Null before we have one.</summary>
    internal Account.IdentityPayload? ExportIdentity()
    {
        if (Me is not { } me) return null;
        byte[] pkcs8 = me.Key.ExportPkcs8PrivateKey();
        try { return new Account.IdentityPayload { Topic = me.Topic, Server = _settings.FriendsServer, Key = Convert.ToBase64String(pkcs8) }; }
        finally { CryptographicOperations.ZeroMemory(pkcs8); }
    }

    /// <summary>
    /// Becomes the account's identity, so friends reach us here with the same friend code as on our other PCs.
    /// The inbox is caught up on what the servers keep (ntfy 12 hours, the relay a week), as after a restart.
    /// </summary>
    internal void AdoptIdentity(Account.IdentityPayload identity)
    {
        byte[] pkcs8 = Convert.FromBase64String(identity.Key);
        var key = ECDiffieHellman.Create();
        try
        {
            key.ImportPkcs8PrivateKey(pkcs8, out _);
            _settings.FriendsKey = Convert.ToBase64String(ProtectKey(pkcs8, KeyEntropy));
        }
        finally { CryptographicOperations.ZeroMemory(pkcs8); }
        _settings.FriendsTopic = identity.Topic;
        if (identity.Server != null && (!FriendIdentity.TryNormalizeServer(identity.Server, out _) || FriendIdentity.IsRetired(identity.Server)))
        {
            _settings.FriendsServer = null;
            _settings.FriendsRelay = true;
            _settings.FriendsRelayServer = FriendIdentity.RelayServer;
        }
        else _settings.FriendsServer = identity.Server;
        if (_settings.FriendsServer == null && FriendIdentity.IsRetired(null))
        {
            _settings.FriendsRelay = true;
            _settings.FriendsRelayServer = FriendIdentity.RelayServer;
        }
        _settings.FriendsSince = Now - 12 * 60 * 60;
        _settings.FriendsRelaySince = Now - 7 * 24 * 60 * 60; // what the relay keeps
        // Keep replay ids: account adoption can restore the same identity and replay its retained notes.
        PruneSeen(Now);
        _settings.IncomingFriendRequests.Clear();
        _me = new FriendIdentity(MyName, identity.Topic, OwnServer, key);
        _loaded = true;
        _settings.Save();
        Restart();
        Diag.Log("Friends: took the account's identity");
    }

    /// <summary>Puts the account's friends list in place of ours (already merged with ours).</summary>
    internal void ReplaceFriends(IReadOnlyList<Friend> friends)
    {
        _settings.Friends.Clear();
        _settings.Friends.AddRange(friends);
        _settings.Save();
        ListChanged?.Invoke();
        SyncProfiles();
    }

    /// <summary>
    /// After taking the account's identity: friends who only knew this PC's old one get our new friend code, and their
    /// notch offers to add us back (as when someone adds them).
    /// </summary>
    internal void Reintroduce(IEnumerable<Friend> friends)
    {
        if (Me is not { } me) return;
        foreach (var friend in friends.ToList())
        {
            var note = new FriendNote(FriendNoteKind.AddedYou, me.Name, Now, Code: me.FriendCode);
            string text = FriendNotes.Seal(me, friend, note);
            _ = Task.Run(async () =>
            {
                try { await FriendInbox.PostAsync(friend, text, CancellationToken.None); }
                catch (IOException ex) { Diag.Log($"Friends: couldn't tell {friend.Name} our new code: {ex.Message}"); }
            });
        }
    }

    public Friend? Find(string publicKey) => _settings.Friends.FirstOrDefault(f => f.Key == publicKey);

    // ───────────── the account's other devices ─────────────

    /// <summary>This device's id among the account's devices, made on first use.</summary>
    public string DeviceId
    {
        get
        {
            if (!OwnDevice.IsId(_settings.DeviceId))
            {
                _settings.DeviceId = OwnDevice.NewId();
                _settings.Save();
            }
            return _settings.DeviceId!;
        }
    }

    /// <summary>The account's other devices that spoke lately (they share our identity: Sharing/OwnDevices.cs).</summary>
    public IReadOnlyList<OwnDevice> OwnDevicesNow => _follow == null ? Array.Empty<OwnDevice>() : _devices.Recent(DateTime.UtcNow);

    /// <summary>Our own inbox as a friend card: what notes to another device of ours are sealed for and posted to.</summary>
    Friend OwnCard(FriendIdentity me, string name) => new() { Name = name, Topic = me.Topic, Key = me.PublicKey, Server = me.Server };

    /// <summary>The device as a card to show and send to (the name is the device's; the inbox is our own).</summary>
    public Friend CardOf(OwnDevice device) => OwnCard(EnsureMe(), device.Name);

    /// <summary>Like <see cref="InviteAsync"/>, for one of our own devices: only that one comes to the relay.</summary>
    /// <exception cref="IOException">The note couldn't be delivered.</exception>
    public Task InviteDeviceAsync(OwnDevice device, byte[] secret, CancellationToken ct)
    {
        var me = EnsureMe();
        var note = new FriendNote(FriendNoteKind.Files, DeviceName(), Now, Secret: secret, Device: DeviceId, ForDevice: device.Id);
        return FriendInbox.PostAsync(OwnCard(me, device.Name), FriendNotes.Seal(me, OwnCard(me, device.Name), note), ct);
    }

    /// <summary>Sending to it just failed: it's gone.</summary>
    public void ForgetDevice(OwnDevice device)
    {
        _devices.Forget(device.Id);
        OwnDevicesChanged?.Invoke();
    }

    /// <summary>Removes a linked phone on this PC: forgets it and ignores its notes until allowed again.</summary>
    public void RemovePhone(string deviceId)
    {
        if (!OwnDevice.IsId(deviceId)) return;
        _settings.RemovedPhoneIds ??= new();
        if (!_settings.RemovedPhoneIds.Contains(deviceId, StringComparer.Ordinal))
        {
            _settings.RemovedPhoneIds.Add(deviceId);
            _settings.Save();
        }
        _devices.Forget(deviceId);
        OwnDevicesChanged?.Invoke();
    }

    /// <summary>Allows previously removed phones to link to this PC again.</summary>
    public void ClearRemovedPhones()
    {
        if (_settings.RemovedPhoneIds is not { Count: > 0 }) return;
        _settings.RemovedPhoneIds.Clear();
        _settings.Save();
        AskForDevices();
    }

    public bool IsPhoneRemoved(string deviceId) => _settings.RemovedPhoneIds?.Contains(deviceId, StringComparer.Ordinal) == true;

    /// <summary>
    /// Says in our inbox that this device runs, for the account's other devices; with <paramref name="query"/> it asks
    /// them to say so too. Live only: the server passes it on and doesn't keep it.
    /// </summary>
    /// <summary>Clears a notification on our phone (closed in the phone tool): a note for that phone only.</summary>
    public void DismissOnPhone(OwnDevice phone, string key)
    {
        if (PhoneStatus.CleanKey(key) is not { } clean) return;
        TellPhone(phone, new PhoneStatus(PhoneStatus.Dismiss, "dismiss", null, 0, true, Key: clean));
    }

    /// <summary>Clears all clearable notifications on our phone (closed in the phone tool): a note for that phone only.</summary>
    public void DismissAllOnPhone(OwnDevice phone)
        => TellPhone(phone, new PhoneStatus(PhoneStatus.DismissAll, "dismiss-all", null, 0, true));

    /// <summary>The phone's player plays, pauses, skips (<see cref="PhoneStatus.MusicControls"/>), from the phone tool.</summary>
    public void ControlPhoneMusic(OwnDevice phone, string what)
    {
        if (Array.IndexOf(PhoneStatus.MusicControls, what) < 0) return;
        TellPhone(phone, new PhoneStatus(PhoneStatus.Control, what, null, 0, true));
    }

    /// <summary>A note for that phone only, in the account's own inbox.</summary>
    void TellPhone(OwnDevice phone, PhoneStatus status)
    {
        if (_follow == null || Me is not { } me) return;
        string text = FriendNotes.Seal(me, OwnCard(me, phone.Name),
            new FriendNote(FriendNoteKind.Phone, DeviceName(), Now, Device: DeviceId, ForDevice: phone.Id, Phone: status));
        _ = Task.Run(async () =>
        {
            try { await FriendInbox.PostAsync(OwnCard(me, phone.Name), text, CancellationToken.None, keep: false); }
            catch (IOException ex) { Diag.Log("Friends: couldn't reach our own inbox: " + ex.Message); }
        });
    }

    /// <summary>Asks the account's other devices to say they are there (the phone tool, opened without a phone).</summary>
    public void AskForDevices() => AnnounceDevice(query: true);

    void AnnounceDevice(bool query)
    {
        if (_follow == null || Me is not { } me) return;
        string text = FriendNotes.Seal(me, OwnCard(me, ""), new FriendNote(FriendNoteKind.Device, DeviceName(), Now, Device: DeviceId, Query: query ? true : null));
        _ = Task.Run(async () =>
        {
            try { await FriendInbox.PostAsync(OwnCard(me, ""), text, CancellationToken.None, keep: false); }
            catch (IOException ex) { Diag.Log("Friends: couldn't reach our own inbox: " + ex.Message); }
        });
    }

    /// <summary>A note sealed by our own identity: from another device of this account, or our own coming back.</summary>
    void HandleOwn(FriendIdentity me, FriendNote note, long age)
    {
        if (note.Device is not { } id || id == DeviceId) return;
        if (IsPhoneRemoved(id)) return;
        switch (note.Kind)
        {
            case FriendNoteKind.Device when age <= FreshSeconds:
                if (_devices.Saw(id, note.From, DateTime.UtcNow, isPhone: note.IsPhone == true)) OwnDevicesChanged?.Invoke();
                if (note.Query == true) AnnounceDevice(query: false);
                break;
            case FriendNoteKind.Phone when age <= 60 && note.Phone is { } status:
                // News only while it's news: a call from minutes ago, read when Holm starts, isn't.
                if (_devices.Saw(id, note.From, DateTime.UtcNow, isPhone: true)) OwnDevicesChanged?.Invoke();
                PhoneStatusReceived?.Invoke(_devices.Find(id) ?? new OwnDevice(id, note.From, DateTime.UtcNow, IsPhone: true), status);
                break;
            case FriendNoteKind.Files when note.ForDevice != DeviceId:
                break; // for another of our devices
            case FriendNoteKind.Files when age > Patience.TotalSeconds:
                Diag.Log("Friends: another device of ours wanted to send files while Holm wasn't running");
                break;
            case FriendNoteKind.Files:
                if (_devices.Saw(id, note.From, DateTime.UtcNow)) OwnDevicesChanged?.Invoke();
                FilesOffered?.Invoke(OwnCard(me, _devices.Find(id)?.Name ?? note.From), note.Secret!);
                break;
            case FriendNoteKind.Chat when note.Code is { } peerKey && Find(peerKey) is { } chatFriend
                                          && note.MessageId is { } msgId && note.Text is { } msgText:
                OwnChatSynced?.Invoke(chatFriend, msgId, msgText, note.Sent, note.Gif);
                break;
            case FriendNoteKind.Reaction when note.Code is { } reactKey && Find(reactKey) is { } reactFriend
                                              && note.MessageId is { } reactId:
                OwnReactionSynced?.Invoke(reactFriend, reactId, note.Text);
                break;
            case FriendNoteKind.GroupInvite when note.GroupId is not null && GroupInvitePayload.Parse(note.Text) is { } syncedGroup:
                GroupInviteReceived?.Invoke(syncedGroup, me.PublicKey);
                break;
            case FriendNoteKind.GroupChat when note.GroupId is { } grpId && note.MessageId is { } grpMsgId && note.Text is { } grpCipher:
                OwnGroupChatSynced?.Invoke(grpId, grpMsgId, grpCipher, note.Sent, note.Gif != null);
                break;
            case FriendNoteKind.Clipboard when age <= 30 && note.Text is { } clipText:
                if (_devices.Saw(id, note.From, DateTime.UtcNow)) OwnDevicesChanged?.Invoke();
                OwnClipboardReceived?.Invoke(_devices.Find(id) ?? new OwnDevice(id, note.From, DateTime.UtcNow), clipText);
                break;
        }
    }

    /// <summary>Sends a non-private clipboard text live to the account's other online devices (never stored offline).</summary>
    public void SendClipboardToOwnDevices(string text)
    {
        if (_follow == null || Me is not { } me) return;
        if (ChatText.Clean(text) is not { } clean) return;
        string name = DeviceName();
        var own = OwnCard(me, name);
        var note = new FriendNote(FriendNoteKind.Clipboard, name, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Text: clean, Device: DeviceId);
        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await FriendInbox.PostAsync(own, FriendNotes.Seal(me, own, note), cts.Token, keep: false).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException)
            {
                Diag.Log("Clipboard sync post failed: " + ex.Message);
            }
        });
    }

    public bool IsMe(Friend card) => Me?.PublicKey == card.Key;

    /// <summary>
    /// Adds or updates a friend. With <paramref name="tellThem"/>, their notch offers to add us back.
    /// Returns false if that note couldn't be delivered (the friend is added all the same).
    /// </summary>
    public async Task<bool> AddAsync(Friend friend, bool tellThem)
    {
        if (!FriendIdentity.IsValidFriend(friend)) throw new ArgumentException("That friend card is not valid.", nameof(friend));
        if (Find(friend.Key) != null) return true;
        var me = EnsureMe();
        _settings.Friends.Add(friend);
        _settings.Save();
        ListChanged?.Invoke();
        SyncProfiles();
        if (!tellThem) return true;
        try
        {
            var note = new FriendNote(FriendNoteKind.AddedYou, me.Name, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Code: me.FriendCode);
            await FriendInbox.PostAsync(friend, FriendNotes.Seal(me, friend, note), CancellationToken.None);
            return true;
        }
        catch (IOException ex)
        {
            Diag.Log("Couldn't tell the new friend: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Sends a friend request and adds the card only after the other person accepts. The request is kept until it's
    /// answered: if nobody answers within <paramref name="timeout"/> it stays open (a later yes still makes you friends,
    /// see <see cref="LateAnswer"/>); only cancelling through <paramref name="ct"/> withdraws it.
    /// <paramref name="invite"/> is the proof of their invite code, if the request was made with one.
    /// </summary>
    public async Task<FriendRequestResult> RequestAddAsync(Friend friend, TimeSpan timeout, CancellationToken ct, string? invite = null)
    {
        if (!FriendIdentity.IsValidFriend(friend)) throw new ArgumentException("That friend card is not valid.", nameof(friend));
        if (Find(friend.Key) is { } existing)
        {
            UpdateFriendCard(existing, friend);
            return FriendRequestResult.Accepted;
        }

        var me = EnsureMe(); // starts our inbox before the request is posted
        var previous = _settings.PendingFriendRequests.FirstOrDefault(p => p.Card.Key == friend.Key);
        string requestId = previous?.RequestId ?? Guid.NewGuid().ToString("N");
        var answer = new TaskCompletionSource<FriendRequestResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_friendRequests.TryAdd(requestId, answer)) return FriendRequestResult.NoResponse;
        var pending = new PendingFriendRequest { RequestId = requestId, Card = friend, Sent = Now, ByInvite = invite != null };
        _settings.PendingFriendRequests.RemoveAll(p => p.Card.Key == friend.Key);
        _settings.PendingFriendRequests.Add(pending);
        _settings.Save();
        InvitesChanged?.Invoke();
        try
        {
            var note = new FriendNote(FriendNoteKind.FriendRequest, me.Name, Now, Code: me.FriendCode, RequestId: requestId, Invite: invite);
            await FriendInbox.PostAsync(friend, FriendNotes.Seal(me, friend, note), ct);
            try
            {
                var result = await answer.Task.WaitAsync(timeout, ct);
                ct.ThrowIfCancellationRequested();
                return result; // the answer itself added them (HandleNote)
            }
            catch (TimeoutException) { return FriendRequestResult.NoResponse; } // stays open
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await CancelRequestAsync(pending);
            throw;
        }
        catch (IOException)
        {
            if (previous == null) ForgetPending(requestId);
            throw;
        }
        finally { _friendRequests.TryRemove(requestId, out _); }
    }

    void ForgetPending(string requestId)
    {
        if (_settings.PendingFriendRequests.RemoveAll(p => p.RequestId == requestId) == 0) return;
        _settings.Save();
        InvitesChanged?.Invoke();
    }

    /// <summary>Withdraws a request we sent: tells them, and forgets it here.</summary>
    public async Task CancelRequestAsync(PendingFriendRequest pending)
    {
        ForgetPending(pending.RequestId);
        if (Me is not { } me) return;
        try { await SendFriendRequestCancellationAsync(me, pending.Card, pending.RequestId); }
        catch (IOException ex) { Diag.Log($"Couldn't withdraw the request to {pending.Card.Name}: {ex.Message}"); }
    }

    async Task SendFriendRequestCancellationAsync(FriendIdentity me, Friend friend, string requestId)
    {
        RemoveFriendAddedByRequest(requestId);
        var cancel = new FriendNote(FriendNoteKind.FriendRequestCancelled, me.Name, DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Code: me.FriendCode, RequestId: requestId);
        await FriendInbox.PostAsync(friend, FriendNotes.Seal(me, friend, cancel), CancellationToken.None);
    }

    /// <summary>Sends the user's answer to a request; accepted cards are saved after the reply is delivered.</summary>
    public async Task RespondToFriendRequestAsync(Friend friend, string requestId, bool accepted, CancellationToken ct = default)
    {
        if (Me is not { } me) me = EnsureMe();
        var note = new FriendNote(FriendNoteKind.FriendResponse, me.Name, DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            RequestId: requestId, Accepted: accepted);
        await FriendInbox.PostAsync(friend, FriendNotes.Seal(me, friend, note), ct);
        if (accepted && !_cancelledFriendRequests.Contains(requestId)) AddOrUpdateFriend(friend, requestId);
        ForgetIncoming(friend.Key, requestId);
    }

    void ForgetIncoming(string sender, string requestId)
    {
        if (_settings.IncomingFriendRequests.RemoveAll(p => p.RequestId == requestId && p.Card.Key == sender) == 0) return;
        _settings.Save();
        InvitesChanged?.Invoke();
    }

    void AddOrUpdateFriend(Friend card, string? requestId = null)
    {
        if (Find(card.Key) is { } existing) UpdateFriendCard(existing, card);
        else
        {
            card.AddedByRequestId = requestId;
            _settings.Friends.Add(card);
            _settings.Save();
            ListChanged?.Invoke();
            SyncProfiles();
        }
    }

    void UpdateFriendCard(Friend existing, Friend card)
    {
        int at = _settings.Friends.IndexOf(existing);
        if (at < 0) return;
        card.AddedByRequestId ??= existing.AddedByRequestId;
        card.KeepProfileOf(existing);
        _settings.Friends[at] = card;
        _settings.Save();
    }

    void RemoveFriendAddedByRequest(string requestId)
    {
        if (_settings.Friends.RemoveAll(friend => friend.AddedByRequestId == requestId) > 0) _settings.Save();
    }

    /// <summary>Removes a friend here and tells them to remove this relationship on their side too.</summary>
    public async Task<bool> RemoveAsync(Friend friend)
    {
        if (_settings.Friends.RemoveAll(f => f.Key == friend.Key) == 0) return true;
        _settings.Save();
        ListChanged?.Invoke();

        if (Me is not { } me)
        {
            Diag.Log($"Couldn't tell {friend.Name} that they were removed: our friend identity is unavailable");
            return false;
        }

        try
        {
            var note = new FriendNote(FriendNoteKind.RemovedYou, me.Name, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await FriendInbox.PostAsync(friend, FriendNotes.Seal(me, friend, note), CancellationToken.None);
            return true;
        }
        catch (IOException ex)
        {
            Diag.Log($"Couldn't tell {friend.Name} that they were removed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Leaves a note asking the friend's notch to meet us on the relay with <paramref name="secret"/>.</summary>
    /// <exception cref="IOException">The note couldn't be delivered.</exception>
    public Task InviteAsync(Friend to, byte[] secret, CancellationToken ct)
    {
        var me = EnsureMe();
        var note = new FriendNote(FriendNoteKind.Files, me.Name, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Secret: secret);
        return PostLoggedAsync("file offer", to, FriendNotes.Seal(me, to, note), ct);
    }

    /// <summary>Posts a note and says in diag.log where it went and whether the inbox took it.</summary>
    static async Task PostLoggedAsync(string what, Friend to, string sealedNote, CancellationToken ct)
    {
        Diag.Log($"Friends: sending a {what} to {to.Name} ({to.Topic} on {to.Server ?? "the default server"})");
        try { await FriendInbox.PostAsync(to, sealedNote, ct).ConfigureAwait(false); }
        catch (Exception ex) { Diag.Log($"Friends: the {what} to {to.Name} failed: {ErrorCodes.Describe(ex)}"); throw; }
        Diag.Log($"Friends: {to.Name}'s inbox took the {what}");
    }

    /// <summary>Leaves a note asking the friend's notch to watch our stream, meeting on the relay with <paramref name="secret"/>.</summary>
    /// <exception cref="IOException">The note couldn't be delivered.</exception>
    public Task InviteToStreamAsync(Friend to, byte[] secret, CancellationToken ct)
    {
        var me = EnsureMe();
        var note = new FriendNote(FriendNoteKind.Stream, me.Name, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Secret: secret);
        return PostLoggedAsync("stream invitation", to, FriendNotes.Seal(me, to, note), ct);
    }

    /// <summary>Waves at a friend. False when it's too soon after the last wave (see <see cref="WaveLimit"/>).</summary>
    public bool Wave(Friend to)
    {
        if (!_wavesOut.Allow(to.Key, DateTime.UtcNow)) return false;
        Tell(EnsureMe(), to, FriendNoteKind.Wave);
        return true;
    }

    /// <summary>Sends one chat message (<see cref="ChatText.MaxBytes"/> at most).</summary>
    /// <exception cref="IOException">The note couldn't be delivered to their inbox.</exception>
    public async Task SendChatAsync(Friend to, string id, string text, long sent, CancellationToken ct, string? gif = null)
    {
        var me = EnsureMe();
        var note = new FriendNote(FriendNoteKind.Chat, me.Name, sent, Text: gif ?? text, MessageId: id, Gif: gif);
        await FriendInbox.PostAsync(to, FriendNotes.Seal(me, to, note), ct).ConfigureAwait(false);
        if (to.Key != me.PublicKey)
        {
            var echo = new FriendNote(FriendNoteKind.Chat, DeviceName(), sent, Code: to.Key, Text: gif ?? text, MessageId: id, Gif: gif, Device: DeviceId);
            _ = EchoOwnAsync(me, echo);
        }
    }

    /// <summary>Reacts to a chat message with an emoji, or takes the reaction away (null).</summary>
    /// <exception cref="IOException">The note couldn't be delivered to their inbox.</exception>
    public async Task SendReactionAsync(Friend to, string messageId, string? emoji, CancellationToken ct)
    {
        var me = EnsureMe();
        long sent = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var note = new FriendNote(FriendNoteKind.Reaction, me.Name, sent, Text: emoji, MessageId: messageId);
        await FriendInbox.PostAsync(to, FriendNotes.Seal(me, to, note), ct).ConfigureAwait(false);
        if (to.Key != me.PublicKey)
        {
            var echo = new FriendNote(FriendNoteKind.Reaction, DeviceName(), sent, Code: to.Key, Text: emoji, MessageId: messageId, Device: DeviceId);
            _ = EchoOwnAsync(me, echo);
        }
    }

    async Task EchoOwnAsync(FriendIdentity me, FriendNote note)
    {
        try
        {
            var own = OwnCard(me, DeviceName());
            await FriendInbox.PostAsync(own, FriendNotes.Seal(me, own, note), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            Diag.Log("Own chat sync echo failed: " + ex.Message);
        }
    }

    /// <summary>Distributes an E2E group invite/update (including its AES-256 group key) to all members over pairwise sealed channels.</summary>
    public async Task SendGroupInviteAsync(ChatGroup group, CancellationToken ct)
    {
        var me = EnsureMe();
        long sent = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string json = GroupInvitePayload.FromGroup(group).Serialize();
        var note = new FriendNote(FriendNoteKind.GroupInvite, me.Name, sent, Text: json, GroupId: group.Id);
        var targets = group.Members.Where(m => m.Key != me.PublicKey).Select(m => m.ToFriend()).Where(FriendIdentity.IsValidFriend).ToList();
        await PostToGroupMembersAsync(me, targets, note, ct).ConfigureAwait(false);
        var echo = new FriendNote(FriendNoteKind.GroupInvite, DeviceName(), sent, Text: json, GroupId: group.Id, Device: DeviceId);
        _ = EchoOwnAsync(me, echo);
    }

    /// <summary>Sends an E2E-encrypted group chat message to all members of the group.</summary>
    public async Task SendGroupChatAsync(ChatGroup group, string messageId, string ciphertext, long sent, bool isGif, CancellationToken ct)
    {
        var me = EnsureMe();
        var note = new FriendNote(FriendNoteKind.GroupChat, me.Name, sent, Text: ciphertext, MessageId: messageId,
                                  Gif: isGif ? "1" : null, GroupId: group.Id);
        var targets = group.Members.Where(m => m.Key != me.PublicKey).Select(m => m.ToFriend()).Where(FriendIdentity.IsValidFriend).ToList();
        await PostToGroupMembersAsync(me, targets, note, ct).ConfigureAwait(false);
        var echo = new FriendNote(FriendNoteKind.GroupChat, DeviceName(), sent, Text: ciphertext, MessageId: messageId,
                                  Gif: isGif ? "1" : null, GroupId: group.Id, Device: DeviceId);
        _ = EchoOwnAsync(me, echo);
    }

    /// <summary>Sends a reaction to a group chat message to all members.</summary>
    public async Task SendGroupReactionAsync(ChatGroup group, string messageId, string? emoji, CancellationToken ct)
    {
        var me = EnsureMe();
        long sent = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var note = new FriendNote(FriendNoteKind.GroupReaction, me.Name, sent, Text: emoji, MessageId: messageId, GroupId: group.Id);
        var targets = group.Members.Where(m => m.Key != me.PublicKey).Select(m => m.ToFriend()).Where(FriendIdentity.IsValidFriend).ToList();
        await PostToGroupMembersAsync(me, targets, note, ct).ConfigureAwait(false);
    }

    /// <summary>Notifies all other members that we left the group.</summary>
    public async Task SendGroupLeaveAsync(ChatGroup group, CancellationToken ct)
    {
        var me = EnsureMe();
        long sent = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var note = new FriendNote(FriendNoteKind.GroupLeave, me.Name, sent, GroupId: group.Id);
        var targets = group.Members.Where(m => m.Key != me.PublicKey).Select(m => m.ToFriend()).Where(FriendIdentity.IsValidFriend).ToList();
        if (targets.Count > 0)
            try { await PostToGroupMembersAsync(me, targets, note, ct).ConfigureAwait(false); }
            catch (IOException) { }
    }

    static async Task PostToGroupMembersAsync(FriendIdentity me, IReadOnlyList<Friend> targets, FriendNote note, CancellationToken ct)
    {
        if (targets.Count == 0) return;
        Exception? lastError = null;
        int succeeded = 0;
        var tasks = targets.Select(async member =>
        {
            try
            {
                await FriendInbox.PostAsync(member, FriendNotes.Seal(me, member, note), ct).ConfigureAwait(false);
                Interlocked.Increment(ref succeeded);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException)
            {
                lastError = ex;
                Diag.Log($"Group note to {member.Name} failed: {ex.Message}");
            }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
        if (succeeded == 0 && lastError != null) throw lastError;
    }

    /// <summary>Asks one friend whether their Holm runs (if not asked a minute ago); <see cref="FriendOnline"/> follows if so.</summary>
    public void AskIfHere(Friend friend)
    {
        if (_follow == null || Me is not { } me) return; // the answer comes to our inbox
        if (_presence.ShouldAsk(friend.Key, DateTime.UtcNow)) Tell(me, friend, FriendNoteKind.Ping);
    }

    /// <summary>Leaves a note that something waits; their Holm answers when it next runs.</summary>
    public void TellWaiting(Friend friend)
    {
        if (_follow == null || Me is not { } me) return;
        Tell(me, friend, FriendNoteKind.Waiting);
    }

    /// <summary>Sending to them just failed: they're not online, whatever they said a minute ago.</summary>
    public void MarkOffline(Friend friend) => _presence.Forget(friend.Key);

    /// <summary>How long until the next wave to this friend may go.</summary>
    public TimeSpan WaveWait(Friend to) => _wavesOut.Wait(to.Key, DateTime.UtcNow);

    /// <summary>Follows our inbox while Beam is on and we have an identity; costs nothing otherwise.</summary>
    public void Restart()
    {
        Stop();
        if (!_settings.Beam || Me is not { } me) return;
        var cts = new CancellationTokenSource();
        _follow = cts;
        Func<string, long, string, Task> received = (id, arrived, text) =>
            _ui(() => { if (_follow == cts) Handle(id, arrived, text); });
        // Our inbox on the server the settings name, or else on HolyNotch's relay. Holm's servers (its relay, ntfy.sh)
        // are never followed: friends still on them have to add us again.
        if (!FriendIdentity.IsRetired(_settings.FriendsServer))
            _ = Task.Run(() => FriendInbox.FollowAsync(_settings.FriendsServer, me.Topic, _settings.FriendsSince, received,
                time => _ = _ui(() => { if (_follow == cts) { _settings.FriendsSince = time; _settings.Save(); } }),
                Diag.Log, cts.Token));
        if (_settings.FriendsServer == null)
            _ = Task.Run(() => FriendInbox.FollowRelayAsync(me.Topic, _settings.FriendsRelaySince ?? Now, received,
                time => _ = _ui(() => { if (_follow == cts) { _settings.FriendsRelaySince = time; _settings.Save(); } }),
                () => _ = _ui(() => { if (_follow == cts) { MoveToRelay(); AnnounceDevice(query: true); } }),
                Diag.Log, cts.Token));
        SyncProfiles();
        if (_settings.FriendsServer != null) AnnounceDevice(query: true);
    }

    /// <summary>
    /// The relay let us in for the first time: from now on our friend code names it, and every friend gets the new code,
    /// which their Holm (any version) takes in silently for a friend it has. Their notes then come to the relay; a friend
    /// that hasn't heard yet still writes to ntfy, which we keep following.
    /// </summary>
    internal void MoveToRelay()
    {
        if (_settings.FriendsServer != null || Me is not { } me) return;
        // Told already, about this relay (HolyNotch's server); a Holm user's inbox may have moved to Holm's relay before.
        if (_settings.FriendsRelay && _settings.FriendsRelayServer == FriendIdentity.RelayServer) return;
        _settings.FriendsRelay = true;
        _settings.FriendsRelayServer = FriendIdentity.RelayServer;
        _settings.Save();
        _me = new FriendIdentity(me.Name, me.Topic, FriendIdentity.RelayServer, me.Key);
        Diag.Log("Friends: our inbox moved to the relay");
        Reintroduce(_settings.Friends);
    }

    public void Stop()
    {
        _follow?.Cancel();
        _follow = null;
    }

    /// <summary>What the friend is doing, as their notch last said; null when they're offline, idle or don't share it.</summary>
    public FriendActivity? ActivityOf(Friend friend)
        => IsOnline(friend) && _activities.TryGetValue(friend.Key, out var activity) ? activity : null;

    /// <summary>How many achievements the friend has, as their notch last said; null if it never said.</summary>
    public int? TrophiesOf(Friend friend) => _trophies.TryGetValue(friend.Key, out int n) ? n : null;

    /// <summary>Whether the friend's Holm answered or asked in the last two minutes.</summary>
    public bool IsOnline(Friend friend) => _presence.IsOnline(friend.Key, DateTime.UtcNow);

    /// <summary>
    /// Asks the first friends (as many as the share view shows) whether their Holm is running; those that are answer
    /// within a second or two and <see cref="FriendOnline"/> follows. Friends heard from lately or asked a minute ago
    /// are skipped. Only friends learn that we're online, and only when they ask.
    /// </summary>
    public void PingAll(int max = 8)
    {
        if (_follow == null || Me is not { } me) return; // their answers come to our inbox, which we only follow while Beam is on
        foreach (var friend in _settings.Friends.Take(max))
            if (_presence.ShouldAsk(friend.Key, DateTime.UtcNow)) Tell(me, friend, FriendNoteKind.Ping);
        // The account's other devices too (the phone app, other PCs), at most once a minute.
        if (_presence.ShouldAsk(me.PublicKey, DateTime.UtcNow)) AnnounceDevice(query: true);
    }

    void Tell(FriendIdentity me, Friend to, FriendNoteKind kind)
    {
        // Asking or answering "are you there?" carries what we're doing, like a status in a chat app.
        bool presence = kind is FriendNoteKind.Ping or FriendNoteKind.Here;
        var activity = presence ? MyActivity?.Invoke() : null;
        int? trophies = presence ? MyTrophies?.Invoke() : null;
        string text = FriendNotes.Seal(me, to, new FriendNote(kind, me.Name, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Activity: activity, Trophies: trophies));
        _ = Task.Run(async () =>
        {
            // "Are you there?" and its answer are stale after 30 seconds: the server passes them on without keeping them.
            try { await FriendInbox.PostAsync(to, text, CancellationToken.None, keep: !presence); }
            catch (IOException ex) { Diag.Log($"Friends: couldn't reach {to.Name}'s inbox: {ex.Message}"); }
        });
    }

    void SawOnline(Friend friend)
    {
        if (_presence.Saw(friend.Key, DateTime.UtcNow)) FriendOnline?.Invoke(friend);
    }

    internal void Handle(string id, long arrived, long serverNow, string text)
    {
        _ = serverNow; // freshness comes from authenticated Sent; retained for deterministic replay/expiry tests
        Handle(id, arrived, text);
    }

    internal void Handle(string id, long arrived, string text)
    {
        if (_me is not { } me) return;
        long now = Now;
        bool pruned = MaintainSeen(now);
        // Catching up after a reconnect can repeat the last transport id.
        if (IsSeenId(id) && _seenIds.Contains(id))
        {
            if (pruned) _settings.Save();
            return;
        }
        bool changed = RememberSeen(id, now);

        if (FriendNotes.Open(me, text, out string sender) is not { } note)
        {
            if (changed || pruned) _settings.Save();
            Diag.Log("Friends: a note that isn't for us");
            return;
        }
        // A mirrored note has another transport id. Its authenticated id, or the sealed-text hash for old senders,
        // catches that replay. Entries live for the full authenticated time range plus future-skew slack.
        bool freshNote = RememberSeen(FriendNotes.ReplayId(text, note), now);
        if (!FriendNotes.IsSentTimePlausible(note.Sent, now))
        {
            if (changed || freshNote || pruned) _settings.Save();
            Diag.Log("Friends: ignored a note with an implausible sender timestamp");
            return;
        }
        if (changed || freshNote || pruned) _settings.Save();
        if (!freshNote) return;
        // Freshness comes from the authenticated sender clock, never from a possibly malicious inbox timestamp.
        // A future-dated note inside the allowed skew is fresh now rather than producing a negative age.
        long age = Math.Max(0, now - note.Sent);
        // Live notes (a file offer, a stream, presence) are also fresh when they reached the server just now: a friend
        // whose clock is off by a minute still gets through. Replays stay caught by the seen ids above, and an old offer
        // only leads to a relay nobody waits on. Requests and invites keep the authenticated sender time alone.
        long liveAge = arrived > 0 ? Math.Min(age, Math.Max(0, now - arrived)) : age;
        var friend = sender == me.PublicKey ? null : Find(sender);
        // Every note, so a failed transfer can be followed in diag.log: what came, from whom, how old by both clocks.
        if (note.Kind is not (FriendNoteKind.Ping or FriendNoteKind.Here) || Diag.Enabled)
            Diag.Log($"Friends: {note.Kind} from {(sender == me.PublicKey ? "own device " + note.Device : friend?.Name ?? "someone not a friend")}, " +
                     $"sent {age}s ago, at the server {(arrived > 0 ? Math.Max(0, now - arrived) + "s ago" : "?")}");
        if (sender == me.PublicKey)
        {
            HandleOwn(me, note, liveAge);
            return;
        }
        switch (note.Kind)
        {
            case FriendNoteKind.Files when friend == null:
                Diag.Log("Friends: someone who isn't a friend wanted to send files");
                break;
            case FriendNoteKind.Files when liveAge > Patience.TotalSeconds:
                Diag.Log($"Friends: {friend.Name} wanted to send files while Holm wasn't running");
                break;
            case FriendNoteKind.Files:
                FilesOffered?.Invoke(friend, note.Secret!);
                break;
            case FriendNoteKind.AddedYou when FriendIdentity.ParseFriendCode(note.Code) is { } card:
                if (friend != null)
                {
                    // Already friends: keep their name and inbox current.
                    int at = _settings.Friends.IndexOf(friend);
                    card.KeepProfileOf(friend);
                    _settings.Friends[at] = card;
                    _settings.Save();
                }
                else AddedUs?.Invoke(card);
                break;
            case FriendNoteKind.FriendRequest when FriendIdentity.ParseFriendCode(note.Code) is { } card && note.RequestId is { } requestId:
                if (age > PendingFor) break;
                if (friend != null)
                {
                    // Repair an older one-sided relationship automatically when the other person requests us.
                    UpdateFriendCard(friend, card);
                    _ = ReplyToRequestAsync(me, card, requestId, accepted: true);
                }
                else if (!_cancelledFriendRequests.Contains(requestId))
                {
                    bool isNew = !_settings.IncomingFriendRequests.Any(p => p.RequestId == requestId && p.Card.Key == sender);
                    if (!isNew) break;
                    OwnInvite? invite = note.Invite is { } proof ? ActiveInvite(proof, arrived) : null;
                    if (note.Invite != null && invite == null && !_proofAttempts.Allow(sender, now))
                    {
                        Diag.Log("Friends: throttled requests carrying an unknown, expired or revoked invite proof");
                        break;
                    }
                    _settings.IncomingFriendRequests.Add(new PendingFriendRequest { Card = card, RequestId = requestId, Sent = note.Sent});
                    _settings.Save();
                    InvitesChanged?.Invoke();
                    if (invite != null) _ = AcceptInviteAsync(card, requestId, invite);
                    else FriendRequested?.Invoke(card, requestId);
                }
                break;
            case FriendNoteKind.FriendResponse when note.RequestId is { } responseId && note.Accepted is { } accepted:
            {
                // Only the person we asked can answer, and the answer counts whenever it comes.
                var pending = _settings.PendingFriendRequests.FirstOrDefault(p => p.RequestId == responseId && p.Card.Key == sender);
                bool waiting = _friendRequests.TryGetValue(responseId, out var answer);
                if (pending != null)
                {
                    ForgetPending(responseId);
                    if (accepted) AddOrUpdateFriend(pending.Card, responseId);
                    if (!waiting) LateAnswer?.Invoke(pending.Card, accepted);
                }
                if (waiting && pending != null) answer!.TrySetResult(accepted ? FriendRequestResult.Accepted : FriendRequestResult.Declined);
                break;
            }
            case FriendNoteKind.FriendRequestCancelled when FriendIdentity.ParseFriendCode(note.Code) is { } card && note.RequestId is { } cancelledId:
                _cancelledFriendRequests.Add(cancelledId);
                ForgetIncoming(sender, cancelledId);
                RemoveFriendAddedByRequest(cancelledId);
                FriendRequestCancelled?.Invoke(card, cancelledId);
                break;
            case FriendNoteKind.RemovedYou when friend != null:
                _settings.Friends.RemoveAll(f => f.Key == friend.Key);
                _settings.Save();
                Diag.Log($"Friends: {friend.Name} removed us");
                RemovedUs?.Invoke(friend);
                ListChanged?.Invoke();
                break;
            case FriendNoteKind.RemovedYou:
                Diag.Log("Friends: ignored a removal note from someone who isn't a friend");
                break;
            case FriendNoteKind.Ping or FriendNoteKind.Here when friend == null || liveAge > FreshSeconds:
                break; // strangers don't learn whether we're online; old ones came while Holm wasn't running
            case FriendNoteKind.Ping:
                Took(friend, note.Activity, note.Trophies);
                SawOnline(friend);
                if (_presence.ShouldAnswer(friend.Key, DateTime.UtcNow)) Tell(me, friend, FriendNoteKind.Here);
                break;
            case FriendNoteKind.Here:
                Took(friend, note.Activity, note.Trophies);
                SawOnline(friend);
                break;
            case FriendNoteKind.Waiting when friend == null || liveAge > WaitingSeconds:
                break; // the inbox server keeps notes for about 12 hours anyway
            case FriendNoteKind.Waiting:
                // They have something for us and will send it once they hear we're here.
                if (liveAge <= FreshSeconds) SawOnline(friend);
                if (_presence.ShouldAnswer(friend.Key, DateTime.UtcNow)) Tell(me, friend, FriendNoteKind.Here);
                break;
            case FriendNoteKind.Wave when friend == null || liveAge > WaveLimit.FreshSeconds:
                break; // strangers can't wave; an old wave came while Holm wasn't running
            case FriendNoteKind.Wave:
                SawOnline(friend);
                if (_settings.Waves && _wavesIn.Allow(friend.Key, DateTime.UtcNow)) Waved?.Invoke(friend);
                break;
            case FriendNoteKind.Stream when friend == null || liveAge > StreamPatience.TotalSeconds:
                break; // strangers can't stream to us; an old invitation came while Holm wasn't running
            case FriendNoteKind.Stream:
                SawOnline(friend);
                StreamOffered?.Invoke(friend, note.Secret!);
                break;
            case FriendNoteKind.Chat when friend == null:
                Diag.Log("Friends: a chat message from someone who isn't a friend");
                break;
            case FriendNoteKind.Chat:
                if (liveAge <= FreshSeconds) SawOnline(friend);
                if (note.Gif != null) GifReceived?.Invoke(friend, note.MessageId!, note.Gif, note.Sent);
                else ChatReceived?.Invoke(friend, note.MessageId!, note.Text!, note.Sent);
                break;
            case FriendNoteKind.Reaction when friend == null:
                break; // only friends react
            case FriendNoteKind.Reaction:
                if (liveAge <= FreshSeconds) SawOnline(friend);
                ReactionReceived?.Invoke(friend, note.MessageId!, note.Text);
                break;
            case FriendNoteKind.Profile when friend == null || note.Profile == null:
                break; // only friends show up with a picture
            case FriendNoteKind.Profile:
                friend.TakeProfile(note.Profile);
                _settings.Save();
                ProfileReceived?.Invoke(friend);
                SyncProfiles(); // they may not have ours yet, e.g. added before profiles existed
                break;
            case FriendNoteKind.GroupInvite when friend == null:
                Diag.Log("Friends: a group invite from someone who isn't a friend");
                break;
            case FriendNoteKind.GroupInvite when GroupInvitePayload.Parse(note.Text) is { } invitedGroup:
                if (liveAge <= FreshSeconds) SawOnline(friend);
                GroupInviteReceived?.Invoke(invitedGroup, sender);
                break;
            case FriendNoteKind.GroupChat when note.GroupId is { } grpId && note.MessageId is { } grpMsgId && note.Text is { } grpCipher:
                if (friend != null && liveAge <= FreshSeconds) SawOnline(friend);
                GroupChatReceived?.Invoke(grpId, sender, friend?.Name ?? note.From, grpMsgId, grpCipher, note.Sent, note.Gif != null);
                break;
            case FriendNoteKind.GroupReaction when note.GroupId is { } rGrpId && note.MessageId is { } rMsgId:
                if (friend != null && liveAge <= FreshSeconds) SawOnline(friend);
                GroupReactionReceived?.Invoke(rGrpId, sender, rMsgId, note.Text);
                break;
            case FriendNoteKind.GroupLeave when note.GroupId is { } lGrpId:
                if (friend != null && liveAge <= FreshSeconds) SawOnline(friend);
                GroupLeaveReceived?.Invoke(lGrpId, sender, friend?.Name ?? note.From);
                break;
        }
    }

    void Took(Friend friend, FriendActivity? activity, int? trophies)
    {
        bool had = _activities.TryGetValue(friend.Key, out var before);
        _activities[friend.Key] = activity;
        bool more = trophies is { } n && (!_trophies.TryGetValue(friend.Key, out int was) || was != n);
        if (trophies is { } count) _trophies[friend.Key] = count;
        if (!had || before != activity || more) ActivityChanged?.Invoke(friend);
    }

    // Server arrival preserves the compatible offline-expiry allowance without trusting backdated dto.T. A malicious
    // inbox can still lie about this one timestamp; that is a documented mitigation, not server-time authentication.
    OwnInvite? ActiveInvite(string proof, long arrived)
        => _settings.FriendInvites.FirstOrDefault(i => !i.Revoked && i.Proof == proof && arrived > 0 && arrived <= i.Expires + InviteArrivalGrace);

    /// <summary>Someone who had our invite code asked: say yes and add them.</summary>
    async Task AcceptInviteAsync(Friend card, string requestId, OwnInvite invite)
    {
        if (invite.SingleUse)
        {
            if (invite.Revoked)
            {
                FriendRequested?.Invoke(card, requestId);
                return;
            }
            // Claim before the network await: two requests handled together cannot both spend a single-use code.
            invite.Revoked = true;
            invite.UsedBy = card.Key;
            invite.RevokedAt = Now;
            _settings.Save();
            InvitesChanged?.Invoke();
        }
        try
        {
            await RespondToFriendRequestAsync(card, requestId, accepted: true);
            Diag.Log("Friends: someone joined with our invite code");
            InviteAccepted?.Invoke(card);
        }
        catch (IOException ex)
        {
            Diag.Log("Friends: couldn't answer an invite request: " + ex.Message);
            FriendRequested?.Invoke(card, requestId); // ask the person instead
        }
    }

    async Task ReplyToRequestAsync(FriendIdentity me, Friend card, string requestId, bool accepted)
    {
        try
        {
            var note = new FriendNote(FriendNoteKind.FriendResponse, me.Name, DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                RequestId: requestId, Accepted: accepted);
            await FriendInbox.PostAsync(card, FriendNotes.Seal(me, card, note), CancellationToken.None);
        }
        catch (IOException ex) { Diag.Log($"Couldn't confirm {card.Name}'s friend request: {ex.Message}"); }
    }
}

/// <summary>An extra brake on guessing the 50-bit invite derivation. Correct proofs bypass this limiter.</summary>
internal sealed class FriendProofAttempts
{
    internal const int PerSender = 5, PerGlobal = 60;
    static readonly TimeSpan SenderWindow = TimeSpan.FromMinutes(10), GlobalWindow = TimeSpan.FromMinutes(1);
    readonly object _gate = new();
    readonly Dictionary<string, List<long>> _senders = new(StringComparer.Ordinal);
    readonly List<long> _global = new();

    public bool Allow(string sender, long now)
    {
        lock (_gate)
        {
            long senderCutoff = now - (long)SenderWindow.TotalSeconds;
            long globalCutoff = now - (long)GlobalWindow.TotalSeconds;
            _global.RemoveAll(t => t < globalCutoff);
            if (_global.Count >= PerGlobal) return false;
            foreach (var pair in _senders.ToList())
            {
                pair.Value.RemoveAll(t => t < senderCutoff);
                if (pair.Value.Count == 0) _senders.Remove(pair.Key);
            }
            if (!_senders.TryGetValue(sender, out var attempts)) _senders[sender] = attempts = new();
            if (attempts.Count >= PerSender) return false;
            attempts.Add(now);
            _global.Add(now);
            return true;
        }
    }
}

/// <summary>
/// Which friends are online, and when to ask or answer them, by public key. Every note counts against the inbox
/// server's daily limit, so nobody is asked or answered more than once in a while.
/// </summary>
internal sealed class FriendPresence
{
    /// <summary>A friend counts as online this long after their notch last answered or asked.</summary>
    public static readonly TimeSpan OnlineFor = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan AskEvery = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan AnswerEvery = TimeSpan.FromSeconds(10);

    readonly Dictionary<string, DateTime> _seen = new(), _asked = new(), _answered = new();

    public bool IsOnline(string key, DateTime now) => _seen.TryGetValue(key, out var at) && now - at < OnlineFor;

    /// <summary>Their notch answered or asked. True if they weren't online before.</summary>
    public bool Saw(string key, DateTime now)
    {
        bool was = IsOnline(key, now);
        _seen[key] = now;
        return !was;
    }

    /// <summary>Whether to ask them now: not if we heard from them lately or asked them a minute ago.</summary>
    public bool ShouldAsk(string key, DateTime now)
    {
        if (IsOnline(key, now) || _asked.TryGetValue(key, out var at) && now - at < AskEvery) return false;
        _asked[key] = now;
        return true;
    }

    /// <summary>They're not online after all (a send to them just failed).</summary>
    public void Forget(string key) => _seen.Remove(key);

    /// <summary>Whether to answer their question now.</summary>
    public bool ShouldAnswer(string key, DateTime now)
    {
        if (_answered.TryGetValue(key, out var at) && now - at < AnswerEvery) return false;
        _answered[key] = now;
        return true;
    }
}
