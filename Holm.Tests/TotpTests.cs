using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using WinNotch;
using WinNotch.Sharing;
using Xunit;

namespace Holm.Tests;

public sealed class TotpTests
{
    [Fact]
    public void Rfc6238Sha1VectorMatches()
    {
        // RFC 6238 test secret: ASCII "12345678901234567890" -> Base32 GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ
        var entry = new TotpEntry
        {
            Title = "RFC",
            Secret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ",
            Digits = 8,
            Period = 30,
            Algorithm = "SHA1",
        };
        var (code59, next59, _, _) = TotpCode.Generate(entry, unixSeconds: 59);
        Assert.Equal("94287082", code59);
        Assert.NotEqual(code59, next59);
        var (code1111111109, _, _, _) = TotpCode.Generate(entry, unixSeconds: 1111111109);
        Assert.Equal("07081804", code1111111109);
    }

    [Fact]
    public void ParsesOtpAuthUriAndFormatsSixDigits()
    {
        var parsed = TotpParser.ParseOtpAuthUri("otpauth://totp/GitHub:elias?secret=JBSWY3DPEHPK3PXP&issuer=GitHub&digits=6&period=30");
        Assert.NotNull(parsed);
        Assert.Equal("GitHub", parsed!.Title);
        Assert.Equal("elias", parsed.Account);
        Assert.Equal("JBSWY3DPEHPK3PXP", parsed.Secret);
        Assert.Equal("123 456", TotpCode.Format("123456"));
    }

    [Fact]
    public void ParsesGoogleAuthenticatorMigrationProtobuf()
    {
        // Build a valid MigrationPayload with 1 OtpParameters entry: secret="Hello!", name="elias@test", issuer="Google", algo=1(SHA1), digits=1(6), type=2(TOTP)
        byte[] secret = Encoding.ASCII.GetBytes("1234567890");
        byte[] name = Encoding.UTF8.GetBytes("elias@test");
        byte[] issuer = Encoding.UTF8.GetBytes("Google");
        using var inner = new MemoryStream();
        inner.WriteByte((1 << 3) | 2); inner.WriteByte((byte)secret.Length); inner.Write(secret);
        inner.WriteByte((2 << 3) | 2); inner.WriteByte((byte)name.Length); inner.Write(name);
        inner.WriteByte((3 << 3) | 2); inner.WriteByte((byte)issuer.Length); inner.Write(issuer);
        inner.WriteByte((4 << 3) | 0); inner.WriteByte(1);
        inner.WriteByte((5 << 3) | 0); inner.WriteByte(1);
        inner.WriteByte((6 << 3) | 0); inner.WriteByte(2);
        byte[] innerBytes = inner.ToArray();

        using var outer = new MemoryStream();
        outer.WriteByte((1 << 3) | 2); outer.WriteByte((byte)innerBytes.Length); outer.Write(innerBytes);
        string b64 = Convert.ToBase64String(outer.ToArray());

        var list = TotpParser.ParseGoogleMigration($"otpauth-migration://offline?data={Uri.EscapeDataString(b64)}");
        Assert.Single(list);
        Assert.Equal("Google", list[0].Title);
        Assert.Equal("elias@test", list[0].Account);
        Assert.Equal(TotpCode.EncodeBase32(secret), list[0].Secret);
    }

    [Fact]
    public void ClipboardDetectionCatchesTotpWithoutFalsePositives()
    {
        Assert.NotNull(TotpParser.DetectInClipboard("otpauth://totp/Test?secret=JBSWY3DPEHPK3PXP"));
        Assert.NotNull(TotpParser.DetectInClipboard("JBSW Y3DP EHPK 3PXP"));
        Assert.Null(TotpParser.DetectInClipboard("hello world this is normal text"));
        Assert.Null(TotpParser.DetectInClipboard("550e8400-e29b-41d4-a716-446655440000"));
        Assert.Null(TotpParser.DetectInClipboard("deadbeef0123456789abcdef01234567"));
    }

    [Fact]
    public void TotpStoreEncryptsAndMergesDeletes()
    {
        string path = Path.Combine(Path.GetTempPath(), "holm-totp-test-" + Guid.NewGuid().ToString("N") + ".json");
        byte[] key = RandomNumberGenerator.GetBytes(32);
        try
        {
            var store = new TotpStore(path, key);
            var added = store.Add(new TotpEntry { Title = "GitHub", Secret = "JBSWY3DPEHPK3PXP" });
            Assert.NotNull(added);
            Assert.StartsWith("V1.", File.ReadAllText(path));

            var reloaded = new TotpStore(path, key);
            Assert.Single(reloaded.Active);
            Assert.Equal("GitHub", reloaded.Active[0].Title);

            var tomb = reloaded.Delete(added!.Id);
            Assert.NotNull(tomb);
            Assert.Empty(reloaded.Active);

            Assert.True(store.Merge(new[] { tomb! }));
            Assert.Empty(store.Active);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ChatStoreEditUpdatesMessageAndMarksEdited()
    {
        var store = new ChatStore(null);
        store.Add("peer", new FriendMessage { Id = "m1", Mine = true, Text = "helo", Sent = 100, State = ChatState.Sent }, unread: false);
        Assert.True(store.Edit("peer", "m1", "hello"));
        var msg = store.Find("peer", "m1");
        Assert.NotNull(msg);
        Assert.Equal("hello", msg!.Text);
        Assert.True(msg.Edited);
    }
}
