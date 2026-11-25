using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace WinNotch;

/// <summary>
/// Holm's sensitive files and runtime data. The settings folder stays where it always was; on Unix it and every
/// runtime folder are private to this user, and files are created 0600 rather than relying on the process umask.
/// </summary>
internal static class PrivateStorage
{
    internal const UnixFileMode PrivateDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    internal const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    static readonly Lazy<string> RuntimeRoot = new(() =>
    {
        string data = Diag.Folder;
        EnsureFolder(data);
        string? xdg = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        string user = Environment.UserName + "\0" + Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string token = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(user)))[..16].ToLowerInvariant();
        return Path.Combine(ChooseRuntimeBase(xdg, data), "holm-" + token);
    }, true);

    internal static Func<string, bool>? RuntimeDirectoryOwner { get; set; }
    internal static string TempRootPath => RuntimeRoot.Value;
    internal static string TempFolder(string name) => Folder(Path.Combine(RuntimeRoot.Value, SafeSegment(name)));
    internal static string DataFolder(string name)
    {
        EnsureFolder(Diag.Folder);
        return Folder(Path.Combine(Diag.Folder, SafeSegment(name)));
    }

    static string Folder(string path)
    {
        EnsureFolder(path);
        return path;
    }

    /// <summary>XDG's per-user runtime directory when it is really private; otherwise a private folder beside the settings.</summary>
    internal static string ChooseRuntimeBase(string? xdg, string dataFolder, Func<string, bool>? ownerCheck = null)
    {
        if (OperatingSystem.IsLinux() && !string.IsNullOrWhiteSpace(xdg) && Path.IsPathFullyQualified(xdg))
            try
            {
                var info = new DirectoryInfo(xdg);
                string full = Path.GetFullPath(xdg);
                Func<string, bool>? isOwner = ownerCheck ?? RuntimeDirectoryOwner;
                if (info.Exists && !IsReparsePoint(info) && info.UnixFileMode == PrivateDirectoryMode && isOwner?.Invoke(full) == true)
                    return full;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        return Path.Combine(dataFolder, "Temp");
    }

    internal static void EnsureFolder(string path)
    {
        string full = Path.GetFullPath(path);
        var info = new DirectoryInfo(full);
        if (info.Exists && IsReparsePoint(info)) throw new IOException("Holm won't use a linked data folder.");
        if (OperatingSystem.IsLinux()) Directory.CreateDirectory(full, PrivateDirectoryMode);
        else Directory.CreateDirectory(full);
        info.Refresh();
        if (IsReparsePoint(info)) throw new IOException("Holm won't use a linked data folder.");
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(full, PrivateDirectoryMode);
    }

    /// <summary>Also tightens files written by older code, without following links.</summary>
    internal static void SecureDataFolder(string folder)
    {
        EnsureFolder(folder);
        if (!OperatingSystem.IsLinux()) return;
        foreach (FileSystemInfo entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
        {
            if (IsReparsePoint(entry)) continue;
            try
            {
                if (entry is FileInfo file) SecureFileInfo(file);
                else if (entry is DirectoryInfo directory) directory.UnixFileMode = PrivateDirectoryMode;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    internal static bool IsReparsePoint(FileSystemInfo info)
    {
        try
        {
            info.Refresh();
            if (!info.Exists)
            {
                // A new path is normal; LinkTarget still identifies a dangling symbolic link where supported.
                try { return info.LinkTarget != null; }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or IOException or UnauthorizedAccessException) { return false; }
            }
            if (info.LinkTarget != null) return true;
            return (info.Attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return false; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    internal static bool IsLink(string path)
    {
        try { return IsReparsePoint(new FileInfo(path)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return true; }
    }

    static void RejectLink(string path)
    {
        if (IsLink(path)) throw new IOException("Holm won't use a linked data file.");
    }

    static void SecureOpenFile(FileStream file)
    {
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(file.SafeFileHandle, PrivateFileMode);
    }

    static void SecureFileInfo(FileInfo info)
    {
        if (IsReparsePoint(info)) throw new IOException("Holm won't use a linked data file.");
        try
        {
            using var file = info.Open(FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            // Recheck after opening; chmod the opened inode, never a replacement path.
            if (IsReparsePoint(info)) throw new IOException("Holm won't use a linked data file.");
            SecureOpenFile(file);
        }
        catch (UnauthorizedAccessException)
        {
            // A file can be write-only or unreadable and still need its owner permissions tightened.
            if (IsReparsePoint(info) || OperatingSystem.IsWindows()) throw;
            info.UnixFileMode = PrivateFileMode;
        }
    }

    internal static FileStream CreateFile(string path, bool useAsync = false, int bufferSize = 4096)
    {
        EnsureFolder(Path.GetDirectoryName(Path.GetFullPath(path))!);
        RejectLink(path);
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = useAsync ? FileOptions.Asynchronous : FileOptions.None,
            BufferSize = bufferSize,
        };
        if (OperatingSystem.IsLinux()) options.UnixCreateMode = PrivateFileMode;
        var file = new FileStream(path, options);
        try
        {
            SecureOpenFile(file);
            return file;
        }
        catch
        {
            file.Dispose();
            try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    internal static FileStream OpenOrCreatePrivate(string path, FileAccess access, FileShare share)
    {
        EnsureFolder(Path.GetDirectoryName(Path.GetFullPath(path))!);
        RejectLink(path);
        var options = new FileStreamOptions
        {
            Mode = FileMode.OpenOrCreate,
            Access = access,
            Share = share,
        };
        if (OperatingSystem.IsLinux()) options.UnixCreateMode = PrivateFileMode;
        var file = new FileStream(path, options);
        try
        {
            SecureOpenFile(file);
            return file;
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    internal static void WriteAllTextAtomic(string path, string text)
    {
        string full = Path.GetFullPath(path);
        EnsureFolder(Path.GetDirectoryName(full)!);
        RejectLink(full);
        string temp = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = CreateFile(temp))
            using (var writer = new StreamWriter(file, new UTF8Encoding(false)))
            {
                writer.Write(text);
                writer.Flush();
                file.Flush(flushToDisk: true);
            }
            RejectLink(full);
            File.Move(temp, full, overwrite: true);
            SecureFile(full);
        }
        finally
        {
            try { File.Delete(temp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    internal static string WriteNewText(string folder, string stem, string text)
    {
        for (int n = 1; ; n++)
        {
            string path = Path.Combine(folder, n == 1 ? stem + ".txt" : $"{stem} ({n}).txt");
            FileStream file;
            try { file = CreateFile(path); }
            catch (IOException) when (File.Exists(path) && !IsLink(path)) { continue; }
            try
            {
                using (file)
                using (var writer = new StreamWriter(file, new UTF8Encoding(false)))
                {
                    writer.Write(text);
                    writer.Flush();
                    file.Flush(flushToDisk: true);
                }
                return path;
            }
            catch
            {
                try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                throw;
            }
        }
    }

    internal static async Task<string> WriteNewTextAsync(string folder, string stem, string text)
    {
        for (int n = 1; ; n++)
        {
            string path = Path.Combine(folder, n == 1 ? stem + ".txt" : $"{stem} ({n}).txt");
            FileStream file;
            try { file = CreateFile(path, useAsync: true); }
            catch (IOException) when (File.Exists(path) && !IsLink(path)) { continue; }
            try
            {
                await using (file)
                await using (var writer = new StreamWriter(file, new UTF8Encoding(false)))
                {
                    await writer.WriteAsync(text);
                    await writer.FlushAsync();
                    await file.FlushAsync();
                    file.Flush(flushToDisk: true);
                }
                return path;
            }
            catch
            {
                try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                throw;
            }
        }
    }

    internal static string ReadAllText(string path)
    {
        SecureFile(path);
        RejectLink(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        SecureOpenFile(file);
        using var reader = new StreamReader(file, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    internal static void AppendAllText(string path, string text)
    {
        using var file = OpenOrCreatePrivate(path, FileAccess.Write, FileShare.Read);
        file.Position = file.Length;
        byte[] bytes = new UTF8Encoding(false).GetBytes(text);
        file.Write(bytes, 0, bytes.Length);
        file.Flush();
    }

    internal static void SecureFile(string path)
    {
        RejectLink(path);
        if (OperatingSystem.IsLinux()) SecureFileInfo(new FileInfo(path));
    }

    internal static bool IsInside(string root, string path)
    {
        try
        {
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string fullPath = Path.GetFullPath(path);
            return fullPath.StartsWith(fullRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return false; }
    }

    static string SafeSegment(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || Path.IsPathRooted(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name.Contains(Path.DirectorySeparatorChar) || name.Contains(Path.AltDirectorySeparatorChar))
            throw new ArgumentException("Not a Holm storage folder name.", nameof(name));
        return name;
    }
}
