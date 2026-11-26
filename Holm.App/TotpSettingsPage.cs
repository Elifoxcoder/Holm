using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using WinNotch;

namespace Holm.App;

/// <summary>
/// Settings › Authenticator: manage TOTP codes, snip a QR code right from the screen, paste a secret or URI,
/// or import from Google Authenticator / Aegis / 2FAS. All entries sync end-to-end with your phone and other PCs.
/// </summary>
sealed class TotpSettingsPage : HubView
{
    readonly TotpStore _store;
    readonly Func<Task<string?>> _scanScreenQr;
    readonly Func<string, Task<bool>> _copy;
    readonly Action<IReadOnlyList<TotpEntry>> _onAdded;
    readonly Action<TotpEntry> _onDeleted;

    readonly StackPanel _list = new() { Spacing = 8 };
    readonly TextBox _titleBox = HubTheme.Field("Title (e.g. GitHub, Google)");
    readonly TextBox _secretBox = HubTheme.Field("Secret key, otpauth:// URI, or Google Auth export URI");
    readonly TextBlock _status = new() { FontSize = 12, Foreground = HubTheme.Dim, Margin = new Thickness(2, 6, 0, 0), TextWrapping = TextWrapping.Wrap, IsVisible = false };
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    public TotpSettingsPage(
        TotpStore store,
        Func<Task<string?>> scanScreenQr,
        Func<string, Task<bool>> copy,
        Action<IReadOnlyList<TotpEntry>> onAdded,
        Action<TotpEntry> onDeleted)
    {
        _store = store;
        _scanScreenQr = scanScreenQr;
        _copy = copy;
        _onAdded = onAdded;
        _onDeleted = onDeleted;

        var root = new StackPanel { Spacing = 14 };

        // ── Quick actions: Scan QR on screen + Import from Authenticator ──
        var actionBar = new WrapPanel { Orientation = Orientation.Horizontal };
        var scanBtn = HubTheme.Action("Scan QR on screen", primary: true);
        scanBtn.Margin = new Thickness(0, 0, 8, 8);
        scanBtn.Click += async (_, _) => await ScanFromScreenAsync();
        var importFileBtn = HubTheme.Action("Import file (Google Auth / Aegis / 2FAS)");
        importFileBtn.Margin = new Thickness(0, 0, 8, 8);
        importFileBtn.Click += async (_, _) => await ImportFromFileAsync();
        actionBar.Children.Add(scanBtn);
        actionBar.Children.Add(importFileBtn);

        var addCard = new StackPanel { Spacing = 8 };
        addCard.Children.Add(new TextBlock { Text = "Add authenticator code", FontSize = 14, FontWeight = FontWeight.SemiBold });
        addCard.Children.Add(new TextBlock
        {
            Text = "Snip any QR code on your screen (including Google Authenticator export QR codes), paste a secret key or otpauth:// URI, or import a backup file.",
            FontSize = 12, Foreground = HubTheme.Dim, TextWrapping = TextWrapping.Wrap,
        });
        addCard.Children.Add(actionBar);

        var manualRow = new Grid { ColumnDefinitions = new ColumnDefinitions("180,8,*,8,Auto") };
        manualRow.Children.Add(_titleBox);
        Grid.SetColumn(_secretBox, 2);
        manualRow.Children.Add(_secretBox);
        var addBtn = HubTheme.Action("Add");
        addBtn.Click += (_, _) => AddFromInputs();
        Grid.SetColumn(addBtn, 4);
        manualRow.Children.Add(addBtn);
        _secretBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; AddFromInputs(); } };
        addCard.Children.Add(manualRow);
        addCard.Children.Add(_status);

        root.Children.Add(Card(addCard));

        // ── Saved codes list ──
        var listHeader = new DockPanel { Margin = new Thickness(2, 4, 2, 0) };
        listHeader.Children.Add(new TextBlock { Text = "Your codes", FontSize = 14, FontWeight = FontWeight.SemiBold });
        var syncNote = new TextBlock
        {
            Text = "End-to-end synced with your phone",
            FontSize = 11.5, Foreground = HubTheme.Green,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(syncNote, Dock.Right);
        listHeader.Children.Add(syncNote);
        root.Children.Add(listHeader);
        root.Children.Add(_list);

        Child = HubTheme.Scroller(root);

        _store.Changed += OnStoreChanged;
        _timer.Tick += (_, _) => RefreshList();
        _timer.Start();
        RefreshList();
    }

    static Border Card(Control child) => new()
    {
        Background = HubTheme.Card,
        BorderBrush = HubTheme.Line,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(HubTheme.Round(12)),
        Padding = new Thickness(14),
        Child = child,
    };

    void OnStoreChanged() => Dispatcher.UIThread.Post(RefreshList);

    void ShowStatus(string text, bool isError = false)
    {
        _status.Text = text;
        _status.Foreground = isError ? HubTheme.Red : HubTheme.Green;
        _status.IsVisible = true;
    }

    async Task ScanFromScreenAsync()
    {
        var text = await _scanScreenQr();
        if (string.IsNullOrWhiteSpace(text))
        {
            ShowStatus("No QR code detected in the selected area.", isError: true);
            return;
        }
        ImportRawText(text, _titleBox.Text);
    }

    async Task ImportFromFileAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import Authenticator Backup (JSON or otpauth:// text)",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Authenticator exports") { Patterns = new[] { "*.json", "*.txt", "*.uri", "*.2fas" } },
                FilePickerFileTypes.All,
            },
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
        try
        {
            string content = await File.ReadAllTextAsync(path);
            ImportRawText(content, null);
        }
        catch (Exception ex)
        {
            ShowStatus("Couldn't read file: " + ex.Message, isError: true);
        }
    }

    void AddFromInputs()
    {
        string raw = (_secretBox.Text ?? "").Trim();
        if (raw.Length == 0)
        {
            ShowStatus("Enter a secret key or otpauth:// URI.", isError: true);
            return;
        }
        ImportRawText(raw, _titleBox.Text);
    }

    void ImportRawText(string raw, string? fallbackTitle)
    {
        var parsed = TotpParser.ParseAny(raw, fallbackTitle);
        if (parsed.Count == 0)
        {
            ShowStatus("Couldn't recognize a TOTP secret, otpauth:// URI, or Google Authenticator export.", isError: true);
            return;
        }
        if (parsed.Count == 1)
        {
            if (!string.IsNullOrWhiteSpace(fallbackTitle)) parsed[0].Title = fallbackTitle.Trim();
            var saved = _store.Add(parsed[0]);
            if (saved != null)
            {
                _onAdded(new[] { saved });
                _titleBox.Text = "";
                _secretBox.Text = "";
                ShowStatus($"Added {saved.DisplayTitle}.");
            }
            return;
        }
        var added = _store.AddMany(parsed);
        if (added.Count > 0)
        {
            _onAdded(added);
            _titleBox.Text = "";
            _secretBox.Text = "";
            ShowStatus($"Imported {added.Count} authenticator codes.");
        }
        else ShowStatus("All codes in that export are already in HolyNotch.");
    }

    void RefreshList()
    {
        _list.Children.Clear();
        var entries = _store.Active;
        if (entries.Count == 0)
        {
            _list.Children.Add(Card(new TextBlock
            {
                Text = "No authenticator codes yet. Scan a QR code on your screen or paste a setup key above.",
                Foreground = HubTheme.Dim, FontSize = 13, TextWrapping = TextWrapping.Wrap,
            }));
            return;
        }

        foreach (var entry in entries)
        {
            var e = entry;
            var (code, rem, _) = TotpCode.Generate(e);
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto") };

            var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(new TextBlock { Text = e.DisplayTitle, FontSize = 14, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            if (!string.IsNullOrWhiteSpace(e.Account))
                left.Children.Add(new TextBlock { Text = e.Account, FontSize = 11.5, Foreground = HubTheme.Dim, TextTrimming = TextTrimming.CharacterEllipsis });
            row.Children.Add(left);

            var codeStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Spacing = 8, Margin = new Thickness(12, 0) };
            codeStack.Children.Add(new TextBlock
            {
                Text = TotpCode.Format(code),
                FontSize = 20, FontWeight = FontWeight.Bold,
                FontFamily = Fonts.Mono,
                Foreground = rem <= 5 ? HubTheme.Red : Look.Ink,
                VerticalAlignment = VerticalAlignment.Center,
            });
            codeStack.Children.Add(new TextBlock
            {
                Text = $"{rem}s",
                FontSize = 11.5, Foreground = rem <= 5 ? HubTheme.Red : HubTheme.Faint,
                VerticalAlignment = VerticalAlignment.Center, Width = 24,
            });
            Grid.SetColumn(codeStack, 1);
            row.Children.Add(codeStack);

            var copyBtn = HubTheme.Action("Copy");
            copyBtn.Margin = new Thickness(0, 0, 6, 0);
            copyBtn.Click += async (_, _) =>
            {
                if (await _copy(code)) ShowStatus($"Copied code for {e.DisplayTitle}.");
            };
            Grid.SetColumn(copyBtn, 2);
            row.Children.Add(copyBtn);

            var delBtn = HubTheme.Action("Delete");
            delBtn.Click += (_, _) =>
            {
                if (_store.Delete(e.Id) is { } tomb)
                {
                    _onDeleted(tomb);
                    ShowStatus($"Removed {e.DisplayTitle}.");
                }
            };
            Grid.SetColumn(delBtn, 3);
            row.Children.Add(delBtn);

            _list.Children.Add(Card(row));
        }
    }

    public override void Detach()
    {
        _timer.Stop();
        _store.Changed -= OnStoreChanged;
    }
}
