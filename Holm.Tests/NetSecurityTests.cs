using System.Net;
using WinNotch;

namespace WinNotch.Tests;

public class NetSecurityTests
{
    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("1.1.1.1", true)]
    [InlineData("2606:4700:4700::1111", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.0.0.1", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fc00::1", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("::127.0.0.1", false)]
    [InlineData("64:ff9b::7f00:1", false)]
    [InlineData("2002:7f00:1::", false)]
    public void Only_public_addresses_are_allowed(string text, bool expected)
        => Assert.Equal(expected, NetSecurity.IsPublicAddress(IPAddress.Parse(text)));

    [Theory]
    [InlineData("example.com", true)]
    [InlineData("media.giphy.com", true)]
    [InlineData("localhost", false)]
    [InlineData("foo.local", false)]
    [InlineData("foo.internal", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("169.254.169.254", false)]
    public void Host_policy_rejects_local_names(string host, bool expected)
        => Assert.Equal(expected, NetSecurity.IsPublicHost(host));
}
