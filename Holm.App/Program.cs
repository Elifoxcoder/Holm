using Avalonia;
using System;
using System.IO;

namespace Holm.App;

class Program
{
    internal static bool JustInstalled { get; private set; }
    internal static Action? WakeRunningInstance;

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Core stays OS-free; the platform supplies the euid check before any XDG runtime path is accepted.
        WinNotch.PrivateStorage.RuntimeDirectoryOwner = Holm.Platform.UnixIdentityPlatform.IsOwnedByCurrentUser;
        // As in the WPF app: Holm --diag writes what it does to diag.log next to the settings.
        // Or always on a computer with a file named diag.on next to the settings (a tester's, a developer's).
        WinNotch.Diag.Enabled = Array.IndexOf(args, "--diag") >= 0
            || System.IO.File.Exists(System.IO.Path.Combine(WinNotch.Diag.Folder, "diag.on"));
        JustInstalled = Array.IndexOf(args, "--installed") >= 0;
#if HOLM_WINDOWS
        // When the published single-file HolyNotch.exe is run from Downloads (or any non-install folder),
        // it installs itself to %LOCALAPPDATA%\Programs\HolyNotch, creates Start Menu & Desktop shortcuts,
        // registers in Windows Installed Apps, and launches the installed copy.
        if (Holm.Platform.Windows.WindowsSelfInstaller.HandleStartup(args)) return;
#endif
        // One Holm at a time, the same way on every system: the first one holds a lock file, a second start finds it
        // taken and wakes the running one before quitting. The lock goes with the process, also when it crashes.
        // Restarting (a new design or install): the old Holm is still letting go of the lock, so wait for it a few seconds.
        bool restart = Array.IndexOf(args, "--restart") >= 0;
        var instance = SingleInstance();
        for (int i = 0; instance == null && restart && i < 50; i++)
        {
            System.Threading.Thread.Sleep(200);
            instance = SingleInstance();
        }
        using var _ = instance;
        if (instance == null)
        {
#if HOLM_WINDOWS
            SignalRunningInstance();
#endif
            return;
        }
#if HOLM_WINDOWS
        if (WinNotch.Diag.DevFolder == null && Holm.Platform.Windows.WindowsStoreInstance.IsRunning())
        {
            WinNotch.Diag.Log("The Microsoft Store copy of Holm is already running.");
            return;
        }
        using var wakeStop = StartWakeListener();
#endif
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

#if HOLM_WINDOWS
    const string WakeEventName = @"Local\HolyNotch_Wake";

    static void SignalRunningInstance()
    {
        try
        {
            using var ev = System.Threading.EventWaitHandle.OpenExisting(WakeEventName);
            ev.Set();
        }
        catch (Exception) { }
    }

    static IDisposable? StartWakeListener()
    {
        try
        {
            var ev = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset, WakeEventName);
            var cts = new System.Threading.CancellationTokenSource();
            var thread = new System.Threading.Thread(() =>
            {
                var handles = new System.Threading.WaitHandle[] { ev, cts.Token.WaitHandle };
                while (!cts.IsCancellationRequested)
                {
                    if (System.Threading.WaitHandle.WaitAny(handles) == 0)
                    {
                        try { WakeRunningInstance?.Invoke(); } catch (Exception) { }
                    }
                }
            }) { IsBackground = true, Name = "HolyNotch wake" };
            thread.Start();
            return new WakeHandle(ev, cts);
        }
        catch (Exception) { return null; }
    }

    sealed class WakeHandle(System.Threading.EventWaitHandle ev, System.Threading.CancellationTokenSource cts) : IDisposable
    {
        public void Dispose() { cts.Cancel(); ev.Dispose(); cts.Dispose(); }
    }
#endif

    static IDisposable? SingleInstance()
    {
        try
        {
            WinNotch.Diag.EnsureFolder();
            // Not the WPF app's mutex: on Windows the port runs next to an installed Holm.
            return WinNotch.PrivateStorage.OpenOrCreatePrivate(Path.Combine(WinNotch.Diag.Folder, "holm-app.lock"), FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException) { return null; } // held by the running one
        catch (UnauthorizedAccessException) { return new MemoryStream(); } // can't tell: start rather than not at all
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .With(Gpu ? new Win32PlatformOptions() : new Win32PlatformOptions { RenderingMode = new[] { Win32RenderingMode.Software } })
            .With(Gpu ? new X11PlatformOptions() : new X11PlatformOptions { RenderingMode = new[] { X11RenderingMode.Software } })
            .LogToTrace();

    /// <summary>
    /// Holm draws on the CPU (Skia's raster backend), not the graphics card. Its windows are small and mostly still, and
    /// the GPU path costs far more than it saves: ANGLE/Direct3D, the graphics driver and Skia's GPU caches held about
    /// 290 MB of private memory in an idle Holm on Windows (340 MB with them, 51 MB without, tools/Measure-Footprint.ps1).
    /// HOLM_GPU=1 brings the graphics card back, should a system draw badly without it.
    /// </summary>
    static bool Gpu => Environment.GetEnvironmentVariable("HOLM_GPU") == "1";
}
