using System.Collections.Concurrent;
using System.Formats.Asn1;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using WinNotch.Sharing;

namespace WinNotch.Tests;

/// <summary>Two notches on "the same network": two services on this machine that announce to each other over loopback.</summary>
public class NearbyServiceTests
{
    static (NearbyService A, NearbyService B) Pair(string nameA, string nameB, TimeSpan? handshakeTimeout = null)
    {
        int portA = Random.Shared.Next(48000, 49000), portB = portA + 1;
        var a = new NearbyService(nameA, portA, new[] { new IPEndPoint(IPAddress.Loopback, portB) });
        var b = handshakeTimeout is { } timeout
            ? new NearbyService(nameB, portB, new[] { new IPEndPoint(IPAddress.Loopback, portA) }, timeout)
            : new NearbyService(nameB, portB, new[] { new IPEndPoint(IPAddress.Loopback, portA) });
        return (a, b);
    }

    static byte[] OfferFrame(string name = "Lena") => new byte[] { 1 }.Concat(JsonSerializer.SerializeToUtf8Bytes(new
    {
        Sender = "Noah", Files = new[] { new { Name = name, Size = 1 } },
    })).ToArray();

    static async Task<NearbyPeer> WaitForPeerAsync(NearbyService service, string name)
    {
        for (int i = 0; i < 100; i++)
        {
            var peer = service.Peers.FirstOrDefault(p => p.Name == name);
            if (peer != null) return peer;
            await Task.Delay(100);
        }
        throw new TimeoutException($"{name} was never discovered.");
    }

    [Fact]
    public async Task Services_find_each_other_and_forget_on_stop()
    {
        var (a, b) = Pair("Noah", "Lena");
        using (a)
        using (b)
        {
            a.Start();
            b.Start();
            var lena = await WaitForPeerAsync(a, "Lena");
            var noah = await WaitForPeerAsync(b, "Noah");

            Assert.Equal(b.Id, lena.Id);
            Assert.Equal(a.Id, noah.Id);
            Assert.DoesNotContain(a.Peers, p => p.Id == a.Id); // never lists itself

            b.Stop(); // says goodbye
            for (int i = 0; i < 50 && a.Peers.Any(p => p.Id == lena.Id); i++) await Task.Delay(100);
            Assert.DoesNotContain(a.Peers, p => p.Id == lena.Id);
        }
    }

    [Fact]
    public async Task A_file_goes_from_one_notch_to_the_other_encrypted()
    {
        using var source = new TempFolder();
        using var downloads = new TempFolder();
        var content = RandomNumberGenerator.GetBytes(200_000);
        string path = source.File("Holiday.zip", content);
        var (a, b) = Pair("Noah", "Lena");
        var received = new TaskCompletionSource<IReadOnlyList<string>?>();
        b.Incoming = async (channel, from, ct) =>
        {
            try { received.TrySetResult(await FileTransfer.ReceiveAsync(channel, _ => Task.FromResult(true), downloads.Path, null, Net.Timeout())); }
            catch (Exception ex) { received.TrySetException(ex); }
        };

        using (a)
        using (b)
        {
            a.Start();
            b.Start();
            var lena = await WaitForPeerAsync(a, "Lena");

            await using var channel = await NearbyService.ConnectAsync(lena, Net.Timeout());
            bool accepted = await FileTransfer.SendAsync(channel, "Noah", new[] { path }, null, Net.Timeout());
            var saved = await received.Task.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.True(accepted);
            Assert.Equal(content, File.ReadAllBytes(saved!.Single()));
        }
    }

    [Fact]
    public async Task Only_one_incoming_transfer_at_a_time()
    {
        var (a, b) = Pair("Noah", "Lena");
        var firstArrived = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        b.Incoming = async (channel, from, ct) => { firstArrived.TrySetResult(); await release.Task; };

        using (a)
        using (b)
        {
            a.Start();
            b.Start();
            var lena = await WaitForPeerAsync(a, "Lena");

            await using var first = await NearbyService.ConnectAsync(lena, Net.Timeout());
            await first.SendAsync(OfferFrame(), Net.Timeout());
            await firstArrived.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // A second valid Offer is turned away only once the first valid transfer owns the slot.
            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                await using var second = await NearbyService.ConnectAsync(lena, Net.Timeout(10));
                await second.SendAsync(OfferFrame(), Net.Timeout(10));
                await second.ReceiveAsync(Net.Timeout(10));
            });
            release.SetResult();
        }
    }

    [Fact]
    public async Task Key_agreement_rejects_secp256k1_even_though_it_is_256_bit()
        => await AssertRejectedKeyAsync(Secp256k1Spki());

    static byte[] Secp256k1Spki()
    {
        byte[] point = Convert.FromHexString("04"
            + "79BE667EF9DCBBAC55A06295CE870B07029BFCDB2DCE28D959F2815B16F81798"
            + "483ADA7726A3C4655DA4FBFC0E1108A8FD17B448A68554199C47D08FFB10D4B8");
        var writer = new AsnWriter(AsnEncodingRules.DER);
        writer.PushSequence();
        writer.PushSequence();
        writer.WriteObjectIdentifier("1.2.840.10045.2.1");
        writer.WriteObjectIdentifier("1.3.132.0.10");
        writer.PopSequence();
        writer.WriteBitString(point, 0);
        writer.PopSequence();
        return writer.Encode();
    }

    [Fact]
    public async Task Key_agreement_rejects_a_malformed_public_key()
        => await AssertRejectedKeyAsync(RandomNumberGenerator.GetBytes(32));

    static async Task AssertRejectedKeyAsync(byte[] publicKey)
    {
        var streams = await Net.LoopbackPairAsync();
        using var receiverStream = streams.A;
        using var senderStream = streams.B;
        var receive = NearbyService.AgreeKeyAsync(receiverStream, initiator: false, Net.Timeout());
        byte[] length = { (byte)(publicKey.Length >> 8), (byte)publicKey.Length };
        await senderStream.WriteAsync(length);
        await senderStream.WriteAsync(publicKey);
        await Assert.ThrowsAsync<InvalidDataException>(() => receive);
    }

    [Fact]
    public void A_live_peer_id_cannot_be_moved_to_another_address()
    {
        var peers = new ConcurrentDictionary<Guid, NearbyPeer>();
        var id = Guid.NewGuid();
        var first = new NearbyPeer(id, "Lena", new IPEndPoint(IPAddress.Loopback, 4000), DateTime.UtcNow);
        Assert.True(NearbyService.TryUpdatePeer(peers, first, out bool isNew));
        Assert.True(isNew);
        Assert.False(first.IsAuthenticated);

        var moved = first with { Endpoint = new IPEndPoint(IPAddress.Parse("127.0.0.2"), 5000), Name = "Mallory" };
        Assert.False(NearbyService.TryUpdatePeer(peers, moved, out isNew));
        Assert.Same(first, peers[id]);

        var refreshed = first with { Endpoint = new IPEndPoint(IPAddress.Loopback, 5001), LastSeen = DateTime.UtcNow };
        Assert.True(NearbyService.TryUpdatePeer(peers, refreshed, out isNew));
        Assert.False(isNew);
        Assert.Same(refreshed, peers[id]);
    }

    [Fact]
    public async Task Concurrent_silent_handshakes_are_bounded_and_release_their_permits()
    {
        var (a, b) = Pair("Noah", "Lena", TimeSpan.FromMilliseconds(500));
        b.Incoming = (_, _, _) => Task.CompletedTask;
        using (a)
        using (b)
        {
            a.Start();
            b.Start();
            var lena = await WaitForPeerAsync(a, "Lena");
            var silent = new List<TcpClient>();
            try
            {
                for (int i = 0; i < NearbyService.MaxConcurrentHandshakes; i++)
                {
                    var client = new TcpClient();
                    await client.ConnectAsync(lena.Endpoint);
                    silent.Add(client);
                }
                await Task.Delay(100);
                using (var extra = new TcpClient())
                {
                    await extra.ConnectAsync(lena.Endpoint);
                    var stream = extra.GetStream();
                    Assert.Equal(0, await stream.ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
                }
            }
            finally
            {
                foreach (var client in silent) client.Dispose();
            }

            await Task.Delay(600); // the bounded handshakes timed out and returned their permits
            using (var probe = new TcpClient())
            {
                await probe.ConnectAsync(lena.Endpoint);
                var header = new byte[2];
                await probe.GetStream().ReadExactlyAsync(header).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
                int length = (header[0] << 8) | header[1];
                Assert.InRange(length, 32, 512); // a new handshake was admitted and sent its P-256 key
            }
        }
    }

    [Fact]
    public async Task A_silent_handshake_times_out_without_blocking_the_next_valid_transfer()
    {
        using var source = new TempFolder();
        using var downloads = new TempFolder();
        string path = source.File("hello.txt", "hello"u8.ToArray());
        var (a, b) = Pair("Noah", "Lena", TimeSpan.FromMilliseconds(300));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new TaskCompletionSource<IReadOnlyList<string>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        b.Incoming = async (channel, from, ct) =>
        {
            entered.TrySetResult();
            try { received.TrySetResult(await FileTransfer.ReceiveAsync(channel, _ => Task.FromResult(true), downloads.Path, null, ct)); }
            catch (Exception ex) { received.TrySetException(ex); }
        };

        using (a)
        using (b)
        {
            a.Start();
            b.Start();
            var lena = await WaitForPeerAsync(a, "Lena");
            using (var silent = new TcpClient())
            {
                await silent.ConnectAsync(lena.Endpoint);
                await Task.Delay(600); // no ECDH key and no Offer
                Assert.False(entered.Task.IsCompleted);
                var stream = silent.GetStream();
                var buffer = new byte[256];
                int bytesRead = 0;
                while (true)
                {
                    int read = await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
                    if (read == 0) break;
                    bytesRead += read;
                }
                Assert.InRange(bytesRead, 32, 512); // it sent its key, then the silent handshake was closed
            }

            await using var channel = await NearbyService.ConnectAsync(lena, Net.Timeout());
            Assert.True(await FileTransfer.SendAsync(channel, "Noah", new[] { path }, null, Net.Timeout()));
            var saved = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("hello", File.ReadAllText(saved!.Single()));
        }
    }
}
