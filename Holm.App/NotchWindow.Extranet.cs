using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Holm.App.Controls;
using WinNotch;

namespace Holm.App;

/// <summary>
/// Per-device Extranet (extranet.bbcag.ch) time-tracking overview widget on the start page and badge-out notification.
/// Strictly read-only (GET /api/timesheet/v1/records/today); never modifies any badge/timesheet records.
/// </summary>
public sealed partial class NotchWindow
{
    const string ExtranetGlyph = "\uE823"; // Clock / Work time

    readonly TextBlock _extranetTitle = CardTitle();
    readonly TextBlock _extranetMore = CardMore();
    NotchButton _todayExtranet = null!;
    readonly DispatcherTimer _extranetPoll = new() { Interval = TimeSpan.FromMinutes(3) };
    readonly DispatcherTimer _extranetTick = new() { Interval = TimeSpan.FromSeconds(15) };
    string? _extranetAccessToken;
    DateTime _extranetTokenExpires;
    ExtranetSnapshot? _extranetSnapshot;
    bool _extranetFetching;
    Action? _extranetSettingsRefresh;

    void BuildExtranet()
    {
        _todayExtranet = Card(ExtranetGlyph, Look.Pick(Dim, GreenBrush), _extranetTitle, _extranetMore);
        _todayExtranet.Margin = new Thickness(0, 8, 0, 0);
        _todayExtranet.IsVisible = false;
        _todayExtranet.Click += () =>
        {
            if (string.IsNullOrEmpty(_settings.ExtranetRefreshToken)) ShowHub("extranet");
            else OpenExtranetInBrowser();
        };
        _todaySection.Children.Add(_todayExtranet);

        _extranetPoll.Tick += (_, _) => _ = RefreshExtranetAsync();
        _extranetTick.Tick += (_, _) => UpdateExtranetUi(notifyIfReady: true);
        if (_settings.ExtranetEnabled) StartExtranet();
    }

    void StartExtranet()
    {
        _extranetPoll.Start();
        _extranetTick.Start();
        UpdateExtranetUi(notifyIfReady: false);
        if (!string.IsNullOrEmpty(_settings.ExtranetRefreshToken))
            _ = RefreshExtranetAsync();
    }

    void StopExtranet()
    {
        _extranetPoll.Stop();
        _extranetTick.Stop();
        _todayExtranet.IsVisible = false;
        ShowHomeSections();
    }

    void OpenExtranetInBrowser()
    {
        try { Process.Start(new ProcessStartInfo(ExtranetClient.WebUrl) { UseShellExecute = true }); }
        catch { }
    }

    async Task RefreshExtranetAsync(CancellationToken ct = default)
    {
        if (!_settings.ExtranetEnabled || _extranetFetching) return;
        string? refreshToken = _settings.ExtranetRefreshToken;
        if (string.IsNullOrEmpty(refreshToken))
        {
            _extranetSnapshot = new ExtranetSnapshot(DateTime.Now, Array.Empty<ExtranetInterval>(), _settings.ExtranetTargetMinutes, SignedIn: false);
            Dispatcher.UIThread.Post(() => UpdateExtranetUi(notifyIfReady: false));
            return;
        }
        _extranetFetching = true;
        try
        {
            if (string.IsNullOrEmpty(_extranetAccessToken) || DateTime.UtcNow >= _extranetTokenExpires)
            {
                var auth = await ExtranetClient.RefreshAsync(refreshToken, ct);
                _extranetAccessToken = auth.AccessToken;
                _extranetTokenExpires = DateTime.UtcNow.AddSeconds(Math.Max(30, auth.ExpiresInSeconds - 20));
                if (!string.IsNullOrEmpty(auth.RefreshToken) && auth.RefreshToken != _settings.ExtranetRefreshToken)
                {
                    _settings.ExtranetRefreshToken = auth.RefreshToken;
                    _settings.Save();
                }
            }
            var intervals = await ExtranetClient.FetchTodayAsync(_extranetAccessToken!, ct);
            _extranetSnapshot = new ExtranetSnapshot(DateTime.Now, intervals, _settings.ExtranetTargetMinutes, SignedIn: true);
        }
        catch (Exception ex)
        {
            Diag.Log("Extranet refresh: " + ex.Message);
            _extranetAccessToken = null;
            _extranetSnapshot = new ExtranetSnapshot(DateTime.Now, _extranetSnapshot?.Intervals ?? Array.Empty<ExtranetInterval>(), _settings.ExtranetTargetMinutes, SignedIn: !string.IsNullOrEmpty(_settings.ExtranetRefreshToken), Error: ex.Message);
        }
        finally
        {
            _extranetFetching = false;
            Dispatcher.UIThread.Post(() =>
            {
                UpdateExtranetUi(notifyIfReady: true);
                _extranetSettingsRefresh?.Invoke();
            });
        }
    }

    void UpdateExtranetUi(bool notifyIfReady)
    {
        if (_todayExtranet == null) return;
        bool on = _settings.ExtranetEnabled;
        _todayExtranet.IsVisible = on;
        if (!on) return;

        var now = DateTime.Now;
        int targetMin = Math.Clamp(_settings.ExtranetTargetMinutes, 60, 16 * 60);
        if (string.IsNullOrEmpty(_settings.ExtranetRefreshToken))
        {
            _extranetTitle.Text = "Extranet · Nicht angemeldet";
            _extranetMore.Text = "Klicken zum Anmelden in den Einstellungen";
            return;
        }
        var snap = _extranetSnapshot;
        if (snap == null)
        {
            _extranetTitle.Text = "Extranet";
            _extranetMore.Text = "Lade Arbeitszeit…";
            return;
        }
        if (snap.Intervals.Count == 0)
        {
            _extranetTitle.Text = snap.Error != null ? "Extranet · Verbindung fehlgeschlagen" : "Extranet · Heute noch nicht gebadgt";
            _extranetMore.Text = snap.Error ?? $"Tagessoll {ExtranetSnapshot.FormatDuration(TimeSpan.FromMinutes(targetMin))}";
            return;
        }
        var liveSnap = snap with { TargetMinutes = targetMin };
        var worked = liveSnap.WorkedAt(now);
        var remaining = liveSnap.RemainingAt(now);
        if (remaining <= TimeSpan.Zero)
        {
            var over = liveSnap.OvertimeAt(now);
            string overText = over.TotalMinutes >= 1 ? $" (+{ExtranetSnapshot.FormatDuration(over)})" : "";
            _extranetTitle.Text = $"Ausbadgen möglich!{overText}";
            _extranetMore.Text = $"Gearbeitet {ExtranetSnapshot.FormatDuration(worked)} · Ziel {ExtranetSnapshot.FormatDuration(liveSnap.Target)} erreicht";
            if (notifyIfReady)
            {
                string todayKey = now.ToString("yyyy-MM-dd");
                if (_settings.ExtranetNotifiedDate != todayKey)
                {
                    _settings.ExtranetNotifiedDate = todayKey;
                    _settings.Save();
                    ShowTask(ExtranetGlyph, GreenBrush, "Ausbadgen möglich!",
                        $"Dein Tagessoll ({ExtranetSnapshot.FormatDuration(liveSnap.Target)}) ist erreicht.",
                        null, ("Extranet öffnen", OpenExtranetInBrowser), ("OK", HideTask));
                }
            }
        }
        else
        {
            var finish = liveSnap.FinishAt(now);
            string untilStr = finish.HasValue ? $"bis {finish.Value:HH:mm}" : "";
            string stateStr = liveSnap.IsClockedIn ? untilStr : $"{untilStr} (pausiert)";
            _extranetTitle.Text = $"Noch {ExtranetSnapshot.FormatDuration(remaining)} · {stateStr}";
            _extranetMore.Text = $"Gearbeitet {ExtranetSnapshot.FormatDuration(worked)} von {ExtranetSnapshot.FormatDuration(liveSnap.Target)}";
        }
    }

    HubView BuildExtranetSettingsPage()
    {
        var page = new ExtranetSettingsPage(
            () => _settings.ExtranetEnabled,
            on =>
            {
                _settings.ExtranetEnabled = on;
                _settings.Save();
                if (on) StartExtranet(); else StopExtranet();
                ShowHomeSections();
            },
            () => _settings.ExtranetTargetMinutes,
            mins =>
            {
                _settings.ExtranetTargetMinutes = Math.Clamp(mins, 60, 16 * 60);
                _settings.Save();
                UpdateExtranetUi(notifyIfReady: false);
            },
            () => (_settings.ExtranetRefreshToken != null, _settings.ExtranetUserName, _extranetSnapshot),
            async (email, password) =>
            {
                var auth = await ExtranetClient.SignInAsync(email, password);
                _extranetAccessToken = auth.AccessToken;
                _extranetTokenExpires = DateTime.UtcNow.AddSeconds(Math.Max(30, auth.ExpiresInSeconds - 20));
                _settings.ExtranetRefreshToken = auth.RefreshToken;
                _settings.ExtranetUserName = auth.UserName ?? email.Trim();
                _settings.ExtranetEnabled = true;
                _settings.Save();
                StartExtranet();
                await RefreshExtranetAsync();
                ShowHomeSections();
            },
            () =>
            {
                _extranetAccessToken = null;
                _extranetSnapshot = null;
                _settings.ExtranetRefreshToken = null;
                _settings.ExtranetUserName = null;
                _settings.Save();
                UpdateExtranetUi(notifyIfReady: false);
            },
            () => RefreshExtranetAsync(),
            OpenExtranetInBrowser);
        _extranetSettingsRefresh = page.Refresh;
        return page;
    }

    sealed class ExtranetSettingsPage : HubView
    {
        readonly Func<bool> _getEnabled;
        readonly Action<bool> _setEnabled;
        readonly Func<int> _getTarget;
        readonly Action<int> _setTarget;
        readonly Func<(bool SignedIn, string? User, ExtranetSnapshot? Snap)> _state;
        readonly Func<string, string, Task> _signIn;
        readonly Action _signOut;
        readonly Func<Task> _refreshNow;
        readonly Action _openBrowser;
        readonly StackPanel _body = new() { Spacing = 14 };

        public ExtranetSettingsPage(
            Func<bool> getEnabled, Action<bool> setEnabled,
            Func<int> getTarget, Action<int> setTarget,
            Func<(bool SignedIn, string? User, ExtranetSnapshot? Snap)> state,
            Func<string, string, Task> signIn, Action signOut,
            Func<Task> refreshNow, Action openBrowser)
        {
            _getEnabled = getEnabled;
            _setEnabled = setEnabled;
            _getTarget = getTarget;
            _setTarget = setTarget;
            _state = state;
            _signIn = signIn;
            _signOut = signOut;
            _refreshNow = refreshNow;
            _openBrowser = openBrowser;
            Child = new ScrollViewer { Content = _body, Padding = new Thickness(0, 0, 12, 16) };
            Refresh();
        }

        public void Refresh()
        {
            _body.Children.Clear();
            var (signedIn, user, snap) = _state();
            int targetMin = _getTarget();
            int targetH = targetMin / 60, targetM = targetMin % 60;

            _body.Children.Add(new TextBlock
            {
                Text = "Extranet (extranet.bbcag.ch)",
                FontSize = 20, FontWeight = FontWeight.SemiBold, Foreground = Look.Ink,
            });
            _body.Children.Add(new TextBlock
            {
                Text = "Zeigt auf der Startseite an, wie lange du heute noch arbeiten musst und bis wann. Anmeldung gilt nur für dieses Gerät (nur Lesezugriff).",
                FontSize = 13, Foreground = HubTheme.Dim, TextWrapping = TextWrapping.Wrap,
            });

            // Enable toggle
            var toggleRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            toggleRow.Children.Add(new TextBlock { Text = "Extranet-Widget aktivieren", FontSize = 14, Foreground = Look.Ink, VerticalAlignment = VerticalAlignment.Center });
            var sw = new ToggleSwitch { Width = 44, Height = 24, Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand) };
            sw.Set(_getEnabled(), animate: false);
            sw.PointerPressed += (_, _) => { bool next = !sw.IsOn; sw.Set(next, animate: true); _setEnabled(next); Refresh(); };
            Grid.SetColumn(sw, 1);
            toggleRow.Children.Add(sw);
            _body.Children.Add(CardBox(toggleRow));

            // Daily target hours + minutes (default 8h 12m)
            var targetRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            targetRow.Children.Add(new TextBlock { Text = "Tagessoll:", FontSize = 14, Foreground = Look.Ink, VerticalAlignment = VerticalAlignment.Center });
            var hBox = HubTheme.Field("8"); hBox.Text = targetH.ToString(); hBox.Width = 56;
            var mBox = HubTheme.Field("12"); mBox.Text = targetM.ToString("00"); mBox.Width = 56;
            var saveTarget = HubTheme.Action("Speichern", primary: false);
            saveTarget.Click += (_, _) =>
            {
                if (int.TryParse(hBox.Text, out int hh) && int.TryParse(mBox.Text, out int mm))
                {
                    _setTarget(Math.Clamp(hh, 1, 15) * 60 + Math.Clamp(mm, 0, 59));
                    Refresh();
                }
            };
            targetRow.Children.Add(hBox);
            targetRow.Children.Add(new TextBlock { Text = "h", Foreground = HubTheme.Dim, VerticalAlignment = VerticalAlignment.Center });
            targetRow.Children.Add(mBox);
            targetRow.Children.Add(new TextBlock { Text = "min", Foreground = HubTheme.Dim, VerticalAlignment = VerticalAlignment.Center });
            targetRow.Children.Add(saveTarget);
            _body.Children.Add(CardBox(targetRow));

            // Account / Login card
            var authStack = new StackPanel { Spacing = 10 };
            if (signedIn)
            {
                authStack.Children.Add(new TextBlock { Text = $"Angemeldet als {user ?? "Extranet-Benutzer"}", FontSize = 14, FontWeight = FontWeight.SemiBold, Foreground = HubTheme.Green });
                if (snap != null && snap.Intervals.Count > 0)
                {
                    var now = DateTime.Now;
                    var worked = snap.WorkedAt(now);
                    var rem = snap.RemainingAt(now);
                    var fin = snap.FinishAt(now);
                    string summary = rem <= TimeSpan.Zero
                        ? $"Gearbeitet: {ExtranetSnapshot.FormatDuration(worked)} · Ziel erreicht (Ausbadgen möglich!)"
                        : $"Gearbeitet: {ExtranetSnapshot.FormatDuration(worked)} · Noch {ExtranetSnapshot.FormatDuration(rem)} (bis {fin:HH:mm})";
                    authStack.Children.Add(new TextBlock { Text = summary, FontSize = 13, Foreground = HubTheme.Dim, TextWrapping = TextWrapping.Wrap });
                }
                else if (snap?.Error != null)
                {
                    authStack.Children.Add(new TextBlock { Text = snap.Error, FontSize = 12, Foreground = HubTheme.Red, TextWrapping = TextWrapping.Wrap });
                }
                var btns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                var refBtn = HubTheme.Action("Aktualisieren", primary: true);
                refBtn.Click += async (_, _) => { refBtn.IsEnabled = false; await _refreshNow(); Refresh(); };
                var webBtn = HubTheme.Action("Im Browser öffnen", primary: false);
                webBtn.Click += (_, _) => _openBrowser();
                var outBtn = HubTheme.Action("Abmelden", primary: false);
                outBtn.Click += (_, _) => { _signOut(); Refresh(); };
                btns.Children.Add(refBtn);
                btns.Children.Add(webBtn);
                btns.Children.Add(outBtn);
                authStack.Children.Add(btns);
            }
            else
            {
                authStack.Children.Add(new TextBlock { Text = "Anmeldung (nur dieses Gerät)", FontSize = 14, FontWeight = FontWeight.SemiBold, Foreground = Look.Ink });
                var emailBox = HubTheme.Field("name@bbcag.ch");
                var passBox = HubTheme.Field("Passwort");
                passBox.PasswordChar = '•';
                var status = new TextBlock { FontSize = 12, Foreground = HubTheme.Dim, TextWrapping = TextWrapping.Wrap };
                var btns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                var loginBtn = HubTheme.Action("Anmelden", primary: true);
                loginBtn.Click += async (_, _) =>
                {
                    loginBtn.IsEnabled = false;
                    status.Foreground = HubTheme.Dim;
                    status.Text = "Melde an…";
                    try
                    {
                        await _signIn(emailBox.Text ?? "", passBox.Text ?? "");
                        Refresh();
                    }
                    catch (Exception ex)
                    {
                        status.Foreground = HubTheme.Red;
                        status.Text = ex.Message;
                        loginBtn.IsEnabled = true;
                    }
                };
                var webBtn = HubTheme.Action("Extranet im Browser öffnen", primary: false);
                webBtn.Click += (_, _) => _openBrowser();
                btns.Children.Add(loginBtn);
                btns.Children.Add(webBtn);
                authStack.Children.Add(emailBox);
                authStack.Children.Add(passBox);
                authStack.Children.Add(btns);
                authStack.Children.Add(status);
            }
            _body.Children.Add(CardBox(authStack));
        }

        static Border CardBox(Control child) => new()
        {
            Background = HubTheme.Card,
            BorderBrush = HubTheme.Edge,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14),
            Child = child,
        };
    }
}
