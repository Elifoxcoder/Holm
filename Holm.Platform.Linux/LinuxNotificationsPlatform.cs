using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Holm.Platform;
using Tmds.DBus.Protocol;
using WinNotch;
using WinNotch.Notify;

namespace Holm.Platform.Linux;

/// <summary>
/// Notifications as they are sent to the desktop's notification server on the session bus (org.freedesktop.
/// Notifications), read through a binary D-Bus monitor so embedded newlines cannot forge message frames; and the
/// numbers apps put on their launcher icons (com.canonical.Unity.LauncherEntry). Nothing is changed or answered.
/// </summary>
public sealed class LinuxNotificationsPlatform : INotificationsPlatform
{
    const int Keep = 60;
    const int KeepLogos = 128;
    const long MaxLogoBytes = 1024 * 1024;
    const long MaxDesktopBytes = 64 * 1024;
    const int MaxText = 4096;

    static readonly Regex Markup = new(@"<[^>]{1,200}>", RegexOptions.Compiled);

    readonly object _gate = new();
    readonly List<SystemNotification> _toasts = new();
    readonly Dictionary<string, string> _badges = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, byte[]> _logos = new(StringComparer.OrdinalIgnoreCase);
    IDisposable? _notificationWatch;
    IDisposable? _badgeWatch;
    uint _nextId = 1;
    bool _running;

    public event Action? Changed;

    public NotificationAccess Access { get; private set; } = NotificationAccess.Off;

    public string? AccessHint => Access == NotificationAccess.Unavailable ? "Session D-Bus monitoring is unavailable – app badge counts only" : null;

    public void OpenAccessSettings() { }

    public async Task StartAsync(bool ask)
    {
        if (_running) return;
        _running = true;
        _notificationWatch = await WatchNotificationsAsync().ConfigureAwait(false);
        Access = _notificationWatch != null ? NotificationAccess.Allowed : NotificationAccess.Unavailable;
        _badgeWatch = await WatchBadgesAsync().ConfigureAwait(false);
        Changed?.Invoke();
    }

    public void Stop()
    {
        _running = false;
        _notificationWatch?.Dispose();
        _notificationWatch = null;
        _badgeWatch?.Dispose();
        _badgeWatch = null;
        lock (_gate) { _toasts.Clear(); _badges.Clear(); _logos.Clear(); }
        Access = NotificationAccess.Off;
    }

    public Task<NotificationSnapshot> ReadAsync()
    {
        lock (_gate)
            return Task.FromResult(new NotificationSnapshot(_toasts.ToList(), new Dictionary<string, string>(_badges), null));
    }

    public byte[]? LogoOf(string appId)
    {
        lock (_gate) return _logos.TryGetValue(appId, out var logo) ? logo : null;
    }

    // ───────────── notifications ─────────────

    async Task<IDisposable?> WatchNotificationsAsync()
    {
        try
        {
            var rule = new MatchRule { Type = MessageType.MethodCall, Interface = "org.freedesktop.Notifications", Member = "Notify" };
            return await Bus.Session.MonitorAsync((error, message) =>
            {
                using (message)
                {
                    if (!_running || error != null) return;
                    try { Take(message.Message); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Text.DecoderFallbackException) { }
                }
            }, new[] { rule }).ConfigureAwait(false);
        }
        catch { return null; }
    }

    internal readonly record struct NotificationFields(string App, string Icon, string Summary, string Body, string? DesktopEntry);

    /// <summary>Reads Notify's binary signature susssasa{sv}i; string boundaries come from D-Bus, never text lines.</summary>
    internal static NotificationFields? ReadNotify(Message message)
    {
        if (message.MessageType != MessageType.MethodCall || message.SignatureAsString != "susssasa{sv}i") return null;
        var reader = message.GetBodyReader();
        string app = Text(reader.ReadString(), 256);
        reader.ReadUInt32(); // replaces id
        string icon = Text(reader.ReadString(), MaxText);
        string summary = Text(reader.ReadString(), MaxText);
        string body = Text(reader.ReadString(), MaxText);

        ArrayEnd actions = reader.ReadArrayStart(DBusType.String);
        int count = 0;
        while (reader.HasNext(actions))
        {
            string action = reader.ReadString();
            if (count++ < 64 && action.Length > MaxText) action = action[..MaxText];
        }

        string? entry = null;
        var hints = reader.ReadDictionaryStart();
        while (reader.HasNext(hints))
        {
            string key = reader.ReadString();
            var value = reader.ReadVariantValue();
            if (key == "desktop-entry" && value.Type == VariantValueType.String)
                entry = Text(value.GetString(), 256);
        }
        reader.ReadInt32(); // timeout
        return new NotificationFields(app, icon, summary, body, entry);
    }

    static string Text(string? value, int maximum) => value == null ? "" : value.Length <= maximum ? value : value[..maximum];

    void Take(Message message)
    {
        if (ReadNotify(message) is not { } n) return;
        string appId = SafeDesktopEntry(n.DesktopEntry) ? n.DesktopEntry! : SafeAppId(n.App);
        if (appId.Length == 0) return;
        // A browser names the site in the body (Chrome: the first line, as a link): put it last, where Windows has it.
        var lines = new List<string> { Plain(n.Summary) };
        lines.AddRange(Plain(n.Body).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
        int site = lines.FindIndex(1, l => l.Length <= 60 && Messengers.All.Any(m => m.WebHosts.Any(h => l.Contains(h, StringComparison.OrdinalIgnoreCase))));
        if (site > 0 && site < lines.Count - 1) { string host = lines[site]; lines.RemoveAt(site); lines.Add(host); }
        lock (_gate)
        {
            // An app updating its notification sends the same again: one entry.
            _toasts.RemoveAll(t => t.AppId == appId && t.Lines.SequenceEqual(lines));
            _toasts.Add(new SystemNotification(_nextId++, appId, n.App, lines, DateTimeOffset.Now));
            if (_toasts.Count > Keep) _toasts.RemoveRange(0, _toasts.Count - Keep);
            if (!_logos.ContainsKey(appId) && IconBytes(n.Icon, SafeDesktopEntry(n.DesktopEntry) ? n.DesktopEntry! : n.App) is { } logo)
            {
                if (_logos.Count >= KeepLogos && _logos.Keys.FirstOrDefault() is { } old) _logos.Remove(old);
                _logos[appId] = logo;
            }
        }
        Changed?.Invoke();
    }

    internal static string Plain(string text) => System.Net.WebUtility.HtmlDecode(Markup.Replace(text, ""));

    internal static bool SafeDesktopEntry(string? entry)
        => !string.IsNullOrWhiteSpace(entry) && entry.Length <= 256 && !entry.Contains("..", StringComparison.Ordinal)
            && !entry.Contains('/') && !entry.Contains('\\') && !Path.IsPathRooted(entry)
            && entry.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '@');

    static string SafeAppId(string app)
        => app.Length is > 0 and <= 256 && !app.Contains("..", StringComparison.Ordinal)
            && !app.Any(c => char.IsControl(c) || c is '/' or '\\') ? app : "";

    /// <summary>The app's icon: the notification's own PNG, or the icon its desktop file names.</summary>
    internal static byte[]? IconBytes(string icon, string desktopEntry)
    {
        string? file = IconFile(icon, desktopEntry);
        return file == null ? null : ReadPng(file);
    }

    static string? IconFile(string icon, string desktopEntry)
    {
        if (icon.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(icon, UriKind.Absolute, out var uri) || !uri.IsFile || uri.Host.Length != 0
                || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return null;
            icon = uri.LocalPath;
        }
        if (Path.IsPathRooted(icon) && icon.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return icon;
        if (LinuxAppVolumePlatform.FindIcon(icon) is { } named) return named;
        if (!SafeDesktopEntry(desktopEntry)) return null;
        foreach (string folder in new[] { "/usr/share/applications", "/var/lib/flatpak/exports/share/applications",
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local/share/applications") })
        {
            string file = Path.Combine(folder, desktopEntry + ".desktop");
            if (!RegularFile(file, MaxDesktopBytes)) continue;
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var reader = new StreamReader(stream);
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (!line.StartsWith("Icon=", StringComparison.Ordinal)) continue;
                    string name = line[5..].Trim();
                    if (LinuxAppVolumePlatform.FindIcon(name) is { } found) return found;
                    break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        return null;
    }

    static byte[]? ReadPng(string path)
    {
        try
        {
            if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || !RegularFile(path, MaxLogoBytes)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            byte[] bytes = BoundedIo.ReadAllBytes(stream, MaxLogoBytes);
            return bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 'P' && bytes[2] == 'N' && bytes[3] == 'G'
                && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A ? bytes : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    static bool RegularFile(string path, long maximum)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists && file.Length <= maximum && (file.Attributes & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    // ───────────── launcher badges ─────────────

    async Task<IDisposable?> WatchBadgesAsync()
    {
        try
        {
            if (await Bus.Session.ConnectAsync().ConfigureAwait(false) is not { } connection) return null;
            var rule = new MatchRule { Type = MessageType.Signal, Interface = "com.canonical.Unity.LauncherEntry", Member = "Update" };
            return await connection.AddMatchAsync(rule, static (Message m, object? _) => ReadBadge(m),
                (Notification<(string App, int Count, bool Visible)?> update) =>
                {
                    if (!update.HasValue || update.Value is not { } badge) return;
                    lock (_gate) _badges[badge.App] = (badge.Visible ? badge.Count : 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    Changed?.Invoke();
                }, false, ObserverFlags.None).ConfigureAwait(false);
        }
        catch { return null; }
    }

    /// <summary>Update(s app_uri, a{sv} properties): "application://org.telegram.desktop.desktop", count and count-visible.</summary>
    static (string App, int Count, bool Visible)? ReadBadge(Message message)
    {
        if (message.SignatureAsString != "sa{sv}") return null;
        var reader = message.GetBodyReader();
        string uri = reader.ReadString();
        long? count = null;
        bool? visible = null;
        ArrayEnd end = reader.ReadDictionaryStart();
        while (reader.HasNext(end))
        {
            string key = reader.ReadString();
            var value = reader.ReadVariantValue();
            if (key == "count" && value.Type is VariantValueType.Int64) count = value.GetInt64();
            else if (key == "count-visible" && value.Type is VariantValueType.Bool) visible = value.GetBool();
        }
        string app = uri.StartsWith("application://", StringComparison.Ordinal) ? uri["application://".Length..] : uri;
        if (app.EndsWith(".desktop", StringComparison.Ordinal)) app = app[..^".desktop".Length];
        if (app.Length == 0 || (count == null && visible == null)) return null;
        return (app, (int)Math.Clamp(count ?? 0, 0, 9999), visible ?? count > 0);
    }
}
