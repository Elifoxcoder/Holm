using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Holm.Platform;

namespace Holm.Platform.Linux;

/// <summary>
/// Handy on Linux: found as an installed program (package or AppImage). When it listens is told by its use of the
/// microphone, which the app sees anyway; its own overlay stays, as Linux has no common way to move another app's window.
/// </summary>
public sealed class LinuxDictationPlatform : IDictationPlatform
{
    public string? FindHandy()
    {
        // An explicit pin is the user's choice; unlike a folder scan it may point at a manually placed AppImage.
        if (Environment.GetEnvironmentVariable("HOLM_HANDY_PATH") is { Length: > 0 } pinned && TryPinnedPath(pinned) is { } explicitPath)
            return explicitPath;
        foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin").Split(Path.PathSeparator))
            if (Path.IsPathFullyQualified(folder) && File.Exists(Path.Combine(folder, "handy"))) return Path.GetFullPath(Path.Combine(folder, "handy"));
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        // Never execute whatever happens to be in Downloads. Only installed locations are scanned automatically.
        foreach (string folder in InstalledAppImageFolders(home))
        {
            try
            {
                if (!Directory.Exists(folder)) continue;
                foreach (string image in Directory.GetFiles(folder, "Handy*.AppImage").OrderByDescending(f => f))
                    if (!IsLink(image)) return Path.GetFullPath(image);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    internal static IEnumerable<string> InstalledAppImageFolders(string home)
        => new[] { Path.Combine(home, "Applications"), Path.Combine(home, ".local", "bin") };

    static string? TryPinnedPath(string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path)) return null;
            string full = Path.GetFullPath(path);
            return File.Exists(full) ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException) { return null; }
    }

    static bool IsLink(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.LinkTarget != null || (file.Attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    public bool ReportsListening => false;
    public event Action<bool>? ListeningChanged { add { } remove { } }
    public void Start(Action<Action> post, Action<Action> invoke, Func<bool> onUiThread, Func<Action, Action<bool>> ticker) { }
    public void Stop() { }
    public bool OverlayOff() => false;

    public void Launch(string program)
    {
        if (Process.GetProcessesByName("handy").Any()) return;
        // Re-resolve at launch time: a stale path or an unpinned file planted in Downloads is never auto-executed.
        if (FindHandy() is not { } trusted || TryPinnedPath(program) is not { } supplied || !string.Equals(supplied, trusted, StringComparison.Ordinal)) return;
        try { Process.Start(new ProcessStartInfo(trusted, "--start-hidden") { UseShellExecute = false })?.Dispose(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException) { WinNotch.Diag.Log("Handy didn't start: " + ex.Message); }
    }

    /// <summary>Handy comes as a package or an AppImage: its release page is where to choose.</summary>
    public Task<bool> InstallAsync(CancellationToken ct) => Task.FromResult(false);
}
