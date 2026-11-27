using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WinNotch;

namespace WinNotch.Sharing;

public sealed record HolmBridgeMessage(string Id, bool Mine, string Text, long Sent);
public sealed record HolmBridgePending(string RequestId, Friend Card, long Sent);

/// <summary>
/// Experimental isolated bridge that speaks upstream Holm's exact protocol against https://relay.holmnotch.ch
/// using its own separate identity, so HolyNotch users can add and chat with upstream Holm users (appearing as a normal Holm user).
/// </summary>
internal sealed class HolmBridgeService : IDisposable
{
    public const string HolmRelay = "https://relay.holmnotch.ch";
    const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    readonly AppSettings _settings;
    readonly Func<Action, Task> _ui;
    FriendIdentity? _me;
    CancellationTokenSource? _pollCts;
    readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    readonly List<HolmBridgePending> _incoming = new();
    string? _activeInviteCode;
    string? _activeInviteProof;

    public event Action? Changed;
    public event Action<Friend, HolmBridgeMessage>? MessageReceived;

    public HolmBridgeService(AppSettings settings, Func<Action, Task> ui)
    {
        _settings = settings;
        _ui = ui;
        if (_settings.HolmBridgeEnabled) Start();
    }

    public bool Enabled => _settings.HolmBridgeEnabled;
    public string? ActiveInviteCode => _activeInviteCode;
    public IReadOnlyList<Friend> Friends => _settings.HolmBridgeFriends;
    public IReadOnlyList<HolmBridgePending> IncomingRequests => _incoming;

    public IReadOnlyList<HolmBridgeMessage> MessagesFor(string friendKey)
        => _settings.HolmBridgeChats.TryGetValue(friendKey, out var list) ? list : Array.Empty<HolmBridgeMessage>();

    public FriendIdentity EnsureMe()
    {
        if (_me != null) return _me;
        string name = string.IsNullOrWhiteSpace(_settings.ProfileName) ? Environment.MachineName : _settings.ProfileName.Trim();
        if (!string.IsNullOrWhiteSpace(_settings.HolmBridgeTopic) && !string.IsNullOrWhiteSpace(_settings.HolmBridgePrivateKey))
        {
            try
            {
                var key = ECDiffieHellman.Create();
                key.ImportPkcs8PrivateKey(Convert.FromBase64String(_settings.HolmBridgePrivateKey), out _);
                _me = new FriendIdentity(name, _settings.HolmBridgeTopic, HolmRelay, key);
                return _me;
            }
            catch { }
        }
        _me = FriendIdentity.Create(name, HolmRelay);
        _settings.HolmBridgeTopic = _me.Topic;
        _settings.HolmBridgePrivateKey = Convert.ToBase64String(_me.Key.ExportPkcs8PrivateKey());
        _settings.Save();
        return _me;
    }

    public void SetEnabled(bool on)
    {
        if (_settings.HolmBridgeEnabled == on) return;
        _settings.HolmBridgeEnabled = on;
        _settings.Save();
        if (on) Start(); else Stop();
        Changed?.Invoke();
    }

    public void Start()
    {
        Stop();
        if (!_settings.HolmBridgeEnabled) return;
        var me = EnsureMe();
        var cts = new CancellationTokenSource();
        _pollCts = cts;
        _ = Task.Run(() => PollLoopAsync(me, cts.Token));
    }

    public void Stop()
    {
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        _pollCts = null;
    }

    /// <summary>Creates a genuine 10-char HOLM-XXXXX-XXXXX invite code and publishes our sealed card to relay.holmnotch.ch.</summary>
    public async Task<string> CreateHolmInviteAsync(CancellationToken ct = default)
    {
        var me = EnsureMe();
        var chars = new char[10];
        byte[] rnd = RandomNumberGenerator.GetBytes(10);
        for (int i = 0; i < 10; i++) chars[i] = Alphabet[rnd[i] & 31];
        string code = $"HOLM-{new string(chars, 0, 5)}-{new string(chars, 5, 5)}";
        long expires = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + (long)FriendInvite.Lifetime.TotalSeconds - 120;
        string sealedCard = FriendInvite.Seal(code, me.FriendCode, expires);
        await PostAsync(HolmRelay, FriendInvite.Topic(code), sealedCard, ct);
        _activeInviteCode = code;
        _activeInviteProof = FriendInvite.Proof(code);
        Changed?.Invoke();
        return code;
    }

    /// <summary>Adds an upstream Holm user via their HOLM-XXXXX-XXXXX invite code or WNF1.… friend code.</summary>
    public async Task<(bool Ok, string Message)> AddHolmUserAsync(string input, CancellationToken ct = default)
    {
        var me = EnsureMe();
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Friend? card = null;
        string? proof = null;

        if (FriendInvite.Find(input) is { } code)
        {
            var lines = await FetchPollAsync(HolmRelay, FriendInvite.Topic(code), since: 0, ct);
            card = lines.Select(l => FriendInvite.Open(code, l.Message, now)).FirstOrDefault(c => c != null);
            if (card == null) return (false, "Holm invite code not found or expired on relay.holmnotch.ch.");
            proof = FriendInvite.Proof(code);
        }
        else if (FriendIdentity.ParseFriendCode(input) is { } parsed)
        {
            card = parsed;
        }
        else
        {
            return (false, "Paste a Holm invite code (HOLM-XXXXX-XXXXX) or friend code (WNF1.…).");
        }

        if (card.Key == me.PublicKey) return (false, "That's your own Holm Bridge code.");

        string requestId = Guid.NewGuid().ToString("N");
        var reqNote = new FriendNote(
            FriendNoteKind.FriendRequest,
            me.Name,
            now,
            Code: me.FriendCode,
            RequestId: requestId,
            Invite: proof,
            NoteId: FriendNotes.NewNoteId());
        string targetServer = string.IsNullOrEmpty(card.Server) ? HolmRelay : card.Server;
        await PostAsync(targetServer, card.Topic, FriendNotes.Seal(me, card, reqNote), ct);

        UpsertFriend(card);
        Changed?.Invoke();
        return (true, proof != null ? $"Added {card.Name} on Holm." : $"Sent Holm friend request to {card.Name}.");
    }

    public async Task AcceptRequestAsync(HolmBridgePending req, CancellationToken ct = default)
    {
        var me = EnsureMe();
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var note = new FriendNote(
            FriendNoteKind.FriendResponse,
            me.Name,
            now,
            Code: me.FriendCode,
            RequestId: req.RequestId,
            Accepted: true,
            NoteId: FriendNotes.NewNoteId());
        string targetServer = string.IsNullOrEmpty(req.Card.Server) ? HolmRelay : req.Card.Server;
        await PostAsync(targetServer, req.Card.Topic, FriendNotes.Seal(me, req.Card, note), ct);
        _incoming.RemoveAll(r => r.RequestId == req.RequestId);
        UpsertFriend(req.Card);
        Changed?.Invoke();
    }

    public void DeclineRequest(HolmBridgePending req)
    {
        _incoming.RemoveAll(r => r.RequestId == req.RequestId);
        Changed?.Invoke();
    }

    public void RemoveFriend(Friend friend)
    {
        _settings.HolmBridgeFriends.RemoveAll(f => f.Key == friend.Key);
        _settings.HolmBridgeChats.Remove(friend.Key);
        _settings.Save();
        Changed?.Invoke();
    }

    public async Task SendChatAsync(Friend friend, string text, CancellationToken ct = default)
    {
        text = text.Trim();
        if (string.IsNullOrEmpty(text)) return;
        var me = EnsureMe();
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string msgId = Guid.NewGuid().ToString("N");
        var note = new FriendNote(
            FriendNoteKind.Chat,
            me.Name,
            now,
            Text: text,
            MessageId: msgId,
            NoteId: FriendNotes.NewNoteId());
        string targetServer = string.IsNullOrEmpty(friend.Server) ? HolmRelay : friend.Server;
        await PostAsync(targetServer, friend.Topic, FriendNotes.Seal(me, friend, note), ct);
        AppendMessage(friend.Key, new HolmBridgeMessage(msgId, Mine: true, text, now));
        Changed?.Invoke();
    }

    async Task PollLoopAsync(FriendIdentity me, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                long since = _settings.HolmBridgeSince > 0 ? _settings.HolmBridgeSince : DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 3600;
                var items = await FetchPollAsync(HolmRelay, me.Topic, since, ct);
                if (items.Count > 0)
                {
                    long maxTime = since;
                    foreach (var item in items)
                    {
                        if (item.Time > maxTime) maxTime = item.Time;
                        if (!_seen.Add(item.Id)) continue;
                        var opened = FriendNotes.Open(me, item.Message, out string? senderKey);
                        if (opened == null || string.IsNullOrEmpty(senderKey)) continue;
                        await _ui(() => HandleIncoming(me, opened, senderKey));
                    }
                    if (maxTime > _settings.HolmBridgeSince)
                    {
                        await _ui(() => { _settings.HolmBridgeSince = maxTime; _settings.Save(); });
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Diag.Log("HolmBridge poll: " + ex.Message); }
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    void HandleIncoming(FriendIdentity me, FriendNote note, string senderKey)
    {
        switch (note.Kind)
        {
            case FriendNoteKind.FriendRequest when FriendIdentity.ParseFriendCode(note.Code) is { } card:
                if (!string.IsNullOrEmpty(_activeInviteProof) && note.Invite == _activeInviteProof)
                {
                    var req = new HolmBridgePending(note.RequestId ?? Guid.NewGuid().ToString("N"), card, note.Sent);
                    _ = Task.Run(() => AcceptRequestAsync(req));
                }
                else if (_settings.HolmBridgeFriends.Any(f => f.Key == card.Key))
                {
                    UpsertFriend(card);
                }
                else if (!_incoming.Any(r => r.Card.Key == card.Key))
                {
                    _incoming.Add(new HolmBridgePending(note.RequestId ?? Guid.NewGuid().ToString("N"), card, note.Sent));
                    Changed?.Invoke();
                }
                break;

            case FriendNoteKind.FriendResponse when note.Accepted == true && FriendIdentity.ParseFriendCode(note.Code) is { } card:
                UpsertFriend(card);
                Changed?.Invoke();
                break;

            case FriendNoteKind.AddedYou when FriendIdentity.ParseFriendCode(note.Code) is { } card:
                UpsertFriend(card);
                Changed?.Invoke();
                break;

            case FriendNoteKind.Chat when !string.IsNullOrWhiteSpace(note.Text):
                var friend = _settings.HolmBridgeFriends.FirstOrDefault(f => f.Key == senderKey);
                if (friend == null) break;
                var msg = new HolmBridgeMessage(note.MessageId ?? Guid.NewGuid().ToString("N"), Mine: false, note.Text!, note.Sent);
                if (AppendMessage(friend.Key, msg))
                {
                    Changed?.Invoke();
                    MessageReceived?.Invoke(friend, msg);
                }
                break;

            case FriendNoteKind.Ping:
                if (_settings.HolmBridgeFriends.FirstOrDefault(f => f.Key == senderKey) is { } pingFriend)
                {
                    var here = new FriendNote(FriendNoteKind.Here, me.Name, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    string srv = string.IsNullOrEmpty(pingFriend.Server) ? HolmRelay : pingFriend.Server;
                    _ = Task.Run(() => PostAsync(srv, pingFriend.Topic, FriendNotes.Seal(me, pingFriend, here), CancellationToken.None));
                }
                break;
        }
    }

    void UpsertFriend(Friend card)
    {
        int idx = _settings.HolmBridgeFriends.FindIndex(f => f.Key == card.Key);
        if (idx >= 0) _settings.HolmBridgeFriends[idx] = card;
        else _settings.HolmBridgeFriends.Add(card);
        _settings.Save();
    }

    bool AppendMessage(string friendKey, HolmBridgeMessage msg)
    {
        if (!_settings.HolmBridgeChats.TryGetValue(friendKey, out var list))
            _settings.HolmBridgeChats[friendKey] = list = new List<HolmBridgeMessage>();
        if (list.Any(m => m.Id == msg.Id)) return false;
        list.Add(msg);
        if (list.Count > 200) list.RemoveRange(0, list.Count - 200);
        _settings.Save();
        return true;
    }

    static async Task PostAsync(string server, string topic, string body, CancellationToken ct)
    {
        string url = $"{server.TrimEnd('/')}/{Uri.EscapeDataString(topic)}";
        using var content = new StringContent(body, Encoding.UTF8, "text/plain");
        using var resp = await Http.PostAsync(url, content, ct);
        if (!resp.IsSuccessStatusCode) throw new IOException($"Holm relay returned HTTP {(int)resp.StatusCode}.");
    }

    sealed record PollLine(string Id, long Time, string Message);

    static async Task<List<PollLine>> FetchPollAsync(string server, string topic, long since, CancellationToken ct)
    {
        string url = $"{server.TrimEnd('/')}/{Uri.EscapeDataString(topic)}/json?poll=1" + (since > 0 ? $"&since={since}" : "");
        using var resp = await Http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return new();
        string raw = await resp.Content.ReadAsStringAsync(ct);
        var list = new List<PollLine>();
        foreach (string line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.TryGetProperty("event", out var ev) && ev.GetString() != "message") continue;
                string id = root.TryGetProperty("id", out var idEl) ? (idEl.GetString() ?? Guid.NewGuid().ToString("N")) : Guid.NewGuid().ToString("N");
                long time = root.TryGetProperty("time", out var tEl) && tEl.TryGetInt64(out long t) ? t : 0;
                string? msg = root.TryGetProperty("message", out var mEl) ? mEl.GetString() : null;
                if (!string.IsNullOrEmpty(msg)) list.Add(new PollLine(id, time, msg));
            }
            catch { }
        }
        return list;
    }

    public void Dispose() => Stop();
}
