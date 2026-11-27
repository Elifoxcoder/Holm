using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WinNotch;

/// <summary>One work interval recorded on extranet.bbcag.ch for today.</summary>
public sealed record ExtranetInterval(TimeSpan From, TimeSpan? To);

/// <summary>Live calculation of today's work time, remaining duration, and target finish time.</summary>
public sealed record ExtranetSnapshot(
    DateTime FetchedAt,
    IReadOnlyList<ExtranetInterval> Intervals,
    int TargetMinutes,
    bool SignedIn,
    string? Error = null)
{
    public bool IsClockedIn => Intervals.Count > 0 && Intervals[^1].To == null;

    /// <summary>Total worked duration today as of <paramref name="now"/>.</summary>
    public TimeSpan WorkedAt(DateTime now)
    {
        var total = TimeSpan.Zero;
        var nowTime = now.TimeOfDay;
        foreach (var interval in Intervals)
        {
            var end = interval.To ?? nowTime;
            var diff = end - interval.From;
            if (diff > TimeSpan.Zero) total += diff;
        }
        return total;
    }

    public TimeSpan Target => TimeSpan.FromMinutes(Math.Clamp(TargetMinutes, 60, 16 * 60));

    public TimeSpan RemainingAt(DateTime now)
    {
        var rem = Target - WorkedAt(now);
        return rem > TimeSpan.Zero ? rem : TimeSpan.Zero;
    }

    public TimeSpan OvertimeAt(DateTime now)
    {
        var over = WorkedAt(now) - Target;
        return over > TimeSpan.Zero ? over : TimeSpan.Zero;
    }

    public bool CanBadgeOutAt(DateTime now) => Intervals.Count > 0 && WorkedAt(now) >= Target;

    public DateTime? FinishAt(DateTime now)
    {
        var rem = RemainingAt(now);
        if (rem <= TimeSpan.Zero) return null;
        return now + rem;
    }

    public static string FormatDuration(TimeSpan d)
    {
        int totalMin = (int)Math.Ceiling(Math.Max(0, d.TotalMinutes));
        int h = totalMin / 60, m = totalMin % 60;
        return h > 0 ? $"{h}h {m:00}m" : $"{m}m";
    }
}

/// <summary>
/// Per-device client for extranet.bbcag.ch (OAuth 2.0 + PKCE via api.extranet.bbcag.ch).
/// Stores only the OAuth refresh token locally (never the password, never synced via Account).
/// </summary>
public static class ExtranetClient
{
    public const string WebUrl = "https://extranet.bbcag.ch/timesheet/record";
    const string ApiBase = "https://api.extranet.bbcag.ch";
    const string ClientId = "7R6bulmurfMbXsYQUdOKa1Hg9Z3I5QNq";
    const string RedirectUri = "https://extranet.bbcag.ch";
    const string Scope = "account:manage";

    public sealed record AuthResult(string AccessToken, string RefreshToken, int ExpiresInSeconds, string? UserName);

    /// <summary>Signs in via the official OAuth 2.0 PKCE flow and returns tokens without storing the password.</summary>
    public static async Task<AuthResult> SignInAsync(string email, string password, CancellationToken ct = default)
    {
        email = email.Trim();
        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
            throw new InvalidOperationException("Please enter your Extranet email and password.");

        var cookies = new CookieContainer();
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = cookies };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };

        string verifier = RandomBase64Url(32);
        string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        string state = RandomBase64Url(16);

        string authUrl = $"{ApiBase}/oauth/auth" +
            $"?response_type=code&client_id={Uri.EscapeDataString(ClientId)}" +
            $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
            $"&scope={Uri.EscapeDataString(Scope)}" +
            $"&state={Uri.EscapeDataString(state)}" +
            $"&code_challenge={Uri.EscapeDataString(challenge)}" +
            $"&code_challenge_method=S256";

        using var authResp = await http.GetAsync(authUrl, ct);
        string? loc = authResp.Headers.Location?.ToString();
        if (string.IsNullOrEmpty(loc) || !loc.Contains("/flow/", StringComparison.Ordinal) || loc.Contains("/flow/error", StringComparison.Ordinal))
            throw new InvalidOperationException("Extranet login flow could not be started.");

        string flowId = loc.Split("/flow/")[1].Split('?')[0];

        using var loginContent = new StringContent(
            JsonSerializer.Serialize(new { username = email, password }),
            Encoding.UTF8, "application/json");
        using var loginResp = await http.PostAsync($"{ApiBase}/api/account/v1/authentication/flow/{Uri.EscapeDataString(flowId)}/usernamepassword", loginContent, ct);
        string loginBody = await loginResp.Content.ReadAsStringAsync(ct);
        if (!loginResp.IsSuccessStatusCode)
            throw new InvalidOperationException("Invalid Extranet email or password.");

        string? userName = null;
        try
        {
            using var doc = JsonDocument.Parse(loginBody);
            if (doc.RootElement.TryGetProperty("userName", out var un)) userName = un.GetString();
            if (doc.RootElement.TryGetProperty("step", out var step) && step.GetString() is { } s && !s.Equals("finish", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Additional login step required ({s}).");
        }
        catch (JsonException) { }

        using var finishContent = new StringContent("{}", Encoding.UTF8, "application/json");
        using var finishResp = await http.PostAsync($"{ApiBase}/api/account/v1/authentication/flow/{Uri.EscapeDataString(flowId)}/finish", finishContent, ct);
        string finishBody = await finishResp.Content.ReadAsStringAsync(ct);
        if (!finishResp.IsSuccessStatusCode)
            throw new InvalidOperationException("Could not complete Extranet login.");

        string? redirectUri = null;
        using (var doc = JsonDocument.Parse(finishBody))
            if (doc.RootElement.TryGetProperty("redirectUri", out var ru)) redirectUri = ru.GetString();
        if (string.IsNullOrEmpty(redirectUri) || !Uri.TryCreate(redirectUri, UriKind.Absolute, out var parsedRedir))
            throw new InvalidOperationException("Extranet did not return an authorization code.");

        string? code = ParseQueryParam(parsedRedir.Query, "code");
        if (string.IsNullOrEmpty(code))
            throw new InvalidOperationException("Missing OAuth code from Extranet.");

        using var tokenForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = RedirectUri,
            ["client_id"] = ClientId,
            ["code_verifier"] = verifier,
        });
        using var tokenResp = await http.PostAsync($"{ApiBase}/oauth/token", tokenForm, ct);
        string tokenBody = await tokenResp.Content.ReadAsStringAsync(ct);
        if (!tokenResp.IsSuccessStatusCode)
            throw new InvalidOperationException("Could not exchange Extranet authorization code.");

        return ParseTokenResponse(tokenBody, userName);
    }

    /// <summary>Exchanges a saved refresh token for a fresh access + rotated refresh token.</summary>
    public static async Task<AuthResult> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = ClientId,
        });
        using var resp = await http.PostAsync($"{ApiBase}/oauth/token", form, ct);
        string body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Extranet session expired ({(int)resp.StatusCode}). Please sign in again.");
        return ParseTokenResponse(body, null);
    }

    /// <summary>Fetches today's work intervals from /api/timesheet/v1/records/today.</summary>
    public static async Task<IReadOnlyList<ExtranetInterval>> FetchTodayAsync(string accessToken, CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/api/timesheet/v1/records/today");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await http.SendAsync(req, ct);
        string body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Extranet returned HTTP {(int)resp.StatusCode}.");
        return ParseTodayRecords(body);
    }

    public static IReadOnlyList<ExtranetInterval> ParseTodayRecords(string json)
    {
        var list = new List<ExtranetInterval>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (item.TryGetProperty("category", out var cat) && cat.GetString() is { } c && !c.Equals("work", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!item.TryGetProperty("interval", out var iv) || iv.ValueKind != JsonValueKind.Object) continue;
            if (!iv.TryGetProperty("from", out var fromEl) || fromEl.GetString() is not { } fromStr) continue;
            if (!TryParseHm(fromStr, out var from)) continue;
            TimeSpan? to = null;
            if (iv.TryGetProperty("to", out var toEl) && toEl.ValueKind == JsonValueKind.String && toEl.GetString() is { } toStr && TryParseHm(toStr, out var parsedTo))
                to = parsedTo;
            list.Add(new ExtranetInterval(from, to));
        }
        list.Sort((a, b) => a.From.CompareTo(b.From));
        return list;
    }

    static bool TryParseHm(string s, out TimeSpan result)
        => TimeSpan.TryParseExact(s.Trim(), new[] { @"hh\:mm", @"h\:mm", @"hh\:mm\:ss" }, CultureInfo.InvariantCulture, out result);

    static AuthResult ParseTokenResponse(string json, string? userName)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string access = root.GetProperty("access_token").GetString() ?? "";
        string refresh = root.TryGetProperty("refresh_token", out var rt) ? (rt.GetString() ?? "") : "";
        int expires = root.TryGetProperty("expires_in", out var ex) && ex.TryGetInt32(out int e) ? e : 300;
        if (string.IsNullOrEmpty(access)) throw new InvalidOperationException("Extranet returned an empty access token.");
        return new AuthResult(access, refresh, expires, userName);
    }

    static string? ParseQueryParam(string query, string key)
    {
        foreach (string part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;
            if (Uri.UnescapeDataString(part[..eq]).Equals(key, StringComparison.Ordinal))
                return Uri.UnescapeDataString(part[(eq + 1)..]);
        }
        return null;
    }

    static string RandomBase64Url(int bytes) => Base64Url(RandomNumberGenerator.GetBytes(bytes));
    static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
