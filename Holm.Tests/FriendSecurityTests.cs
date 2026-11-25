using System.Formats.Asn1;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WinNotch.Sharing;

namespace WinNotch.Tests;

[CollectionDefinition("Friend security", DisableParallelization = true)]
public class FriendSecurityCollection { }

[Collection("Friend security")]
public sealed class FriendSecurityTests : IDisposable
{
    readonly bool _wasInsecure = FriendIdentity.AllowInsecureServers;
    readonly Func<byte[], byte[], byte[]> _wasProtect = FriendsService.ProtectKey;
    readonly Func<byte[], byte[], byte[]> _wasUnprotect = FriendsService.UnprotectKey;

    public FriendSecurityTests()
    {
        FriendIdentity.AllowInsecureServers = false;
        FriendsService.ProtectKey = (data, _) => data.ToArray();
        FriendsService.UnprotectKey = (data, _) => data.ToArray();
    }

    public void Dispose()
    {
        FriendIdentity.AllowInsecureServers = _wasInsecure;
        FriendsService.ProtectKey = _wasProtect;
        FriendsService.UnprotectKey = _wasUnprotect;
    }

    static string Card(string key, string? server = "https://example.org")
        => "WNF1." + FriendIdentity.Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
        {
            n = "Friend",
            t = "wnf-1234567890abcdefghijklmnopqrst",
            k = key,
            s = server,
        }));

    [Theory]
    [InlineData("https://example.org")]
    [InlineData("https://example.org/ntfy/")]
    [InlineData("https://8.8.8.8")]
    [InlineData("https://[2606:4700:4700::1111]")]
    [InlineData("https://holynotch.62-83-85-236.sslip.io")]
    public void Public_https_friend_inboxes_are_accepted(string server)
    {
        using var me = FriendIdentity.Create("Me", null);
        var card = FriendIdentity.ParseFriendCode(Card(me.PublicKey, server));
        Assert.NotNull(card);
        Assert.Equal(server.TrimEnd('/'), card!.Server);
    }

    [Theory]
    [InlineData("http://example.org")]
    [InlineData("https://user:password@example.org")]
    [InlineData("https://@example.org")]
    [InlineData("https://example.org?token=secret")]
    [InlineData("https://example.org?")]
    [InlineData("https://example.org#fragment")]
    [InlineData("https://example.org#")]
    [InlineData("https://localhost")]
    [InlineData("https://127.0.0.1")]
    [InlineData("https://10.0.0.1")]
    [InlineData("https://172.16.0.1")]
    [InlineData("https://192.168.1.1")]
    [InlineData("https://169.254.169.254")]
    [InlineData("https://100.64.0.1")]
    [InlineData("https://0.0.0.0")]
    [InlineData("https://[::1]")]
    [InlineData("https://[fc00::1]")]
    [InlineData("https://[fe80::1]")]
    [InlineData("https://foo.local")]
    [InlineData("ftp://example.org")]
    public void Unsafe_friend_inbox_urls_are_rejected(string server)
    {
        using var me = FriendIdentity.Create("Me", null);
        Assert.Null(FriendIdentity.ParseFriendCode(Card(me.PublicKey, server)));
        Assert.False(FriendIdentity.TryNormalizeServer(server, out _));
    }

    [Fact]
    public void The_http_loopback_allowance_is_explicit_and_test_only()
    {
        using var me = FriendIdentity.Create("Me", null);
        Assert.Null(FriendIdentity.ParseFriendCode(Card(me.PublicKey, "http://localhost:8080")));
        FriendIdentity.AllowInsecureServers = true;
        Assert.Equal("http://localhost:8080", FriendIdentity.ParseFriendCode(Card(me.PublicKey, "http://localhost:8080"))!.Server);
        // Even the test allowance does not permit credentials, queries or fragments.
        Assert.Null(FriendIdentity.ParseFriendCode(Card(me.PublicKey, "http://user@localhost:8080")));
        Assert.Null(FriendIdentity.ParseFriendCode(Card(me.PublicKey, "http://localhost:8080?x=1")));
    }

    [Fact]
    public async Task An_inbox_post_rejects_http_and_private_destinations_before_connecting()
    {
        var friend = new Friend { Topic = "wnf-test", Server = "http://127.0.0.1:8080" };
        await Assert.ThrowsAsync<IOException>(() => FriendInbox.PostAsync(friend, "wnf1 test", CancellationToken.None));
        friend.Server = "https://169.254.169.254";
        await Assert.ThrowsAsync<IOException>(() => FriendInbox.PostAsync(friend, "wnf1 test", CancellationToken.None));
    }

    [Fact]
    public async Task Production_connections_recheck_dns_and_reject_a_loopback_answer()
    {
        FriendIdentity.AllowInsecureServers = false;
        var ex = await Assert.ThrowsAsync<IOException>(() =>
            NetSecurity.ConnectPublicAsync(new DnsEndPoint("localhost", 9), CancellationToken.None).AsTask());
        Assert.Contains("not public", ex.Message);
    }

    [Fact]
    public void P384_and_secp256k1_cards_are_rejected_and_note_open_does_not_throw()
    {
        using var me = FriendIdentity.Create("Me", null);
        using var p384 = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP384);
        string p384Key = FriendIdentity.Base64Url(p384.ExportSubjectPublicKeyInfo());
        string secp256k1Key = Secp256k1Spki(me);
        Assert.Null(FriendIdentity.ParseFriendCode(Card(p384Key)));
        Assert.Null(FriendIdentity.ParseFriendCode(Card(secp256k1Key)));

        var note = new FriendNote(FriendNoteKind.Chat, "Me", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Text: "hi", MessageId: Guid.NewGuid().ToString("N"));
        Assert.Throws<CryptographicException>(() => FriendNotes.Seal(me, new Friend { Topic = me.Topic, Key = p384Key }, note));
        Assert.Throws<CryptographicException>(() => FriendNotes.Seal(me, new Friend { Topic = me.Topic, Key = secp256k1Key }, note));
        string sealedJunk = FriendIdentity.Base64Url(new byte[32]);
        Assert.Null(FriendNotes.Open(me, $"wnf1 {p384Key} {sealedJunk}", out _));
        Assert.Null(FriendNotes.Open(me, $"wnf1 {secp256k1Key} {sealedJunk}", out _));
    }

    static string Secp256k1Spki(FriendIdentity source)
    {
        var point = source.Key.ExportParameters(false).Q;
        byte[] uncompressed = new byte[1 + point.X!.Length + point.Y!.Length];
        uncompressed[0] = 0x04;
        point.X!.CopyTo(uncompressed, 1);
        point.Y!.CopyTo(uncompressed, 1 + point.X!.Length);
        var writer = new AsnWriter(AsnEncodingRules.DER);
        writer.PushSequence();
        writer.PushSequence();
        writer.WriteObjectIdentifier("1.2.840.10045.2.1");
        writer.WriteObjectIdentifier("1.3.132.0.10"); // secp256k1, not prime256v1
        writer.PopSequence();
        writer.WriteBitString(uncompressed, 0);
        writer.PopSequence();
        return FriendIdentity.Base64Url(writer.Encode());
    }

    [Fact]
    public async Task Add_friend_boundaries_reject_a_non_p256_card_before_saving_or_networking()
    {
        using var p384 = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP384);
        var bad = new Friend
        {
            Name = "Bad curve",
            Topic = "wnf-1234567890abcdefghijklmnopqrst",
            Key = FriendIdentity.Base64Url(p384.ExportSubjectPublicKeyInfo()),
            Server = "https://example.org",
        };
        var settings = new AppSettings { Beam = false };
        var service = new FriendsService(settings, _ => Task.CompletedTask);
        service.EnsureMe();
        await Assert.ThrowsAsync<ArgumentException>(() => service.RequestAddAsync(bad, TimeSpan.Zero, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => service.AddAsync(bad, tellThem: false));
        Assert.Empty(service.Friends);
        Assert.Empty(service.Pending);
    }

    [Fact]
    public void Wrong_invite_proofs_are_throttled_per_sender_and_globally()
    {
        var limit = new FriendProofAttempts();
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        for (int i = 0; i < FriendProofAttempts.PerSender; i++) Assert.True(limit.Allow("sender", now));
        Assert.False(limit.Allow("sender", now));
        Assert.True(limit.Allow("other", now));
        Assert.True(limit.Allow("sender", now + 601));

        var global = new FriendProofAttempts();
        for (int sender = 0; sender < FriendProofAttempts.PerGlobal / FriendProofAttempts.PerSender; sender++)
            for (int i = 0; i < FriendProofAttempts.PerSender; i++) Assert.True(global.Allow($"sender-{sender}", now));
        Assert.False(global.Allow("one-too-many", now));
        Assert.True(global.Allow("after-a-minute", now + 61));
    }
}
