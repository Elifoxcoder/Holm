using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using WinNotch;
using WinNotch.Sharing;

namespace Holm.App;

/// <summary>
/// Experimental tab in HolyNotch Settings allowing 1:1 E2E encrypted chat with upstream Holm users
/// by emulating a standard Holm client on https://relay.holmnotch.ch with a separate identity.
/// </summary>
public sealed partial class NotchWindow
{
    HolmBridgeService? _holmBridge;

    HolmBridgeService EnsureHolmBridge()
    {
        if (_holmBridge != null) return _holmBridge;
        _holmBridge = new HolmBridgeService(_settings, a => Dispatcher.UIThread.InvokeAsync(a).GetTask());
        _holmBridge.MessageReceived += (friend, msg) =>
        {
            if (!_settings.MutePopOuts)
                ShowTask(ChatGlyph, new SolidColorBrush(Color.FromRgb(0x38, 0x98, 0xFF)), $"{friend.Name} (Holm)", msg.Text, null,
                    ("Open", () => { HideTask(); ShowHub("experimental"); }),
                    ("Dismiss", HideTask));
        };
        return _holmBridge;
    }

    HubView BuildExperimentalPage() => new ExperimentalHolmPage(EnsureHolmBridge(), text => _ = TrySetClipboardAsync(text));

    sealed class ExperimentalHolmPage : HubView
    {
        readonly HolmBridgeService _bridge;
        readonly Action<string> _copy;
        readonly StackPanel _root = new() { Spacing = 14 };
        string? _selectedFriendKey;

        public ExperimentalHolmPage(HolmBridgeService bridge, Action<string> copy)
        {
            _bridge = bridge;
            _copy = copy;
            _bridge.Changed += OnBridgeChanged;
            DetachedFromVisualTree += (_, _) => _bridge.Changed -= OnBridgeChanged;
            Child = new ScrollViewer { Content = _root, Padding = new Thickness(0, 0, 12, 16) };
            Refresh();
        }

        void OnBridgeChanged() => Dispatcher.UIThread.Post(() => Refresh());

        void Refresh(string? statusMsg = null, bool isError = false)
        {
            _root.Children.Clear();

            _root.Children.Add(new TextBlock
            {
                Text = "Experimental · Holm Bridge",
                FontSize = 20, FontWeight = FontWeight.SemiBold, Foreground = Look.Ink,
            });
            _root.Children.Add(new TextBlock
            {
                Text = "Chatte Ende-zu-Ende-verschlüsselt mit originalen Holm-Nutzern über relay.holmnotch.ch. HolyNotch tritt dort mit einer eigenen Holm-Identität als normaler Holm-Nutzer auf.",
                FontSize = 13, Foreground = HubTheme.Dim, TextWrapping = TextWrapping.Wrap,
            });

            // Toggle card
            var toggleRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            toggleRow.Children.Add(new TextBlock
            {
                Text = "Holm Bridge aktivieren (Experimental)",
                FontSize = 14, Foreground = Look.Ink, VerticalAlignment = VerticalAlignment.Center,
            });
            var sw = new ToggleSwitch { Width = 44, Height = 24, Cursor = new Cursor(StandardCursorType.Hand) };
            sw.Set(_bridge.Enabled, animate: false);
            sw.PointerPressed += (_, _) =>
            {
                bool next = !sw.IsOn;
                sw.Set(next, animate: true);
                _bridge.SetEnabled(next);
            };
            Grid.SetColumn(sw, 1);
            toggleRow.Children.Add(sw);
            _root.Children.Add(CardBox(toggleRow));

            if (!string.IsNullOrEmpty(statusMsg))
                _root.Children.Add(new TextBlock { Text = statusMsg, FontSize = 12.5, Foreground = isError ? HubTheme.Red : HubTheme.Green, TextWrapping = TextWrapping.Wrap });

            if (!_bridge.Enabled) return;

            // Invite & Add Holm User card
            var connectStack = new StackPanel { Spacing = 10 };
            connectStack.Children.Add(new TextBlock { Text = "Mit Holm-Nutzer verbinden", FontSize = 14, FontWeight = FontWeight.SemiBold, Foreground = Look.Ink });

            var myCodeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            var codeLabel = new TextBlock
            {
                Text = _bridge.ActiveInviteCode ?? "Noch kein HOLM-Code erstellt",
                FontSize = 14, FontWeight = FontWeight.SemiBold,
                Foreground = _bridge.ActiveInviteCode != null ? Look.Accent : HubTheme.Dim,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var genBtn = HubTheme.Action(_bridge.ActiveInviteCode == null ? "HOLM-Code erstellen" : "Neuer Code", primary: false);
            genBtn.Click += async (_, _) =>
            {
                genBtn.IsEnabled = false;
                try
                {
                    string c = await _bridge.CreateHolmInviteAsync();
                    _copy(c);
                    Refresh($"Holm-Code erstellt & kopiert: {c}");
                }
                catch (Exception ex) { Refresh("Fehler: " + ex.Message, isError: true); }
            };
            myCodeRow.Children.Add(codeLabel);
            myCodeRow.Children.Add(genBtn);
            if (_bridge.ActiveInviteCode != null)
            {
                var copyBtn = HubTheme.Action("Kopieren", primary: false);
                copyBtn.Click += (_, _) => { _copy(_bridge.ActiveInviteCode); Refresh("Code kopiert."); };
                myCodeRow.Children.Add(copyBtn);
            }
            connectStack.Children.Add(myCodeRow);

            var addRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var addInput = HubTheme.Field("HOLM-XXXXX-XXXXX oder WNF1.… einfügen");
            var addBtn = HubTheme.Action("Hinzufügen", primary: true);
            addBtn.Margin = new Thickness(8, 0, 0, 0);
            async Task DoAdd()
            {
                string val = addInput.Text ?? "";
                if (string.IsNullOrWhiteSpace(val)) return;
                addBtn.IsEnabled = false;
                try
                {
                    var (ok, msg) = await _bridge.AddHolmUserAsync(val);
                    Refresh(msg, isError: !ok);
                }
                catch (Exception ex) { Refresh("Fehler: " + ex.Message, isError: true); }
            }
            addBtn.Click += (_, _) => _ = DoAdd();
            addInput.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; _ = DoAdd(); } };
            addRow.Children.Add(addInput);
            Grid.SetColumn(addBtn, 1);
            addRow.Children.Add(addBtn);
            connectStack.Children.Add(addRow);
            _root.Children.Add(CardBox(connectStack));

            // Incoming Holm friend requests
            if (_bridge.IncomingRequests.Count > 0)
            {
                var reqStack = new StackPanel { Spacing = 8 };
                reqStack.Children.Add(new TextBlock { Text = "Eingehende Holm-Anfragen", FontSize = 14, FontWeight = FontWeight.SemiBold, Foreground = Look.Ink });
                foreach (var req in _bridge.IncomingRequests.ToList())
                {
                    var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
                    row.Children.Add(new TextBlock { Text = req.Card.Name, FontSize = 13.5, Foreground = Look.Ink, VerticalAlignment = VerticalAlignment.Center });
                    var acc = HubTheme.Action("Annehmen", primary: true);
                    acc.Margin = new Thickness(8, 0, 0, 0);
                    acc.Click += async (_, _) =>
                    {
                        try { await _bridge.AcceptRequestAsync(req); Refresh($"{req.Card.Name} angenommen."); }
                        catch (Exception ex) { Refresh("Fehler: " + ex.Message, isError: true); }
                    };
                    var dec = HubTheme.Action("Ablehnen", primary: false);
                    dec.Margin = new Thickness(6, 0, 0, 0);
                    dec.Click += (_, _) => _bridge.DeclineRequest(req);
                    Grid.SetColumn(acc, 1); Grid.SetColumn(dec, 2);
                    row.Children.Add(acc); row.Children.Add(dec);
                    reqStack.Children.Add(row);
                }
                _root.Children.Add(CardBox(reqStack));
            }

            // Holm friends + 1:1 chat panel
            var friends = _bridge.Friends;
            if (friends.Count == 0)
            {
                _root.Children.Add(CardBox(new TextBlock
                {
                    Text = "Noch keine Holm-Kontakte. Erstelle oben einen HOLM-XXXXX-XXXXX-Code oder füge den Code eines Holm-Nutzers ein.",
                    FontSize = 13, Foreground = HubTheme.Dim, TextWrapping = TextWrapping.Wrap,
                }));
                return;
            }

            var selected = friends.FirstOrDefault(f => f.Key == _selectedFriendKey) ?? friends[0];
            _selectedFriendKey = selected.Key;

            var split = new Grid { ColumnDefinitions = new ColumnDefinitions("180,12,*"), MinHeight = 300 };

            // Left: Holm contacts list
            var friendList = new StackPanel { Spacing = 6 };
            foreach (var f in friends)
            {
                bool active = f.Key == selected.Key;
                var item = new Border
                {
                    Background = active ? HubTheme.Selected : HubTheme.ActionSurface,
                    BorderBrush = HubTheme.Edge,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(10, 8),
                    Cursor = new Cursor(StandardCursorType.Hand),
                    Child = new TextBlock { Text = f.Name, FontSize = 13, FontWeight = active ? FontWeight.SemiBold : FontWeight.Normal, Foreground = Look.Ink, TextTrimming = TextTrimming.CharacterEllipsis },
                };
                item.PointerPressed += (_, _) => { _selectedFriendKey = f.Key; Refresh(); };
                friendList.Children.Add(item);
            }
            split.Children.Add(friendList);

            // Right: Chat with selected Holm friend
            var chatGrid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
            var chatHeader = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 8) };
            chatHeader.Children.Add(new TextBlock { Text = $"{selected.Name} (Holm)", FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = Look.Ink, VerticalAlignment = VerticalAlignment.Center });
            var remBtn = HubTheme.Action("Entfernen", primary: false);
            remBtn.Click += (_, _) => { _bridge.RemoveFriend(selected); _selectedFriendKey = null; Refresh(); };
            Grid.SetColumn(remBtn, 1);
            chatHeader.Children.Add(remBtn);
            chatGrid.Children.Add(chatHeader);

            var msgStack = new StackPanel { Spacing = 6 };
            foreach (var m in _bridge.MessagesFor(selected.Key))
            {
                var bubble = new Border
                {
                    Background = m.Mine ? HubTheme.ActionFill : HubTheme.ActionSurface,
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(10, 6),
                    HorizontalAlignment = m.Mine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                    MaxWidth = 360,
                    Child = new TextBlock
                    {
                        Text = m.Text,
                        FontSize = 13,
                        Foreground = m.Mine ? HubTheme.OnAccent : Look.Ink,
                        TextWrapping = TextWrapping.Wrap,
                    },
                };
                msgStack.Children.Add(bubble);
            }
            var msgScroll = new ScrollViewer { Content = msgStack, MaxHeight = 240, Padding = new Thickness(0, 0, 6, 6) };
            Grid.SetRow(msgScroll, 1);
            chatGrid.Children.Add(msgScroll);

            var sendRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 8, 0, 0) };
            var msgInput = HubTheme.Field($"Nachricht an {selected.Name}…");
            var sendBtn = HubTheme.Action("Senden", primary: true);
            sendBtn.Margin = new Thickness(8, 0, 0, 0);
            async Task DoSend()
            {
                string txt = msgInput.Text ?? "";
                if (string.IsNullOrWhiteSpace(txt)) return;
                sendBtn.IsEnabled = false;
                try
                {
                    await _bridge.SendChatAsync(selected, txt);
                    Refresh();
                }
                catch (Exception ex) { Refresh("Senden fehlgeschlagen: " + ex.Message, isError: true); }
            }
            sendBtn.Click += (_, _) => _ = DoSend();
            msgInput.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; _ = DoSend(); } };
            sendRow.Children.Add(msgInput);
            Grid.SetColumn(sendBtn, 1);
            sendRow.Children.Add(sendBtn);
            Grid.SetRow(sendRow, 2);
            chatGrid.Children.Add(sendRow);

            Grid.SetColumn(chatGrid, 2);
            split.Children.Add(chatGrid);
            _root.Children.Add(CardBox(split));
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
