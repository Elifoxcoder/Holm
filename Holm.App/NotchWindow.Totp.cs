using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Holm.App.Controls;
using Holm.Platform;
using WinNotch;

namespace Holm.App;

/// <summary>
/// TOTP Authenticator in the open notch: compact list with title + larger code, click to copy,
/// screen-QR snipping in Settings, automatic clipboard TOTP suggestion, and E2E sync with your phone.
/// </summary>
public sealed partial class NotchWindow
{
    const string TotpGlyph = "\uE8D7";

    TotpStore _totp = null!;
    NotchButton _totpToolBtn = null!;
    readonly StackPanel _totpPage = new() { IsVisible = false };
    readonly StackPanel _totpRows = new() { Spacing = 4 };
    readonly TextBlock _totpEmpty = new()
    {
        Text = "No authenticator codes yet.",
        FontSize = 12.5, Foreground = Faint, Margin = new Thickness(2, 4, 2, 6),
    };
    readonly DispatcherTimer _totpTick = new() { Interval = TimeSpan.FromSeconds(1) };
    string? _copiedTotpId;
    long _copiedTotpUntil;
    string? _lastCopiedTotpCode;
    string? _lastSuggestedTotpSecret;

    void InitTotp()
    {
        _totp = new TotpStore(Path.Combine(Diag.Folder, "totp.json"), _friends.ChatStorageKey());
        _totp.Changed += () => Dispatcher.UIThread.Post(() => { if (_tool == Tool.Totp) ShowTotpPage(); });
        _totpTick.Tick += (_, _) =>
        {
            if (_tool == Tool.Totp && _state == NotchState.Expanded) ShowTotpPage();
            else _totpTick.Stop();
        };
        _friends.TotpSyncReceived += OnTotpSyncReceived;
        _friends.OwnDevicesChanged += () =>
        {
            if (_friends.OwnDevicesNow.Count > 0)
                _friends.SendTotpSyncToOwnDevices(_totp.AllForSync, query: true);
        };
    }

    void OnTotpSyncReceived(WinNotch.Sharing.OwnDevice device, List<TotpEntry> entries, bool query)
    {
        if (entries.Count > 0) _totp.Merge(entries);
        if (query && _totp.AllForSync is { Count: > 0 } mine)
            _friends.SendTotpSyncToOwnDevices(mine, query: false);
    }

    Control BuildTotpPage()
    {
        var header = new Grid { Margin = new Thickness(2, 0, 0, 6), ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(new TextBlock { Text = "Authenticator", FontSize = 11, Foreground = Dim, VerticalAlignment = VerticalAlignment.Center });
        var manageBtn = new NotchButton(new TextBlock { Text = "+", FontSize = 14, FontWeight = FontWeight.SemiBold, Foreground = Dim })
        {
            Width = 24, Height = 24, HorizontalAlignment = HorizontalAlignment.Right,
        };
        ToolTip.SetTip(manageBtn, "Add or import TOTP codes in Settings");
        manageBtn.Click += () => ShowHub("totp");
        Grid.SetColumn(manageBtn, 1);
        header.Children.Add(manageBtn);

        _totpPage.Children.Add(header);
        _totpPage.Children.Add(_totpEmpty);
        _totpPage.Children.Add(new ScrollViewer
        {
            MaxHeight = 230,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden,
            Content = _totpRows,
        });
        return _totpPage;
    }

    void ShowTotpPage()
    {
        _totpRows.Children.Clear();
        var entries = _totp.Active;
        _totpEmpty.IsVisible = entries.Count == 0;
        if (entries.Count == 0)
        {
            var addRow = NotchButton.Row(new TextBlock
            {
                Text = "Add QR or code in Settings…",
                FontSize = 12.5, Foreground = Look.Accent, Margin = new Thickness(4, 2),
            });
            addRow.Click += () => ShowHub("totp");
            _totpRows.Children.Add(addRow);
        }
        else
        {
            long nowMs = Environment.TickCount64;
            for (int i = 0; i < entries.Count; i += 2)
            {
                var pair = new Grid { ColumnDefinitions = new ColumnDefinitions("*,6,*") };
                pair.Children.Add(BuildTotpTile(entries[i], nowMs));
                if (i + 1 < entries.Count)
                {
                    var rightTile = BuildTotpTile(entries[i + 1], nowMs);
                    Grid.SetColumn(rightTile, 2);
                    pair.Children.Add(rightTile);
                }
                _totpRows.Children.Add(pair);
            }
        }
        if (!_totpTick.IsEnabled && _tool == Tool.Totp && _state == NotchState.Expanded) _totpTick.Start();
        if (_tool == Tool.Totp && _state == NotchState.Expanded) ApplyState(animate: true);
    }

    Control BuildTotpTile(TotpEntry entry, long nowMs)
    {
        var e = entry;
        var (code, nextCode, rem, _) = TotpCode.Generate(e);
        bool copied = _copiedTotpId == e.Id && nowMs < _copiedTotpUntil;

        var content = new StackPanel { Spacing = 2, Margin = new Thickness(4, 3) };

        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        top.Children.Add(new TextBlock
        {
            Text = e.DisplayTitle,
            FontSize = 12, FontWeight = FontWeight.Medium,
            Foreground = Dim,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 6, 0),
        });
        var remText = new TextBlock
        {
            Text = $"{rem}s",
            FontSize = 10,
            Foreground = rem <= 5 ? RedBrush : Faint,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(remText, 1);
        top.Children.Add(remText);
        content.Children.Add(top);

        // Upcoming (next) code shown small above the current code — never copied.
        content.Children.Add(new TextBlock
        {
            Text = $"Next {TotpCode.Format(nextCode)}",
            FontSize = 10,
            FontFamily = Fonts.Mono,
            Foreground = Faint,
        });

        content.Children.Add(new TextBlock
        {
            Text = copied ? "Copied" : TotpCode.Format(code),
            FontSize = copied ? 15 : 21,
            FontWeight = FontWeight.Bold,
            FontFamily = copied ? Look.Font : Fonts.Mono,
            Foreground = copied ? GreenBrush : (rem <= 5 ? RedBrush : Look.Ink),
        });

        var tile = NotchButton.Row(content);
        ToolTip.SetTip(tile, "Click to copy current code");
        tile.Click += () => _ = CopyTotpCodeAsync(e, code);
        return tile;
    }

    async Task CopyTotpCodeAsync(TotpEntry entry, string code)
    {
        _lastCopiedTotpCode = code;
        if (await TrySetClipboardAsync(code))
        {
            _copiedTotpId = entry.Id;
            _copiedTotpUntil = Environment.TickCount64 + 1400;
            ShowTotpPage();
        }
    }

    HubView BuildTotpSettingsPage() => new TotpSettingsPage(
        _totp,
        ScanQrFromScreenAsync,
        async code => { _lastCopiedTotpCode = code; return await TrySetClipboardAsync(code); },
        added => _friends.SendTotpSyncToOwnDevices(added),
        deleted => _friends.SendTotpSyncToOwnDevices(new[] { deleted }));

    /// <summary>Captures the screen and lets the user drag a Snipping-Tool box over a QR code.</summary>
    async Task<string?> ScanQrFromScreenAsync()
    {
        var settingsWin = _settingsWindow;
        if (settingsWin != null) settingsWin.WindowState = WindowState.Minimized;
        await Task.Delay(240);
        try
        {
            var screen = ChosenScreen() ?? Screens.Primary ?? (Screens.All.Count > 0 ? Screens.All[0] : null);
            if (screen == null) return null;
            var bounds = screen.Bounds;
            var shot = await _screen.CaptureAsync(bounds.X, bounds.Y, bounds.Width, bounds.Height);
            if (shot == null || shot.Width <= 0 || shot.Height <= 0) return null;
            var snip = new QrSnipWindow(shot, bounds, screen.Scaling);
            var done = new TaskCompletionSource<string?>();
            snip.Done += text => done.TrySetResult(text);
            snip.Show();
            return await done.Task;
        }
        finally
        {
            if (settingsWin != null)
            {
                settingsWin.WindowState = WindowState.Normal;
                settingsWin.Activate();
            }
        }
    }

    /// <summary>Called on clipboard change: if a TOTP URI or Base32 key was copied/selected, suggest adding it to HolyNotch.</summary>
    async Task CheckClipboardForTotpAsync()
    {
        if (Clipboard is not { } clipboard) return;
        try
        {
            if (await PrivacyOfClipboardAsync(clipboard) != ClipPrivacy.Keep) return;
            if (await clipboard.TryGetTextAsync() is not { } text) return;
            if (text == _lastCopiedTotpCode) return;

            var detected = TotpParser.DetectInClipboard(text);
            if (detected == null || detected.Count == 0) return;

            var fresh = detected.Where(e => !_totp.HasSecret(e.Secret)).ToList();
            if (fresh.Count == 0) return;

            string fingerprint = fresh[0].Secret;
            if (fingerprint == _lastSuggestedTotpSecret) return;
            _lastSuggestedTotpSecret = fingerprint;

            string title = fresh.Count == 1 ? $"Add {fresh[0].DisplayTitle} to Authenticator?" : $"Import {fresh.Count} TOTP codes?";
            string detail = fresh.Count == 1 ? "TOTP key detected on clipboard" : "Google Authenticator export detected";
            ShowTask(TotpGlyph, ShareBlue, title, detail, null,
                ("Add", () =>
                {
                    var added = _totp.AddMany(fresh);
                    if (added.Count > 0) _friends.SendTotpSyncToOwnDevices(added);
                    EndTask(TotpGlyph, GreenBrush, added.Count == 1 ? $"Added {added[0].DisplayTitle}" : $"Added {added.Count} codes", "Synced to your devices");
                }),
                ("Not now", HideTask));
            _taskHide.Interval = TimeSpan.FromSeconds(18);
            _taskHide.Start();
        }
        catch { }
    }
}
