using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Holm.App.Controls;
using WinNotch;

namespace Holm.App;

/// <summary>
/// Start-page card beside the Extranet work-time widget showing the next window-opening ("Fenster öffnen")
/// and dining-room cleaning ("Essraum putzen") duties, plus break-time & cleaning notifications and
/// a searchable name picker with automatic top suggestions in Settings.
/// </summary>
public sealed partial class NotchWindow
{
    const string DutiesGlyph = "\uE787"; // Calendar

    Grid? _workDutiesRow;
    readonly TextBlock _dutiesTitle = CardTitle();
    readonly TextBlock _dutiesMore = CardMore();
    NotchButton _todayDuties = null!;
    readonly DispatcherTimer _dutiesTick = new() { Interval = TimeSpan.FromSeconds(20) };

    Grid EnsureWorkDutiesRow()
    {
        if (_workDutiesRow != null) return _workDutiesRow;
        _workDutiesRow = new Grid { Margin = new Thickness(0, 8, 0, 0), IsVisible = false };
        _todaySection.Children.Add(_workDutiesRow);
        return _workDutiesRow;
    }

    void UpdateWorkDutiesLayout()
    {
        if (_workDutiesRow == null) return;
        bool extOn = _todayExtranet != null && _todayExtranet.IsVisible;
        bool dutOn = _todayDuties != null && _todayDuties.IsVisible;
        _workDutiesRow.IsVisible = extOn || dutOn;
        if (!extOn && !dutOn) return;

        if (extOn && dutOn)
        {
            _workDutiesRow.ColumnDefinitions = new ColumnDefinitions("*,8,*");
            Grid.SetColumn(_todayExtranet!, 0);
            Grid.SetColumn(_todayDuties!, 2);
        }
        else if (extOn)
        {
            _workDutiesRow.ColumnDefinitions = new ColumnDefinitions("*");
            Grid.SetColumn(_todayExtranet!, 0);
        }
        else
        {
            _workDutiesRow.ColumnDefinitions = new ColumnDefinitions("*");
            Grid.SetColumn(_todayDuties!, 0);
        }
    }

    DutyBreakTimes CurrentDutyBreaks() => new(
        DutyBreakTimes.ParseOr(_settings.DutiesMorningOpen, DutyBreakTimes.Default.MorningOpen),
        DutyBreakTimes.ParseOr(_settings.DutiesMorningClose, DutyBreakTimes.Default.MorningClose),
        DutyBreakTimes.ParseOr(_settings.DutiesNoonOpen, DutyBreakTimes.Default.NoonOpen),
        DutyBreakTimes.ParseOr(_settings.DutiesNoonClose, DutyBreakTimes.Default.NoonClose),
        DutyBreakTimes.ParseOr(_settings.DutiesAfternoonOpen, DutyBreakTimes.Default.AfternoonOpen),
        DutyBreakTimes.ParseOr(_settings.DutiesAfternoonClose, DutyBreakTimes.Default.AfternoonClose));

    void BuildDuties()
    {
        var row = EnsureWorkDutiesRow();
        _todayDuties = Card(DutiesGlyph, Look.Pick(Dim, new SolidColorBrush(Color.FromRgb(0x38, 0x98, 0xFF))), _dutiesTitle, _dutiesMore);
        _todayDuties.IsVisible = false;
        _todayDuties.Click += () => ShowHub("duties");
        row.Children.Add(_todayDuties);

        _dutiesTick.Tick += (_, _) => UpdateDutiesUi(notify: true);
        if (_settings.DutiesEnabled) StartDuties();
        else UpdateWorkDutiesLayout();
    }

    void StartDuties()
    {
        _dutiesTick.Start();
        UpdateDutiesUi(notify: true);
        ShowHomeSections();
    }

    void StopDuties()
    {
        _dutiesTick.Stop();
        if (_todayDuties != null) _todayDuties.IsVisible = false;
        UpdateWorkDutiesLayout();
        ShowHomeSections();
    }

    void UpdateDutiesUi(bool notify)
    {
        if (_todayDuties == null) return;
        bool on = _settings.DutiesEnabled;
        _todayDuties.IsVisible = on;
        UpdateWorkDutiesLayout();
        if (!on) return;

        string? person = _settings.DutiesPersonName;
        if (string.IsNullOrWhiteSpace(person))
        {
            _dutiesTitle.Text = "Ämtli · Name wählen";
            _dutiesMore.Text = "Klicken zum Auswählen in den Einstellungen";
            return;
        }

        var now = DateTime.Now;
        var today = DateOnly.FromDateTime(now);
        var breaks = CurrentDutyBreaks();
        var nextWin = DutySchedule.NextWindowDuty(person, now, breaks);
        var nextClean = DutySchedule.NextCleaningDuty(person, now);

        string winText = nextWin != null
            ? $"Fenster: {DutySchedule.FormatShortDate(nextWin.Date, today)} {nextWin.SlotLabel} ({nextWin.OpenAt:HH:mm})"
            : "Fenster: Kein Termin geplant";
        string cleanText = nextClean != null
            ? $"Essraum: {DutySchedule.FormatShortDate(nextClean.Date, today)} {nextClean.At:HH:mm} (Team {nextClean.TeamNumber})"
            : "Essraum: Kein Putzdienst";

        // Put whichever duty is sooner on the primary title line.
        bool cleanFirst = nextClean != null && (nextWin == null || nextClean.Date < nextWin.Date);
        _dutiesTitle.Text = cleanFirst ? cleanText : winText;
        _dutiesMore.Text = cleanFirst ? winText : cleanText;

        if (notify && !_settings.MutePopOuts)
        {
            var seen = new HashSet<string>(_settings.DutiesNotifiedKeys, StringComparer.Ordinal);
            var notif = DutySchedule.CheckNotification(person, now, breaks, seen);
            if (notif != null)
            {
                _settings.DutiesNotifiedKeys.Add(notif.Key);
                if (_settings.DutiesNotifiedKeys.Count > 40)
                    _settings.DutiesNotifiedKeys.RemoveRange(0, _settings.DutiesNotifiedKeys.Count - 40);
                _settings.Save();

                var brush = notif.IsCleaning
                    ? new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x0A))
                    : new SolidColorBrush(Color.FromRgb(0x38, 0x98, 0xFF));
                ShowTask(DutiesGlyph, brush, notif.Title, notif.Body, null,
                    ("Ämtli öffnen", () => { HideTask(); ShowHub("duties"); }),
                    ("OK", HideTask));
            }
        }
    }

    HubView BuildDutiesSettingsPage() => new DutiesSettingsPage(
        _settings,
        CurrentDutyBreaks,
        () => DutySchedule.SuggestMatches(_settings.ProfileName, _settings.ExtranetUserName, Environment.UserName),
        () =>
        {
            _settings.Save();
            if (_settings.DutiesEnabled) StartDuties(); else StopDuties();
        });

    sealed class DutiesSettingsPage : HubView
    {
        readonly AppSettings _settings;
        readonly Func<DutyBreakTimes> _breaks;
        readonly Func<IReadOnlyList<string>> _suggest;
        readonly Action _onChanged;
        readonly StackPanel _root = new() { Spacing = 14 };
        bool _dropdownOpen;
        string _searchQuery = "";

        public DutiesSettingsPage(
            AppSettings settings,
            Func<DutyBreakTimes> breaks,
            Func<IReadOnlyList<string>> suggest,
            Action onChanged)
        {
            _settings = settings;
            _breaks = breaks;
            _suggest = suggest;
            _onChanged = onChanged;
            Child = new ScrollViewer { Content = _root, Padding = new Thickness(0, 0, 12, 16) };
            Rebuild();
        }

        void Rebuild()
        {
            _root.Children.Clear();

            // 1. Enable toggle
            var toggle = new ToggleSwitch();
            toggle.Set(_settings.DutiesEnabled, animate: false);
            toggle.PointerPressed += (_, _) =>
            {
                _settings.DutiesEnabled = !_settings.DutiesEnabled;
                toggle.Set(_settings.DutiesEnabled, animate: true);
                _onChanged();
                Rebuild();
            };

            var toggleRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var toggleText = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            toggleText.Children.Add(new TextBlock { Text = "Ämtli auf Startseite & Benachrichtigungen", FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Look.Ink });
            toggleText.Children.Add(new TextBlock
            {
                Text = "Zeigt rechts neben der Arbeitszeit den nächsten Fenster- und Putzdienst an und benachrichtigt kurz vor/nach den Pausen sowie vor dem Putzdienst.",
                FontSize = 11.5, Foreground = HubTheme.Dim, TextWrapping = TextWrapping.Wrap,
            });
            Grid.SetColumn(toggleText, 0);
            toggle.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(toggle, 1);
            toggleRow.Children.Add(toggleText);
            toggleRow.Children.Add(toggle);
            _root.Children.Add(CardBox(toggleRow));

            // 2. Searchable Name Dropdown with smart top suggestions
            var pickerPanel = new StackPanel { Spacing = 8 };
            pickerPanel.Children.Add(new TextBlock { Text = "Dein Name", FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Look.Ink });
            pickerPanel.Children.Add(new TextBlock
            {
                Text = "Wähle deinen Namen aus dem Lüftungs- und Putzplan. Passende Vorschläge stehen ganz oben.",
                FontSize = 11.5, Foreground = HubTheme.Dim, TextWrapping = TextWrapping.Wrap,
            });

            string currentLabel = string.IsNullOrWhiteSpace(_settings.DutiesPersonName)
                ? "Namen auswählen…"
                : _settings.DutiesPersonName!;
            var dropBtnContent = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            dropBtnContent.Children.Add(new TextBlock
            {
                Text = currentLabel, FontSize = 12.5,
                FontWeight = string.IsNullOrWhiteSpace(_settings.DutiesPersonName) ? FontWeight.Normal : FontWeight.SemiBold,
                Foreground = string.IsNullOrWhiteSpace(_settings.DutiesPersonName) ? HubTheme.Dim : Look.Ink,
                VerticalAlignment = VerticalAlignment.Center,
            });
            var chevron = new TextBlock
            {
                Text = _dropdownOpen ? "▴" : "▾", FontSize = 12, Foreground = HubTheme.Dim,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
            };
            Grid.SetColumn(chevron, 1);
            dropBtnContent.Children.Add(chevron);

            var dropBtn = new Button
            {
                Content = dropBtnContent, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(12, 8), CornerRadius = new CornerRadius(7),
                Background = HubTheme.ActionSurface, BorderBrush = Look.InkA(0x24), BorderThickness = new Thickness(1),
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            dropBtn.Click += (_, _) => { _dropdownOpen = !_dropdownOpen; Rebuild(); };
            pickerPanel.Children.Add(dropBtn);

            if (_dropdownOpen)
            {
                var dropContainer = new Border
                {
                    Background = HubTheme.ActionSurface, BorderBrush = Look.InkA(0x24), BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8), Padding = new Thickness(8),
                };
                var dropInner = new StackPanel { Spacing = 6 };
                var searchBox = new TextBox
                {
                    Text = _searchQuery,
                    PlaceholderText = "Namen suchen…",
                    FontSize = 12.5,
                };
                var listStack = new StackPanel { Spacing = 2 };
                var listScroll = new ScrollViewer { Content = listStack, MaxHeight = 220 };

                void PopulateList(string query)
                {
                    listStack.Children.Clear();
                    string q = query.Trim();
                    var suggestions = _suggest();
                    var suggestionSet = new HashSet<string>(suggestions, StringComparer.OrdinalIgnoreCase);

                    var matchingSuggested = suggestions
                        .Where(n => string.IsNullOrEmpty(q) || n.Contains(q, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    var matchingRest = DutySchedule.AllNames
                        .Where(n => !suggestionSet.Contains(n) && (string.IsNullOrEmpty(q) || n.Contains(q, StringComparison.OrdinalIgnoreCase)))
                        .ToList();

                    if (matchingSuggested.Count > 0)
                    {
                        listStack.Children.Add(new TextBlock
                        {
                            Text = "VORSCHLAG", FontSize = 10, FontWeight = FontWeight.SemiBold,
                            Foreground = HubTheme.Dim, Margin = new Thickness(6, 2, 0, 2),
                        });
                        foreach (string name in matchingSuggested)
                            listStack.Children.Add(BuildNameOption(name, isSuggestion: true));
                        if (matchingRest.Count > 0)
                            listStack.Children.Add(new Border { Height = 1, Background = Look.InkA(0x16), Margin = new Thickness(4, 4) });
                    }

                    foreach (string name in matchingRest)
                        listStack.Children.Add(BuildNameOption(name, isSuggestion: false));

                    if (matchingSuggested.Count == 0 && matchingRest.Count == 0)
                        listStack.Children.Add(new TextBlock
                        {
                            Text = "Kein Name gefunden.", FontSize = 12, Foreground = HubTheme.Dim, Margin = new Thickness(6, 4),
                        });
                }

                PopulateList(_searchQuery);
                searchBox.TextChanged += (_, _) =>
                {
                    _searchQuery = searchBox.Text ?? "";
                    PopulateList(_searchQuery);
                };

                dropInner.Children.Add(searchBox);
                dropInner.Children.Add(listScroll);
                dropContainer.Child = dropInner;
                pickerPanel.Children.Add(dropContainer);
            }

            _root.Children.Add(CardBox(pickerPanel));

            // 3. Upcoming duties preview
            if (!string.IsNullOrWhiteSpace(_settings.DutiesPersonName))
            {
                string person = _settings.DutiesPersonName!;
                var now = DateTime.Now;
                var today = DateOnly.FromDateTime(now);
                var breaks = _breaks();
                var upcomingWin = DutySchedule.UpcomingWindowDuties(person, now, breaks, 3);
                var nextClean = DutySchedule.NextCleaningDuty(person, now);

                var preview = new StackPanel { Spacing = 8 };
                preview.Children.Add(new TextBlock { Text = $"Nächste Termine für {person}", FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Look.Ink });

                preview.Children.Add(new TextBlock { Text = "FENSTER ÖFFNEN & SCHLIESSEN", FontSize = 10.5, FontWeight = FontWeight.SemiBold, Foreground = HubTheme.Dim });
                if (upcomingWin.Count == 0)
                {
                    preview.Children.Add(new TextBlock { Text = "Keine kommenden Termine im aktuellen Plan.", FontSize = 12, Foreground = HubTheme.Dim });
                }
                else
                {
                    foreach (var w in upcomingWin)
                    {
                        preview.Children.Add(new TextBlock
                        {
                            Text = $"• {DutySchedule.FormatShortDate(w.Date, today)} ({w.Date:dd.MM.yyyy}) · {w.SlotLabel} · Öffnen {w.OpenAt:HH:mm} / Schliessen {w.CloseAt:HH:mm}",
                            FontSize = 12, Foreground = Look.Ink, TextWrapping = TextWrapping.Wrap,
                        });
                    }
                }

                preview.Children.Add(new TextBlock { Text = "ESSRAUM PUTZEN (FREITAG 12:45)", FontSize = 10.5, FontWeight = FontWeight.SemiBold, Foreground = HubTheme.Dim, Margin = new Thickness(0, 4, 0, 0) });
                if (nextClean == null)
                {
                    preview.Children.Add(new TextBlock { Text = "Kein Putzdienst zugeteilt.", FontSize = 12, Foreground = HubTheme.Dim });
                }
                else
                {
                    preview.Children.Add(new TextBlock
                    {
                        Text = $"• {DutySchedule.FormatShortDate(nextClean.Date, today)} ({nextClean.Date:dd.MM.yyyy}) um {nextClean.At:HH:mm} · Team {nextClean.TeamNumber} ({string.Join(", ", nextClean.Members)})",
                        FontSize = 12, Foreground = Look.Ink, TextWrapping = TextWrapping.Wrap,
                    });
                }

                _root.Children.Add(CardBox(preview));
            }

            // 4. Break times configuration (window open before break / close after break)
            var timesPanel = new StackPanel { Spacing = 8 };
            timesPanel.Children.Add(new TextBlock { Text = "Pausen-Zeiten für Fenster-Benachrichtigungen", FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Look.Ink });
            timesPanel.Children.Add(new TextBlock
            {
                Text = "Kurz vor der Pause öffnen, nach der Pause wieder schliessen (Format HH:mm).",
                FontSize = 11.5, Foreground = HubTheme.Dim, TextWrapping = TextWrapping.Wrap,
            });

            var mOpen = TimeInput(_settings.DutiesMorningOpen);
            var mClose = TimeInput(_settings.DutiesMorningClose);
            var nOpen = TimeInput(_settings.DutiesNoonOpen);
            var nClose = TimeInput(_settings.DutiesNoonClose);
            var aOpen = TimeInput(_settings.DutiesAfternoonOpen);
            var aClose = TimeInput(_settings.DutiesAfternoonClose);

            timesPanel.Children.Add(TimeRow("Morgen", mOpen, mClose));
            timesPanel.Children.Add(TimeRow("Mittag", nOpen, nClose));
            timesPanel.Children.Add(TimeRow("Nachmittag", aOpen, aClose));

            var statusLbl = new TextBlock { FontSize = 11.5, Foreground = HubTheme.Dim, IsVisible = false };
            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var saveBtn = HubTheme.Action("Zeiten speichern", primary: true);
            saveBtn.Click += (_, _) =>
            {
                _settings.DutiesMorningOpen = DutyBreakTimes.ParseOr(mOpen.Text, DutyBreakTimes.Default.MorningOpen).ToString("HH:mm");
                _settings.DutiesMorningClose = DutyBreakTimes.ParseOr(mClose.Text, DutyBreakTimes.Default.MorningClose).ToString("HH:mm");
                _settings.DutiesNoonOpen = DutyBreakTimes.ParseOr(nOpen.Text, DutyBreakTimes.Default.NoonOpen).ToString("HH:mm");
                _settings.DutiesNoonClose = DutyBreakTimes.ParseOr(nClose.Text, DutyBreakTimes.Default.NoonClose).ToString("HH:mm");
                _settings.DutiesAfternoonOpen = DutyBreakTimes.ParseOr(aOpen.Text, DutyBreakTimes.Default.AfternoonOpen).ToString("HH:mm");
                _settings.DutiesAfternoonClose = DutyBreakTimes.ParseOr(aClose.Text, DutyBreakTimes.Default.AfternoonClose).ToString("HH:mm");
                _onChanged();
                statusLbl.Text = "Gespeichert.";
                statusLbl.IsVisible = true;
            };
            var resetBtn = HubTheme.Action("Standard");
            resetBtn.Click += (_, _) =>
            {
                _settings.DutiesMorningOpen = DutyBreakTimes.Default.MorningOpen.ToString("HH:mm");
                _settings.DutiesMorningClose = DutyBreakTimes.Default.MorningClose.ToString("HH:mm");
                _settings.DutiesNoonOpen = DutyBreakTimes.Default.NoonOpen.ToString("HH:mm");
                _settings.DutiesNoonClose = DutyBreakTimes.Default.NoonClose.ToString("HH:mm");
                _settings.DutiesAfternoonOpen = DutyBreakTimes.Default.AfternoonOpen.ToString("HH:mm");
                _settings.DutiesAfternoonClose = DutyBreakTimes.Default.AfternoonClose.ToString("HH:mm");
                _onChanged();
                Rebuild();
            };
            btnRow.Children.Add(saveBtn);
            btnRow.Children.Add(resetBtn);
            timesPanel.Children.Add(btnRow);
            timesPanel.Children.Add(statusLbl);

            _root.Children.Add(CardBox(timesPanel));
        }

        static Border CardBox(Control child) => new()
        {
            Background = HubTheme.Card,
            BorderBrush = HubTheme.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 12),
            Child = child,
        };

        Button BuildNameOption(string name, bool isSuggestion)
        {
            bool selected = string.Equals(_settings.DutiesPersonName, name, StringComparison.OrdinalIgnoreCase);
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            row.Children.Add(new TextBlock
            {
                Text = name, FontSize = 12.5,
                FontWeight = selected || isSuggestion ? FontWeight.SemiBold : FontWeight.Normal,
                Foreground = Look.Ink, VerticalAlignment = VerticalAlignment.Center,
            });
            string badgeText = selected ? "✓ Ausgewählt" : isSuggestion ? "★ Vorschlag" : "";
            if (!string.IsNullOrEmpty(badgeText))
            {
                var badge = new TextBlock
                {
                    Text = badgeText, FontSize = 11, Foreground = HubTheme.Dim,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(badge, 1);
                row.Children.Add(badge);
            }

            var btn = new Button
            {
                Content = row, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(10, 6), CornerRadius = new CornerRadius(6),
                Background = selected ? Look.InkA(0x18) : Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            btn.Click += (_, _) =>
            {
                _settings.DutiesPersonName = name;
                _settings.DutiesEnabled = true;
                _dropdownOpen = false;
                _searchQuery = "";
                _onChanged();
                Rebuild();
            };
            return btn;
        }

        static TextBox TimeInput(string text) => new()
        {
            Text = text, Width = 72, FontSize = 12,
        };

        static Grid TimeRow(string label, TextBox openBox, TextBox closeBox)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("100,Auto,Auto,Auto,Auto") };
            g.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = Look.Ink, VerticalAlignment = VerticalAlignment.Center });
            var l1 = new TextBlock { Text = "Öffnen:", FontSize = 11.5, Foreground = HubTheme.Dim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            Grid.SetColumn(l1, 1); g.Children.Add(l1);
            Grid.SetColumn(openBox, 2); g.Children.Add(openBox);
            var l2 = new TextBlock { Text = "Schliessen:", FontSize = 11.5, Foreground = HubTheme.Dim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 6, 0) };
            Grid.SetColumn(l2, 3); g.Children.Add(l2);
            Grid.SetColumn(closeBox, 4); g.Children.Add(closeBox);
            return g;
        }
    }
}
