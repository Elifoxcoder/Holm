using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WinNotch.Sharing;

/// <summary>
/// Pairs two Holm apps over the internet through a "transit relay" (the public one run by the
/// magic-wormhole project). Both sides connect, name the same channel, and the relay pipes bytes between
/// them. The channel name is derived from the share code and everything sent is encrypted with a key
/// derived from it too, so the relay only ever sees encrypted data.
/// </summary>
public static class RelayLink
{
    /// <summary>HolyNotch's own transit relay (server/, on the VPS).</summary>
    public const string DefaultHost = HolyNotchServer.Host;
    public const int DefaultPort = HolyNotchServer.TransitPort;
    /// <summary>
    /// The public relay of the magic-wormhole project, which Holm uses: tried at the same time as ours, so a friend on
    /// Holm still gets our files (settable for tests; null: ours only).
    /// </summary>
    public static string? HolmHost { get; internal set; } = "transit.magic-wormhole.io";
    /// <summary>How long a pair on Holm's relay waits for one on ours (a HolyNotch partner pairs on both at once).</summary>
    internal static TimeSpan OursGrace = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Connects and waits until the other side has connected to the same channel too. On the default relay this is
    /// HolyNotch's and Holm's at once: ours wins when the partner is there (a HolyNotch partner pairs on both, and both
    /// sides then take ours), Holm's when only a Holm partner came.
    /// </summary>
    /// <exception cref="IOException">The relay refused or dropped the connection.</exception>
    public static Task<Stream> ConnectAsync(string host, int port, byte[] token, CancellationToken ct)
        => host == DefaultHost && port == DefaultPort && HolmHost is { } holm
            ? ConnectEitherAsync(token, holm, ct)
            : ConnectOneAsync(host, port, token, ct);

    static async Task<Stream> ConnectEitherAsync(byte[] token, string holmHost, CancellationToken ct)
    {
        using var both = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var ours = ConnectOneAsync(DefaultHost, DefaultPort, token, both.Token);
        var theirs = ConnectOneAsync(holmHost, DefaultPort, token, both.Token);
        Stream? chosen = null;
        try
        {
            var first = await Task.WhenAny(ours, theirs).ConfigureAwait(false);
            if (first == ours && ours.IsCompletedSuccessfully) return chosen = ours.Result;
            if (first == theirs && theirs.IsCompletedSuccessfully)
            {
                var grace = Task.Delay(OursGrace, ct);
                if (await Task.WhenAny(ours, grace).ConfigureAwait(false) == ours && ours.IsCompletedSuccessfully) return chosen = ours.Result;
                return chosen = theirs.Result;
            }
            // One relay failed (down, unreachable): the other one alone; when both fail, ours says why.
            var other = first == ours ? theirs : ours;
            try { return chosen = await other.ConfigureAwait(false); }
            catch (Exception) when (other == theirs) { await ours.ConfigureAwait(false); throw; }
        }
        finally
        {
            both.Cancel(); // the other one stops waiting; if it paired anyway, its connection closes
            foreach (var t in new[] { ours, theirs })
                _ = t.ContinueWith(done => { if (done.IsCompletedSuccessfully && !ReferenceEquals(done.Result, chosen)) done.Result.Dispose(); },
                                   CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    static async Task<Stream> ConnectOneAsync(string host, int port, byte[] token, CancellationToken ct)
    {
        if (token.Length != 32) throw new ArgumentException("Tokens are 32 bytes.", nameof(token));
        var client = await ReachAsync(host, port, ct).ConfigureAwait(false);
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
