using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace WinNotch;

public enum WindowSlot
{
    Morning,
    Noon,
    Afternoon,
}

public sealed record WindowDuty(DateOnly Date, WindowSlot Slot, string Person, TimeOnly OpenAt, TimeOnly CloseAt)
{
    public string SlotLabel => Slot switch
    {
        WindowSlot.Morning => "Morgen",
        WindowSlot.Noon => "Mittag",
        _ => "Nachmittag",
    };
}

public sealed record CleaningDuty(DateOnly Date, int TeamNumber, IReadOnlyList<string> Members, TimeOnly At);

public sealed record DutyNotification(string Key, string Title, string Body, bool IsCleaning);

public sealed record DutyBreakTimes(
    TimeOnly MorningOpen, TimeOnly MorningClose,
    TimeOnly NoonOpen, TimeOnly NoonClose,
    TimeOnly AfternoonOpen, TimeOnly AfternoonClose)
{
    public static readonly DutyBreakTimes Default = new(
        new TimeOnly(9, 28), new TimeOnly(9, 45),
        new TimeOnly(11, 28), new TimeOnly(12, 0),
        new TimeOnly(14, 58), new TimeOnly(15, 15));

    public (TimeOnly Open, TimeOnly Close) ForSlot(WindowSlot slot) => slot switch
    {
        WindowSlot.Morning => (MorningOpen, MorningClose),
        WindowSlot.Noon => (NoonOpen, NoonClose),
        _ => (AfternoonOpen, AfternoonClose),
    };

    public static TimeOnly ParseOr(string? value, TimeOnly fallback)
        => TimeOnly.TryParseExact(value?.Trim(), new[] { "HH:mm", "H:mm" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : fallback;
}

/// <summary>
/// Schedule for BBC window-opening ("Lüftungsdienst") and dining-room cleaning ("Putzdienst") duties.
/// </summary>
public static class DutySchedule
{
    // Teams for Friday 12:45 dining-room cleaning (from Putzdienst CSV).
    static readonly IReadOnlyDictionary<int, string[]> CleaningTeams = new Dictionary<int, string[]>
    {
        [1] = new[] { "Emily", "Nikola", "Oscar", "Niall" },
        [2] = new[] { "Leon", "Lorik", "Djordje", "Trevor" },
        [3] = new[] { "Erdi", "Bruno", "Elias", "Koa" },
        [4] = new[] { "Fynn", "Casimir", "Niklas", "Pascal M.", "Noam" },
        [5] = new[] { "Nevio", "Ayaan", "Luis", "Pascal S.", "Thomas" },
    };

    public static readonly TimeOnly CleaningTime = new(12, 45);

    // Scheduled cleaning Fridays (vacation weeks skipped per BBC Lehrablaufplan).
    static readonly (DateOnly Date, int Team)[] CleaningRotation =
    {
        (new DateOnly(2026, 9, 11), 1),
        (new DateOnly(2026, 9, 18), 2),
        (new DateOnly(2026, 9, 25), 3),
        (new DateOnly(2026, 10, 2), 4),
        // 2026-10-09 Herbstferien
        (new DateOnly(2026, 10, 16), 5),
        (new DateOnly(2026, 10, 23), 1),
        (new DateOnly(2026, 10, 30), 2),
        (new DateOnly(2026, 11, 6), 3),
        (new DateOnly(2026, 11, 13), 4),
        (new DateOnly(2026, 11, 20), 5),
        (new DateOnly(2026, 11, 27), 1),
        (new DateOnly(2026, 12, 4), 2),
        (new DateOnly(2026, 12, 11), 3),
        (new DateOnly(2026, 12, 18), 4),
        // 2026-12-25, 2027-01-01 Festtage
        (new DateOnly(2027, 1, 8), 5),
        (new DateOnly(2027, 1, 15), 1),
        (new DateOnly(2027, 1, 22), 2),
        (new DateOnly(2027, 1, 29), 3),
        (new DateOnly(2027, 2, 5), 4),
        (new DateOnly(2027, 2, 12), 5),
        (new DateOnly(2027, 2, 19), 1),
        (new DateOnly(2027, 2, 26), 2),
        (new DateOnly(2027, 3, 5), 3),
        (new DateOnly(2027, 3, 12), 4),
        (new DateOnly(2027, 3, 19), 5),
        (new DateOnly(2027, 3, 26), 1),
        (new DateOnly(2027, 4, 2), 2),
        (new DateOnly(2027, 4, 9), 3),
        (new DateOnly(2027, 4, 16), 4),
        (new DateOnly(2027, 4, 23), 5),
        // 2027-04-30, 2027-05-07 Frühlingsferien
        (new DateOnly(2027, 5, 14), 1),
        (new DateOnly(2027, 5, 21), 2),
        (new DateOnly(2027, 5, 28), 3),
        (new DateOnly(2027, 6, 4), 4),
        (new DateOnly(2027, 6, 11), 5),
        (new DateOnly(2027, 6, 18), 1),
        (new DateOnly(2027, 6, 25), 2),
        (new DateOnly(2027, 7, 2), 3),
        (new DateOnly(2027, 7, 9), 4),
        (new DateOnly(2027, 7, 16), 5),
        // 2027-07-23, 2027-07-30 Sommerferien
        (new DateOnly(2027, 8, 6), 1),
        (new DateOnly(2027, 8, 13), 2),
        (new DateOnly(2027, 8, 20), 3),
        (new DateOnly(2027, 8, 27), 4),
        (new DateOnly(2027, 9, 3), 5),
        (new DateOnly(2027, 9, 10), 1),
    };

    // Window-opening schedule indexed by (IsoYear, IsoWeek) -> 5 weekdays (Mon..Fri) x 3 slots (Morning, Noon, Afternoon).
    static readonly IReadOnlyDictionary<(int Year, int Week), string[,]> WindowWeeks = new Dictionary<(int, int), string[,]>
    {
        // November 2026
        [(2026, 47)] = new[,]
        {
            { "Andrii", "Even", "Luka Toni" },
            { "Aleandro", "Kirish", "Norbu" },
            { "Janis", "Fernando", "Giulia" },
            { "Ayaan", "Elias", "Koa" },
            { "Bruno", "Djordje", "Emily" },
        },
        [(2026, 48)] = new[,]
        {
            { "Malik", "Mile", "David" },
            { "Serhii", "Cédric", "Ivo" },
            { "Matyas", "Rui", "Hanna" },
            { "Luis", "Oscar", "Trevor" },
            { "Erdi", "Leon", "Niall" },
        },
        // Dezember 2026
        [(2026, 49)] = new[,]
        {
            { "Ngoedup", "Lio", "Dmytrii" },
            { "David", "Aleandro", "Kirish" },
            { "Pascal S.", "Nikola", "Casimir" },
            { "Thomas", "Ayaan", "Elias" },
            { "Emily", "Erdi", "Leon" },
        },
        [(2026, 50)] = new[,]
        {
            { "Matti", "Andrii", "Even" },
            { "Norbu", "Serhii", "Cédric" },
            { "Pascal M.", "Noam", "Nevio" },
            { "Koa", "Luis", "Oscar" },
            { "Niall", "Pascal S.", "Nikola" },
        },
        [(2026, 51)] = new[,]
        {
            { "Luka Toni", "Malik", "Mile" },
            { "Ivo", "Janis", "Fernando" },
            { "Niklas", "Lorik", "Fynn" },
            { "Trevor", "Bruno", "Djordje" },
            { "Casimir", "Pascal M.", "Noam" },
        },
        // Januar 2027
        [(2027, 1)] = new[,]
        {
            { "Giulia", "Matyas", "Rui" },
            { "Mile", "David", "Aleandro" },
            { "Rui", "Hanna", "Ngoedup" },
            { "Nevio", "Niklas", "Lorik" },
            { "Djordje", "Emily", "Erdi" },
        },
        [(2027, 2)] = new[,]
        {
            { "Hanna", "Ngoedup", "Lio" },
            { "Kirish", "Norbu", "Serhii" },
            { "Lio", "Dmytrii", "Matti" },
            { "Fynn", "Thomas", "Ayaan" },
            { "Leon", "Niall", "Pascal S." },
        },
        [(2027, 3)] = new[,]
        {
            { "Dmytrii", "Matti", "Andrii" },
            { "Cédric", "Ivo", "Janis" },
            { "Andrii", "Even", "Luka Toni" },
            { "Elias", "Koa", "Luis" },
            { "Nikola", "Casimir", "Pascal M." },
        },
        [(2027, 4)] = new[,]
        {
            { "Even", "Luka Toni", "Malik" },
            { "Fernando", "Giulia", "Matyas" },
            { "Malik", "Mile", "David" },
            { "Oscar", "Trevor", "Bruno" },
            { "Noam", "Nevio", "Niklas" },
        },
        // Februar 2027
        [(2027, 5)] = new[,]
        {
            { "Aleandro", "Kirish", "Norbu" },
            { "Matti", "Andrii", "Even" },
            { "Lorik", "Fynn", "Thomas" },
            { "Pascal S.", "Nikola", "Casimir" },
            { "Trevor", "Bruno", "Djordje" },
        },
        [(2027, 6)] = new[,]
        {
            { "Serhii", "Cédric", "Ivo" },
            { "Luka Toni", "Malik", "Mile" },
            { "Ayaan", "Elias", "Koa" },
            { "Pascal M.", "Noam", "Nevio" },
            { "Emily", "Erdi", "Leon" },
        },
        [(2027, 7)] = new[,]
        {
            { "Janis", "Fernando", "Giulia" },
            { "David", "Aleandro", "Kirish" },
            { "Luis", "Oscar", "Trevor" },
            { "Niklas", "Lorik", "Fynn" },
            { "Niall", "Pascal S.", "Nikola" },
        },
        [(2027, 8)] = new[,]
        {
            { "Matyas", "Rui", "Hanna" },
            { "Norbu", "Serhii", "Cédric" },
            { "Bruno", "Djordje", "Emily" },
            { "Thomas", "Ayaan", "Elias" },
            { "Casimir", "Pascal M.", "Noam" },
        },
        [(2027, 9)] = new[,]
        {
            { "Ngoedup", "Lio", "Dmytrii" },
            { "Ivo", "Janis", "Fernando" },
            { "Erdi", "Leon", "Niall" },
            { "Koa", "Luis", "Oscar" },
            { "Nevio", "Niklas", "Lorik" },
        },
        // März 2027
        [(2027, 10)] = new[,]
        {
            { "Giulia", "Matyas", "Rui" },
            { "Mile", "David", "Aleandro" },
            { "Rui", "Hanna", "Ngoedup" },
            { "Fynn", "Thomas", "Ayaan" },
            { "Leon", "Niall", "Pascal S." },
        },
        [(2027, 11)] = new[,]
        {
            { "Hanna", "Ngoedup", "Lio" },
            { "Kirish", "Norbu", "Serhii" },
            { "Lio", "Dmytrii", "Matti" },
            { "Elias", "Koa", "Luis" },
            { "Nikola", "Casimir", "Pascal M." },
        },
        [(2027, 12)] = new[,]
        {
            { "Dmytrii", "Matti", "Andrii" },
            { "Cédric", "Ivo", "Janis" },
            { "Andrii", "Even", "Luka Toni" },
            { "Oscar", "Trevor", "Bruno" },
            { "Noam", "Nevio", "Niklas" },
        },
        [(2027, 13)] = new[,]
        {
            { "Even", "Luka Toni", "Malik" },
            { "Fernando", "Giulia", "Matyas" },
            { "Malik", "Mile", "David" },
            { "Djordje", "Emily", "Erdi" },
            { "Lorik", "Fynn", "Thomas" },
        },
        // April 2027
        [(2027, 14)] = new[,]
        {
            { "Aleandro", "Kirish", "Norbu" },
            { "Matyas", "Rui", "Hanna" },
            { "Ayaan", "Elias", "Koa" },
            { "Erdi", "Leon", "Niall" },
            { "Niklas", "Lorik", "Fynn" },
        },
        [(2027, 15)] = new[,]
        {
            { "Serhii", "Cédric", "Ivo" },
            { "Ngoedup", "Lio", "Dmytrii" },
            { "Luis", "Oscar", "Trevor" },
            { "Pascal S.", "Nikola", "Casimir" },
            { "Thomas", "Ayaan", "Elias" },
        },
        [(2027, 16)] = new[,]
        {
            { "Janis", "Fernando", "Giulia" },
            { "Matti", "Andrii", "Even" },
            { "Bruno", "Djordje", "Emily" },
            { "Pascal M.", "Noam", "Nevio" },
            { "Koa", "Luis", "Oscar" },
        },
        // Mai 2027 (KW 17–18 Frühlingsferien)
        [(2027, 19)] = new[,]
        {
            { "Luka Toni", "Malik", "Mile" },
            { "Giulia", "Matyas", "Rui" },
            { "Mile", "David", "Aleandro" },
            { "Trevor", "Bruno", "Djordje" },
            { "Nevio", "Niklas", "Lorik" },
        },
        [(2027, 20)] = new[,]
        {
            { "David", "Aleandro", "Kirish" },
            { "Hanna", "Ngoedup", "Lio" },
            { "Kirish", "Norbu", "Serhii" },
            { "Emily", "Erdi", "Leon" },
            { "Fynn", "Thomas", "Ayaan" },
        },
        [(2027, 21)] = new[,]
        {
            { "Norbu", "Serhii", "Cédric" },
            { "Dmytrii", "Matti", "Andrii" },
            { "Cédric", "Ivo", "Janis" },
            { "Niall", "Pascal S.", "Nikola" },
            { "Elias", "Koa", "Luis" },
        },
        [(2027, 22)] = new[,]
        {
            { "Ivo", "Janis", "Fernando" },
            { "Even", "Luka Toni", "Malik" },
            { "Fernando", "Giulia", "Matyas" },
            { "Casimir", "Pascal M.", "Noam" },
            { "Oscar", "Trevor", "Bruno" },
        },
        // Juni 2027
        [(2027, 23)] = new[,]
        {
            { "Rui", "Hanna", "Ngoedup" },
            { "Aleandro", "Kirish", "Norbu" },
            { "Djordje", "Emily", "Erdi" },
            { "Lorik", "Fynn", "Thomas" },
            { "Erdi", "Leon", "Niall" },
        },
        [(2027, 24)] = new[,]
        {
            { "Lio", "Dmytrii", "Matti" },
            { "Serhii", "Cédric", "Ivo" },
            { "Leon", "Niall", "Pascal S." },
            { "Ayaan", "Elias", "Koa" },
            { "Pascal S.", "Nikola", "Casimir" },
        },
        [(2027, 25)] = new[,]
        {
            { "Andrii", "Even", "Luka Toni" },
            { "Janis", "Fernando", "Giulia" },
            { "Nikola", "Casimir", "Pascal M." },
            { "Luis", "Oscar", "Trevor" },
            { "Pascal M.", "Noam", "Nevio" },
        },
        [(2027, 26)] = new[,]
        {
            { "Malik", "Mile", "David" },
            { "Matyas", "Rui", "Hanna" },
            { "Noam", "Nevio", "Niklas" },
            { "Bruno", "Djordje", "Emily" },
            { "Niklas", "Lorik", "Fynn" },
        },
        // Juli 2027
        [(2027, 27)] = new[,]
        {
            { "Ngoedup", "Lio", "Dmytrii" },
            { "Luka Toni", "Malik", "Mile" },
            { "Thomas", "Ayaan", "Elias" },
            { "Trevor", "Bruno", "Djordje" },
            { "Niall", "Pascal S.", "Nikola" },
        },
        [(2027, 28)] = new[,]
        {
            { "Matti", "Andrii", "Even" },
            { "David", "Aleandro", "Kirish" },
            { "Koa", "Luis", "Oscar" },
            { "Emily", "Erdi", "Leon" },
            { "Casimir", "Pascal M.", "Noam" },
        },
    };

    public static IReadOnlyList<string> AllNames { get; } = BuildAllNames();

    static IReadOnlyList<string> BuildAllNames()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var grid in WindowWeeks.Values)
            foreach (string name in grid)
                set.Add(name);
        foreach (var team in CleaningTeams.Values)
            foreach (string name in team)
                set.Add(name);
        var list = set.ToList();
        list.Sort(StringComparer.CurrentCultureIgnoreCase);
        return list;
    }

    static string Normalize(string s)
    {
        string formD = s.Trim().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(formD.Length);
        foreach (char c in formD)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Returns names from <see cref="AllNames"/> that match any user name/first-name hint
    /// (e.g. profile name, Extranet display name, Windows username), ordered by best match first.
    /// </summary>
    public static IReadOnlyList<string> SuggestMatches(params string?[] hints)
    {
        var tokens = new List<string>();
        var fullHints = new List<string>();
        foreach (string? raw in hints)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string normFull = Normalize(raw);
            if (normFull.Length >= 2) fullHints.Add(normFull);
            foreach (string part in raw.Split(new[] { ' ', '.', '_', '-', '@', ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string norm = Normalize(part);
                if (norm.Length >= 2) tokens.Add(norm);
            }
        }
        if (tokens.Count == 0 && fullHints.Count == 0) return Array.Empty<string>();

        var scored = new List<(string Name, int Score)>();
        foreach (string candidate in AllNames)
        {
            string normCand = Normalize(candidate);
            string candFirst = normCand.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
            int best = 0;
            foreach (string full in fullHints)
            {
                if (full == normCand) best = Math.Max(best, 100);
            }
            foreach (string tok in tokens)
            {
                if (tok == normCand) best = Math.Max(best, 95);
                else if (tok == candFirst && candFirst.Length >= 3) best = Math.Max(best, 80);
            }
            if (best > 0) scored.Add((candidate, best));
        }
        return scored.OrderByDescending(x => x.Score).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).Select(x => x.Name).ToList();
    }

    public static IReadOnlyList<WindowDuty> UpcomingWindowDuties(string person, DateTime now, DutyBreakTimes breaks, int maxCount = 5)
    {
        if (string.IsNullOrWhiteSpace(person)) return Array.Empty<WindowDuty>();
        var result = new List<WindowDuty>();
        var today = DateOnly.FromDateTime(now);
        var nowTime = TimeOnly.FromDateTime(now);

        foreach (var (date, slot, assigned) in EnumerateAllWindowDuties())
        {
            if (!string.Equals(assigned, person, StringComparison.OrdinalIgnoreCase)) continue;
            var (openAt, closeAt) = breaks.ForSlot(slot);
            if (date < today) continue;
            if (date == today && nowTime > closeAt.AddMinutes(5)) continue;
            result.Add(new WindowDuty(date, slot, assigned, openAt, closeAt));
            if (result.Count >= maxCount) break;
        }
        return result;
    }

    public static WindowDuty? NextWindowDuty(string person, DateTime now, DutyBreakTimes breaks)
        => UpcomingWindowDuties(person, now, breaks, 1).FirstOrDefault();

    public static CleaningDuty? NextCleaningDuty(string person, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(person)) return null;
        int? teamNum = FindCleaningTeam(person);
        if (teamNum == null) return null;
        var today = DateOnly.FromDateTime(now);
        var nowTime = TimeOnly.FromDateTime(now);

        foreach (var (date, team) in CleaningRotation)
        {
            if (team != teamNum.Value) continue;
            if (date < today) continue;
            if (date == today && nowTime > CleaningTime.AddMinutes(45)) continue;
            return new CleaningDuty(date, team, CleaningTeams[team], CleaningTime);
        }
        return null;
    }

    public static int? FindCleaningTeam(string person)
    {
        foreach (var kv in CleaningTeams)
            if (kv.Value.Any(m => string.Equals(m, person, StringComparison.OrdinalIgnoreCase)))
                return kv.Key;
        return null;
    }

    static IEnumerable<(DateOnly Date, WindowSlot Slot, string Person)> EnumerateAllWindowDuties()
    {
        foreach (var kv in WindowWeeks.OrderBy(k => k.Key.Year).ThenBy(k => k.Key.Week))
        {
            DateOnly monday = DateOnly.FromDateTime(ISOWeek.ToDateTime(kv.Key.Year, kv.Key.Week, DayOfWeek.Monday));
            var grid = kv.Value;
            for (int d = 0; d < 5; d++)
            {
                DateOnly date = monday.AddDays(d);
                yield return (date, WindowSlot.Morning, grid[d, 0]);
                yield return (date, WindowSlot.Noon, grid[d, 1]);
                yield return (date, WindowSlot.Afternoon, grid[d, 2]);
            }
        }
    }

    /// <summary>
    /// Checks whether a window open/close or dining-room cleaning notification should fire at <paramref name="now"/>.
    /// </summary>
    public static DutyNotification? CheckNotification(string person, DateTime now, DutyBreakTimes breaks, ISet<string> alreadyNotified)
    {
        if (string.IsNullOrWhiteSpace(person)) return null;
        var today = DateOnly.FromDateTime(now);
        var nowTime = TimeOnly.FromDateTime(now);

        // 1. Check today's window duties (open shortly before break, close right after break)
        int isoYear = ISOWeek.GetYear(now);
        int isoWeek = ISOWeek.GetWeekOfYear(now);
        int dayIdx = (int)now.DayOfWeek - 1; // Monday=0 .. Friday=4
        if (dayIdx >= 0 && dayIdx < 5 && WindowWeeks.TryGetValue((isoYear, isoWeek), out var grid))
        {
            for (int s = 0; s < 3; s++)
            {
                if (!string.Equals(grid[dayIdx, s], person, StringComparison.OrdinalIgnoreCase)) continue;
                var slot = (WindowSlot)s;
                var (openAt, closeAt) = breaks.ForSlot(slot);
                string slotLabel = slot switch { WindowSlot.Morning => "Morgen", WindowSlot.Noon => "Mittag", _ => "Nachmittag" };

                string openKey = $"win-open:{today:yyyy-MM-dd}:{slot}";
                if (!alreadyNotified.Contains(openKey) && nowTime >= openAt && nowTime <= openAt.AddMinutes(10))
                {
                    return new DutyNotification(
                        openKey,
                        $"Fenster öffnen ({slotLabel})",
                        $"Du bist heute ({slotLabel}) dran – bitte kurz vor der Pause die Fenster öffnen ({openAt:HH:mm}).",
                        IsCleaning: false);
                }

                string closeKey = $"win-close:{today:yyyy-MM-dd}:{slot}";
                if (!alreadyNotified.Contains(closeKey) && nowTime >= closeAt && nowTime <= closeAt.AddMinutes(10))
                {
                    return new DutyNotification(
                        closeKey,
                        $"Fenster schliessen ({slotLabel})",
                        $"Pause vorbei ({slotLabel}) – bitte die Fenster wieder schliessen ({closeAt:HH:mm}).",
                        IsCleaning: false);
                }
            }
        }

        // 2. Check today's dining-room cleaning duty (Friday 12:45, notify from 12:40 to 13:05)
        var cleaning = NextCleaningDuty(person, now);
        if (cleaning != null && cleaning.Date == today)
        {
            string cleanKey = $"clean:{today:yyyy-MM-dd}";
            var notifyFrom = CleaningTime.AddMinutes(-5);
            var notifyUntil = CleaningTime.AddMinutes(20);
            if (!alreadyNotified.Contains(cleanKey) && nowTime >= notifyFrom && nowTime <= notifyUntil)
            {
                return new DutyNotification(
                    cleanKey,
                    $"Essraum putzen ({CleaningTime:HH:mm})",
                    $"Team {cleaning.TeamNumber} ist heute dran: {string.Join(", ", cleaning.Members)}.",
                    IsCleaning: true);
            }
        }

        return null;
    }

    public static string FormatShortDate(DateOnly date, DateOnly today)
    {
        if (date == today) return "Heute";
        if (date == today.AddDays(1)) return "Morgen";
        string dow = date.DayOfWeek switch
        {
            DayOfWeek.Monday => "Mo",
            DayOfWeek.Tuesday => "Di",
            DayOfWeek.Wednesday => "Mi",
            DayOfWeek.Thursday => "Do",
            DayOfWeek.Friday => "Fr",
            DayOfWeek.Saturday => "Sa",
            _ => "So",
        };
        return $"{dow} {date:dd.MM.}";
    }
}
