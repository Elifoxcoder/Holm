using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WinNotch.Sharing;

/// <summary>
/// Pairs two HolyNotch apps over the internet through a "transit relay" (HolyNotch's own, on the same
/// protocol as magic-wormhole's). Both sides connect, name the same channel, and the relay pipes bytes between
/// them. The channel name is derived from the share code and everything sent is encrypted with a key
/// derived from it too, so the relay only ever sees encrypted data.
/// </summary>
public static class RelayLink
{
    /// <summary>HolyNotch's own transit relay (server/, on the VPS).</summary>
    public const string DefaultHost = HolyNotchServer.Host;
    public const int DefaultPort = HolyNotchServer.TransitPort;

    /// <summary>
    /// Connects and waits until the other side has connected to the same channel too. Only HolyNotch's own relay:
    /// magic-wormhole's public relay (Holm's) is no longer used.
    /// </summary>
    /// <exception cref="IOException">The relay refused or dropped the connection.</exception>
    public static async Task<Stream> ConnectAsync(string host, int port, byte[] token, CancellationToken ct)
    {
        if (token.Length != 32) throw new ArgumentException("Tokens are 32 bytes.", nameof(token));
        TcpClient? client = null;
        try
        {
            client = await ReachAsync(host, port, ct).ConfigureAwait(false);
        }
        catch (IOException) when (port == DefaultPort && !IsLoopbackHost(host))
        {
            // Corporate/campus firewalls often block raw TCP 4001; fall back to WSS on HTTPS port 443.
            Diag.Log($"TCP relay {host}:{port} unreachable, falling back to WSS/443");
            return await ConnectWebSocketAsync(host, token, ct).ConfigureAwait(false);
        }
        try
        {
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            var stream = new NetworkStream(client.Client, ownsSocket: true);

            string side = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
            string handshake = $"please relay {Convert.ToHexString(token).ToLowerInvariant()} for side {side}\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(handshake), ct).ConfigureAwait(false);

            // The relay answers only once both sides are there, which can take minutes.
            string reply = await ReadLineAsync(stream, ct).ConfigureAwait(false);
            if (reply != "ok") throw new IOException("The relay said: " + reply);
            return stream;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    static bool IsLoopbackHost(string host)
        => host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip));

    static async Task<Stream> ConnectWebSocketAsync(string host, byte[] token, CancellationToken ct)
    {
        string side = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        string tokenHex = Convert.ToHexString(token).ToLowerInvariant();
        var uri = new Uri($"wss://{host}/transit/{tokenHex}?side={side}");
        var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(25);
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(TimeSpan.FromSeconds(10));
            await ws.ConnectAsync(uri, connectCts.Token).ConfigureAwait(false);
            var stream = new WebSocketStream(ws);
            string reply = await ReadLineAsync(stream, ct).ConfigureAwait(false);
            if (reply != "ok") { stream.Dispose(); throw new IOException("The WSS relay said: " + reply); }
            return stream;
        }
        catch (Exception ex) when (ex is not IOException and not OperationCanceledException)
        {
            ws.Dispose();
            throw new IOException("The HTTPS fallback relay couldn't be reached.", ex);
        }
        catch
        {
            ws.Dispose();
            throw;
        }
    }

    /// <summary>Minimal binary Stream adapter over a ClientWebSocket for SecureChannel.</summary>
    sealed class WebSocketStream : Stream
    {
        readonly ClientWebSocket _ws;
        readonly SemaphoreSlim _writeLock = new(1, 1);
        byte[] _pending = Array.Empty<byte>();
        int _pendingOffset, _pendingCount;

        public WebSocketStream(ClientWebSocket ws) => _ws = ws;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_pendingCount > 0)
            {
                int take = Math.Min(buffer.Length, _pendingCount);
                _pending.AsMemory(_pendingOffset, take).CopyTo(buffer);
                _pendingOffset += take;
                _pendingCount -= take;
                return take;
            }
            if (buffer.Length == 0) return 0;
            try
            {
                var res = await _ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                if (res.MessageType == WebSocketMessageType.Close) return 0;
                return res.Count;
            }
            catch (WebSocketException ex)
            {
                throw new IOException("WSS relay read failed.", ex);
            }
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => await ReadAsync(buffer.AsMemory(offset, count), ct).ConfigureAwait(false);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await _ws.SendAsync(buffer, WebSocketMessageType.Binary, endOfMessage: true, ct).ConfigureAwait(false);
            }
            catch (WebSocketException ex)
            {
                throw new IOException("WSS relay write failed.", ex);
            }
            finally { _writeLock.Release(); }
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => await WriteAsync(buffer.AsMemory(offset, count), ct).ConfigureAwait(false);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _ws.Abort(); } catch { }
                _ws.Dispose();
                _writeLock.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>How long one address of the relay gets to answer before the next one is tried.</summary>
    internal static TimeSpan AddressPatience = TimeSpan.FromSeconds(5);

    /// <summary>
    /// A TCP connection to the relay, IPv4 addresses first, each given <see cref="AddressPatience"/>. Trying the host name
    /// as a whole went through its addresses one by one, IPv6 first: a network that silently drops IPv6 spent the whole
    /// wait for a friend on that one address, and the send failed every time.
    /// </summary>
    /// <exception cref="IOException">No address of the relay could be reached.</exception>
    static async Task<TcpClient> ReachAsync(string host, int port, CancellationToken ct)
    {
        IPAddress[] addresses;
        try { addresses = IPAddress.TryParse(host, out var ip) ? new[] { ip } : await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false); }
        catch (SocketException ex) { throw new IOException("The relay's address couldn't be found.", ex); }
        Exception? last = null;
        foreach (var address in addresses.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1))
        {
            var client = new TcpClient(address.AddressFamily) { NoDelay = true };
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attempt.CancelAfter(AddressPatience);
            try
            {
                await client.ConnectAsync(address, port, attempt.Token).ConfigureAwait(false);
                return client;
            }
            catch (Exception ex) when (ex is SocketException || ex is OperationCanceledException && !ct.IsCancellationRequested)
            {
                client.Dispose();
                last = ex;
            }
        }
        throw new IOException("The relay couldn't be reached.", last);
    }

    static async Task<string> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        var line = new StringBuilder();
        var one = new byte[1];
        while (line.Length < 200)
        {
            int read = await stream.ReadAsync(one, ct).ConfigureAwait(false);
            if (read == 0) throw new IOException("The relay closed the connection.");
            if (one[0] == (byte)'\n') return line.ToString();
            line.Append((char)one[0]);
        }
        throw new IOException("The relay sent something unexpected.");
    }

    /// <summary>
    /// Keeps trying until the other side shows up, the time runs out or it is cancelled
    /// (the relay may drop a connection that waits for a long time).
    /// </summary>
    public static async Task<Stream> ConnectWithRetryAsync(string host, int port, byte[] token, TimeSpan patience, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(patience);
        while (true)
        {
            try { return await ConnectAsync(host, port, token, deadline.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or SocketException && !deadline.IsCancellationRequested)
            {
                Diag.Log("Relay connection lost, retrying: " + ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(3), deadline.Token).ConfigureAwait(false);
            }
        }
    }
}
