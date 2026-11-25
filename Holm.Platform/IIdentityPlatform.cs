using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace Holm.Platform;

/// <summary>Keeping a secret (the friends identity's private key) for this user only, and the user's name.</summary>
public interface IIdentityPlatform
{
    byte[] Protect(byte[] data, byte[] entropy);
    byte[] Unprotect(byte[] data, byte[] entropy);

    /// <summary>The account's full name ("Lena Hoffmann"), or its login name.</summary>
    string AccountName();
}

/// <summary>
/// Linux: sealed with AES-GCM under a key of this user's own, in a file only they can read (as ssh and
/// gpg keep theirs). Copying the settings to another computer leaves the secret behind, as with Windows' DPAPI.
/// </summary>
public class UnixIdentityPlatform : IIdentityPlatform
{
    readonly string _keyFile;
    static readonly object KeyGate = new();

    const int LinuxRegularFile = 0x8000, LinuxDirectory = 0x4000, FileTypeMask = 0xF000, PermissionMask = 0xFFF;
    const int OpenReadOnly = 0x00000000, OpenDirectory = 0x00010000, OpenNoFollow = 0x00020000, OpenNonBlock = 0x00000800, OpenCloseExec = 0x00080000;

    [DllImport("libc", EntryPoint = "open", SetLastError = true)] static extern int Open(string path, int flags, uint mode);
    [DllImport("libc", EntryPoint = "link", SetLastError = true)] static extern int Link(string oldPath, string newPath);
    [DllImport("libc", EntryPoint = "unlink", SetLastError = true)] static extern int Unlink(string path);
    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)] static extern int FStat(int fd, out StatBuffer stat);
    [DllImport("libc", EntryPoint = "fchmod", SetLastError = true)] static extern int FChmod(int fd, uint mode);
    [DllImport("libc", EntryPoint = "geteuid", SetLastError = true)] static extern uint GetEffectiveUserId();

    // Linux x64's struct stat, all 144 bytes: the times are struct timespec (seconds and nanoseconds each). A shorter
    // buffer let fstat write past it onto the stack. The handle, not the path, is checked.
    [StructLayout(LayoutKind.Sequential, Size = 144)]
    struct StatBuffer
    {
        public ulong Device, Inode, Links;
        public uint Mode, UserId, GroupId, Pad0;
        public ulong DeviceId;
        public long Size, BlockSize, Blocks;
        public long AccessSeconds, AccessNanoseconds, ModificationSeconds, ModificationNanoseconds, ChangeSeconds, ChangeNanoseconds;
        public long Reserved0, Reserved1, Reserved2;
    }

    public UnixIdentityPlatform(string folder) => _keyFile = Path.Combine(folder, "secret.key");

    /// <summary>Whether a Linux directory is a real directory owned by this effective user (used for XDG_RUNTIME_DIR).</summary>
    public static bool IsOwnedByCurrentUser(string directory)
    {
        if (!OperatingSystem.IsLinux()) return false;
        try
        {
            int fd = Open(directory, OpenReadOnly | OpenDirectory | OpenNoFollow | OpenNonBlock | OpenCloseExec, 0);
            if (fd < 0) return false;
            using var handle = new SafeFileHandle(new IntPtr(fd), ownsHandle: true);
            return FStat(handle.DangerousGetHandle().ToInt32(), out StatBuffer stat) == 0
                && (stat.Mode & FileTypeMask) == LinuxDirectory
                && stat.UserId == GetEffectiveUserId();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public byte[] Protect(byte[] data, byte[] entropy)
    {
        byte[] key = Key(create: true);
        byte[] nonce = RandomNumberGenerator.GetBytes(12), tag = new byte[16], sealedData = new byte[data.Length];
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, data, sealedData, tag, entropy);
        var result = new byte[1 + 12 + 16 + data.Length];
        result[0] = 1; // version
        nonce.CopyTo(result, 1);
        tag.CopyTo(result, 13);
        sealedData.CopyTo(result, 29);
        return result;
    }

    public byte[] Unprotect(byte[] data, byte[] entropy)
    {
        if (data.Length < 29 || data[0] != 1) throw new CryptographicException("Not sealed here.");
        byte[] key = Key(create: false);
        var plain = new byte[data.Length - 29];
        using (var aes = new AesGcm(key, 16)) aes.Decrypt(data.AsSpan(1, 12), data.AsSpan(29), data.AsSpan(13, 16), plain, entropy);
        return plain;
    }

    byte[] Key(bool create)
    {
        lock (KeyGate)
        {
            try
            {
                string folder = Path.GetDirectoryName(_keyFile)!;
                EnsurePrivateFolder(folder);
                if (File.Exists(_keyFile)) return ReadKey();
                if (!create) throw new CryptographicException("No key on this computer.");

                // Same-directory temp + atomic link publishes one complete key, including when two Holm processes start together.
                string temp = Path.Combine(folder, $".secret.key.{Guid.NewGuid():N}.tmp");
                try
                {
                    byte[] key = RandomNumberGenerator.GetBytes(32);
                    var options = new FileStreamOptions
                    {
                        Mode = FileMode.CreateNew,
                        Access = FileAccess.Write,
                        Share = FileShare.None,
                    };
                    if (OperatingSystem.IsLinux()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                    using (var file = new FileStream(temp, options))
                    {
                        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(file.SafeFileHandle, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                        VerifyHandle(file.SafeFileHandle, directory: false, fixDirectoryMode: false);
                        file.Write(key);
                        file.Flush(flushToDisk: true);
                    }
                    PublishKeyAtomically(temp, _keyFile);
                }
                finally
                {
                    try { File.Delete(temp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
                return ReadKey(); // read the winner, never return bytes that were not atomically published
            }
            catch (CryptographicException) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                throw new CryptographicException("Holm's identity key is not private to this user.", ex);
            }
        }
    }

    /// <summary>
    /// Publishes a completed key without a check-then-rename race. Linux link(2) fails atomically with EEXIST when
    /// another Holm process won; unlike File.Move's no-overwrite fallback, it can never replace that winner.
    /// </summary>
    static bool PublishKeyAtomically(string temp, string keyFile)
    {
        if (!OperatingSystem.IsLinux())
        {
            try { File.Move(temp, keyFile, overwrite: false); return true; }
            catch (IOException) when (File.Exists(keyFile)) { return false; }
        }

        if (Link(temp, keyFile) == 0)
        {
            Unlink(temp); // best effort; the caller's finally also removes its own temp name
            return true;
        }
        int error = Marshal.GetLastPInvokeError();
        if (File.Exists(keyFile)) return false; // a concurrent process published first; ReadKey validates it
        throw new IOException("Couldn't publish Holm's identity key.", error);
    }

    byte[] ReadKey()
    {
        if (!OperatingSystem.IsLinux())
        {
            RejectLink(_keyFile);
            byte[] bytes = File.ReadAllBytes(_keyFile);
            if (bytes.Length != 32) throw new CryptographicException("The identity key has the wrong length.");
            return bytes;
        }

        int fd = Open(_keyFile, OpenReadOnly | OpenNoFollow | OpenNonBlock | OpenCloseExec, 0);
        if (fd < 0) throw new IOException("Couldn't open Holm's identity key.", Marshal.GetLastPInvokeError());
        using var handle = new SafeFileHandle(new IntPtr(fd), ownsHandle: true);
        VerifyHandle(handle, directory: false, fixDirectoryMode: false);
        using var file = new FileStream(handle, FileAccess.Read, bufferSize: 4096, isAsync: false);
        if (file.Length != 32) throw new CryptographicException("The identity key has the wrong length.");
        byte[] key = new byte[32];
        file.ReadExactly(key);
        return key;
    }

    void EnsurePrivateFolder(string folder)
    {
        string full = Path.GetFullPath(folder);
        var info = new DirectoryInfo(full);
        if (info.Exists && IsLink(info)) throw new IOException("Holm won't keep its identity key in a linked folder.");
        if (OperatingSystem.IsLinux()) Directory.CreateDirectory(full, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        else Directory.CreateDirectory(full);
        if (!OperatingSystem.IsLinux()) return;
        int fd = Open(full, OpenReadOnly | OpenDirectory | OpenNoFollow | OpenCloseExec, 0);
        if (fd < 0) throw new IOException("Couldn't open Holm's private identity folder.", Marshal.GetLastPInvokeError());
        using var handle = new SafeFileHandle(new IntPtr(fd), ownsHandle: true);
        VerifyHandle(handle, directory: true, fixDirectoryMode: true);
    }

    void VerifyHandle(SafeFileHandle handle, bool directory, bool fixDirectoryMode)
    {
        if (!OperatingSystem.IsLinux()) return;
        int fd = handle.DangerousGetHandle().ToInt32();
        if (FStat(fd, out StatBuffer stat) != 0) throw new IOException("Couldn't inspect Holm's identity key.", Marshal.GetLastPInvokeError());
        if ((stat.Mode & FileTypeMask) != (directory ? LinuxDirectory : LinuxRegularFile))
            throw new CryptographicException("Holm's identity key is not a regular private file.");
        if (stat.UserId != GetEffectiveUserId()) throw new CryptographicException("Holm's identity key belongs to another user.");
        const uint PrivateDirectory = 0x1C0, PrivateFile = 0x180; // 0700 and 0600
        uint permissions = stat.Mode & PermissionMask;
        if (directory && fixDirectoryMode && permissions != PrivateDirectory)
        {
            if (FChmod(fd, PrivateDirectory) != 0 || FStat(fd, out stat) != 0)
                throw new IOException("Couldn't make Holm's identity folder private.", Marshal.GetLastPInvokeError());
            permissions = stat.Mode & PermissionMask;
        }
        if (permissions != (directory ? PrivateDirectory : PrivateFile))
            throw new CryptographicException("Holm's identity key has unsafe permissions.");
    }

    static bool IsLink(FileSystemInfo info)
    {
        try { return info.LinkTarget != null || (info.Attributes & FileAttributes.ReparsePoint) != 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    static void RejectLink(string path)
    {
        if (IsLink(new FileInfo(path))) throw new IOException("Holm won't read its identity key through a link.");
    }

    public virtual string AccountName()
    {
        try
        {
            // The full name from the account's entry (GECOS: "Lena Hoffmann,,,").
            foreach (string line in File.ReadLines("/etc/passwd"))
            {
                var fields = line.Split(':');
                if (fields.Length > 4 && fields[0] == Environment.UserName)
                {
                    string full = fields[4].Split(',')[0].Trim();
                    if (full.Length > 0) return full;
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return Environment.UserName;
    }
}
