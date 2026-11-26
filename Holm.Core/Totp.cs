using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinNotch;

/// <summary>One TOTP (RFC 6238) authenticator entry, synced end-to-end across your own devices.</summary>
public sealed class TotpEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public string? Account { get; set; }
    /// <summary>Base32-encoded secret (uppercase, no spaces or padding).</summary>
    public string Secret { get; set; } = "";
    public int Digits { get; set; } = 6;
    public int Period { get; set; } = 30;
    public string Algorithm { get; set; } = "SHA1";
    public long UpdatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    public bool Deleted { get; set; }

    [JsonIgnore]
    public string DisplayTitle => !string.IsNullOrWhiteSpace(Title) ? Title.Trim()
        : !string.IsNullOrWhiteSpace(Account) ? Account.Trim() : "Authenticator";

    public TotpEntry Clone() => new()
    {
        Id = Id, Title = Title, Account = Account, Secret = Secret,
        Digits = Digits, Period = Period, Algorithm = Algorithm,
        UpdatedAt = UpdatedAt, Deleted = Deleted,
    };
}

/// <summary>RFC 6238 / RFC 4226 TOTP generator and Base32 codec.</summary>
public static class TotpCode
{
    const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string NormalizeSecret(string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret)) return "";
        var sb = new StringBuilder(secret.Length);
        foreach (char c in secret)
        {
            if (char.IsWhiteSpace(c) || c == '-' || c == '=') continue;
            char u = char.ToUpperInvariant(c);
            if (Base32Alphabet.IndexOf(u) < 0) return "";
            sb.Append(u);
        }
        return sb.ToString();
    }

    public static byte[]? DecodeBase32(string? secret)
    {
        string clean = NormalizeSecret(secret);
        if (clean.Length < 8) return null;
        int outLen = clean.Length * 5 / 8;
        if (outLen <= 0) return null;
        var output = new byte[outLen];
        int buffer = 0, bits = 0, idx = 0;
        foreach (char c in clean)
        {
            int val = Base32Alphabet.IndexOf(c);
            if (val < 0) return null;
            buffer = (buffer << 5) | val;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                if (idx < output.Length) output[idx++] = (byte)((buffer >> bits) & 0xFF);
            }
        }
        return output;
    }

    public static string EncodeBase32(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return "";
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (byte b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                sb.Append(Base32Alphabet[(buffer >> bits) & 31]);
            }
        }
        if (bits > 0) sb.Append(Base32Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    public static (string Code, int Remaining, int Period) Generate(TotpEntry entry, long? unixSeconds = null)
    {
        int period = entry.Period is >= 5 and <= 300 ? entry.Period : 30;
        int digits = entry.Digits is 6 or 7 or 8 ? entry.Digits : 6;
        long now = unixSeconds ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long counter = now / period;
        int remaining = period - (int)(now % period);

        byte[]? key = DecodeBase32(entry.Secret);
        if (key == null || key.Length == 0) return (new string('-', digits), remaining, period);

        Span<byte> msg = stackalloc byte[8];
        for (int i = 7; i >= 0; i--)
        {
            msg[i] = (byte)(counter & 0xFF);
            counter >>= 8;
        }

        byte[] hash = (entry.Algorithm ?? "SHA1").ToUpperInvariant() switch
        {
            "SHA256" => HMACSHA256.HashData(key, msg),
            "SHA512" => HMACSHA512.HashData(key, msg),
            _ => HMACSHA1.HashData(key, msg),
        };

        int offset = hash[^1] & 0x0F;
        int binary = ((hash[offset] & 0x7F) << 24)
                   | ((hash[offset + 1] & 0xFF) << 16)
                   | ((hash[offset + 2] & 0xFF) << 8)
                   | (hash[offset + 3] & 0xFF);

        int mod = digits switch { 7 => 10_000_000, 8 => 100_000_000, _ => 1_000_000 };
        string code = (binary % mod).ToString().PadLeft(digits, '0');
        return (code, remaining, period);
    }

    public static string Format(string code)
    {
        if (code.Length == 6) return code[..3] + " " + code[3..];
        if (code.Length == 8) return code[..4] + " " + code[4..];
        return code;
    }
}

/// <summary>
/// Parses TOTP entries from otpauth:// URIs, Google Authenticator otpauth-migration:// protobuf exports,
/// Aegis / 2FAS JSON exports, newline-separated URI lists, and raw Base32 setup keys.
/// </summary>
public static class TotpParser
{
    public static List<TotpEntry> ParseAny(string? input, string? fallbackTitle = null)
    {
        var results = new List<TotpEntry>();
        if (string.IsNullOrWhiteSpace(input)) return results;
        string trimmed = input.Trim();

        if (trimmed.StartsWith("{", StringComparison.Ordinal) || trimmed.StartsWith("[", StringComparison.Ordinal))
        {
            results.AddRange(ParseJsonExport(trimmed));
            if (results.Count > 0) return results;
        }

        // Line-by-line URIs (supports otpauth:// and otpauth-migration://)
        var lines = trimmed.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (line.StartsWith("otpauth-migration://", StringComparison.OrdinalIgnoreCase))
                results.AddRange(ParseGoogleMigration(line));
            else if (line.StartsWith("otpauth://", StringComparison.OrdinalIgnoreCase))
            {
                if (ParseOtpAuthUri(line) is { } entry) results.Add(entry);
            }
        }
        if (results.Count > 0) return results;

        // Raw Base32 secret
        string clean = TotpCode.NormalizeSecret(trimmed);
        if (clean.Length is >= 16 and <= 128 && TotpCode.DecodeBase32(clean) != null)
        {
            results.Add(new TotpEntry
            {
                Title = string.IsNullOrWhiteSpace(fallbackTitle) ? "Authenticator" : fallbackTitle.Trim(),
                Secret = clean,
            });
        }
        return results;
    }

    /// <summary>
    /// Detects TOTP URIs or setup keys on the clipboard without false-positiving on normal words, hex hashes, or UUIDs.
    /// </summary>
    public static List<TotpEntry>? DetectInClipboard(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string trimmed = text.Trim();
        if (trimmed.Length > 8192) return null;

        if (trimmed.StartsWith("otpauth-migration://", StringComparison.OrdinalIgnoreCase))
        {
            var list = ParseGoogleMigration(trimmed);
            return list.Count > 0 ? list : null;
        }
        if (trimmed.StartsWith("otpauth://totp/", StringComparison.OrdinalIgnoreCase))
        {
            var entry = ParseOtpAuthUri(trimmed);
            return entry != null ? new List<TotpEntry> { entry } : null;
        }

        // Standalone Base32 setup key heuristic:
        // Must be single line, 16..64 Base32 chars after stripping spaces/hyphens/=,
        // must only contain [A-Za-z2-7 \- =], must not be all digits or pure hex (unless 4-char grouped with spaces).
        if (trimmed.Contains('\n') || trimmed.Contains('\r') || trimmed.Length > 96) return null;
        string clean = TotpCode.NormalizeSecret(trimmed);
        if (clean.Length is < 16 or > 64) return null;
        if (clean.All(char.IsDigit) || clean.All(char.IsLetter)) return null;

        bool grouped4 = trimmed.Contains(' ') && trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(p => p.Length == 4);
        bool allHex = clean.All(c => (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F'));
        // Avoid matching random 32-char MD5 / SHA hex strings or UUIDs unless formatted in 4-char TOTP groups.
        if (allHex && !grouped4) return null;
        if (Guid.TryParse(trimmed, out _)) return null;
        // Require at least one letter G-Z (uniquely Base32) OR 4-char grouping with uppercase/digits.
        bool hasBase32Letter = clean.Any(c => c is >= 'G' and <= 'Z');
        if (!hasBase32Letter && !grouped4) return null;

        return new List<TotpEntry>
        {
            new() { Title = "New 2FA Code", Secret = clean },
        };
    }

    public static TotpEntry? ParseOtpAuthUri(string uriText)
    {
        if (!Uri.TryCreate(uriText.Trim(), UriKind.Absolute, out var uri)) return null;
        if (!string.Equals(uri.Scheme, "otpauth", StringComparison.OrdinalIgnoreCase)) return null;
        if (!string.Equals(uri.Host, "totp", StringComparison.OrdinalIgnoreCase)) return null;

        var query = ParseQuery(uri.Query);
        if (!query.TryGetValue("secret", out string? rawSecret)) return null;
        string secret = TotpCode.NormalizeSecret(rawSecret);
        if (secret.Length < 8 || TotpCode.DecodeBase32(secret) == null) return null;

        string label = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
        string? issuer = query.TryGetValue("issuer", out string? iss) && !string.IsNullOrWhiteSpace(iss) ? iss.Trim() : null;
        string? account = null;
        int colon = label.IndexOf(':');
        if (colon >= 0)
        {
            if (string.IsNullOrWhiteSpace(issuer)) issuer = label[..colon].Trim();
            account = label[(colon + 1)..].Trim();
        }
        else if (!string.IsNullOrWhiteSpace(label))
        {
            if (string.IsNullOrWhiteSpace(issuer)) issuer = label.Trim();
            else account = label.Trim();
        }

        int digits = query.TryGetValue("digits", out string? d) && int.TryParse(d, out int dv) && dv is 6 or 7 or 8 ? dv : 6;
        int period = query.TryGetValue("period", out string? p) && int.TryParse(p, out int pv) && pv is >= 5 and <= 300 ? pv : 30;
        string algo = query.TryGetValue("algorithm", out string? a) && a.ToUpperInvariant() is "SHA1" or "SHA256" or "SHA512"
            ? a.ToUpperInvariant() : "SHA1";

        return new TotpEntry
        {
            Title = !string.IsNullOrWhiteSpace(issuer) ? issuer : (!string.IsNullOrWhiteSpace(account) ? account : "Authenticator"),
            Account = string.IsNullOrWhiteSpace(account) || string.Equals(account, issuer, StringComparison.OrdinalIgnoreCase) ? null : account,
            Secret = secret,
            Digits = digits,
            Period = period,
            Algorithm = algo,
        };
    }

    /// <summary>Decodes Google Authenticator's otpauth-migration://offline?data=... protobuf payload.</summary>
    public static List<TotpEntry> ParseGoogleMigration(string uriText)
    {
        var results = new List<TotpEntry>();
        if (!Uri.TryCreate(uriText.Trim(), UriKind.Absolute, out var uri)) return results;
        var query = ParseQuery(uri.Query);
        if (!query.TryGetValue("data", out string? b64) || string.IsNullOrWhiteSpace(b64)) return results;

        byte[] bytes;
        try
        {
            string normalized = b64.Replace(' ', '+').Replace('-', '+').Replace('_', '/');
            int pad = normalized.Length % 4;
            if (pad > 0) normalized += new string('=', 4 - pad);
            bytes = Convert.FromBase64String(normalized);
        }
        catch (FormatException) { return results; }

        int pos = 0;
        while (pos < bytes.Length)
        {
            if (!ReadVarint(bytes, ref pos, out ulong tag)) break;
            int field = (int)(tag >> 3);
            int wire = (int)(tag & 7);
            if (field == 1 && wire == 2)
            {
                if (!ReadLengthDelimited(bytes, ref pos, out var paramBytes)) break;
                if (DecodeGoogleOtpParam(paramBytes) is { } entry) results.Add(entry);
            }
            else if (!SkipField(bytes, ref pos, wire)) break;
        }
        return results;
    }

    static TotpEntry? DecodeGoogleOtpParam(ReadOnlySpan<byte> buf)
    {
        byte[]? secretBytes = null;
        string? name = null, issuer = null;
        int algo = 1, digits = 1, type = 2; // defaults: SHA1, 6 digits, TOTP

        int pos = 0;
        while (pos < buf.Length)
        {
            if (!ReadVarint(buf, ref pos, out ulong tag)) break;
            int field = (int)(tag >> 3);
            int wire = (int)(tag & 7);
            switch (field)
            {
                case 1 when wire == 2:
                    if (!ReadLengthDelimited(buf, ref pos, out var s)) return null;
                    secretBytes = s.ToArray();
                    break;
                case 2 when wire == 2:
                    if (!ReadLengthDelimited(buf, ref pos, out var n)) return null;
                    name = Encoding.UTF8.GetString(n);
                    break;
                case 3 when wire == 2:
                    if (!ReadLengthDelimited(buf, ref pos, out var iss)) return null;
                    issuer = Encoding.UTF8.GetString(iss);
                    break;
                case 4 when wire == 0:
                    if (!ReadVarint(buf, ref pos, out ulong a)) return null;
                    algo = (int)a;
                    break;
                case 5 when wire == 0:
                    if (!ReadVarint(buf, ref pos, out ulong d)) return null;
                    digits = (int)d;
                    break;
                case 6 when wire == 0:
                    if (!ReadVarint(buf, ref pos, out ulong t)) return null;
                    type = (int)t;
                    break;
                default:
                    if (!SkipField(buf, ref pos, wire)) return null;
                    break;
            }
        }
        // Only TOTP (type == 2 or unspecified 0); skip HOTP (1).
        if (type == 1 || secretBytes == null || secretBytes.Length == 0) return null;
        string secret = TotpCode.EncodeBase32(secretBytes);
        if (secret.Length < 8) return null;

        string? title = !string.IsNullOrWhiteSpace(issuer) ? issuer.Trim() : null;
        string? account = !string.IsNullOrWhiteSpace(name) ? name.Trim() : null;
        if (account != null && account.Contains(':'))
        {
            int c = account.IndexOf(':');
            if (title == null) title = account[..c].Trim();
            account = account[(c + 1)..].Trim();
        }
        return new TotpEntry
        {
            Title = !string.IsNullOrWhiteSpace(title) ? title : (!string.IsNullOrWhiteSpace(account) ? account : "Authenticator"),
            Account = string.Equals(account, title, StringComparison.OrdinalIgnoreCase) ? null : account,
            Secret = secret,
            Digits = digits == 2 ? 8 : 6,
            Period = 30,
            Algorithm = algo switch { 2 => "SHA256", 3 => "SHA512", _ => "SHA1" },
        };
    }

    static List<TotpEntry> ParseJsonExport(string json)
    {
        var results = new List<TotpEntry>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Aegis JSON export: { "db": { "entries": [ { "type": "totp", "name": "...", "issuer": "...", "info": { "secret": "...", "algo": "SHA1", "digits": 6, "period": 30 } } ] } }
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("db", out var db)
                && db.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in entries.EnumerateArray())
                {
                    if (Str(e, "type")?.Equals("totp", StringComparison.OrdinalIgnoreCase) == false) continue;
                    if (!e.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Object) continue;
                    string secret = TotpCode.NormalizeSecret(Str(info, "secret"));
                    if (secret.Length < 8) continue;
                    string? iss = Str(e, "issuer"), name = Str(e, "name");
                    results.Add(new TotpEntry
                    {
                        Title = !string.IsNullOrWhiteSpace(iss) ? iss.Trim() : (!string.IsNullOrWhiteSpace(name) ? name.Trim() : "Authenticator"),
                        Account = !string.IsNullOrWhiteSpace(name) && !string.Equals(name, iss, StringComparison.OrdinalIgnoreCase) ? name.Trim() : null,
                        Secret = secret,
                        Digits = Int(info, "digits", 6),
                        Period = Int(info, "period", 30),
                        Algorithm = Str(info, "algo")?.ToUpperInvariant() is "SHA256" or "SHA512" ? Str(info, "algo")!.ToUpperInvariant() : "SHA1",
                    });
                }
                return results;
            }

            // 2FAS JSON export: { "services": [ { "name": "...", "secret": "...", "otp": { "account": "...", "digits": 6, "period": 30, "algorithm": "SHA1", "tokenType": "TOTP" } } ] }
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("services", out var services) && services.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in services.EnumerateArray())
                {
                    string secret = TotpCode.NormalizeSecret(Str(s, "secret"));
                    if (secret.Length < 8) continue;
                    int digits = 6, period = 30;
                    string algo = "SHA1";
                    string? account = null;
                    if (s.TryGetProperty("otp", out var otp) && otp.ValueKind == JsonValueKind.Object)
                    {
                        if (Str(otp, "tokenType")?.Equals("HOTP", StringComparison.OrdinalIgnoreCase) == true) continue;
                        digits = Int(otp, "digits", 6);
                        period = Int(otp, "period", 30);
                        algo = Str(otp, "algorithm")?.ToUpperInvariant() is "SHA256" or "SHA512" ? Str(otp, "algorithm")!.ToUpperInvariant() : "SHA1";
                        account = Str(otp, "account");
                    }
                    string title = Str(s, "name")?.Trim() ?? account ?? "Authenticator";
                    results.Add(new TotpEntry
                    {
                        Title = title,
                        Account = string.Equals(account, title, StringComparison.OrdinalIgnoreCase) ? null : account,
                        Secret = secret,
                        Digits = digits,
                        Period = period,
                        Algorithm = algo,
                    });
                }
                return results;
            }
        }
        catch (JsonException) { }
        return results;
    }

    static string? Str(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static int Int(JsonElement e, string prop, int fallback)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : fallback;

    static Dictionary<string, string> ParseQuery(string query)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string q = query.TrimStart('?');
        foreach (string part in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;
            string k = Uri.UnescapeDataString(part[..eq]);
            string v = Uri.UnescapeDataString(part[(eq + 1)..]);
            map[k] = v;
        }
        return map;
    }

    static bool ReadVarint(ReadOnlySpan<byte> buf, ref int pos, out ulong value)
    {
        value = 0;
        int shift = 0;
        while (pos < buf.Length && shift < 64)
        {
            byte b = buf[pos++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return true;
            shift += 7;
        }
        return false;
    }

    static bool ReadLengthDelimited(ReadOnlySpan<byte> buf, ref int pos, out ReadOnlySpan<byte> slice)
    {
        slice = default;
        if (!ReadVarint(buf, ref pos, out ulong len) || len > (ulong)(buf.Length - pos)) return false;
        slice = buf.Slice(pos, (int)len);
        pos += (int)len;
        return true;
    }

    static bool SkipField(ReadOnlySpan<byte> buf, ref int pos, int wire)
    {
        switch (wire)
        {
            case 0: return ReadVarint(buf, ref pos, out _);
            case 1: if (pos + 8 > buf.Length) return false; pos += 8; return true;
            case 2: return ReadLengthDelimited(buf, ref pos, out _);
            case 5: if (pos + 4 > buf.Length) return false; pos += 4; return true;
            default: return false;
        }
    }
}

/// <summary>Encrypted-at-rest local store for TOTP entries with CRDT-style timestamp merge for cross-device sync.</summary>
public sealed class TotpStore
{
    const string SealedPrefix = "V1.";
    static readonly byte[] StorageAad = "HolyNotch TotpStore v1"u8.ToArray();
    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    readonly object _gate = new();
    readonly string _file;
    readonly byte[]? _key;
    readonly Dictionary<string, TotpEntry> _entries = new(StringComparer.Ordinal);

    public event Action? Changed;

    public TotpStore(string file, byte[]? storageKey = null)
    {
        _file = file;
        _key = storageKey is { Length: 32 } ? (byte[])storageKey.Clone() : null;
        Load();
    }

    public IReadOnlyList<TotpEntry> Active
    {
        get
        {
            lock (_gate)
            {
                return _entries.Values
                    .Where(e => !e.Deleted)
                    .OrderBy(e => e.DisplayTitle, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(e => e.Id, StringComparer.Ordinal)
                    .Select(e => e.Clone())
                    .ToList();
            }
        }
    }

    public IReadOnlyList<TotpEntry> AllForSync
    {
        get
        {
            lock (_gate) return _entries.Values.Select(e => e.Clone()).ToList();
        }
    }

    public bool HasSecret(string secret)
    {
        string norm = TotpCode.NormalizeSecret(secret);
        if (norm.Length == 0) return false;
        lock (_gate) return _entries.Values.Any(e => !e.Deleted && string.Equals(e.Secret, norm, StringComparison.OrdinalIgnoreCase));
    }

    public TotpEntry? Add(TotpEntry entry)
    {
        string secret = TotpCode.NormalizeSecret(entry.Secret);
        if (secret.Length < 8 || TotpCode.DecodeBase32(secret) == null) return null;
        var clean = entry.Clone();
        clean.Secret = secret;
        clean.Id = string.IsNullOrWhiteSpace(clean.Id) ? Guid.NewGuid().ToString("N") : clean.Id.Trim();
        clean.Title = clean.DisplayTitle;
        clean.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        clean.Deleted = false;

        lock (_gate)
        {
            // Deduplicate by secret if an active entry with the same secret already exists
            var existing = _entries.Values.FirstOrDefault(e => !e.Deleted && string.Equals(e.Secret, secret, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.Title = clean.Title;
                existing.Account = clean.Account ?? existing.Account;
                existing.UpdatedAt = clean.UpdatedAt;
                clean = existing.Clone();
            }
            else _entries[clean.Id] = clean;
            Save();
        }
        Changed?.Invoke();
        return clean;
    }

    public List<TotpEntry> AddMany(IEnumerable<TotpEntry> incoming)
    {
        var added = new List<TotpEntry>();
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        lock (_gate)
        {
            foreach (var raw in incoming)
            {
                string secret = TotpCode.NormalizeSecret(raw.Secret);
                if (secret.Length < 8 || TotpCode.DecodeBase32(secret) == null) continue;
                var existing = _entries.Values.FirstOrDefault(e => !e.Deleted && string.Equals(e.Secret, secret, StringComparison.OrdinalIgnoreCase));
                if (existing != null) continue;
                var clean = raw.Clone();
                clean.Secret = secret;
                clean.Id = string.IsNullOrWhiteSpace(clean.Id) ? Guid.NewGuid().ToString("N") : clean.Id.Trim();
                clean.Title = clean.DisplayTitle;
                clean.UpdatedAt = now;
                clean.Deleted = false;
                _entries[clean.Id] = clean;
                added.Add(clean.Clone());
            }
            if (added.Count > 0) Save();
        }
        if (added.Count > 0) Changed?.Invoke();
        return added;
    }

    public TotpEntry? Delete(string id)
    {
        TotpEntry? tombstone = null;
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out var existing) || existing.Deleted) return null;
            existing.Deleted = true;
            existing.UpdatedAt = Math.Max(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), existing.UpdatedAt + 1);
            tombstone = existing.Clone();
            Save();
        }
        Changed?.Invoke();
        return tombstone;
    }

    public bool Merge(IEnumerable<TotpEntry> remote)
    {
        bool changed = false;
        lock (_gate)
        {
            foreach (var raw in remote)
            {
                if (string.IsNullOrWhiteSpace(raw.Id)) continue;
                string secret = raw.Deleted ? "" : TotpCode.NormalizeSecret(raw.Secret);
                if (!raw.Deleted && (secret.Length < 8 || TotpCode.DecodeBase32(secret) == null)) continue;

                if (_entries.TryGetValue(raw.Id, out var local))
                {
                    if (raw.UpdatedAt > local.UpdatedAt || (raw.UpdatedAt == local.UpdatedAt && raw.Deleted && !local.Deleted))
                    {
                        var next = raw.Clone();
                        next.Secret = raw.Deleted ? local.Secret : secret;
                        _entries[raw.Id] = next;
                        changed = true;
                    }
                }
                else
                {
                    // Avoid duplicate active secret from different IDs
                    if (!raw.Deleted && _entries.Values.Any(e => !e.Deleted && string.Equals(e.Secret, secret, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    var next = raw.Clone();
                    next.Secret = secret;
                    _entries[raw.Id] = next;
                    changed = true;
                }
            }
            if (changed) Save();
        }
        if (changed) Changed?.Invoke();
        return changed;
    }

    void Load()
    {
        if (!File.Exists(_file)) return;
        try
        {
            string raw = File.ReadAllText(_file, Encoding.UTF8).Trim();
            string? json = null;
            if (raw.StartsWith(SealedPrefix, StringComparison.Ordinal) && _key != null)
            {
                byte[] blob = Convert.FromBase64String(raw[SealedPrefix.Length..]);
                if (blob.Length >= 28)
                {
                    var plain = new byte[blob.Length - 28];
                    using var aes = new AesGcm(_key, 16);
                    aes.Decrypt(blob.AsSpan(0, 12), blob.AsSpan(28), blob.AsSpan(12, 16), plain, StorageAad);
                    json = Encoding.UTF8.GetString(plain);
                }
            }
            else if (raw.StartsWith("[", StringComparison.Ordinal))
            {
                json = raw;
            }
            if (json == null) return;
            var list = JsonSerializer.Deserialize<List<TotpEntry>>(json, JsonOpts);
            if (list == null) return;
            foreach (var e in list)
                if (!string.IsNullOrWhiteSpace(e.Id)) _entries[e.Id] = e;
        }
        catch { }
    }

    void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            byte[] plain = JsonSerializer.SerializeToUtf8Bytes(_entries.Values.ToList(), JsonOpts);
            string content;
            if (_key != null)
            {
                var nonce = RandomNumberGenerator.GetBytes(12);
                var cipher = new byte[plain.Length];
                var tag = new byte[16];
                using var aes = new AesGcm(_key, 16);
                aes.Encrypt(nonce, plain, cipher, tag, StorageAad);
                var blob = new byte[12 + 16 + cipher.Length];
                Buffer.BlockCopy(nonce, 0, blob, 0, 12);
                Buffer.BlockCopy(tag, 0, blob, 12, 16);
                Buffer.BlockCopy(cipher, 0, blob, 28, cipher.Length);
                content = SealedPrefix + Convert.ToBase64String(blob);
            }
            else content = Encoding.UTF8.GetString(plain);

            string tmp = _file + ".tmp";
            File.WriteAllText(tmp, content, Encoding.UTF8);
            File.Move(tmp, _file, overwrite: true);
        }
        catch { }
    }
}
