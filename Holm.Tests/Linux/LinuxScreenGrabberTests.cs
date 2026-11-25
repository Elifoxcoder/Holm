using System;
using Holm.Platform.Linux;

namespace WinNotch.Tests.Linux;

public class LinuxScreenGrabberTests
{
    [Fact]
    public void Capture_bounds_are_clamped_to_the_current_root()
    {
        Assert.True(LinuxScreenGrabber.TryCaptureBounds(-10, 20, 200, 100, 1920, 1080, out int x, out int y, out int w, out int h));
        Assert.Equal((0, 20, 190, 100), (x, y, w, h));
        Assert.True(LinuxScreenGrabber.TryCaptureBounds(1900, 1070, 200, 100, 1920, 1080, out x, out y, out w, out h));
        Assert.Equal((1900, 1070, 20, 10), (x, y, w, h));
        Assert.False(LinuxScreenGrabber.TryCaptureBounds(2000, 10, 100, 100, 1920, 1080, out _, out _, out _, out _));
    }

    [Fact]
    public void Empty_or_unbounded_cursors_are_not_read()
    {
        Assert.False(LinuxScreenGrabber.CanDrawCursor(0, 32, IntPtr.Zero));
        Assert.False(LinuxScreenGrabber.CanDrawCursor(32, 0, IntPtr.Zero));
        Assert.False(LinuxScreenGrabber.CanDrawCursor(32, 32, IntPtr.Zero));
        Assert.False(LinuxScreenGrabber.CanDrawCursor(ushort.MaxValue, 32, new IntPtr(1)));
        Assert.True(LinuxScreenGrabber.CanDrawCursor(32, 32, new IntPtr(1)));
    }
}
