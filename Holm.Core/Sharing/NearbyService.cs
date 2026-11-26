using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WinNotch.Sharing;

/// <summary>Another Holm on the same network. Discovery and its ephemeral ECDH do not authenticate an account or person.</summary>
public sealed record NearbyPeer(Guid Id, string Name, IPEndPoint Endpoint, DateTime LastSeen)
{
    public bool IsAuthenticated => false;
}

/// <summary>
/// Finds other Holm apps on the local network (a small UDP broadcast) and accepts their transfers
/// (TCP). Each connection agrees on a fresh key (ECDH P-256), so nothing crosses the network in clear text.
/// Nothing polls: there is one announcement a minute, and others answer when someone asks.
/// </summary>
public sealed class NearbyService : IDisposable
{
    public const int DefaultDiscoveryPort = 47811;
    const string App = "WinNotch"; // the app's former name, kept so Holm and older versions still find each other
    static readonly TimeSpan PeerLifetime = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(20);
    internal const int MaxConcurrentHandshakes = 4;

    readonly Guid _id = Guid.NewGuid();
    readonly string _name;
    readonly int _discoveryPort;
    readonly TimeSpan _handshakeTimeout;
    readonly IReadOnlyList<IPEndPoint> _extraTargets;
    readonly ConcurrentDictionary<Guid, NearbyPeer> _peers = new();
    readonly SemaphoreSlim _incomingSlot = new(1, 1);
    readonly SemaphoreSlim _handshakes = new(MaxConcurrentHandshakes, MaxConcurrentHandshakes);
    UdpClient? _udp;
    TcpListener? _tcp;
    CancellationTokenSource? _cts;
    int _tcpPort;
    IPEndPoint[]? _targets; // where to announce; looked up again when an address changes

    /// <summary>The peer list changed. Raised on a worker thread.</summary>
    public event Action? PeersChanged;

    /// <summary>
    /// Handles an incoming transfer (normally <see cref="FileTransfer.ReceiveAsync"/> with the user's answer).
    /// Only one runs at a time; others are turned away while it does.
    /// </summary>
    public Func<SecureChannel, string, CancellationToken, Task>? Incoming { get; set; }

    /// <param name="extraTargets">Also announce to these endpoints (used by the tests, which run two services on one machine).</param>
    public NearbyService(string name, int discoveryPort = DefaultDiscoveryPort, IReadOnlyList<IPEndPoint>? extraTargets = null)
        : this(name, discoveryPort, extraTargets, HandshakeTimeout) { }

    internal NearbyService(string name, int discoveryPort, IReadOnlyList<IPEndPoint>? extraTargets, TimeSpan handshakeTimeout)
    {
        _name = name;
        _discoveryPort = discoveryPort;
        _handshakeTimeout = handshakeTimeout > TimeSpan.Zero ? handshakeTimeout : HandshakeTimeout;
        _extraTargets = extraTargets ?? Array.Empty<IPEndPoint>();
    }

    public bool IsRunning => _cts != null;
    public Guid Id => _id;

    public IReadOnlyList<NearbyPeer> Peers
        => _peers.Values.Where(p => DateTime.UtcNow - p.LastSeen < PeerLifetime).OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    public void Start()
    {
        if (_cts != null) return;
        var cts = new CancellationTokenSource();
        try
        {
            _tcp = new TcpListener(IPAddress.Any, 0);
            _tcp.Start(4);
            _tcpPort = ((IPEndPoint)_tcp.LocalEndpoint).Port;

            _udp = new UdpClient(AddressFamily.InterNetwork) { ExclusiveAddressUse = false, EnableBroadcast = true };
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, _discoveryPort));
        }
        catch
        {
            _tcp?.Stop(); _tcp = null;
            _udp?.Dispose(); _udp = null;
            cts.Dispose();
            throw;
        }
        _cts = cts;
        NetworkChange.NetworkAddressChanged += OnAddressChanged;
        _ = ListenAsync(cts.Token);
        _ = AcceptAsync(cts.Token);
        _ = AnnounceAsync(cts.Token);
    }

    public void Stop()
    {
        if (_cts == null) return;
        try { Broadcast("bye"); } catch { }
        NetworkChange.NetworkAddressChanged -= OnAddressChanged;
        _targets = null;
        _cts.Cancel();
        _cts.Dispose();
        _cts = null;
        _tcp?.Stop(); _tcp = null;
        _udp?.Dispose(); _udp = null;
        if (!_peers.IsEmpty) { _peers.Clear(); PeersChanged?.Invoke(); }
    }

    public void Dispose() => Stop();

    /// <summary>Asks everyone to answer now, and waits a moment for the answers.</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (_cts == null) return;
        Broadcast("query");
        await Task.Delay(700, ct).ConfigureAwait(false);
    }

    /// <summary>Connects to a peer for sending; the returned channel is encrypted.</summary>
    public static async Task<SecureChannel> ConnectAsync(NearbyPeer peer, CancellationToken ct)
    {
        var client = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            await client.ConnectAsync(peer.Endpoint, timeout.Token).ConfigureAwait(false);
            var stream = new NetworkStream(client.Client, ownsSocket: true);
            var key = await AgreeKeyAsync(stream, initiator: true, timeout.Token).ConfigureAwait(false);
            return new SecureChannel(stream, key, initiator: true);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    // ───────────── discovery ─────────────

    sealed record Packet(string App, int V, string T, Guid Id, string Name, int Port);

    async Task AnnounceAsync(CancellationToken ct)
    {
        try
        {
            Broadcast("query"); // introduces us and asks the others to say hello
            await Task.Delay(TimeSpan.FromMilliseconds(900), ct).ConfigureAwait(false);
            Broadcast("query"); // a second packet in case the first Wi-Fi broadcast was dropped
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                Broadcast("query");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Diag.Log("Nearby announce stopped: " + ex.Message); }
    }

    void Broadcast(string type)
    {
        var udp = _udp;
        if (udp == null) return;
        var bytes = Serialize(type);
        foreach (var target in Targets())
        {
            try { udp.Send(bytes, bytes.Length, target); }
            catch (SocketException) { } // an interface that went away
        }
    }

    byte[] Serialize(string type)
        => JsonSerializer.SerializeToUtf8Bytes(new Packet(App, 1, type, _id, _name, _tcpPort));

    void OnAddressChanged(object? sender, EventArgs e)
    {
        _targets = null;
        try { Broadcast("query"); } catch { }
    }

    /// <summary>
    /// The broadcast address of every IPv4 network we're on, plus the extra targets. Listing the network adapters
    /// is the expensive part of the announcement, so the list is kept until Windows reports a changed address.
    /// </summary>
    IPEndPoint[] Targets() => _targets ??= FindTargets().ToArray();

    IEnumerable<IPEndPoint> FindTargets()
    {
        var seen = new HashSet<IPAddress> { IPAddress.Broadcast };
        yield return new IPEndPoint(IPAddress.Broadcast, _discoveryPort);
        NetworkInterface[] interfaces;
        try { interfaces = NetworkInterface.GetAllNetworkInterfaces(); } catch { interfaces = Array.Empty<NetworkInterface>(); }
        foreach (var nic in interfaces)
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            UnicastIPAddressInformationCollection addresses;
            try { addresses = nic.GetIPProperties().UnicastAddresses; } catch { continue; }
            foreach (var address in addresses)
            {
                if (address.Address.AddressFamily != AddressFamily.InterNetwork || address.IPv4Mask == null) continue;
                var ip = address.Address.GetAddressBytes();
                var mask = address.IPv4Mask.GetAddressBytes();
                if (mask.All(b => b == 0)) continue;
                var broadcast = new byte[4];
                for (int i = 0; i < 4; i++) broadcast[i] = (byte)(ip[i] | ~mask[i]);
                var target = new IPAddress(broadcast);
                if (seen.Add(target)) yield return new IPEndPoint(target, _discoveryPort);
            }
        }
        foreach (var extra in _extraTargets) yield return extra;
    }

    async Task ListenAsync(CancellationToken ct)
    {
        var udp = _udp!;
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try { result = await udp.ReceiveAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; } // e.g. ICMP "port unreachable" from an earlier send

            Packet? packet;
            try { packet = result.Buffer.Length <= 2048 ? JsonSerializer.Deserialize<Packet>(result.Buffer) : null; }
            catch (JsonException) { continue; }
            if (packet == null || packet.App != App || packet.V != 1 || packet.Id == _id) continue;

            if (packet.T == "bye")
            {
                // A different host must not be able to remove a live peer merely by repeating its random id.
                if (_peers.TryGetValue(packet.Id, out var known) && known.Endpoint.Address.Equals(result.RemoteEndPoint.Address)
                    && _peers.TryRemove(packet.Id, out _)) PeersChanged?.Invoke();
                continue;
            }
            if (packet.Port is <= 0 or > 65535) continue;

            string name = FileTransfer.CleanDisplayName(packet.Name, 40);
            var peer = new NearbyPeer(packet.Id, name.Length > 0 ? name : "Holm", new IPEndPoint(result.RemoteEndPoint.Address, packet.Port), DateTime.UtcNow);
            if (!TryUpdatePeer(_peers, peer, out bool isNew)) continue;
            // The same device started again (an update, a restart): same name and address, new id. Its old entry goes
            // now, not after three minutes, or it shows twice. Display names never authenticate or merge identities.
            if (isNew)
                foreach (var old in _peers.Values.Where(p => p.Id != packet.Id && p.Name == peer.Name && p.Endpoint.Address.Equals(peer.Endpoint.Address)).ToList())
                    _peers.TryRemove(old.Id, out _);
            if (isNew) PeersChanged?.Invoke();

            if (packet.T == "query")
            {
                var reply = Serialize("hello");
                try { await udp.SendAsync(reply, reply.Length, result.RemoteEndPoint).ConfigureAwait(false); }
                catch (Exception) { }
            }
        }
    }

    /// <summary>A live id is bound to the address that first announced it; only its port and name may change.</summary>
    internal static bool TryUpdatePeer(ConcurrentDictionary<Guid, NearbyPeer> peers, NearbyPeer peer, out bool isNew)
    {
        isNew = false;
        while (true)
        {
            if (peers.TryGetValue(peer.Id, out var known))
            {
                if (!known.Endpoint.Address.Equals(peer.Endpoint.Address))
                {
                    // Roaming is fine after the old announcement is no longer live; a live id stays bound to its IP.
                    if (DateTime.UtcNow - known.LastSeen < PeerLifetime || !peers.TryUpdate(peer.Id, peer, known)) return false;
                    isNew = true;
                    return true;
                }
                if (peers.TryUpdate(peer.Id, peer, known)) return true;
            }
            else
            {
                if (peers.TryAdd(peer.Id, peer)) { isNew = true; return true; }
            }
        }
    }

    // ───────────── incoming transfers ─────────────

    async Task AcceptAsync(CancellationToken ct)
    {
        var listener = _tcp!;
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; }
            if (!_handshakes.Wait(0)) { client.Dispose(); continue; }
            _ = HandleAsync(client, ct);
        }
    }

    async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        bool handshakeHeld = true;
        try
        {
            using (client)
            {
                bool hasSlot = false;
                try
                {
                    var handler = Incoming;
                    if (handler == null) return;
                    client.NoDelay = true;
                    using var stream = new NetworkStream(client.Client, ownsSocket: false);
                    using var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    handshake.CancelAfter(_handshakeTimeout);
                    byte[] key = await AgreeKeyAsync(stream, initiator: false, handshake.Token).ConfigureAwait(false);
                    await using var channel = new SecureChannel(stream, key, initiator: false);

                    // Do not let a silent connection take the only slot. Validate and buffer the Offer first; the normal
                    // FileTransfer handler receives that same frame, so neither the wire format nor the app API changes.
                    var first = await channel.ReceiveAndBufferAsync(handshake.Token).ConfigureAwait(false);
                    FileTransfer.ParseOfferFrame(first);
                    _handshakes.Release();
                    handshakeHeld = false;
                    if (!await _incomingSlot.WaitAsync(0, handshake.Token).ConfigureAwait(false)) return;
                    hasSlot = true;
                    handshake.CancelAfter(Timeout.InfiniteTimeSpan); // the bounded handshake is over; a valid prompt may wait
                    string from = ((IPEndPoint)client.Client.RemoteEndPoint!).Address.ToString();
                    await handler(channel, from, ct).ConfigureAwait(false);
                }
                catch (Exception ex) { Diag.Log("Incoming transfer failed: " + ex.Message); }
                finally { if (hasSlot) _incomingSlot.Release(); }
            }
        }
        finally { if (handshakeHeld) _handshakes.Release(); }
    }

    /// <summary>
    /// Ephemeral ECDH: both sides send a fresh public key; the shared key also covers both public keys,
    /// so it is bound to this one connection.
    /// </summary>
    internal static async Task<byte[]> AgreeKeyAsync(Stream stream, bool initiator, CancellationToken ct)
    {
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        byte[] mine = ecdh.PublicKey.ExportSubjectPublicKeyInfo();
        var lengthPrefix = new byte[] { (byte)(mine.Length >> 8), (byte)mine.Length };
        await stream.WriteAsync(lengthPrefix, ct).ConfigureAwait(false);
        await stream.WriteAsync(mine, ct).ConfigureAwait(false);

        var header = new byte[2];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        int length = (header[0] << 8) | header[1];
        if (length is < 32 or > 512) throw new InvalidDataException("Bad key.");
        var theirs = new byte[length];
        await stream.ReadExactlyAsync(theirs, ct).ConfigureAwait(false);

        using var other = ECDiffieHellman.Create();
        ECParameters parameters;
        try
        {
            other.ImportSubjectPublicKeyInfo(theirs, out _);
            parameters = other.ExportParameters(includePrivateParameters: false);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException or PlatformNotSupportedException)
        {
            throw new InvalidDataException("Bad key.");
        }
        // KeySize alone also accepts other 256-bit curves. The installed wire protocol is specifically NIST P-256.
        if (other.KeySize != 256 || !string.Equals(parameters.Curve.Oid.Value, "1.2.840.10045.3.1.7", StringComparison.Ordinal))
            throw new InvalidDataException("Bad key.");

        byte[] transcript = SHA256.HashData(initiator ? Concat(mine, theirs) : Concat(theirs, mine));
        return ecdh.DeriveKeyFromHash(other.PublicKey, HashAlgorithmName.SHA256, Encoding.ASCII.GetBytes("WinNotch LAN v1"), transcript);
    }

    static byte[] Concat(byte[] a, byte[] b)
    {
        var all = new byte[a.Length + b.Length];
        a.CopyTo(all, 0);
        b.CopyTo(all, a.Length);
        return all;
    }
}
