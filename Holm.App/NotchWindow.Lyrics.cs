using System;
using System.Collections.Generic;
using System.Threading;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using WinNotch;

namespace Holm.App;

/// <summary>
/// Optional Apple Music-style time-synced lyrics panel inside the expanded music player.
/// Strictly uses real LRC timestamps from LRCLIB (never fake duration-based interpolation).
/// </summary>
public sealed partial class NotchWindow
{
    Border _lyricsCard = null!;
    TextBlock _lyricsPrev = null!, _lyricsCurrent = null!, _lyricsNext1 = null!, _lyricsNext2 = null!;
    readonly DispatcherTimer _lyricsTimer = new() { Interval = TimeSpan.FromMilliseconds(60) };
    CancellationTokenSource? _lyricsFetchCts;
    string? _lyricsTrackKey;
    SyncedLyrics? _currentLyrics;
    int _lyricsActiveIdx = -2;
    int _lyricsHighlightedWords = -1;

    static readonly IBrush LyricsLitBrush = Brushes.White;
    static readonly IBrush LyricsDimActiveBrush = new SolidColorBrush(Color.FromArgb(0x78, 0xFF, 0xFF, 0xFF)).ToImmutable();
    static readonly IBrush LyricsUpcomingBrush = new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF)).ToImmutable();
    static readonly IBrush LyricsFarBrush = new SolidColorBrush(Color.FromArgb(0x36, 0xFF, 0xFF, 0xFF)).ToImmutable();

    void BuildLyrics()
    {
        _lyricsPrev = MakeLyricText(12.5, FontWeight.SemiBold, LyricsFarBrush);
        _lyricsCurrent = MakeLyricText(16.5, FontWeight.Bold, LyricsLitBrush);
        _lyricsNext1 = MakeLyricText(14, FontWeight.SemiBold, LyricsUpcomingBrush);
        _lyricsNext2 = MakeLyricText(13, FontWeight.SemiBold, LyricsFarBrush);

        WireLyricSeek(_lyricsPrev, -1);
        WireLyricSeek(_lyricsNext1, +1);
        WireLyricSeek(_lyricsNext2, +2);

        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(_lyricsPrev);
        stack.Children.Add(_lyricsCurrent);
        stack.Children.Add(_lyricsNext1);
        stack.Children.Add(_lyricsNext2);

        _lyricsCard = new Border
        {
            Margin = new Thickness(0, 12, 0, 0),
            Padding = new Thickness(14, 12),
            CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            IsVisible = false,
            Child = stack,
        };
        _mediaPanel.Children.Add(_lyricsCard);
        _lyricsTimer.Tick += (_, _) => TickLyrics();
    }

    static TextBlock MakeLyricText(double size, FontWeight weight, IBrush fg) => new()
    {
        FontSize = size,
        FontWeight = weight,
        Foreground = fg,
        TextWrapping = TextWrapping.Wrap,
        LineHeight = size * 1.24,
        Transitions = new Transitions
        {
            new DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(220) },
        },
    };

    void WireLyricSeek(TextBlock tb, int offset)
    {
        tb.Cursor = new Cursor(StandardCursorType.Hand);
        tb.PointerPressed += (_, _) =>
        {
            if (_info is not { CanSeek: true } || _currentLyrics == null) return;
            int target = _lyricsActiveIdx + offset;
            if (target >= 0 && target < _currentLyrics.Lines.Count)
                _media.Seek(_currentLyrics.Lines[target].Time);
        };
    }

    void SetLyrics(bool on)
    {
        _settings.Lyrics = on;
        _settings.Save();
        if (!on)
        {
            StopLyrics();
            _lyricsCard.IsVisible = false;
            ApplyState(animate: true);
        }
        else
        {
            _lyricsTrackKey = null;
            RefreshLyricsForTrack();
        }
    }

    void StopLyrics()
    {
        _lyricsTimer.Stop();
        _lyricsFetchCts?.Cancel();
        _lyricsFetchCts?.Dispose();
        _lyricsFetchCts = null;
        _currentLyrics = null;
        _lyricsTrackKey = null;
        _lyricsActiveIdx = -2;
        _lyricsHighlightedWords = -1;
    }

    void RefreshLyricsForTrack()
    {
        if (_lyricsCard == null) return;
        var info = _info;
        if (!_settings.Lyrics || info == null || string.IsNullOrWhiteSpace(info.Title) || string.IsNullOrWhiteSpace(info.Artist))
        {
            _lyricsTimer.Stop();
            if (_lyricsCard.IsVisible)
            {
                _lyricsCard.IsVisible = false;
                if (_state == NotchState.Expanded) ApplyState(animate: true);
            }
            return;
        }

        // Subtle album-art tinted frosted backdrop (Apple Music style)
        var c = info.Accent;
        _lyricsCard.Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x46, c.R, c.G, c.B), 0),
                new GradientStop(Color.FromArgb(0x22, (byte)(c.R / 2), (byte)(c.G / 2), (byte)(c.B / 2)), 1),
            },
        };

        string key = LyricsService.MakeTrackKey(info.Title, info.Artist);
        if (key == _lyricsTrackKey)
        {
            UpdateLyricsTimerState();
            TickLyrics();
            return;
        }

        _lyricsTrackKey = key;
        _currentLyrics = null;
        _lyricsActiveIdx = -2;
        _lyricsHighlightedWords = -1;
        _lyricsTimer.Stop();
        if (_lyricsCard.IsVisible)
        {
            _lyricsCard.IsVisible = false;
            if (_state == NotchState.Expanded) ApplyState(animate: true);
        }

        _lyricsFetchCts?.Cancel();
        _lyricsFetchCts?.Dispose();
        var cts = _lyricsFetchCts = new CancellationTokenSource();
        string title = info.Title, artist = info.Artist, album = info.Album;
        var duration = info.Duration;

        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                var lyrics = await LyricsService.FetchAsync(title, artist, album, duration, cts.Token);
                if (cts.IsCancellationRequested) return;
                Dispatcher.UIThread.Post(() =>
                {
                    if (cts.IsCancellationRequested || _lyricsTrackKey != key || !_settings.Lyrics) return;
                    _currentLyrics = lyrics;
                    bool show = lyrics != null && lyrics.Lines.Count > 0;
                    _lyricsCard.IsVisible = show;
                    if (show)
                    {
                        _lyricsActiveIdx = -2;
                        _lyricsHighlightedWords = -1;
                        TickLyrics();
                        UpdateLyricsTimerState();
                    }
                    if (_state == NotchState.Expanded) ApplyState(animate: true);
                });
            }
            catch { }
        });
    }

    void UpdateLyricsTimerState()
    {
        bool shouldRun = _settings.Lyrics && _currentLyrics != null && _info?.IsPlaying == true;
        if (shouldRun && !_lyricsTimer.IsEnabled) _lyricsTimer.Start();
        else if (!shouldRun && _lyricsTimer.IsEnabled) _lyricsTimer.Stop();
    }

    void TickLyrics()
    {
        var lyrics = _currentLyrics;
        var info = _info;
        if (!_settings.Lyrics || lyrics == null || info == null || !_lyricsCard.IsVisible) return;

        var (idx, progress) = LyricsService.FindActive(lyrics.Lines, info.CurrentPosition, info.Duration);
        var lines = lyrics.Lines;

        // Before the first lyric line starts: show upcoming lines dimmed
        string prevText = idx > 0 ? lines[idx - 1].Text : "";
        string currText = idx >= 0 ? lines[idx].Text : "•••";
        string next1Text = idx + 1 < lines.Count ? lines[idx + 1].Text : "";
        string next2Text = idx + 2 < lines.Count ? lines[idx + 2].Text : "";

        string[] words = currText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int litWords = idx < 0 ? 0 : Math.Clamp((int)Math.Ceiling(progress * words.Length), 1, words.Length);

        if (idx == _lyricsActiveIdx && litWords == _lyricsHighlightedWords) return;
        _lyricsActiveIdx = idx;
        _lyricsHighlightedWords = litWords;

        _lyricsPrev.Text = prevText;
        _lyricsPrev.IsVisible = prevText.Length > 0;

        // Apple Music word-by-word highlight on the active line: spoken words bright white, remaining words soft grey
        _lyricsCurrent.Inlines ??= new InlineCollection();
        _lyricsCurrent.Inlines.Clear();
        if (idx < 0 || words.Length == 0)
        {
            _lyricsCurrent.Inlines.Add(new Run(currText) { Foreground = LyricsDimActiveBrush });
        }
        else if (litWords >= words.Length)
        {
            _lyricsCurrent.Inlines.Add(new Run(currText) { Foreground = LyricsLitBrush });
        }
        else
        {
            string litPart = string.Join(" ", words, 0, litWords) + " ";
            string dimPart = string.Join(" ", words, litWords, words.Length - litWords);
            _lyricsCurrent.Inlines.Add(new Run(litPart) { Foreground = LyricsLitBrush });
            _lyricsCurrent.Inlines.Add(new Run(dimPart) { Foreground = LyricsDimActiveBrush });
        }

        _lyricsNext1.Text = next1Text;
        _lyricsNext1.IsVisible = next1Text.Length > 0;
        _lyricsNext2.Text = next2Text;
        _lyricsNext2.IsVisible = next2Text.Length > 0;
    }
}
