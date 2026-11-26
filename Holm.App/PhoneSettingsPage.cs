using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using WinNotch.Sharing;

namespace Holm.App;

/// <summary>
/// Settings › Phone: shows the phone linked through your HolyNotch account, lets you remove it from this PC,
/// or re-allow previously removed phones.
/// </summary>
internal sealed class PhoneSettingsPage : HubView
{
    readonly Func<OwnDevice?> _getPhone;
    readonly Func<bool> _isConnected;
    readonly Func<DateTime> _lastSeen;
    readonly Func<string?> _battery;
    readonly Func<bool> _charging;
    readonly Func<bool> _signedIn;
    readonly Func<string?> _email;
    readonly Func<int> _removedCount;
    readonly Action<OwnDevice> _removePhone;
    readonly Action _lookAgain;
    readonly Action _openAccount;
    readonly Action<Action> _unsubscribe;
    readonly Border _host = new();

    public PhoneSettingsPage(
        Func<OwnDevice?> getPhone,
        Func<bool> isConnected,
        Func<DateTime> lastSeen,
        Func<string?> battery,
        Func<bool> charging,
        Func<bool> signedIn,
        Func<string?> email,
        Func<int> removedCount,
        Action<OwnDevice> removePhone,
        Action lookAgain,
        Action openAccount,
        Action<Action> subscribe,
        Action<Action> unsubscribe)
    {
        _getPhone = getPhone;
        _isConnected = isConnected;
        _lastSeen = lastSeen;
        _battery = battery;
        _charging = charging;
        _signedIn = signedIn;
        _email = email;
        _removedCount = removedCount;
        _removePhone = removePhone;
        _lookAgain = lookAgain;
        _openAccount = openAccount;
        _unsubscribe = unsubscribe;

        Child = HubTheme.Scroller(_host);
        subscribe(OnChanged);
        Render();
    }

    void OnChanged() => Dispatcher.UIThread.Post(Render);

    public override void Detach() => _unsubscribe(OnChanged);

    void Render()
    {
        var content = new StackPanel { Spacing = 14 };
        var phone = _getPhone();

        if (phone != null)
        {
            var body = new StackPanel { Spacing = 10 };
            var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            header.Children.Add(new Border
            {
                Width = 40, Height = 40, CornerRadius = new CornerRadius(HubTheme.Round(10)),
                Background = new SolidColorBrush(Color.FromRgb(0x5E, 0x5C, 0xE6)).ToImmutable(),
                Child = HubTheme.Glyph("\uE8EA", 18, Brushes.White),
            });

            var parts = new List<string> { _isConnected() ? "Connected" : "Last seen " + FormatAgo(DateTime.UtcNow - _lastSeen()) };
            if (_battery() is { } bat) parts.Add(_charging() ? $"{bat} · charging" : bat);

            var info = new StackPanel { Margin = new Thickness(14, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock { Text = phone.Name, FontSize = 16, FontWeight = FontWeight.SemiBold });
            info.Children.Add(new TextBlock { Text = string.Join(" · ", parts), FontSize = 13, Foreground = HubTheme.Dim });
            Grid.SetColumn(info, 1);
            header.Children.Add(info);

            var remove = HubTheme.Action("Remove phone");
            remove.Foreground = HubTheme.Red;
            remove.VerticalAlignment = VerticalAlignment.Center;
            remove.Click += (_, _) => Ask(
                $"Remove {phone.Name}?",
                "This PC will unlink the phone and ignore its notifications and status until you look for it again.",
                ("Cancel", () => { }),
                ("Remove", () => _removePhone(phone)));
            Grid.SetColumn(remove, 2);
            header.Children.Add(remove);

            body.Children.Add(header);
            body.Children.Add(Words("Linked automatically through your HolyNotch account. No friend code is needed.", dim: true));
            content.Children.Add(Card(body));
        }
        else
        {
            var body = new StackPanel { Spacing = 10 };
            body.Children.Add(new TextBlock { Text = "No phone linked", FontSize = 16, FontWeight = FontWeight.SemiBold });
            if (!_signedIn())
            {
                body.Children.Add(Words(
                    "Your phone links automatically through your HolyNotch account — no friend code needed. Sign in to the same account here and in the HolyNotch phone app.",
                    dim: true));
                var signIn = HubTheme.Action("Sign in…", primary: true);
                signIn.HorizontalAlignment = HorizontalAlignment.Left;
                signIn.Click += (_, _) => _openAccount();
                body.Children.Add(signIn);
            }
            else
            {
                body.Children.Add(Words(
                    $"Signed in as {_email()}. Open the HolyNotch app on your phone, signed in to the same account, and it will link automatically.",
                    dim: true));
                var look = HubTheme.Action("Look for phone", primary: true);
                look.HorizontalAlignment = HorizontalAlignment.Left;
                look.Click += (_, _) => _lookAgain();
                body.Children.Add(look);
            }
            content.Children.Add(Card(body));
        }

        int removed = _removedCount();
        if (removed > 0)
        {
            var removedCard = new StackPanel { Spacing = 8 };
            removedCard.Children.Add(new TextBlock
            {
                Text = removed == 1 ? "1 removed phone" : $"{removed} removed phones",
                FontSize = 14, FontWeight = FontWeight.SemiBold,
            });
            removedCard.Children.Add(Words("Removed phones stay unlinked on this PC until you allow them again.", dim: true));
            var allow = HubTheme.Action("Allow removed phones again");
            allow.HorizontalAlignment = HorizontalAlignment.Left;
            allow.Click += (_, _) => _lookAgain();
            removedCard.Children.Add(allow);
            content.Children.Add(Card(removedCard));
        }

        _host.Child = content;
    }

    static string FormatAgo(TimeSpan span) => span.TotalMinutes < 1 ? "now"
        : span.TotalHours < 1 ? $"{(int)span.TotalMinutes} min ago"
        : span.TotalDays < 1 ? $"{(int)span.TotalHours} h ago"
        : $"{(int)span.TotalDays} d ago";

    void Ask(string title, string message, params (string Label, Action Run)[] choices)
    {
        if (TopLevel.GetTopLevel(this) is SettingsWindow window) window.ShowDialog(title, message, choices);
    }

    static Border Card(Control child) => new()
    {
        Background = Look.Current.Glass && Look.Current.IsDark ? HubTheme.Raised : HubTheme.Card,
        CornerRadius = new CornerRadius(12), Padding = new Thickness(18), Child = child,
    };

    static TextBlock Words(string text, bool dim) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13, Foreground = dim ? HubTheme.Dim : Look.Ink,
    };
}
