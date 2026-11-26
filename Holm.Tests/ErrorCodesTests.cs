using System.Net.Sockets;
using System.Security.Cryptography;

namespace WinNotch.Tests;

public class ErrorCodesTests
{
    [Fact]
    public void Each_kind_of_failure_has_its_own_code()
    {
        Assert.Equal(ErrorCodes.PartnerNoShow, ErrorCodes.Of(new OperationCanceledException()).Code);
        Assert.Equal(ErrorCodes.RelayUnreachable, ErrorCodes.Of(new SocketException((int)SocketError.HostNotFound)).Code);
        Assert.Equal(ErrorCodes.Dropped, ErrorCodes.Of(new SocketException((int)SocketError.ConnectionReset)).Code);
        Assert.Equal(ErrorCodes.Crypto, ErrorCodes.Of(new AuthenticationTagMismatchException()).Code);
        Assert.Equal(ErrorCodes.Protocol, ErrorCodes.Of(new InvalidDataException()).Code);
        Assert.Equal(ErrorCodes.InboxRetired, ErrorCodes.Of(new IOException("This inbox is on Holm's server, which HolyNotch no longer uses.")).Code);
        Assert.Equal(ErrorCodes.Unknown, ErrorCodes.Of(new InvalidOperationException()).Code);
    }

    [Fact]
    public void An_inbox_refusal_names_its_HTTP_status()
    {
        var (code, reason) = ErrorCodes.Of(new IOException("holynotch.example refused the note (429)"));
        Assert.Equal(ErrorCodes.InboxFailed, code);
        Assert.Contains("429", reason);
        Assert.StartsWith("Error HN-I01 · ", ErrorCodes.Describe(new IOException("x refused the note (503)")));
    }
}
