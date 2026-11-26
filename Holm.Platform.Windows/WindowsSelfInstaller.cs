using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading;
using Microsoft.Win32;
using WinNotch;

namespace Holm.Platform.Windows;

/// <summary>
/// Turns the published single-file HolyNotch.exe into a zero-UAC per-user Windows installer:
/// running it from Downloads (or any other folder) copies it to %LOCALAPPDATA%\Programs\HolyNotch\HolyNotch.exe,
/// creates Start Menu and Desktop shortcuts, registers it in Windows Installed Apps (with --uninstall support),
/// and launches the installed copy. Auto-updates in place keep working because the installed file is still the
/// same single-file HolyNotch.exe.
/// </summary>
public static class WindowsSelfInstaller
{
    const string AppName = "HolyNotch";
    const string ExeName = "HolyNotch.exe";
    const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\HolyNotch";

    public static string InstallDirectory
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);

    public static string InstalledExePath => Path.Combine(InstallDirectory, ExeName);

    static string StartMenuShortcutPath
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk");

    static string DesktopShortcutPath
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk");

    [UnconditionalSuppressMessage("SingleFile", "IL3000", Justification = "An empty assembly location identifies the published single-file executable.")]
    static bool IsPublishedSingleFileExe(out string currentPath)
    {
        currentPath = Environment.ProcessPath ?? "";
        return OperatingSystem.IsWindows()
            && Assembly.GetExecutingAssembly().Location.Length == 0
            && currentPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            && File.Exists(currentPath);
    }

    /// <summary>
    /// Handles --uninstall or automatic self-installation when launched outside the standard install directory.
    /// Returns true if the current process should exit (either uninstalled, or handed off to the installed copy).
    /// </summary>
    public static bool HandleStartup(string[] args)
    {
        if (Array.IndexOf(args, "--uninstall") >= 0)
        {
            Uninstall();
            return true;
        }

        if (Array.IndexOf(args, "--portable") >= 0
            || Array.IndexOf(args, "--no-install") >= 0
            || Environment.GetEnvironmentVariable("HOLM_PORTABLE") == "1"
            || Diag.DevFolder != null)
        {
            return false;
        }

        if (!IsPublishedSingleFileExe(out string currentPath)) return false;

        string targetPath = InstalledExePath;
        if (PathsEqual(currentPath, targetPath))
        {
            // Already running from the installed location: quietly keep shortcuts and version metadata current.
            TryRefreshRegistration(targetPath, createDesktopShortcut: false);
            return false;
        }

        try
        {
            InstallAndLaunch(currentPath, targetPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or COMException)
        {
            // Never prevent the app from running if installation fails for any reason.
            Diag.Log("Self-install failed, running in place: " + ex.Message);
            return false;
        }
    }

    static void InstallAndLaunch(string sourceExe, string targetExe)
    {
        Directory.CreateDirectory(InstallDirectory);
        StopOtherHolyNotchInstances();

        // An older copy (a download from weeks ago, or what the old autostart entry still starts) never replaces a newer
        // installed one: that one has updated itself meanwhile. It is only started.
        if (!File.Exists(targetExe) || FileVersion(sourceExe) >= FileVersion(targetExe))
        {
            string staged = targetExe + ".new";
            File.Copy(sourceExe, staged, overwrite: true);
            File.Move(staged, targetExe, overwrite: true);
        }
        else Diag.Log("Self-install: the installed HolyNotch is newer, starting it instead");

        TryRefreshRegistration(targetExe, createDesktopShortcut: true);

        Process.Start(new ProcessStartInfo(targetExe, "--restart --installed")
        {
            UseShellExecute = false,
            WorkingDirectory = InstallDirectory,
        });
    }

    static Version FileVersion(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
        }
        catch (Exception) { return new Version(0, 0); }
    }

    /// <summary>
    /// "Start with Windows" names the exe it was switched on from (often Downloads). Once installed, it must start the
    /// installed copy, or every sign-in would start the old download instead.
    /// </summary>
    static void MoveAutostart(string targetExe)
    {
        using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
        if (run?.GetValue("Holm") is string cmd && !cmd.Contains(targetExe, StringComparison.OrdinalIgnoreCase))
            run.SetValue("Holm", $"\"{targetExe}\"");
    }

    static void TryRefreshRegistration(string targetExe, bool createDesktopShortcut)
    {
        try { MoveAutostart(targetExe); }
        catch (Exception ex) { Diag.Log("Autostart entry: " + ex.Message); }

        try { CreateShortcut(StartMenuShortcutPath, targetExe, InstallDirectory, "HolyNotch"); }
        catch (Exception ex) { Diag.Log("Start Menu shortcut: " + ex.Message); }

        if (createDesktopShortcut)
        {
            try { CreateShortcut(DesktopShortcutPath, targetExe, InstallDirectory, "HolyNotch"); }
            catch (Exception ex) { Diag.Log("Desktop shortcut: " + ex.Message); }
        }

        try
        {
            string version = CurrentVersionString();
            using var key = Registry.CurrentUser.CreateSubKey(UninstallKeyPath);
            key.SetValue("DisplayName", AppName);
            key.SetValue("DisplayVersion", version);
            key.SetValue("Publisher", AppName);
            key.SetValue("InstallLocation", InstallDirectory);
            key.SetValue("DisplayIcon", $"{targetExe},0");
            key.SetValue("UninstallString", $"\"{targetExe}\" --uninstall");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            long kb = Math.Max(1, new FileInfo(targetExe).Length / 1024);
            key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, kb), RegistryValueKind.DWord);
        }
        catch (Exception ex) { Diag.Log("Uninstall registry: " + ex.Message); }
    }

    static void Uninstall()
    {
        StopOtherHolyNotchInstances();

        TryDeleteFile(StartMenuShortcutPath);
        TryDeleteFile(DesktopShortcutPath);

        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKeyPath, throwOnMissingSubKey: false); }
        catch (Exception) { }

        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            run?.DeleteValue("HolyNotch", throwOnMissingValue: false);
            run?.DeleteValue("Holm", throwOnMissingValue: false);
        }
        catch (Exception) { }

        // Remove the install folder after this process exits.
        string dir = InstallDirectory;
        if (Directory.Exists(dir))
        {
            try
            {
                string cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
                Process.Start(new ProcessStartInfo(cmd, $"/c ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"{dir}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                });
            }
            catch (Exception) { }
        }
    }

    static void StopOtherHolyNotchInstances()
    {
        using var current = Process.GetCurrentProcess();
        int session = current.SessionId;
        foreach (string name in new[] { "HolyNotch", "Holm.App" })
        {
            Process[] list;
            try { list = Process.GetProcessesByName(name); }
            catch (Exception) { continue; }
            foreach (var p in list)
            {
                try
                {
                    if (p.Id == current.Id || p.SessionId != session) continue;
                    p.Kill(entireProcessTree: false);
                    p.WaitForExit(4000);
                }
                catch (Exception) { }
                finally { p.Dispose(); }
            }
        }
        Thread.Sleep(250);
    }

    static string CurrentVersionString()
    {
        var v = Assembly.GetEntryAssembly()?.GetName().Version;
        return v == null ? "1.0.0" : $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
    }

    static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception) { }
    }

    public static bool PathsEqual(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Creates a Windows .lnk shortcut natively via IShellLinkW (no external scripts or windows).</summary>
    static void CreateShortcut(string shortcutPath, string targetPath, string workingDir, string description)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(targetPath);
            link.SetWorkingDirectory(workingDir);
            link.SetDescription(description);
            link.SetIconLocation(targetPath, 0);
            ((IPersistFile)link).Save(shortcutPath, false);
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    class ShellLink { }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, IntPtr pfd, int fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
        void Resolve(IntPtr hwnd, int fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}
