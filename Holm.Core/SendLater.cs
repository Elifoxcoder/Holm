using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WinNotch.Sharing;

namespace WinNotch;

/// <summary>Files waiting for a friend whose Holm wasn't running. Saved with the settings, so they survive a restart.</summary>
internal sealed class QueuedSend
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string FriendKey { get; set; } = "";
    public string FriendName { get; set; } = "";
    /// <summary>What to send. Files Holm made (a folder's ZIP, a beamed text) were moved to <see cref="SendLater.Folder"/>.</summary>
    public List<string> Paths { get; set; } = new();
    public bool Text { get; set; }
    public DateTime QueuedUtc { get; set; }
    public DateTime? AskedUtc { get; set; }
    public DateTime? NotedUtc { get; set; }
}

/// <summary>
/// When to look for a friend who is offline, and for how long. Their Holm only says it's running when asked, and
/// every question is a note on the inbox server (a few hundred a day are allowed): so ask every 15 minutes for the
/// first two hours, then hourly; and every six hours leave a note that something waits (which a Holm 1.7 answers
/// as soon as it starts, even hours later). After a week it's given up.
/// </summary>
internal static class SendLater
{
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromDays(7);
    public static readonly TimeSpan NoteEvery = TimeSpan.FromHours(6);
    // Queued files must survive a restart, so they live in the private data folder rather than XDG runtime storage.
    public static string Folder => PrivateStorage.DataFolder("Later");

    /// <summary>
    /// Moves queued files made by older versions out of the shared temp folder. Settings paths are updated in place;
    /// missing or linked legacy files are not followed. Returns true when the queue needs to be saved again.
    /// </summary>
    internal static bool MigrateLegacyPaths(List<QueuedSend> queue, string? tempRoot = null)
    {
        string root = tempRoot ?? Path.GetTempPath();
        string[] legacy = { Path.Combine(root, "Holm", "Beam"), Path.Combine(root, "Holm", "Later") };
        bool changed = false;
        foreach (string folder in legacy)
        {
            if (Path.GetFullPath(Folder).Equals(Path.GetFullPath(folder), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                continue;
            try { PrivateStorage.SecureDataFolder(folder); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (QueuedSend send in queue)
            {
                if (!IsSafeId(send.Id)) continue;
                var kept = new List<string>();
                foreach (string path in send.Paths)
                {
                    string full;
                    try { full = Path.GetFullPath(path); }
                    catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { changed = true; continue; }
                    if (!PrivateStorage.IsInside(folder, full)) { kept.Add(path); continue; }
                    if (!File.Exists(full) || PrivateStorage.IsLink(full)) { changed = true; continue; }
                    try
                    {
                        PrivateStorage.SecureFile(full);
                        string own = Path.Combine(Folder, send.Id);
                        PrivateStorage.EnsureFolder(own);
                        string moved = FileTransfer.UniquePath(own, Path.GetFileName(full));
                        File.Move(full, moved, overwrite: false);
                        PrivateStorage.SecureFile(moved);
                        kept.Add(moved);
                        changed = true;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { kept.Add(path); }
                }
                if (kept.Count != send.Paths.Count || !kept.SequenceEqual(send.Paths, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
                {
                    send.Paths = kept;
                    changed = true;
                }
            }
        }
        return changed;
    }

    public static TimeSpan AskEvery(TimeSpan waited) => waited < TimeSpan.FromHours(2) ? TimeSpan.FromMinutes(15) : TimeSpan.FromHours(1);

    public static bool Expired(QueuedSend send, DateTime now) => now - send.QueuedUtc >= GiveUpAfter;

    public static bool AskDue(QueuedSend send, DateTime now)
        => send.AskedUtc is not { } asked || now - asked >= AskEvery(now - send.QueuedUtc) || now < asked;

    public static bool NoteDue(QueuedSend send, DateTime now)
        => send.NotedUtc is not { } noted || now - noted >= NoteEvery || now < noted;

    /// <summary>How long until something is due for any of them; null when nothing waits.</summary>
    public static TimeSpan? NextLook(IEnumerable<QueuedSend> queue, DateTime now)
    {
        TimeSpan? next = null;
        foreach (var send in queue)
        {
            var times = new List<TimeSpan>
            {
                send.QueuedUtc + GiveUpAfter - now,
                (send.AskedUtc ?? DateTime.MinValue) + AskEvery(now - send.QueuedUtc) - now,
                (send.NotedUtc ?? DateTime.MinValue) + NoteEvery - now,
            };
            var soonest = times.Min();
            if (soonest < TimeSpan.Zero) soonest = TimeSpan.Zero;
            if (next == null || soonest < next) next = soonest;
        }
        return next;
    }

    /// <summary>"Holiday photos.zip", "3 files", "a text".</summary>
    public static string Describe(QueuedSend send)
        => send.Text ? "a text" : send.Paths.Count == 1 ? Path.GetFileName(send.Paths[0]) : $"{send.Paths.Count} files";

    /// <summary>Whether a path is one Holm made for sending (and so may move and delete).</summary>
    public static bool IsHolmsOwn(string path, string tempFolder) => PrivateStorage.IsInside(tempFolder, path);

    internal static bool IsSafeId(string id) => Guid.TryParseExact(id, "N", out _);
}
