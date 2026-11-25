using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace WinNotch;

/// <summary>Reads an untrusted stream without trusting its declared length or buffering past a hard cap.</summary>
internal static class BoundedIo
{
    public static byte[] ReadAllBytes(Stream source, long maximum)
    {
        if (maximum < 0) throw new ArgumentOutOfRangeException(nameof(maximum));
        if (source.CanSeek && source.Length > maximum) throw new IOException("The stream is too large.");
        using var memory = new MemoryStream();
        byte[] buffer = new byte[64 * 1024];
        while (true)
        {
            int read = source.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            if (memory.Length + read > maximum) throw new IOException("The stream is too large.");
            memory.Write(buffer, 0, read);
        }
        return memory.ToArray();
    }

    public static async Task<byte[]> ReadAllBytesAsync(Stream source, long maximum, CancellationToken cancellationToken = default)
    {
        if (maximum < 0) throw new ArgumentOutOfRangeException(nameof(maximum));
        if (source.CanSeek && source.Length > maximum) throw new IOException("The stream is too large.");
        using var memory = new MemoryStream();
        byte[] buffer = new byte[64 * 1024];
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (memory.Length + read > maximum) throw new IOException("The stream is too large.");
            memory.Write(buffer, 0, read);
        }
        return memory.ToArray();
    }
}
