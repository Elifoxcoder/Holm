using System;
using WinNotch;
using Xunit;

namespace Holm.Tests;

public sealed class LyricsServiceTests
{
    [Fact]
    public void ParseLrc_ParsesTimestampsAndMultiTagLines()
    {
        const string lrc = """
            [00:10.50]Let's catch a movie and get caught in the rain
            [00:15.20][01:02.00]Wherever you are is my favourite place
            [00:20.000]And it's a cruel world without you, baby
            """;

        var lines = LyricsService.ParseLrc(lrc);
        Assert.NotNull(lines);
        Assert.Equal(4, lines!.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(10500), lines[0].Time);
        Assert.Equal("Let's catch a movie and get caught in the rain", lines[0].Text);
        Assert.Equal(TimeSpan.FromMilliseconds(15200), lines[1].Time);
        Assert.Equal(TimeSpan.FromMilliseconds(20000), lines[2].Time);
        Assert.Equal(TimeSpan.FromMilliseconds(62000), lines[3].Time);
        Assert.Equal("Wherever you are is my favourite place", lines[3].Text);
    }

    [Fact]
    public void ParseLrc_RejectsPlainUnsyncedLyrics()
    {
        const string plain = "Line one without timestamps\nLine two without timestamps\nLine three";
        Assert.Null(LyricsService.ParseLrc(plain));
        Assert.Null(LyricsService.ParseLrc("[00:10.00]Only one line"));
    }

    [Fact]
    public void FindActive_TracksExactLineAndWordProgress()
    {
        var lines = LyricsService.ParseLrc("[00:10.00]First line here\n[00:14.00]Second line here\n[00:18.00]Third line")!;

        // Before first line
        var before = LyricsService.FindActive(lines, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(3));
        Assert.Equal(-1, before.Index);

        // Middle of first line (12s into 10s..14s)
        var mid = LyricsService.FindActive(lines, TimeSpan.FromSeconds(12), TimeSpan.FromMinutes(3));
        Assert.Equal(0, mid.Index);
        Assert.InRange(mid.LineProgress, 0.45, 0.75);

        // Second line
        var second = LyricsService.FindActive(lines, TimeSpan.FromSeconds(14.5), TimeSpan.FromMinutes(3));
        Assert.Equal(1, second.Index);
    }

    [Fact]
    public void CleanTitle_StripsRemasterAndFeatSuffixes()
    {
        Assert.Equal("Blinding Lights", LyricsService.CleanTitle("Blinding Lights - Remastered 2020"));
        Assert.Equal("Stay", LyricsService.CleanTitle("Stay (feat. Justin Bieber)"));
    }
}
