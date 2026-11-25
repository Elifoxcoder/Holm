using System.Runtime.CompilerServices;
using WinNotch.Sharing;

namespace WinNotch.Tests;

/// <summary>Tests talk to their own fake servers only: not to Holm's relays, which HolyNotch also uses alongside its own.</summary>
static class NoHolmServers
{
    [ModuleInitializer]
    internal static void Init()
    {
        FriendIdentity.HolmRelayServer = null;
        RelayLink.HolmHost = null;
    }
}
