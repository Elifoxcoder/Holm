using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace WinNotch;

public sealed record LyricLine(TimeSpan Time, string Text);

public sealed record SyncedLyrics(string TrackKey, IReadOnlyList<LyricLine> Lines);

/// <summary>
/// Fetches genuine time-synced (LRC) lyrics from LRCLIB.net with millisecond timestamps.
/// Never falls back to fake duration-based interpolation when synced timestamps are unavailable.
/// </summary>
public static class LyricsService
{
    static readonly HttpClient Http = CreateClient();
    static readonly ConcurrentDictionary<string, SyncedLyrics?> Cache = new(StringComparer.OrdinalIgnoreCase);
    static readonly Regex TimestampRegex = new(@"\[(\d{1,3}):(\d{2})(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled);
    static readonly Regex SuffixNoiseRegex = new(@"\s*[\(\[](?:feat\.?|ft\.?|remaster(?:ed)?|live|deluxe|radio edit|bonus track|explicit|clean|version)[^\)\]]*[\)\]]|\s+-\s+(?:remaster(?:ed)?|live|single version|radio edit|feat\.?).*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("HolyNotch/2.5 (https://holynotch.elifox.ch)");
        return client;
    }

    public static string MakeTrackKey(string title, string artist)
        => $"{title.Trim()}|{artist.Trim()}";

    public static string CleanTitle(string title)
    {
        string cleaned = SuffixNoiseRegex.Replace(title ?? "", "").Trim();
        return cleaned.Length > 0 ? cleaned : (title ?? "").Trim();
    }

    /// <summary>
    /// Parses standard LRC format into sorted timestamped lines.
    /// Supports multiple timestamps per line (e.g. [00:12.50][01:20.00]Chorus).
    /// Returns null if fewer than 2 valid timestamped lines exist.
    /// </summary>
    public static IReadOnlyList<LyricLine>? ParseLrc(string? lrc)
    {
        if (string.IsNullOrWhiteSpace(lrc)) return null;
        var lines = new List<LyricLine>();
        foreach (string raw in lrc.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            var matches = TimestampRegex.Matches(line);
            if (matches.Count == 0) continue;

            string text = TimestampRegex.Replace(line, "").Trim();
            if (text.Length == 0) continue;

            foreach (Match m in matches)
            {
                if (!int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int mins)) continue;
                if (!int.TryParse(m.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int secs)) continue;
                int ms = 0;
                if (m.Groups[3].Success)
                {
                    string frac = m.Groups[3].Value;
                    if (frac.Length == 1) ms = int.Parse(frac, CultureInfo.InvariantCulture) * 100;
                    else if (frac.Length == 2) ms = int.Parse(frac, CultureInfo.InvariantCulture) * 10;
                    else ms = int.Parse(frac[..3], CultureInfo.InvariantCulture);
                }
                lines.Add(new LyricLine(new TimeSpan(0, 0, mins, secs, ms), text));
            }
        }
        if (lines.Count < 2) return null;
        lines.Sort((a, b) => a.Time.CompareTo(b.Time));
        return lines;
    }

    /// <summary>
    /// Finds the active lyric line index and normalized progress [0..1] within that line at <paramref name="position"/>.
    /// Uses a 120ms lookahead so transitions feel crisp on the beat.
    /// </summary>
    public static (int Index, double LineProgress) FindActive(IReadOnlyList<LyricLine> lines, TimeSpan position, TimeSpan trackDuration)
    {
        if (lines.Count == 0) return (-1, 0);
        var effective = position + TimeSpan.FromMilliseconds(120);
        if (effective < lines[0].Time) return (-1, 0);

        int lo = 0, hi = lines.Count - 1, idx = 0;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (lines[mid].Time <= effective) { idx = mid; lo = mid + 1; }
            else hi = mid - 1;
        }

        TimeSpan start = lines[idx].Time;
        TimeSpan end = idx + 1 < lines.Count
            ? lines[idx + 1].Time
            : (trackDuration > start ? trackDuration : start + TimeSpan.FromSeconds(5));
        double spanMs = Math.Max(400, (end - start).TotalMilliseconds);
        // Slightly faster word sweep (finish highlighting ~88% into the line window, like Apple Music)
        double progress = Math.Clamp((effective - start).TotalMilliseconds / (spanMs * 0.88), 0, 1);
        return (idx, progress);
    }

    public static async Task<SyncedLyrics?> FetchAsync(string title, string artist, string? album, TimeSpan duration, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist)) return null;
        string key = MakeTrackKey(title, artist);
        if (Cache.TryGetValue(key, out var cached)) return cached;

        SyncedLyrics? result = null;
        try
        {
            // 1. Exact lookup via /api/get
            result = await TryGetExactAsync(key, title.Trim(), artist.Trim(), album?.Trim(), duration, ct).ConfigureAwait(false);

            // 2. Fallback to /api/search with original & cleaned title (still strictly requiring syncedLyrics!)
            if (result == null)
            {
                string clean = CleanTitle(title);
                result = await TrySearchAsync(key, clean, artist.Trim(), duration, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Diag.Log("Lyrics fetch: " + ex.Message); return null; }

        if (Cache.Count > 64) Cache.Clear();
        Cache[key] = result;
        return result;
    }

    static async Task<SyncedLyrics?> TryGetExactAsync(string key, string title, string artist, string? album, TimeSpan duration, CancellationToken ct)
    {
        var query = $"track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(artist)}";
        if (!string.IsNullOrWhiteSpace(album)) query += $"&album_name={Uri.EscapeDataString(album)}";
        if (duration.TotalSeconds >= 5) query += $"&duration={(int)Math.Round(duration.TotalSeconds)}";

        using var res = await Http.GetAsync("https://lrclib.net/api/get?" + query, ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode) return null;
        string json = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("syncedLyrics", out var syncedEl) && syncedEl.ValueKind == JsonValueKind.String)
        {
            var parsed = ParseLrc(syncedEl.GetString());
            if (parsed != null) return new SyncedLyrics(key, parsed);
        }
        return null;
    }

    static async Task<SyncedLyrics?> TrySearchAsync(string key, string title, string artist, TimeSpan duration, CancellationToken ct)
    {
        // Take primary artist if multiple are comma/semicolon separated
        string primaryArtist = artist.Split(new[] { ',', ';', '&' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? artist;
        var query = $"track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(primaryArtist)}";

        using var res = await Http.GetAsync("https://lrclib.net/api/search?" + query, ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode) return null;
        string json = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

        IReadOnlyList<LyricLine>? bestLines = null;
        double bestDiff = double.MaxValue;
        double targetSec = duration.TotalSeconds;

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("syncedLyrics", out var syncedEl) || syncedEl.ValueKind != JsonValueKind.String) continue;
            var parsed = ParseLrc(syncedEl.GetString());
            if (parsed == null) continue;

            double diff = 0;
            if (targetSec >= 5 && item.TryGetProperty("duration", out var durEl) && durEl.TryGetDouble(out double d))
                diff = Math.Abs(d - targetSec);
            if (diff < bestDiff)
            {
                bestDiff = diff;
                bestLines = parsed;
                if (diff <= 1.5) break;
            }
        }
        // Only accept if duration is reasonably close (or unknown) so we never show wrong-version timestamps
        if (bestLines != null && (targetSec < 5 || bestDiff <= 12))
            return new SyncedLyrics(key, bestLines);
        return null;
    }
}
