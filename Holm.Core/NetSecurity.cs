using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace WinNotch;

/// <summary>Network destination checks shared by remote artwork and friend inbox clients.</summary>
internal static class NetSecurity
{
    /// <summary>Whether an address is safe for a client-initiated request to an Internet service.</summary>
    public static bool IsPublicAddress(IPAddress? address)
    {
        if (address == null) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] b = address.GetAddressBytes();
            uint n = ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
            return !IPAddress.IsLoopback(address) && !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.Broadcast)
                && !(n >> 24 == 10) && !(n >> 24 == 127)                    // loopback / RFC 1918
                && !(n >> 16 is >= 0xAC10 and <= 0xAC1F)                   // 172.16.0.0/12
                && !(n >> 16 == 0xA9FE)                                     // 169.254.0.0/16 (link-local/metadata)
                && !(n >> 16 == 0xC0A8)                                     // 192.168.0.0/16
                && !(n >> 24 == 100 && (b[1] & 0xC0) == 64)                 // 100.64.0.0/10 (CGNAT)
                && !(n >> 16 == 0xC612)                                     // 198.18.0.0/15 (benchmarking)
                && !(n >> 24 is 0 or >= 224)                               // "this" network, multicast, reserved
                && !(n >> 24 == 192 && b[1] == 0 && b[2] == 0)             // 192.0.0.0/24
                && !(n >> 24 is 192 && b[1] == 0 && b[2] == 2)             // documentation
                && !(n >> 24 == 198 && (b[1] == 51 ? b[2] == 100 : b[1] == 19)) // 198.51.100/24, 198.19/16
                && !(n >> 24 == 203 && b[1] == 0 && b[2] == 113);          // documentation
        }

        byte[] v6 = address.GetAddressBytes();
        if (v6.AsSpan(0, 12).IndexOfAnyExcept((byte)0) < 0)
            return IsPublicAddress(new IPAddress(v6.AsSpan(12, 4).ToArray())); // deprecated IPv4-compatible form
        return !IPAddress.IsLoopback(address) && !address.Equals(IPAddress.IPv6Any) && !address.Equals(IPAddress.IPv6None)
            && !address.IsIPv6LinkLocal && !address.IsIPv6SiteLocal && !address.IsIPv6Multicast
            && (v6[0] & 0xFE) != 0xFC                                      // unique local
            && !(v6[0] == 0x20 && v6[1] == 0x01 && v6[2] == 0x0D && v6[3] == 0xB8) // documentation
            && !(v6[0] == 0x00 && v6[1] == 0x64 && v6[2] == 0xFF && v6[3] == 0x9B) // NAT64 (including local-use /48)
            && !(v6[0] == 0x20 && v6[1] == 0x02)                            // 6to4
            && !(v6[0] == 0x20 && v6[1] == 0x01 && v6[2] == 0x00);        // Teredo
    }

    /// <summary>Cheap host-name policy before DNS; resolved addresses must still be checked at connect time.</summary>
    public static bool IsPublicHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        string name = host.Trim().TrimEnd('.');
        if (name.StartsWith('[') && name.EndsWith(']')) name = name[1..^1];
        if (IPAddress.TryParse(name, out var literal)) return IsPublicAddress(literal);
        if (!name.Contains('.')) return false;
        string lower = name.ToLowerInvariant();
        return !lower.EndsWith(".localhost", StringComparison.Ordinal) && lower != "localhost"
            && !lower.EndsWith(".local", StringComparison.Ordinal) && !lower.EndsWith(".lan", StringComparison.Ordinal)
            && !lower.EndsWith(".internal", StringComparison.Ordinal) && !lower.EndsWith(".home", StringComparison.Ordinal)
            && !lower.EndsWith(".test", StringComparison.Ordinal) && !lower.EndsWith(".invalid", StringComparison.Ordinal);
    }

    /// <summary>An HTTP handler that pins connections to checked public IPs and never follows redirects.</summary>
    public static SocketsHttpHandler PublicHandler() => new()
    {
        ConnectTimeout = TimeSpan.FromSeconds(10),
        AllowAutoRedirect = false,
        ConnectCallback = async (context, cancellationToken) => await ConnectPublicAsync(context.DnsEndPoint, cancellationToken).ConfigureAwait(false),
    };

    /// <summary>
    /// Resolves and connects only to public addresses. Connecting to the checked address ourselves prevents DNS
    /// rebinding between validation and connection; TLS still receives the original host name from HttpClient.
    /// </summary>
    public static async ValueTask<NetworkStream> ConnectPublicAsync(DnsEndPoint endpoint, CancellationToken cancellationToken)
    {
        if (!IsPublicHost(endpoint.Host)) throw new IOException("The request destination is not public.");
        IPAddress[] addresses = await Dns.GetHostAddressesAsync(endpoint.Host, cancellationToken).ConfigureAwait(false);
        List<Exception> errors = new();
        foreach (IPAddress address in addresses.Where(IsPublicAddress))
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, endpoint.Port), cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException or IOException)
            {
                errors.Add(ex);
                socket.Dispose();
                if (cancellationToken.IsCancellationRequested) throw;
            }
        }
        throw new IOException("The request destination has no reachable public address.", errors.Count == 1 ? errors[0] : null);
    }
}
