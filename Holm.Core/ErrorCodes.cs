using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace WinNotch;

/// <summary>
/// Short codes for what went wrong with a transfer, a stream or a connection, shown with the message ("Error HN-R02 ·
/// the other side didn't come to the relay") so a report says exactly which step failed. The phone app uses the same
/// codes (mobile/lib/net/errors.dart). The full exception goes to diag.log.
/// </summary>
public static class ErrorCodes
{
    /// <summary>The relay or the HolyNotch server couldn't be reached (DNS, no internet, port 4001 blocked).</summary>
    public const string RelayUnreachable = "HN-R01";
    /// <summary>The relay was reached, but the other side never came to it within the wait.</summary>
    public const string PartnerNoShow = "HN-R02";
    /// <summary>The connection dropped in the middle (Wi-Fi, sleep, the relay closed it).</summary>
    public const string Dropped = "HN-R03";
    /// <summary>The friend's inbox couldn't take the invitation (server down, refused, rate-limited).</summary>
    public const string InboxFailed = "HN-I01";
    /// <summary>The friend's inbox is on Holm's server, which HolyNotch doesn't use any more.</summary>
    public const string InboxRetired = "HN-I02";
    /// <summary>The encrypted data didn't check out (a wrong code, or both sides on different keys).</summary>
    public const string Crypto = "HN-C01";
    /// <summary>The other side sent something HolyNotch doesn't understand (another version, damaged data).</summary>
    public const string Protocol = "HN-P01";
    /// <summary>Reading or writing a file on this computer failed (disk full, no permission).</summary>
    public const string LocalFile = "HN-F01";
    /// <summary>The screen couldn't be read for a stream.</summary>
    public const string ScreenCapture = "HN-S01";
    /// <summary>Anything else: the exception's type names it.</summary>
    public const string Unknown = "HN-X00";

    static readonly Regex HttpStatus = new(@"\((\d{3})\)", RegexOptions.CultureInvariant);

    /// <summary>The code and a few words for <paramref name="ex"/>, e.g. ("HN-I01", "the inbox answered 429").</summary>
    public static (string Code, string Reason) Of(Exception ex)
    {
        switch (ex)
        {
            case OperationCanceledException or TimeoutException:
                return (PartnerNoShow, "the other side didn't come to the relay in time");
            case SocketException socket:
                return socket.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted
                    ? (Dropped, "the connection was reset")
                    : (RelayUnreachable, "couldn't reach the relay (" + socket.SocketErrorCode + ")");
            case AuthenticationTagMismatchException or CryptographicException:
                return (Crypto, "the encrypted data didn't check out");
            case InvalidDataException:
                return (Protocol, "unexpected data from the other side");
            case EndOfStreamException:
                return (Dropped, "the connection was closed");
            case HttpRequestException http:
                return (InboxFailed, http.StatusCode is { } status ? $"the server answered {(int)status}" : "couldn't reach the server");
            case UnauthorizedAccessException:
                return (LocalFile, "no permission to write the file");
            case IOException io when io.Message.Contains("Holm's server", StringComparison.Ordinal):
                return (InboxRetired, "their inbox is still on Holm's server");
            case IOException io when io.Message.StartsWith("Couldn't reach", StringComparison.Ordinal)
                                     || io.Message.Contains("refused the note", StringComparison.Ordinal):
                return (InboxFailed, HttpStatus.Match(io.Message) is { Success: true } m ? $"the inbox answered {m.Groups[1].Value}" : "couldn't reach their inbox");
            case IOException io when io.InnerException is SocketException inner:
                return Of(inner);
            case IOException io when io.HResult is unchecked((int)0x80070070) or unchecked((int)0x80070027):
                return (LocalFile, "the disk is full");
            case IOException:
                return (Dropped, "the connection dropped");
            default:
                return (Unknown, ex.GetType().Name);
        }
    }

    /// <summary>"Error HN-R02 · the other side didn't come to the relay in time", for a notch line.</summary>
    public static string Describe(Exception ex)
    {
        var (code, reason) = Of(ex);
        return $"Error {code} · {reason}";
    }
}
