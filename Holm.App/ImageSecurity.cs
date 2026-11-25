using System;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;
using WinNotch;

namespace Holm.App;

/// <summary>Header-first, bounded image decoding for pictures received from peers and media sessions.</summary>
internal static class ImageSecurity
{
    public const int MaxImageBytes = 8 * 1024 * 1024;
    public const int MaxAvatarBytes = 1500;
    public const int MaxAvatarSide = 512;
    public const long MaxAvatarPixels = MaxAvatarSide * MaxAvatarSide;
    public const int MaxStreamSide = 8192;
    public const long MaxStreamPixels = 32L * 1024 * 1024;
    public const int MaxStreamBytes = 1024 * 1024;
    public const int MaxCoverSide = 4096;
    public const long MaxCoverPixels = 16L * 1024 * 1024;

    public static bool TryInspect(byte[]? bytes, int maxSide, long maxPixels, out SKImageInfo info)
    {
        info = default;
        if (bytes == null || bytes.Length == 0 || bytes.Length > MaxImageBytes || maxSide <= 0 || maxPixels <= 0) return false;
        try
        {
            using var data = SKData.CreateCopy(bytes);
            using var codec = SKCodec.Create(data);
            if (codec == null) return false;
            info = codec.Info;
            return info.Width > 0 && info.Height > 0 && info.Width <= maxSide && info.Height <= maxSide
                && (long)info.Width * info.Height <= maxPixels;
        }
        catch (Exception ex) when (ex is not (StackOverflowException or AccessViolationException))
        {
            return false;
        }
    }

    /// <summary>Decodes at the requested size (or the original size when already small), never at an unchecked size.</summary>
    public static SKBitmap? DecodeScaled(byte[]? bytes, int maxSide, long maxPixels, int targetWidth = 0)
    {
        if (bytes == null || bytes.Length == 0 || bytes.Length > MaxImageBytes) return null;
        try
        {
            using var data = SKData.CreateCopy(bytes);
            using var codec = SKCodec.Create(data);
            if (codec == null) return null;
            var source = codec.Info;
            if (source.Width <= 0 || source.Height <= 0 || source.Width > maxSide || source.Height > maxSide
                || (long)source.Width * source.Height > maxPixels) return null;

            double scale = targetWidth > 0 ? Math.Min(1.0, targetWidth / (double)source.Width) : 1.0;
            int width = Math.Max(1, (int)Math.Round(source.Width * scale));
            int height = Math.Max(1, (int)Math.Round(source.Height * scale));
            var output = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            var bitmap = new SKBitmap(output);
            if (bitmap.GetPixels() == IntPtr.Zero) { bitmap.Dispose(); return null; }
            var result = codec.GetPixels(output, bitmap.GetPixels(), new SKCodecOptions(0));
            if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput)) { bitmap.Dispose(); return null; }
            return bitmap;
        }
        catch (Exception ex) when (ex is not (StackOverflowException or AccessViolationException))
        {
            Diag.Log("Image rejected: " + ex.GetType().Name);
            return null;
        }
    }

    public static Bitmap? Avatar(string? base64)
    {
        if (base64 == null || base64.Length > MaxAvatarBytes * 2) return null;
        try
        {
            byte[] bytes = Convert.FromBase64String(base64);
            if (bytes.Length > MaxAvatarBytes) return null;
            using var source = DecodeScaled(bytes, MaxAvatarSide, MaxAvatarPixels, MaxAvatarSide);
            return source == null ? null : ToWriteable(source);
        }
        catch (Exception ex) when (ex is not (StackOverflowException or AccessViolationException))
        {
            return null;
        }
    }

    public static Bitmap? StreamFrame(byte[]? bytes)
    {
        if (bytes == null || bytes.Length > MaxStreamBytes) return null;
        try
        {
            using var source = DecodeScaled(bytes, MaxStreamSide, MaxStreamPixels);
            return source == null ? null : ToWriteable(source);
        }
        catch (Exception ex) when (ex is not (StackOverflowException or AccessViolationException))
        {
            Diag.Log("Stream image rejected: " + ex.GetType().Name);
            return null;
        }
    }

    public static WriteableBitmap ToWriteable(SKBitmap source)
    {
        var bitmap = new WriteableBitmap(new PixelSize(source.Width, source.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var frame = bitmap.Lock();
        int row = Math.Min(frame.RowBytes, source.RowBytes);
        var buffer = new byte[row];
        for (int y = 0; y < source.Height; y++)
        {
            Marshal.Copy(source.GetPixels() + y * source.RowBytes, buffer, 0, row);
            Marshal.Copy(buffer, 0, frame.Address + y * frame.RowBytes, row);
        }
        return bitmap;
    }
}
