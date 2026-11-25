using System;
using System.Runtime.InteropServices;
using Holm.Platform;

namespace Holm.Platform.Linux;

/// <summary>
/// Pictures of one area of the X screen for a stream: the root window's pixels, made smaller by averaging (so text
/// stays readable), with the mouse pointer from XFixes drawn in. Keeps its own connection to the X server; one thread only.
/// Under Wayland only X windows show, as for <see cref="LinuxScreenPlatform.CaptureAsync"/>.
/// </summary>
sealed unsafe class LinuxScreenGrabber : IScreenGrabber
{
    const string X11 = "libX11.so.6", XFixes = "libXfixes.so.3";
    const int ZPixmap = 2;

    readonly IntPtr _display, _root;
    readonly int _x, _y, _areaWidth, _areaHeight, _width, _height;
    readonly int[] _columnStart, _rowStart; // where each picture column/row begins in the area; one more for the end
    readonly bool _pointer;

    LinuxScreenGrabber(IntPtr display, int x, int y, int areaWidth, int areaHeight, int width, int height)
    {
        _display = display;
        _root = XDefaultRootWindow(display);
        (_x, _y, _areaWidth, _areaHeight) = (x, y, areaWidth, areaHeight);
        (_width, _height) = (Math.Min(width, areaWidth), Math.Min(height, areaHeight));
        _columnStart = Bounds(_width, _areaWidth);
        _rowStart = Bounds(_height, _areaHeight);
        try { _pointer = XFixesQueryExtension(display, out _, out _); }
        catch (DllNotFoundException) { _pointer = false; }
        catch (EntryPointNotFoundException) { _pointer = false; }
    }

    public static LinuxScreenGrabber? Open(int x, int y, int areaWidth, int areaHeight, int width, int height)
    {
        if (areaWidth <= 0 || areaHeight <= 0 || width <= 0 || height <= 0) return null;
        try
        {
            IntPtr display = XOpenDisplay(IntPtr.Zero);
            return display == IntPtr.Zero ? null : new LinuxScreenGrabber(display, x, y, areaWidth, areaHeight, width, height);
        }
        catch (DllNotFoundException) { return null; }
    }

    static int[] Bounds(int count, int over)
    {
        var bounds = new int[count + 1];
        for (int i = 0; i <= count; i++) bounds[i] = (int)((long)i * over / count);
        return bounds;
    }

    public ScreenImage? Grab()
    {
        IntPtr image = IntPtr.Zero, previousHandler = IntPtr.Zero;
        try
        {
            // Xlib's default error handler exits the process. Monitor geometry can change between frames, so both
            // geometry and capture run under a scoped handler and are clamped to the current root window.
            previousHandler = XSetErrorHandler(IgnoreErrors);
            if (XGetGeometry(_display, _root, out _, out _, out _, out uint rootWidth, out uint rootHeight, out _, out _) == 0)
                return null;
            if (!TryCaptureBounds(_x, _y, _areaWidth, _areaHeight, rootWidth, rootHeight,
                    out int x0, out int y0, out int captureWidth, out int captureHeight)) return null;

            image = XGetImage(_display, _root, x0, y0, (uint)captureWidth, (uint)captureHeight, ulong.MaxValue, ZPixmap);
            if (image == IntPtr.Zero) return null;
            var info = (XImage*)image;
            if (info->BitsPerPixel != 32 || info->ByteOrder != 0 || info->Data == IntPtr.Zero
                || info->Width < captureWidth || info->Height < captureHeight || info->BytesPerLine < captureWidth * 4) return null;

            var pixels = new byte[_width * _height * 4];
            byte* data = (byte*)info->Data;
            int stride = info->BytesPerLine;
            fixed (byte* target = pixels)
            {
                for (int row = 0; row < _height; row++)
                {
                    int sy0 = Math.Min(captureHeight - 1, _rowStart[row]);
                    int sy1 = Math.Clamp(_rowStart[row + 1], sy0 + 1, captureHeight);
                    for (int column = 0; column < _width; column++)
                    {
                        int sx0 = Math.Min(captureWidth - 1, _columnStart[column]);
                        int sx1 = Math.Clamp(_columnStart[column + 1], sx0 + 1, captureWidth);
                        int b = 0, g = 0, r = 0, n = (sx1 - sx0) * (sy1 - sy0);
                        for (int sy = sy0; sy < sy1; sy++)
                        {
                            byte* p = data + sy * stride + sx0 * 4;
                            for (int sx = sx0; sx < sx1; sx++, p += 4) { b += p[0]; g += p[1]; r += p[2]; }
                        }
                        byte* q = target + (row * _width + column) * 4;
                        q[0] = (byte)(b / n); q[1] = (byte)(g / n); q[2] = (byte)(r / n); q[3] = 0xFF;
                    }
                }
                if (_pointer) DrawPointer(target);
            }
            return new ScreenImage(_width, _height, pixels);
        }
        finally
        {
            if (image != IntPtr.Zero)
            {
                var info = (XImage*)image;
                if (info->Data != IntPtr.Zero) XFree(info->Data);
                XFree(image);
            }
            if (previousHandler != IntPtr.Zero) XSetErrorHandler(previousHandler);
        }
    }

    internal static bool TryCaptureBounds(int x, int y, int areaWidth, int areaHeight, uint rootWidth, uint rootHeight,
                                          out int left, out int top, out int width, out int height)
    {
        left = Math.Max(0, x);
        top = Math.Max(0, y);
        long right = Math.Min((long)rootWidth, (long)x + areaWidth);
        long bottom = Math.Min((long)rootHeight, (long)y + areaHeight);
        width = (int)Math.Max(0, right - left);
        height = (int)Math.Max(0, bottom - top);
        return width > 0 && height > 0;
    }

    internal static bool CanDrawCursor(ushort width, ushort height, IntPtr pixels)
        => width > 0 && height > 0 && width <= 256 && height <= 256 && pixels != IntPtr.Zero;

    /// <summary>The pointer as XFixes has it (premultiplied ARGB in longs), shrunk like the windows' picture on Windows.</summary>
    void DrawPointer(byte* target)
    {
        var cursor = XFixesGetCursorImage(_display);
        if (cursor == null) return;
        try
        {
            if (!CanDrawCursor(cursor->Width, cursor->Height, (IntPtr)cursor->Pixels)) return;
            double scale = _width / (double)_areaWidth;
            // As on Windows: a smaller picture gets a smaller pointer, never smaller than 12 pixels on a 32-pixel pointer.
            double size = scale < 1 ? Math.Max(12, 32 * scale) / 32 : 1;
            int w = Math.Max(1, (int)Math.Round(cursor->Width * size)), h = Math.Max(1, (int)Math.Round(cursor->Height * size));
            int left = (int)Math.Round((cursor->X - _x) * scale - cursor->XHot * size);
            int top = (int)Math.Round((cursor->Y - _y) * scale - cursor->YHot * size);
            for (int py = 0; py < h; py++)
            {
                int ty = top + py;
                if (ty < 0 || ty >= _height) continue;
                int sy = Math.Min(cursor->Height - 1, (int)(py / size));
                for (int px = 0; px < w; px++)
                {
                    int tx = left + px;
                    if (tx < 0 || tx >= _width) continue;
                    int sx = Math.Min(cursor->Width - 1, (int)(px / size));
                    uint argb = (uint)cursor->Pixels[sy * cursor->Width + sx];
                    uint a = argb >> 24;
                    if (a == 0) continue;
                    byte* q = target + (ty * _width + tx) * 4;
                    uint keep = 255 - a;
                    q[0] = (byte)Math.Min(255, (argb & 0xFF) + q[0] * keep / 255);
                    q[1] = (byte)Math.Min(255, ((argb >> 8) & 0xFF) + q[1] * keep / 255);
                    q[2] = (byte)Math.Min(255, ((argb >> 16) & 0xFF) + q[2] * keep / 255);
                }
            }
        }
        finally { XFree((IntPtr)cursor); }
    }

    public void Dispose() => XCloseDisplay(_display);

    [StructLayout(LayoutKind.Sequential)]
    struct XImage
    {
        public int Width, Height, XOffset, Format;
        public IntPtr Data;
        public int ByteOrder, BitmapUnit, BitmapBitOrder, BitmapPad, Depth, BytesPerLine, BitsPerPixel;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct XFixesCursorImage
    {
        public short X, Y;
        public ushort Width, Height, XHot, YHot;
        public nuint CursorSerial;
        public nuint* Pixels; // one unsigned long per pixel, ARGB in its low 32 bits
        public nuint Atom;
        public IntPtr Name;
    }

    [DllImport(X11)] static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport(X11)] static extern int XCloseDisplay(IntPtr display);
    [DllImport(X11)] static extern IntPtr XDefaultRootWindow(IntPtr display);
    [DllImport(X11)] static extern int XGetGeometry(IntPtr display, IntPtr drawable, out IntPtr root, out int x, out int y, out uint width, out uint height, out uint border, out uint depth);
    [DllImport(X11)] static extern IntPtr XGetImage(IntPtr display, IntPtr drawable, int x, int y, uint width, uint height, ulong planeMask, int format);
    [DllImport(X11)] static extern int XFree(IntPtr data);
    [DllImport(XFixes)] static extern bool XFixesQueryExtension(IntPtr display, out int eventBase, out int errorBase);
    [DllImport(XFixes)] static extern XFixesCursorImage* XFixesGetCursorImage(IntPtr display);
    [DllImport(X11)] static extern IntPtr XSetErrorHandler(ErrorHandler handler);
    [DllImport(X11)] static extern IntPtr XSetErrorHandler(IntPtr handler);

    static readonly ErrorHandler IgnoreErrors = (_, _) => 0;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int ErrorHandler(IntPtr display, IntPtr error);
}
