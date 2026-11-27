using System;
using System.Collections.Generic;
using WinNotch;
using Xunit;

namespace Holm.Tests;

public sealed class DutyScheduleTests
{
    [Fact]
    public void AllNames_ContainsWindowAndCleaningNames()
    {
        Assert.Contains("Elias", DutySchedule.AllNames);
        Assert.Contains("Cédric", DutySchedule.AllNames);
        Assert.Contains("Pascal M.", DutySchedule.AllNames);
        Assert.Contains("Pascal S.", DutySchedule.AllNames);
        Assert.Contains("Luka Toni", DutySchedule.AllNames);
        Assert.True(DutySchedule.AllNames.Count >= 35);
    }

    [Fact]
    public void SuggestMatches_MatchesFirstNameAndDiacritics()
    {
        var elias = DutySchedule.SuggestMatches("Elias Muster", "zfuche");
        Assert.Equal(new[] { "Elias" }, elias);

        var cedric = DutySchedule.SuggestMatches("Cedric Keller");
        Assert.Equal(new[] { "Cédric" }, cedric);

        var pascal = DutySchedule.SuggestMatches("Pascal");
        Assert.Contains("Pascal M.", pascal);
        Assert.Contains("Pascal S.", pascal);

        var pascalExact = DutySchedule.SuggestMatches("Pascal M.");
        Assert.Equal("Pascal M.", pascalExact[0]);
    }

    [Fact]
    public void NextWindowDuty_FindsExactSlotFromPdf()
    {
        // KW 49 Donnerstag (2026-12-03): Thomas (Morgen), Ayaan (Mittag), Elias (Nachmittag)
        var next = DutySchedule.NextWindowDuty("Elias", new DateTime(2026, 11, 27, 15, 0, 0), DutyBreakTimes.Default);
        Assert.NotNull(next);
        Assert.Equal(new DateOnly(2026, 12, 3), next!.Date);
        Assert.Equal(WindowSlot.Afternoon, next.Slot);
    }

    [Fact]
    public void NextCleaningDuty_FindsExactTeamAndSkipsVacations()
    {
        // Elias is in Team 3 -> 2026-12-11
        var next = DutySchedule.NextCleaningDuty("Elias", new DateTime(2026, 11, 27, 15, 0, 0));
        Assert.NotNull(next);
        Assert.Equal(new DateOnly(2026, 12, 11), next!.Date);
        Assert.Equal(3, next.TeamNumber);
        Assert.Contains("Elias", next.Members);

        // Team 5 after 2026-12-18 should skip 2026-12-25 & 2027-01-01 (Festtage) and land on 2027-01-08
        var team5 = DutySchedule.NextCleaningDuty("Nevio", new DateTime(2026, 12, 19, 10, 0, 0));
        Assert.NotNull(team5);
        Assert.Equal(new DateOnly(2027, 1, 8), team5!.Date);
        Assert.Equal(5, team5.TeamNumber);
    }

    [Fact]
    public void CheckNotification_FiresForWindowOpenCloseAndCleaningOnce()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        // 2026-12-03 Afternoon is Elias (open 14:58, close 15:15)
        var openNotif = DutySchedule.CheckNotification("Elias", new DateTime(2026, 12, 3, 14, 58, 0), DutyBreakTimes.Default, seen);
        Assert.NotNull(openNotif);
        Assert.False(openNotif!.IsCleaning);
        Assert.Contains("öffnen", openNotif.Title);
        seen.Add(openNotif.Key);

        // Deduplicated
        Assert.Null(DutySchedule.CheckNotification("Elias", new DateTime(2026, 12, 3, 15, 0, 0), DutyBreakTimes.Default, seen));

        // Close at 15:15
        var closeNotif = DutySchedule.CheckNotification("Elias", new DateTime(2026, 12, 3, 15, 15, 0), DutyBreakTimes.Default, seen);
        Assert.NotNull(closeNotif);
        Assert.Contains("schliessen", closeNotif!.Title);

        // Cleaning on 2026-12-11 at 12:42
        var cleanNotif = DutySchedule.CheckNotification("Elias", new DateTime(2026, 12, 11, 12, 42, 0), DutyBreakTimes.Default, seen);
        Assert.NotNull(cleanNotif);
        Assert.True(cleanNotif!.IsCleaning);
        Assert.Contains("Team 3", cleanNotif.Body);
    }
}
