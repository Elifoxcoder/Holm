using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WinNotch.Sharing;

// Streaming your screen to friends. The streamer leaves each friend a sealed "stream" note with a fresh secret, as for
// files; both notches meet on the transit relay with it and talk over a SecureChannel. The viewer says Watch (or No),
// then the streamer sends JPEG frames as they come, a sign of life while the screen doesn't change, and End when it
// stops. Each viewer has a channel of their own; a viewer that is still busy with one frame skips the next, so a slow
// connection gets fewer frames instead of falling behind.
//
// Paced viewers (Holm 1.12 and later) say so with a second byte after Watch; the streamer then asks for a receipt
// (Paced) and the viewer answers each frame with Ack once it's shown. A frame "sent" into the socket was only in this
// PC's buffers: the relay and the routers on the way could still hold seconds of earlier frames. Waiting for the receipt
// keeps one frame on the way per viewer, and how long it took sets the next frames' quality. Older Holm on either side
// ignores the extra bytes and gets the old behaviour.

public static class StreamProtocol
{
    public const byte Watch = (byte)'W', No = (byte)'N', Frame = (byte)'F', Alive = (byte)'K', End = (byte)'E';
    /// <summary>After Watch: this viewer sends receipts. From the streamer: send them. The viewer's receipt for a frame.</summary>
    public const byte Receipts = (byte)'R', Paced = (byte)'P', Ack = (byte)'A';

    /// <summary>The JPEG quality streams start at, and the range it moves in with the connection.</summary>
    public const int StartQuality = 60, MinQuality = 30, MaxQuality = 75;

    /// <summary>
    /// The next frames' JPEG quality, from how long the last frame took to reach the slowest viewer and come back: lower
    /// when it took long, higher again when there's room. Between <see cref="MinQuality"/> and <see cref="MaxQuality"/>.
    /// </summary>
    /// <remarks>0 means no receipts to go by (older Holm watching, or none yet): the quality stays where it is.</remarks>
    public static int NextQuality(int quality, double roundTripMs)
        => roundTripMs <= 0 ? Math.Clamp(quality, MinQuality, MaxQuality) : Math.Clamp(roundTripMs > 700 ? quality - 10 : roundTripMs > 400 ? quality - 4 : roundTripMs < 200 ? quality + 3 : quality, MinQuality, MaxQuality);

    /// <summary>The size a screen is sent at: within <paramref name="max"/> on both sides, the same shape, never larger.</summary>
    public static (int Width, int Height) FitWithin(int width, int height, int max)
    {
        double scale = Math.Min(1.0, max / (double)Math.Max(width, height));
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    /// <summary>Without a frame or a sign of life for this long, the stream is gone.</summary>
    public static readonly TimeSpan Silence = TimeSpan.FromSeconds(20);
    /// <summary>A sign of life goes out when the screen hasn't changed for this long.</summary>
    public static readonly TimeSpan AliveEvery = TimeSpan.FromSeconds(3);

    /// <summary>Derives a deterministic 32-byte reconnect secret for attempt 1..N so both sides meet on the relay without a new invite.</summary>
    public static byte[] ReconnectSecret(byte[] originalSecret, int attempt)
    {
        var label = System.Text.Encoding.UTF8.GetBytes($"holm-stream-reconnect:{attempt}");
        var input = new byte[originalSecret.Length + label.Length];
        originalSecret.CopyTo(input, 0);
        label.CopyTo(input, originalSecret.Length);
        return System.Security.Cryptography.SHA256.HashData(input);
    }

    public static byte[] FrameMessage(byte[] jpeg)
    {
        if (jpeg.Length + 1 > SecureChannel.MaxPlaintext) throw new ArgumentException("Frame too large.", nameof(jpeg));
        var message = new byte[jpeg.Length + 1];
        message[0] = Frame;
        jpeg.CopyTo(message, 1);
        return message;
    }

    /// <summary>
    /// The viewer's side: says it watches, then hands each frame to <paramref name="frame"/> until the streamer ends,
    /// goes silent or <paramref name="ct"/> fires. True if the streamer said End.
    /// </summary>
    public static async Task<bool> WatchAsync(SecureChannel channel, Action<byte[]> frame, CancellationToken ct)
    {
        await channel.SendAsync(new[] { Watch, Receipts }, ct).ConfigureAwait(false);
        bool paced = false;
        while (true)
        {
            using var quiet = CancellationTokenSource.CreateLinkedTokenSource(ct);
            quiet.CancelAfter(Silence);
            byte[] message;
            try { message = await channel.ReceiveAsync(quiet.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Tell the streamer we closed intentionally so they don't wait to reconnect.
                try
                {
                    using var bye = new CancellationTokenSource(TimeSpan.FromMilliseconds(350));
                    await channel.SendAsync(new[] { End }, bye.Token).ConfigureAwait(false);
                }
                catch { }
                throw;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; } // silence
            if (message.Length == 0) continue;
            switch (message[0])
            {
                case Frame when message.Length > 1:
                    frame(message[1..]);
                    if (paced) await channel.SendAsync(new[] { Ack }, ct).ConfigureAwait(false);
                    break;
                case Paced: paced = true; break;
                case End: return true;
                // Alive, and anything a newer Holm may send: keep watching.
            }
        }
    }

    /// <summary>The viewer's "not now", so the streamer stops waiting.</summary>
    public static Task DeclineAsync(SecureChannel channel, CancellationToken ct) => channel.SendAsync(new[] { No }, ct);

    /// <summary>The streamer's side: waits for the viewer's answer. Watch, and whether the viewer sends receipts.</summary>
    public static async Task<(bool Watch, bool Receipts)> AnswerAsync(SecureChannel channel, CancellationToken ct)
    {
        byte[] answer = await channel.ReceiveAsync(ct).ConfigureAwait(false);
        bool watch = answer.Length > 0 && answer[0] == Watch;
        return (watch, watch && answer.Length > 1 && answer[1] == Receipts);
    }
}

/// <summary>The streamer's viewers, each with their channel; frames go to every viewer that is ready for one.</summary>
public sealed class StreamHost : IAsyncDisposable
{
    static readonly TimeSpan SendPatience = TimeSpan.FromSeconds(15);

    /// <summary>How long a paced viewer may take to send a frame's receipt before it counts as gone.</summary>
    public TimeSpan ReceiptPatience { get; init; } = SendPatience;

    long _receipts;

    /// <summary>Receipts so far, from every viewer: the quality moves once per receipt, not once per captured frame.</summary>
    public long Receipts => Interlocked.Read(ref _receipts);

    sealed class Viewer
    {
        public required string Key { get; init; }
        public required string Name { get; init; }
        public required SecureChannel Channel { get; init; }
        public bool Paced { get; init; }
        /// <summary>1 while a frame is on its way (until its receipt, for a paced viewer).</summary>
        public int Busy;
        /// <summary>What this viewer saw last, and when: a viewer that skipped a frame still gets the next change.</summary>
        public int LastHash;
        public DateTime LastSent = DateTime.MinValue;
        public long SentAt;
        public double RoundTripMs;
    }

    readonly List<Viewer> _viewers = new();
    readonly HashSet<Task> _sends = new();
    readonly object _lock = new();
    readonly CancellationTokenSource _stop = new();
    bool _ended;

    /// <summary>A viewer joined or left (any thread).</summary>
    public event Action? ViewersChanged;
    /// <summary>A viewer disconnected (key, name, cleanEnd: true if viewer sent End intentionally).</summary>
    public event Action<string, string, bool>? ViewerDropped;

    public DateTime Started { get; } = DateTime.UtcNow;

    public IReadOnlyList<string> Names { get { lock (_lock) return _viewers.Select(v => v.Name).ToList(); } }

    public bool Has(string key) { lock (_lock) return _viewers.Any(v => v.Key == key); }

    /// <summary>A viewer who said Watch (and whether they send receipts). The host owns the channel from now on.</summary>
    public void Add(string key, string name, SecureChannel channel, bool paced = false)
    {
        // Its clock starts now: DropSilent mustn't take the wait for the first receipt as 0 + forever.
        var viewer = new Viewer { Key = key, Name = name, Channel = channel, Paced = paced, SentAt = System.Diagnostics.Stopwatch.GetTimestamp() };
        lock (_lock)
        {
            if (_ended) { channel.Dispose(); return; }
            if (paced) viewer.Busy = 1; // until it has been asked for receipts
            _viewers.Add(viewer);
            // Start under the same lock as Publish/Dispose so its first send is tracked before teardown can snapshot.
            if (paced) _ = PaceAsync(viewer);
        }
        ViewersChanged?.Invoke();
    }

    /// <summary>
    /// Whether any viewer can take a frame now. When none can, taking a picture of the screen (and making it a JPEG)
    /// would be for nothing.
    /// </summary>
    public bool AnyReady
    {
        get
        {
            lock (_lock) return _viewers.Count == 0 || _viewers.Any(v => Volatile.Read(ref v.Busy) == 0);
        }
    }

    /// <summary>The longest a frame took to reach a viewer and its receipt to come back, lately (0 before any).</summary>
    public double SlowestRoundTripMs
    {
        get { lock (_lock) return _viewers.Count == 0 ? 0 : _viewers.Max(v => v.RoundTripMs); }
    }

    /// <summary>
    /// Lets go of paced viewers whose receipt is overdue: a half-open connection would otherwise count as watching for
    /// good, and keep <see cref="AnyReady"/> false. The capture loop calls it every round, picture or not.
    /// </summary>
    public void DropSilent()
    {
        Viewer[] overdue;
        lock (_lock)
            overdue = _viewers.Where(v => v.Paced && Volatile.Read(ref v.Busy) != 0
                                          && System.Diagnostics.Stopwatch.GetElapsedTime(Interlocked.Read(ref v.SentAt)) > ReceiptPatience).ToArray();
        foreach (var viewer in overdue) Remove(viewer);
    }

    /// <summary>
    /// A new picture of the screen (from the capture thread). To each viewer ready for one: the picture, or, when it's
    /// what they saw last, a sign of life every few seconds.
    /// </summary>
    public void Publish(byte[] jpeg)
    {
        int hash = Hash(jpeg);
        byte[]? frame = null;
        var now = DateTime.UtcNow;
        lock (_lock)
        {
            if (_ended) return;
            foreach (var viewer in _viewers.ToArray())
            {
                if (Volatile.Read(ref viewer.Busy) != 0)
                {
                    continue; // still on the last one
                }
                bool same = hash == viewer.LastHash;
                if (same && now - viewer.LastSent < StreamProtocol.AliveEvery) continue;
                if (Interlocked.CompareExchange(ref viewer.Busy, 1, 0) != 0) continue;
                viewer.LastHash = hash;
                viewer.LastSent = now;
                TrackSend(SendAsync(viewer, same ? new[] { StreamProtocol.Alive } : frame ??= StreamProtocol.FrameMessage(jpeg),
                                   waitForReceipt: !same && viewer.Paced));
            }
        }
    }

    void TrackSend(Task send)
    {
        _sends.Add(send); // callers hold _lock, so Dispose cannot miss a send it must drain
        _ = send.ContinueWith(t => { _ = t.Exception; lock (_lock) _sends.Remove(t); }, CancellationToken.None,
                              TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    async Task SendAsync(Viewer viewer, byte[] message, bool waitForReceipt)
    {
        try
        {
            using var patience = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            patience.CancelAfter(SendPatience);
            Interlocked.Exchange(ref viewer.SentAt, System.Diagnostics.Stopwatch.GetTimestamp());
            await viewer.Channel.SendAsync(message, patience.Token).ConfigureAwait(false);
            if (!waitForReceipt) Interlocked.Exchange(ref viewer.Busy, 0); // a paced viewer's receipt frees it
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or System.Net.Sockets.SocketException)
        {
            Remove(viewer); // they closed the window or the connection died
        }
    }

    /// <summary>Asks a paced viewer for receipts, then frees it for the next frame as each one comes.</summary>
    async Task PaceAsync(Viewer viewer)
    {
        try
        {
            var send = viewer.Channel.SendAsync(new[] { StreamProtocol.Paced }, _stop.Token);
            TrackSend(send);
            await send.ConfigureAwait(false);
            Interlocked.Exchange(ref viewer.Busy, 0);
            while (true)
            {
                byte[] message = await viewer.Channel.ReceiveAsync(_stop.Token).ConfigureAwait(false);
                if (message.Length == 0) continue;
                if (message[0] == StreamProtocol.End) { Remove(viewer, cleanEnd: true); return; }
                if (message[0] != StreamProtocol.Ack) continue;
                double took = System.Diagnostics.Stopwatch.GetElapsedTime(Interlocked.Read(ref viewer.SentAt)).TotalMilliseconds;
                lock (_lock) viewer.RoundTripMs = viewer.RoundTripMs == 0 ? took : viewer.RoundTripMs * 0.6 + took * 0.4;
                Interlocked.Increment(ref _receipts);
                Interlocked.Exchange(ref viewer.Busy, 0);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or System.Net.Sockets.SocketException
                                   or InvalidDataException or System.Security.Cryptography.CryptographicException)
        {
            Remove(viewer, cleanEnd: false);
        }
    }

    void Remove(Viewer viewer, bool cleanEnd = false)
    {
        bool removed, ended;
        lock (_lock) { removed = _viewers.Remove(viewer); ended = _ended; }
        if (!removed) return;
        try { viewer.Channel.Dispose(); } catch (Exception) { }
        if (!ended) ViewerDropped?.Invoke(viewer.Key, viewer.Name, cleanEnd);
        ViewersChanged?.Invoke();
    }

    /// <summary>Tells every viewer the stream ended and hangs up.</summary>
    public async ValueTask DisposeAsync()
    {
        Viewer[] viewers;
        Task[] sends;
        lock (_lock)
        {
            if (_ended) return;
            _ended = true;
            viewers = _viewers.ToArray();
            sends = _sends.ToArray();
            _viewers.Clear();
        }
        _stop.Cancel();
        if (sends.Length > 0)
        {
            try { await Task.WhenAll(sends).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch { } // a broken stream may ignore cancellation; SecureChannel still keeps End from interleaving bytes
        }
        foreach (var viewer in viewers)
        {
            try
            {
                using var bye = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await viewer.Channel.SendAsync(new[] { StreamProtocol.End }, bye.Token).ConfigureAwait(false);
            }
            catch (Exception) { } // gone already
            try { viewer.Channel.Dispose(); } catch (Exception) { }
        }
        _stop.Dispose();
        if (viewers.Length > 0) ViewersChanged?.Invoke();
    }

    /// <summary>A quick fingerprint: the same JPEG bytes mean the same screen.</summary>
    static int Hash(byte[] data)
    {
        var hash = new HashCode();
        hash.AddBytes(data);
        return hash.ToHashCode();
    }
}
