using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WinNotch.Sharing;
using Xunit;

namespace WinNotch.Tests;

/// <summary>HolyNotch never contacts Holm's servers (its relay, ntfy.sh, magic-wormhole's transit relay).</summary>
public class RetiredServerTests
{
    [Fact]
    public void Holms_inbox_servers_are_retired_and_ours_is_not()
    {
        Assert.True(FriendIdentity.IsRetired("https://relay.holmnotch.ch"));
        Assert.True(FriendIdentity.IsRetired("https://NTFY.sh/"));
        Assert.False(FriendIdentity.IsRetired(FriendIdentity.RelayServer));
        Assert.False(FriendIdentity.IsRetired(HolyNotchServer.Url));
    }

    [Fact]
    public async Task A_note_for_a_friend_on_Holms_relay_fails_before_any_request()
    {
        var friend = new Friend { Name = "Old", Topic = "wnf-old", Key = "k", Server = "https://relay.holmnotch.ch" };
        var ex = await Assert.ThrowsAsync<IOException>(() => FriendInbox.PostAsync(friend, "note", CancellationToken.None));
        Assert.Contains("Holm's server", ex.Message);
    }

    [Fact]
    public void Transfers_use_only_HolyNotchs_relay()
    {
        Assert.Equal(HolyNotchServer.Host, RelayLink.DefaultHost);
        Assert.Null(typeof(RelayLink).GetProperty("HolmHost"));
    }
}
