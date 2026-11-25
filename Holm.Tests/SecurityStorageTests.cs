using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Holm.Platform;
using Holm.Platform.Linux;
using WinNotch.Notify;
using WinNotch.Sharing;

namespace WinNotch.Tests;

public sealed class SecurityStorageTests : IDisposable
{
    readonly TempFolder _folder = new();

    [Fact]
    public void Queued_files_from_the_legacy_shared_temp_folder_migrate_to_private_storage()
    {
        string legacy = Path.Combine(_folder.Path, "legacy", "Holm", "Beam");
        Directory.CreateDirectory(legacy);
        string old = Path.Combine(legacy, "waiting.zip");
        File.WriteAllBytes(old, new byte[] { 1, 2, 3 });
        var send = new QueuedSend { Id = Guid.NewGuid().ToString("N"), Paths = [old] };
        var queue = new List<QueuedSend> { send };

        Assert.True(SendLater.MigrateLegacyPaths(queue, Path.Combine(_folder.Path, "legacy")));
        Assert.False(File.Exists(old));
        string moved = Assert.Single(send.Paths);
        Assert.True(SendLater.IsHolmsOwn(moved, SendLater.Folder));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(moved));
        if (OperatingSystem.IsLinux())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(moved));
        Assert.False(SendLater.MigrateLegacyPaths(queue, Path.Combine(_folder.Path, "legacy")));
    }

    [Fact]
    public void Runtime_and_queued_files_no_longer_use_the_predictable_shared_temp_folder()
    {
        string old = Path.Combine(Path.GetTempPath(), "Holm");
        Assert.NotEqual(old, PrivateStorage.TempRootPath);
        Assert.Equal(Path.Combine(Diag.Folder, "Later"), SendLater.Folder);
        Assert.True(SendLater.IsHolmsOwn(SendLater.Folder, Diag.Folder));
    }

    [Fact]
    public void Test_data_folder_and_its_tmp_subfolder_are_private_on_unix()
    {
        if (!OperatingSystem.IsLinux()) return;
        const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        Assert.Equal(Private, File.GetUnixFileMode(Diag.Folder));
        Assert.Equal(Private, File.GetUnixFileMode(Path.Combine(Diag.Folder, "tmp")));
    }

    [Fact]
    public void Private_files_are_create_new_and_unix_modes_are_exact()
    {
        string folder = Path.Combine(_folder.Path, "private");
        PrivateStorage.EnsureFolder(folder);
        string path = Path.Combine(folder, "settings.json");
        using (var file = PrivateStorage.CreateFile(path))
            file.WriteByte(1);
        Assert.Throws<IOException>(() => PrivateStorage.CreateFile(path));

        if (!OperatingSystem.IsLinux()) return;
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(folder));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Fact]
    public void Atomic_text_replacement_keeps_the_file_private()
    {
        string path = Path.Combine(_folder.Path, "settings.json");
        PrivateStorage.WriteAllTextAtomic(path, "one");
        PrivateStorage.WriteAllTextAtomic(path, "two");
        Assert.Equal("two", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(_folder.Path, "settings.json.*.tmp"));
        if (!OperatingSystem.IsLinux()) return;
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Fact]
    public void Existing_sensitive_data_files_are_tightened_without_following_links()
    {
        if (!OperatingSystem.IsLinux()) return;
        string folder = Path.Combine(_folder.Path, "data");
        Directory.CreateDirectory(folder);
        string chats = Path.Combine(folder, "chats.json");
        string account = Path.Combine(folder, "account.json");
        File.WriteAllText(chats, "{}");
        File.WriteAllText(account, "{}");
        File.SetUnixFileMode(chats, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        File.SetUnixFileMode(account, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        string outside = _folder.File("outside", new byte[] { 1 });
        File.CreateSymbolicLink(Path.Combine(folder, "linked.json"), outside);

        PrivateStorage.SecureDataFolder(folder);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(chats));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(account));
        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(outside));
    }

    [Fact]
    public void Chat_history_uses_the_atomic_private_writer()
    {
        string folder = Path.Combine(_folder.Path, "chat");
        PrivateStorage.EnsureFolder(folder);
        string path = Path.Combine(folder, "chats.json");
        var chat = new ChatStore(path);
        Assert.True(chat.Add("friend", new FriendMessage { Id = "one", Mine = true, Text = "hello", Sent = 1 }, unread: false));
        Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
        if (!OperatingSystem.IsLinux()) return;
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Fact]
    public void Atomic_writer_refuses_a_symlink_destination()
    {
        if (!OperatingSystem.IsLinux()) return;
        string outside = _folder.File("outside", System.Text.Encoding.UTF8.GetBytes("unchanged"));
        string link = Path.Combine(_folder.Path, "settings.json");
        File.CreateSymbolicLink(link, outside);
        Assert.Throws<IOException>(() => PrivateStorage.WriteAllTextAtomic(link, "changed"));
        Assert.Equal("unchanged", File.ReadAllText(outside));
    }

    [Fact]
    public void Runtime_base_uses_only_a_private_xdg_directory()
    {
        string fallback = Path.Combine(_folder.Path, "fallback");
        string xdg = Path.Combine(_folder.Path, "xdg");
        PrivateStorage.EnsureFolder(xdg);
        if (!OperatingSystem.IsLinux())
        {
            Assert.Equal(Path.Combine(fallback, "Temp"), PrivateStorage.ChooseRuntimeBase(xdg, fallback));
            return;
        }
        Assert.True(UnixIdentityPlatform.IsOwnedByCurrentUser(xdg));
        Assert.Equal(Path.GetFullPath(xdg), PrivateStorage.ChooseRuntimeBase(xdg, fallback, _ => true));
        Assert.Equal(Path.Combine(fallback, "Temp"), PrivateStorage.ChooseRuntimeBase(xdg, fallback, _ => false));
        string linked = Path.Combine(_folder.Path, "xdg-link");
        File.CreateSymbolicLink(linked, xdg);
        Assert.Equal(Path.Combine(fallback, "Temp"), PrivateStorage.ChooseRuntimeBase(linked, fallback));
        File.SetUnixFileMode(xdg, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        Assert.Equal(Path.Combine(fallback, "Temp"), PrivateStorage.ChooseRuntimeBase(xdg, fallback));
    }

    [Fact]
    public void Send_later_ids_and_path_boundaries_cannot_escape_holm_storage()
    {
        string root = Path.Combine(_folder.Path, "Runtime");
        Assert.True(SendLater.IsHolmsOwn(Path.Combine(root, "Later", "file.txt"), root));
        Assert.False(SendLater.IsHolmsOwn(root, root));
        Assert.False(SendLater.IsHolmsOwn(Path.Combine(root + "-other", "file.txt"), root));
        Assert.True(SendLater.IsSafeId(Guid.NewGuid().ToString("N")));
        Assert.False(SendLater.IsSafeId("../outside"));
        Assert.False(SendLater.IsSafeId(""));
        if (OperatingSystem.IsLinux())
            Assert.False(SendLater.IsHolmsOwn(Path.Combine(root.ToUpperInvariant(), "file.txt"), root));
    }

    [Fact]
    public void Core_windows_imports_are_restricted_to_system32()
    {
        Type[] interopTypes =
        {
            typeof(WinSqlite),
            typeof(UsbDrives),
        };
        foreach (Type type in interopTypes)
        {
            var imports = type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Select(m => m.GetCustomAttribute<DefaultDllImportSearchPathsAttribute>()).Where(a => a != null).ToList();
            Assert.NotEmpty(imports);
            Assert.All(imports, a => Assert.Equal(DllImportSearchPath.System32, a!.Paths));
        }
    }

    [Fact]
    public void Fallback_identity_uses_application_data_not_temp()
    {
        var identity = new NoPlatformServices().Identity;
        var field = typeof(UnixIdentityPlatform).GetField("_keyFile", BindingFlags.Instance | BindingFlags.NonPublic)!;
        string path = (string)field.GetValue(identity)!;
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Holm", "secret.key"), path);
    }

    [Fact]
    public void Linux_handy_scan_never_includes_downloads()
    {
        string home = Path.Combine(_folder.Path, "home");
        string[] folders = LinuxDictationPlatform.InstalledAppImageFolders(home).ToArray();
        Assert.Equal(new[] { Path.Combine(home, "Applications"), Path.Combine(home, ".local", "bin") }, folders);
        Assert.DoesNotContain(Path.Combine(home, "Downloads"), folders);
    }

    [Fact]
    public void Unix_identity_key_is_private_and_round_trips()
    {
        if (!OperatingSystem.IsLinux()) return;
        string folder = Path.Combine(_folder.Path, "identity");
        var identity = new UnixIdentityPlatform(folder);
        byte[] plain = System.Text.Encoding.UTF8.GetBytes("identity");
        byte[] entropy = new byte[] { 1, 2, 3 };
        byte[] sealedData = identity.Protect(plain, entropy);
        Assert.Equal(plain, identity.Unprotect(sealedData, entropy));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(folder));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(folder, "secret.key")));
        Assert.Single(Directory.GetFiles(folder));
    }

    [Fact]
    public async Task Unix_identity_key_creation_is_atomic_across_concurrent_callers()
    {
        if (!OperatingSystem.IsLinux()) return;
        string folder = Path.Combine(_folder.Path, "identity");
        byte[] plain = new byte[] { 4, 5, 6 };
        byte[] entropy = new byte[] { 7, 8, 9 };
        var identities = Enumerable.Range(0, 8).Select(_ => new UnixIdentityPlatform(folder)).ToArray();
        byte[][] sealedValues = await Task.WhenAll(identities.Select(i => Task.Run(() => i.Protect(plain, entropy))));
        foreach (byte[] sealedValue in sealedValues)
            Assert.Equal(plain, identities[0].Unprotect(sealedValue, entropy));
        Assert.Single(Directory.GetFiles(folder));
    }

    [Fact]
    public async Task Unix_identity_publication_is_atomic_without_the_in_process_lock()
    {
        if (!OperatingSystem.IsLinux()) return;
        string folder = Path.Combine(_folder.Path, "identity-race");
        PrivateStorage.EnsureFolder(folder);
        string key = Path.Combine(folder, "secret.key");
        var publish = typeof(UnixIdentityPlatform).GetMethod("PublishKeyAtomically", BindingFlags.Static | BindingFlags.NonPublic)!;
        byte[][] candidates = Enumerable.Range(0, 16).Select(i => Enumerable.Repeat((byte)i, 32).ToArray()).ToArray();
        string[] temp = candidates.Select((_, i) => Path.Combine(folder, $".key-{i}.tmp")).ToArray();
        for (int i = 0; i < temp.Length; i++)
        {
            File.WriteAllBytes(temp[i], candidates[i]);
            File.SetUnixFileMode(temp[i], UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        using var start = new ManualResetEventSlim(false);
        Task<bool>[] tasks = temp.Select(path => Task.Run(() =>
        {
            start.Wait();
            return (bool)publish.Invoke(null, new object[] { path, key })!;
        })).ToArray();
        start.Set();
        bool[] wins = await Task.WhenAll(tasks);
        foreach (string path in temp) File.Delete(path);

        Assert.Equal(1, wins.Count(won => won));
        byte[] expected = candidates[Array.IndexOf(wins, true)];
        Assert.Equal(expected, File.ReadAllBytes(key));
        Assert.Single(Directory.GetFiles(folder));
    }

    [Fact]
    public void Unix_identity_refuses_symlinked_wrong_mode_and_wrong_length_keys()
    {
        if (!OperatingSystem.IsLinux()) return;
        byte[] request = new byte[29];
        request[0] = 1;
        string folder = Path.Combine(_folder.Path, "identity");
        Directory.CreateDirectory(folder);
        string key = Path.Combine(folder, "secret.key");
        string outside = _folder.File("outside-key", new byte[32]);
        File.CreateSymbolicLink(key, outside);
        Assert.Throws<CryptographicException>(() => new UnixIdentityPlatform(folder).Unprotect(request, Array.Empty<byte>()));
        File.Delete(key);

        File.WriteAllBytes(key, new byte[32]);
        File.SetUnixFileMode(key, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        Assert.Throws<CryptographicException>(() => new UnixIdentityPlatform(folder).Unprotect(request, Array.Empty<byte>()));
        File.WriteAllBytes(key, new byte[31]);
        File.SetUnixFileMode(key, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Assert.Throws<CryptographicException>(() => new UnixIdentityPlatform(folder).Unprotect(request, Array.Empty<byte>()));
    }

    public void Dispose() => _folder.Dispose();
}
