using WinNotch.Sharing;

namespace WinNotch.Tests;

/// <summary>
/// Two friends on the real HolyNotch server (run by hand: Category=Online): the file offer goes through the inbox and
/// both sides meet on the transit relay, as a beam to a friend does in the app.
/// </summary>
[Trait("Category", "Online")]
public class LiveFriendTransferTests
{
    static Task Ui(Action a) { lock (typeof(LiveFriendTransferTests)) a(); return Task.CompletedTask; }

    static async Task Until(Func<bool> done, string what, int seconds = 30)
    {
        for (int i = 0; i < seconds * 10 && !done(); i++) await Task.Delay(100);
        Assert.True(done(), what);
    }

    [Fact]
    public async Task A_file_offer_reaches_a_friend_and_both_meet_on_the_transit_relay()
    {
        Assert.Equal(HolyNotchServer.Url, FriendIdentity.RelayServer);
        FriendsService.ProtectKey = (data, _) => data.ToArray();
        FriendsService.UnprotectKey = (data, _) => data.ToArray();
        var alice = new FriendsService(new AppSettings { Beam = true }, Ui);
        var bob = new FriendsService(new AppSettings { Beam = true }, Ui);
        byte[]? offered = null;
        bob.FilesOffered += (_, secret) => offered = secret;
        try
        {
            alice.EnsureMe(); bob.EnsureMe();
            alice.Restart(); bob.Restart();
            await Until(() => alice.Me!.Server == HolyNotchServer.Url && bob.Me!.Server == HolyNotchServer.Url, "both inboxes on the HolyNotch server");
            Assert.True(await alice.AddAsync(bob.Me!.AsFriend(), tellThem: false));
            Assert.True(await bob.AddAsync(alice.Me!.AsFriend(), tellThem: false));
            await Task.Delay(1500); // both followers connected

            byte[] secret = ShareCode.NewSecret();
            await alice.InviteAsync(alice.Find(bob.Me!.PublicKey)!, secret, CancellationToken.None);
            await Until(() => offered != null, "bob's notch heard the file offer");
            Assert.Equal(secret, offered);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var a = RelayLink.ConnectAsync(RelayLink.DefaultHost, RelayLink.DefaultPort, ShareCode.RelayToken(secret), cts.Token);
            var b = RelayLink.ConnectAsync(RelayLink.DefaultHost, RelayLink.DefaultPort, ShareCode.RelayToken(secret), cts.Token);
            await using var sa = await a;
            await using var sb = await b;
            await sa.WriteAsync("hello"u8.ToArray(), cts.Token);
            await sa.FlushAsync(cts.Token);
            var buffer = new byte[5];
            await sb.ReadExactlyAsync(buffer, cts.Token);
            Assert.Equal("hello"u8.ToArray(), buffer);
        }
        finally { alice.Stop(); bob.Stop(); }
    }
}
