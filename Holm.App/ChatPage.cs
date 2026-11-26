using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using WinNotch.Sharing;

namespace Holm.App;

/// <summary>
/// Chat, as in the messengers people know (the WPF app's ChatPage): the conversations on the left (searchable, newest
/// first), the one you're in on the right with bubbles that run together, big emoji, reactions, and a round message box.
/// </summary>
internal sealed class ChatPage : HubView
{
    static IBrush B(uint argb) => new SolidColorBrush(Color.FromUInt32(argb)).ToImmutable();

    // The page's own shades: the list a little lighter than the conversation, bubbles that stand out from both.
    static readonly IBrush ListBack = Look.InkA(0x0E), ConversationBack = B(0x40000000), TheirBubble = HubTheme.CardStrong,
        Composer = HubTheme.CardStrong, ComposerEdge = Look.InkA(0x1C), DotEdge = HubTheme.Panel;
    static readonly IBrush MyBubble = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.FromRgb(0x2E, 0x7B, 0xFF), 0), new GradientStop(Color.FromRgb(0x7B, 0x4D, 0xFF), 1) },
    }.ToImmutable();

    /// <summary>Messages this close together from the same side run together in one group.</summary>
    const long GroupSeconds = 180;
    /// <summary>After a pause this long the time shows again between the messages.</summary>
    const long TimeGapSeconds = 15 * 60;
    /// <summary>How many of the latest messages show; "Show earlier messages" adds more.</summary>
    internal const int ShownAtFirst = 80;

    readonly SocialContext _social;
    readonly StackPanel _list = new(), _messages = new();
    readonly ScrollViewer _scroll = null!;
    readonly Border _header = new();
    readonly Border _conversationView;
    readonly TextBox _input = null!, _search = null!;
    readonly TextBlock _counter = null!;
    readonly Border _send = null!, _attach = null!;
    readonly Border _editBar = null!;
    readonly TextBlock _editPreview = new() { FontSize = 11.5, Foreground = HubTheme.Dim, TextTrimming = TextTrimming.CharacterEllipsis };
    Friend? _friend;
    ChatGroup? _group;
    FriendMessage? _editing;
    string? _justReacted;
    readonly Dictionary<string, (string Look, Control View)> _bubbles = new();
    int _shown = ShownAtFirst;
    HashSet<string>? _changed;

    string? CurrentKey => _group != null ? ChatStore.GroupKey(_group.Id) : _friend?.Key;

    public ChatPage(SocialContext social, Friend? with)
    {
        _social = social;
        // One card, split in two: the conversations and the one you're in.
        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("272,1,*") };

        // ── Left: search and conversations ──
        var left = new Grid { Background = ListBack, RowDefinitions = new RowDefinitions("Auto,*") };
        var searchRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(12, 12, 12, 8) };
        var searchHost = new Grid { VerticalAlignment = VerticalAlignment.Center };
        _search = HubTheme.Field("Search");
        _search.Padding = new Thickness(30, 6, 6, 6);
        _search.Background = Look.InkA(0x14);
        _search.BorderBrush = Brushes.Transparent;
        searchHost.Children.Add(_search);
        var magnifier = HubTheme.Glyph("", 12, HubTheme.Faint);
        magnifier.HorizontalAlignment = HorizontalAlignment.Left;
        magnifier.Margin = new Thickness(12, 0, 0, 0);
        magnifier.IsHitTestVisible = false;
        searchHost.Children.Add(magnifier);
        _search.TextChanged += (_, _) => ShowList();
        searchRow.Children.Add(searchHost);
        var newGroup = RoundButton(HubTheme.Glyph("\uE716", 14), "New group chat", 34, HubTheme.Card, HubTheme.Selected);
        newGroup.VerticalAlignment = VerticalAlignment.Center;
        newGroup.Margin = new Thickness(6, 0, 0, 0);
        newGroup.PointerReleased += (_, e) => { e.Handled = true; ShowCreateGroupFlyout(newGroup); };
        Grid.SetColumn(newGroup, 1);
        searchRow.Children.Add(newGroup);
        var addFriend = RoundButton(HubTheme.Glyph("", 14), "Add a friend", 34, HubTheme.Card, HubTheme.Selected);
        addFriend.VerticalAlignment = VerticalAlignment.Center;
        addFriend.Margin = new Thickness(6, 0, 0, 0);
        addFriend.PointerReleased += (_, e) => { e.Handled = true; social.Open?.Invoke("add", null); };
        Grid.SetColumn(addFriend, 2);
        searchRow.Children.Add(addFriend);
        left.Children.Add(searchRow);
        _list.Margin = new Thickness(8, 0, 2, 8);
        var listScroll = HubTheme.Scroller(_list);
        Grid.SetRow(listScroll, 1);
        left.Children.Add(listScroll);
        layout.Children.Add(left);

        var divider = new Border { Background = HubTheme.Line };
        Grid.SetColumn(divider, 1);
        layout.Children.Add(divider);

        // ── Right: header, messages, message box ──
        var conversation = new Grid { Background = ConversationBack, RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        _header.Padding = new Thickness(16, 10, 12, 10);
        _header.BorderBrush = HubTheme.Line;
        _header.BorderThickness = new Thickness(0, 0, 0, 1);
        _header.Background = ListBack;
        conversation.Children.Add(_header);
        _messages.Margin = new Thickness(14, 6, 12, 10);
        _scroll = HubTheme.Scroller(_messages);
        Grid.SetRow(_scroll, 1);
        conversation.Children.Add(_scroll);

        // The message box: attachments on the left, GIF and send on the right.
        var box = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
        _attach = RoundButton(HubTheme.Glyph("", 14, HubTheme.Dim), "Send files", 30, Brushes.Transparent, HubTheme.Hover);
        _attach.Margin = new Thickness(5, 0, 0, 0);
        _attach.VerticalAlignment = VerticalAlignment.Center;
        _attach.PointerReleased += async (_, e) =>
        {
            e.Handled = true;
            if (_friend is not { } to || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Choose files to send", AllowMultiple = true });
            var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToArray();
            if (paths.Length > 0) _social.SendFiles(to, paths);
            _input.Focus();
        };
        box.Children.Add(_attach);
        var gif = Chip("GIF", "Send a GIF");
        gif.Margin = new Thickness(2, 0, 2, 0);
        gif.VerticalAlignment = VerticalAlignment.Center;
        gif.PointerReleased += (_, e) =>
        {
            e.Handled = true;
            if (_group is { } grp)
            {
                ChatExtras.ShowGifPicker(gif, _social, url => { _social.SendGroupGif(grp, url); _input.Focus(); });
                return;
            }
            if (_friend is not { } to) return;
            ChatExtras.ShowGifPicker(gif, _social, url => { _social.SendGif(to, url); _input.Focus(); });
        };
        Grid.SetColumn(gif, 2);
        box.Children.Add(gif);
        _input = HubTheme.Field("Message…", multiline: true);
        _input.Background = Brushes.Transparent;
        _input.BorderThickness = new Thickness(0);
        foreach (string state in new[] { "PointerOver", "Focused" }) _input.Resources["TextControlBackground" + state] = Brushes.Transparent;
        _input.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(0);
        _input.Padding = new Thickness(4, 9, 4, 9);
        _input.VerticalContentAlignment = VerticalAlignment.Center;
        _input.FontSize = 13.5;
        _input.MaxHeight = 120;
        _input.AcceptsReturn = false;
        _input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && _editing != null)
            {
                e.Handled = true;
                CancelEdit();
                return;
            }
            // Enter sends, Shift+Enter starts a new line.
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                int caret = _input.CaretIndex;
                _input.Text = (_input.Text ?? "").Insert(caret, "\n");
                _input.CaretIndex = caret + 1;
                return;
            }
            SendInput();
        };
        var inputHost = new Grid();
        inputHost.Children.Add(_input);
        _counter = new TextBlock { FontSize = 10.5, Foreground = HubTheme.Faint, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -14, 6, 0), IsHitTestVisible = false };
        inputHost.Children.Add(_counter);
        Grid.SetColumn(inputHost, 1);
        box.Children.Add(inputHost);
        _send = RoundButton(HubTheme.Glyph("", 13, HubTheme.OnAccent), "Send (Enter)", 32, HubTheme.Accent, HubTheme.Accent);
        _send.Margin = new Thickness(4, 4, 5, 4);
        _send.VerticalAlignment = VerticalAlignment.Bottom;
        _send.PointerReleased += (_, e) => { e.Handled = true; SendInput(); };
        Grid.SetColumn(_send, 3);
        box.Children.Add(_send);
        _input.TextChanged += (_, _) => RefreshComposer();
        var composer = new Border
        {
            Background = Composer, BorderBrush = ComposerEdge, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(21),
            MinHeight = 42, Margin = new Thickness(14, 6, 14, 14), Child = box,
        };
        var editRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        editRow.Children.Add(HubTheme.Glyph("\uE70F", 12, HubTheme.Accent));
        var editStack = new StackPanel { Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        editStack.Children.Add(new TextBlock { Text = "Editing message", FontSize = 11.5, FontWeight = FontWeight.SemiBold, Foreground = HubTheme.Accent });
        editStack.Children.Add(_editPreview);
        Grid.SetColumn(editStack, 1);
        editRow.Children.Add(editStack);
        var cancelEdit = RoundButton(HubTheme.Glyph("\uE711", 10, HubTheme.Dim), "Cancel edit (Esc)", 24, Brushes.Transparent, HubTheme.Hover);
        cancelEdit.PointerReleased += (_, e) => { e.Handled = true; CancelEdit(); };
        Grid.SetColumn(cancelEdit, 2);
        editRow.Children.Add(cancelEdit);
        _editBar = new Border
        {
            Background = HubTheme.Card, BorderBrush = HubTheme.Line, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 6),
            Margin = new Thickness(14, 4, 14, 0), IsVisible = false, Child = editRow,
        };
        var composerHost = new StackPanel();
        composerHost.Children.Add(_editBar);
        composerHost.Children.Add(composer);
        Grid.SetRow(composerHost, 2);
        conversation.Children.Add(composerHost);
        Grid.SetColumn(conversation, 2);
        layout.Children.Add(conversation);

        _conversationView = new Border { CornerRadius = new CornerRadius(14), ClipToBounds = true, BorderBrush = HubTheme.Line, BorderThickness = new Thickness(1), Child = layout };

        RefreshComposer();
        _friend = with;
        RefreshFriends();
        social.Chats.Changed += OnChanged;
        social.Changed += OnFriendsChanged;
        AttachedToVisualTree += (_, _) => _input.Focus();
    }

    /// <summary>The friend whose conversation is shown.</summary>
    public Friend? Friend => _friend;

    /// <summary>The group whose conversation is shown.</summary>
    public ChatGroup? Group => _group;

    public override void Detach()
    {
        _social.Chats.Changed -= OnChanged;
        _social.Changed -= OnFriendsChanged;
    }

    /// <summary>Marks the shown conversation read when the window comes forward.</summary>
    public void Seen()
    {
        if (CurrentKey is { } key && TopLevel.GetTopLevel(this) is Window { IsActive: true }) _social.Chats.MarkRead(key);
    }

    void OnFriendsChanged() => Dispatcher.UIThread.Post(() => { if (Parent != null) RefreshFriends(); });

    void RefreshFriends()
    {
        var friends = _social.Friends();
        var groups = _social.Groups();
        if (_group is { } currentGroup && groups.FirstOrDefault(g => g.Id == currentGroup.Id) is { } updatedGroup && Child == _conversationView)
        {
            _group = updatedGroup;
            ShowHeader();
            ShowList();
            return;
        }
        if (_friend is { } current && friends.FirstOrDefault(f => f.Key == current.Key) is { } updated && Child == _conversationView)
        {
            _friend = updated;
            ShowHeader();
            ShowList();
            return;
        }

        if (_group != null && groups.FirstOrDefault(g => g.Id == _group.Id) is { } keepGroup)
        {
            _input.Text = "";
            Child = _conversationView;
            SelectGroup(keepGroup);
            return;
        }

        var next = friends.FirstOrDefault(f => f.Key == _friend?.Key)
            ?? friends.OrderByDescending(f => _social.Chats.Unread(f.Key)).ThenByDescending(f => _social.Chats.Last(f.Key)?.Sent ?? 0).FirstOrDefault();
        if (next != null)
        {
            _input.Text = "";
            Child = _conversationView;
            Select(next);
        }
        else if (groups.Count > 0)
        {
            _input.Text = "";
            Child = _conversationView;
            SelectGroup(groups[0]);
        }
        else if (_friend != null || _group != null || Child == null)
        {
            _friend = null;
            _group = null;
            _header.Child = null;
            _messages.Children.Clear();
            _list.Children.Clear();
            _bubbles.Clear();
            _shown = ShownAtFirst;
            _input.Text = "";
            Child = AddFriendPage.EmptyWithAdd(_social, "", "No friends yet", "Send a friend your invite code, or type in theirs. Then chat here.");
        }
    }

    ContextMenu FriendMenu(Friend friend)
    {
        var remove = new MenuItem { Header = "Remove friend…" };
        remove.Click += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (TopLevel.GetTopLevel(this) is SettingsWindow window)
                window.ShowDialog($"Remove {friend.Name}?", "This removes the friend and deletes your local conversation history.",
                    ("Cancel", () => { }), ("Remove friend", () => _ = RemoveFriendAsync(friend, window)));
        }, DispatcherPriority.Background);
        return new ContextMenu { Items = { remove } };
    }

    async System.Threading.Tasks.Task RemoveFriendAsync(Friend friend, SettingsWindow window)
    {
        if (!await _social.Remove(friend) && TopLevel.GetTopLevel(this) == window)
            window.ShowDialog($"Couldn't notify {friend.Name}", "They may still have you as a friend.", ("OK", () => { }));
    }

    /// <summary>A conversation changed: the page catches up once, after input and drawing.</summary>
    void OnChanged(string key)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_changed != null) { _changed.Add(key); return; }
            _changed = new HashSet<string> { key };
            Dispatcher.UIThread.Post(CatchUp, DispatcherPriority.Background);
        });
    }

    void CatchUp()
    {
        var changed = _changed;
        _changed = null;
        if (changed == null || Parent == null) return; // the page was left meanwhile
        if (CurrentKey is { } key && changed.Contains(key))
        {
            // A reaction to an older message mustn't jump to the end; a new message at the end does.
            bool atEnd = _scroll.Offset.Y >= _scroll.Extent.Height - _scroll.Viewport.Height - 24;
            var offset = _scroll.Offset;
            ShowMessages(scrollToEnd: atEnd);
            if (!atEnd) _scroll.Offset = offset;
            if (TopLevel.GetTopLevel(this) is Window { IsActive: true }) _social.Chats.MarkRead(key);
        }
        ShowList();
    }

    public void Select(Friend friend)
    {
        if (_friend?.Key != friend.Key || _group != null)
        {
            _bubbles.Clear();
            _shown = ShownAtFirst;
        }
        _group = null;
        _friend = _social.Friends().FirstOrDefault(f => f.Key == friend.Key) ?? friend;
        _attach.IsVisible = true;
        ShowHeader();
        ShowMessages();
        _social.Chats.MarkRead(_friend.Key); // the page is open on them
        ShowList();
        _input.Focus();
    }

    public void SelectGroup(ChatGroup group)
    {
        if (_group?.Id != group.Id || _friend != null)
        {
            _bubbles.Clear();
            _shown = ShownAtFirst;
        }
        _friend = null;
        _group = _social.Groups().FirstOrDefault(g => g.Id == group.Id) ?? group;
        _attach.IsVisible = false;
        ShowHeader();
        ShowMessages();
        _social.Chats.MarkRead(ChatStore.GroupKey(_group.Id));
        ShowList();
        _input.Focus();
    }

    void BeginEdit(FriendMessage message)
    {
        if (!message.Mine || message.Gif != null) return;
        _editing = message;
        _editPreview.Text = message.Text.Replace('\n', ' ');
        _editBar.IsVisible = true;
        _input.Text = message.Text;
        _input.CaretIndex = _input.Text.Length;
        _input.Focus();
    }

    void CancelEdit()
    {
        _editing = null;
        _editBar.IsVisible = false;
        _input.Text = "";
    }

    void SendInput()
    {
        if ((_input.Text ?? "").Trim().Length == 0) return;
        if (_editing is { } editing)
        {
            string updated = _input.Text!;
            if (_group != null) _social.EditGroup(_group, editing, updated);
            else if (_friend != null) _social.Edit(_friend, editing, updated);
            CancelEdit();
            return;
        }
        if (_group != null)
        {
            _social.SendGroup(_group, _input.Text!);
            _input.Text = "";
            return;
        }
        if (_friend == null) return;
        _social.Send(_friend, _input.Text!);
        _input.Text = "";
    }

    /// <summary>The send button lights up when there's something to send; a long text says how many messages it makes.</summary>
    void RefreshComposer()
    {
        string text = _input.Text ?? "";
        bool any = text.Trim().Length > 0;
        _send.Opacity = any ? 1 : 0.35;
        int parts = any ? ChatText.Split(text).Count : 0;
        _counter.Text = parts > 1 ? $"{parts} messages" : "";
    }

    // ───────────── the conversations ─────────────

    void ShowList()
    {
        _list.Children.Clear();
        string filter = (_search.Text ?? "").Trim();
        var entries = new List<(long Sent, string Name, Control Row)>();
        foreach (var g in _social.Groups())
        {
            if (filter.Length > 0 && !g.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase)) continue;
            long sent = _social.Chats.Last(ChatStore.GroupKey(g.Id))?.Sent ?? g.Created;
            entries.Add((sent, g.Name, GroupRow(g)));
        }
        foreach (var f in _social.Friends())
        {
            if (filter.Length > 0 && !f.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase)) continue;
            long sent = _social.Chats.Last(f.Key)?.Sent ?? 0;
            entries.Add((sent, f.Name, Row(f)));
        }
        if (entries.Count == 0)
        {
            _list.Children.Add(new TextBlock { Text = $"No chat called “{filter}”", Foreground = HubTheme.Dim, FontSize = 12, Margin = new Thickness(10, 12, 10, 0), TextWrapping = TextWrapping.Wrap });
            return;
        }
        foreach (var entry in entries.OrderByDescending(e => e.Sent).ThenBy(e => e.Name))
            _list.Children.Add(entry.Row);
    }

    Control GroupRow(ChatGroup group)
    {
        string key = ChatStore.GroupKey(group.Id);
        int unread = _social.Chats.Unread(key);
        var last = _social.Chats.Last(key);
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        row.Children.Add(GroupFace(group, 44));

        var words = new Grid { Margin = new Thickness(11, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center, ColumnDefinitions = new ColumnDefinitions("*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto") };
        words.Children.Add(new TextBlock { Text = group.Name, FontSize = 13.5, FontWeight = unread > 0 ? FontWeight.Bold : FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        if (last != null)
        {
            var when = new TextBlock
            {
                Text = ListTime(DateTimeOffset.FromUnixTimeSeconds(last.Sent).LocalDateTime), FontSize = 11, Margin = new Thickness(8, 1, 0, 0),
                Foreground = unread > 0 ? HubTheme.Accent : HubTheme.Faint, FontWeight = unread > 0 ? FontWeight.SemiBold : FontWeight.Normal,
            };
            Grid.SetColumn(when, 1);
            words.Children.Add(when);
        }
        IBrush previewColor = unread > 0 ? Look.Ink : HubTheme.Dim;
        string senderPrefix = last == null ? "" : last.Mine ? "You: " : (last.SenderName is { Length: > 0 } sn ? sn + ": " : "");
        string text = last == null ? $"{group.Members.Count} members · End-to-end encrypted"
                    : last.Gif != null ? senderPrefix + "GIF"
                    : senderPrefix + last.Text.Replace('\n', ' ');
        var preview = new TextBlock
        {
            Text = text, FontSize = 12, Foreground = previewColor, TextTrimming = TextTrimming.CharacterEllipsis,
            FontWeight = unread > 0 ? FontWeight.SemiBold : FontWeight.Normal, Margin = new Thickness(0, 3, 0, 0),
        };
        Grid.SetRow(preview, 1);
        words.Children.Add(preview);
        if (unread > 0)
        {
            var badge = new Border
            {
                Background = HubTheme.Accent, CornerRadius = new CornerRadius(9), MinWidth = 18, Height = 18, Padding = new Thickness(5, 0, 5, 0),
                Margin = new Thickness(8, 3, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = unread > 99 ? "99+" : unread.ToString(CultureInfo.CurrentCulture), FontSize = 10.5, FontWeight = FontWeight.Bold, Foreground = HubTheme.OnAccent, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            Grid.SetRow(badge, 1);
            Grid.SetColumn(badge, 1);
            words.Children.Add(badge);
        }
        Grid.SetColumn(words, 1);
        row.Children.Add(words);

        bool selected = group.Id == _group?.Id;
        var item = new Border
        {
            Padding = new Thickness(8, 7, 8, 7), CornerRadius = new CornerRadius(10), Cursor = new Cursor(StandardCursorType.Hand), Child = row,
            Background = selected ? HubTheme.Selected : Brushes.Transparent, Margin = new Thickness(0, 0, 0, 1),
        };
        item.PointerEntered += (_, _) => { if (!selected) item.Background = HubTheme.Hover; };
        item.PointerExited += (_, _) => { if (!selected) item.Background = Brushes.Transparent; };
        item.ContextMenu = GroupMenu(group);
        item.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left) SelectGroup(group); };
        return item;
    }

    ContextMenu GroupMenu(ChatGroup group)
    {
        var leave = new MenuItem { Header = "Leave group…" };
        leave.Click += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (TopLevel.GetTopLevel(this) is SettingsWindow window)
                window.ShowDialog($"Leave {group.Name}?", "You will leave this group chat and delete its local history.",
                    ("Cancel", () => { }), ("Leave group", () => _ = _social.LeaveGroup(group)));
        }, DispatcherPriority.Background);
        return new ContextMenu { Items = { leave } };
    }

    void ShowCreateGroupFlyout(Control target)
    {
        var friends = _social.Friends();
        var panel = new StackPanel { Width = 300, Spacing = 8, Margin = new Thickness(6) };
        panel.Children.Add(new TextBlock { Text = "New E2E Group Chat", FontSize = 14, FontWeight = FontWeight.SemiBold });
        var nameBox = HubTheme.Field("Group name");
        panel.Children.Add(nameBox);

        var selected = new HashSet<string>(StringComparer.Ordinal);
        var memberList = new StackPanel { Spacing = 4 };
        if (friends.Count == 0)
        {
            memberList.Children.Add(new TextBlock { Text = "Add friends first to create a group.", Foreground = HubTheme.Dim, FontSize = 12 });
        }
        else
        {
            foreach (var f in friends)
            {
                var cb = new CheckBox { Content = f.Name, FontSize = 13 };
                cb.IsCheckedChanged += (_, _) => { if (cb.IsChecked == true) selected.Add(f.Key); else selected.Remove(f.Key); };
                memberList.Children.Add(cb);
            }
        }
        var scroller = HubTheme.Scroller(memberList);
        scroller.MaxHeight = 200;
        panel.Children.Add(scroller);

        var create = HubTheme.Action("Create group", primary: true);
        create.HorizontalAlignment = HorizontalAlignment.Right;
        panel.Children.Add(create);

        var flyout = new Flyout
        {
            Placement = PlacementMode.BottomEdgeAlignedLeft,
            ShowMode = FlyoutShowMode.Transient,
            Content = new Border
            {
                Background = HubTheme.Raised, BorderBrush = Look.InkA(0x30), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14), Padding = new Thickness(10), Child = panel,
            },
        };
        flyout.FlyoutPresenterClasses.Add("bare");
        create.Click += async (_, _) =>
        {
            string groupName = (nameBox.Text ?? "").Trim();
            var chosen = friends.Where(f => selected.Contains(f.Key)).ToList();
            if (groupName.Length == 0 || chosen.Count == 0) return;
            flyout.Hide();
            if (await _social.CreateGroup(groupName, chosen) is { } created)
            {
                Child = _conversationView;
                SelectGroup(created);
            }
        };
        flyout.Opened += (_, _) => nameBox.Focus();
        flyout.ShowAt(target);
    }

    void ShowGroupMembersFlyout(Control target, ChatGroup group)
    {
        var panel = new StackPanel { Width = 280, Spacing = 8, Margin = new Thickness(6) };
        panel.Children.Add(new TextBlock { Text = $"{group.Name} ({group.Members.Count})", FontSize = 14, FontWeight = FontWeight.SemiBold });
        var list = new StackPanel { Spacing = 4 };
        string? myKey = _social.MyKey();
        foreach (var m in group.Members)
            list.Children.Add(new TextBlock { Text = m.Key == myKey ? $"{m.Name} (You)" : m.Name, FontSize = 12.5, Foreground = Look.Ink });
        panel.Children.Add(list);

        var addable = _social.Friends().Where(f => group.Members.All(m => m.Key != f.Key)).ToList();
        var flyout = new Flyout
        {
            Placement = PlacementMode.BottomEdgeAlignedRight,
            ShowMode = FlyoutShowMode.Transient,
            Content = new Border
            {
                Background = HubTheme.Raised, BorderBrush = Look.InkA(0x30), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14), Padding = new Thickness(10), Child = panel,
            },
        };
        flyout.FlyoutPresenterClasses.Add("bare");
        if (addable.Count > 0 && group.Members.Count < ChatGroup.MaxMembers)
        {
            panel.Children.Add(new Border { Height = 1, Background = HubTheme.Line, Margin = new Thickness(0, 4) });
            panel.Children.Add(new TextBlock { Text = "ADD FRIEND", FontSize = 10.5, FontWeight = FontWeight.SemiBold, Foreground = HubTheme.Dim });
            foreach (var f in addable)
            {
                var addBtn = HubTheme.Action($"+ {f.Name}");
                addBtn.HorizontalAlignment = HorizontalAlignment.Stretch;
                addBtn.Click += async (_, _) => { flyout.Hide(); await _social.AddGroupMember(group, f); };
                panel.Children.Add(addBtn);
            }
        }
        panel.Children.Add(new Border { Height = 1, Background = HubTheme.Line, Margin = new Thickness(0, 4) });
        var leaveBtn = HubTheme.Action("Leave group");
        leaveBtn.Foreground = HubTheme.Red;
        leaveBtn.HorizontalAlignment = HorizontalAlignment.Right;
        leaveBtn.Click += (_, _) =>
        {
            flyout.Hide();
            if (TopLevel.GetTopLevel(this) is SettingsWindow window)
                window.ShowDialog($"Leave {group.Name}?", "You will leave this group chat and delete its local history.",
                    ("Cancel", () => { }), ("Leave group", () => _ = _social.LeaveGroup(group)));
        };
        panel.Children.Add(leaveBtn);
        flyout.ShowAt(target);
    }

    void ShowEncryptionDialog()
    {
        if (TopLevel.GetTopLevel(this) is not SettingsWindow window) return;
        if (_group is { } group)
        {
            string fp = ChatCrypto.TryDecodeGroupKey(group.Key, out var rawKey)
                ? ChatCrypto.GroupFingerprint(rawKey, group.Id)
                : "Unavailable";
            window.ShowDialog("End-to-end encrypted group",
                $"Messages in “{group.Name}” are encrypted with per-message HKDF-SHA256 + AES-256-GCM keys and distributed over pairwise ECDH P-256 channels. Local chat history is encrypted on disk.\n\nGroup key fingerprint (Epoch {group.KeyEpoch}):\n{fp}",
                ("OK", () => { }));
            return;
        }
        if (_friend is { } friend)
        {
            string safety = _social.MyKey() is { } myKey
                ? ChatCrypto.SafetyNumber(myKey, friend.Key)
                : "Unavailable";
            window.ShowDialog("End-to-end encrypted chat",
                $"Messages with {friend.Name} are encrypted with ECDH P-256 + AES-256-GCM in transit and encrypted locally on disk.\n\nCompare this safety number with {friend.Name} to verify your connection:\n\n{safety}",
                ("OK", () => { }));
        }
    }

    Control Row(Friend friend)
    {
        int unread = _social.Chats.Unread(friend.Key);
        var last = _social.Chats.Last(friend.Key);
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        row.Children.Add(FaceWithDot(friend, 44));

        var words = new Grid { Margin = new Thickness(11, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center, ColumnDefinitions = new ColumnDefinitions("*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto") };
        words.Children.Add(WithMigrationBadge(friend, new TextBlock { Text = friend.Name, FontSize = 13.5, FontWeight = unread > 0 ? FontWeight.Bold : FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis }));
        if (last != null)
        {
            var when = new TextBlock
            {
                Text = ListTime(DateTimeOffset.FromUnixTimeSeconds(last.Sent).LocalDateTime), FontSize = 11, Margin = new Thickness(8, 1, 0, 0),
                Foreground = unread > 0 ? HubTheme.Accent : HubTheme.Faint, FontWeight = unread > 0 ? FontWeight.SemiBold : FontWeight.Normal,
            };
            Grid.SetColumn(when, 1);
            words.Children.Add(when);
        }
        Control preview;
        IBrush previewColor = unread > 0 ? Look.Ink : HubTheme.Dim;
        if (last?.Gif is { } gif)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            line.Children.Add(GifPlayer.View(gif, 24, 16, 4, animate: false, maxSide: 48));
            line.Children.Add(new TextBlock { Text = (last.Mine ? "You: " : "") + "GIF", FontSize = 12, Margin = new Thickness(6, 0, 0, 0), Foreground = previewColor, VerticalAlignment = VerticalAlignment.Center });
            preview = line;
        }
        else
        {
            string text = FriendIdentity.IsRetired(friend.Server) ? "Unreachable · still on Holm's server"
                        : last == null ? friend.Status ?? (_social.IsOnline(friend) ? "Active now" : "Say hi 👋")
                        : (last.Mine ? "You: " : "") + last.Text.Replace('\n', ' ');
            preview = new TextBlock
            {
                Text = text, FontSize = 12, Foreground = previewColor, TextTrimming = TextTrimming.CharacterEllipsis,
                FontWeight = unread > 0 ? FontWeight.SemiBold : FontWeight.Normal,
            };
        }
        preview.Margin = new Thickness(0, 3, 0, 0);
        Grid.SetRow(preview, 1);
        words.Children.Add(preview);
        if (unread > 0)
        {
            var badge = new Border
            {
                Background = HubTheme.Accent, CornerRadius = new CornerRadius(9), MinWidth = 18, Height = 18, Padding = new Thickness(5, 0, 5, 0),
                Margin = new Thickness(8, 3, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = unread > 99 ? "99+" : unread.ToString(CultureInfo.CurrentCulture), FontSize = 10.5, FontWeight = FontWeight.Bold, Foreground = HubTheme.OnAccent, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            Grid.SetRow(badge, 1);
            Grid.SetColumn(badge, 1);
            words.Children.Add(badge);
        }
        Grid.SetColumn(words, 1);
        row.Children.Add(words);

        bool selected = friend.Key == _friend?.Key;
        var item = new Border
        {
            Padding = new Thickness(8, 7, 8, 7), CornerRadius = new CornerRadius(10), Cursor = new Cursor(StandardCursorType.Hand), Child = row,
            Background = selected ? HubTheme.Selected : Brushes.Transparent, Margin = new Thickness(0, 0, 0, 1),
        };
        item.PointerEntered += (_, _) => { if (!selected) item.Background = HubTheme.Hover; };
        item.PointerExited += (_, _) => { if (!selected) item.Background = Brushes.Transparent; };
        item.ContextMenu = FriendMenu(friend);
        item.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left) Select(friend); };
        return item;
    }

    /// <summary>
    /// The name, and a "Not Migrated" badge beside it while the friend's inbox is still on Holm's server (they haven't
    /// moved to HolyNotch yet): nothing reaches them until they add you again with a new code.
    /// </summary>
    internal static Control WithMigrationBadge(Friend friend, TextBlock name)
    {
        if (!FriendIdentity.IsRetired(friend.Server)) return name;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        name.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(name);
        var badge = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x0A)), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6, 1, 6, 1), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = "Not Migrated", FontSize = 10.5, FontWeight = FontWeight.SemiBold, Foreground = Brushes.Black },
        };
        ToolTip.SetTip(badge, "Still on Holm's server. Ask them to install HolyNotch and send you a new invite code.");
        row.Children.Add(badge);
        return row;
    }

    /// <summary>When the last message came, as short as the list has room for: 17:42, Mon, 3 Nov.</summary>
    static string ListTime(DateTime at)
        => at.Date == DateTime.Today ? at.ToString("t", CultureInfo.CurrentCulture)
         : at.Date > DateTime.Today.AddDays(-7) ? at.ToString("ddd", CultureInfo.CurrentCulture)
         : at.ToString("d MMM", CultureInfo.CurrentCulture);

    // ───────────── the one you're in ─────────────

    void ShowHeader()
    {
        if (_group is { } group)
        {
            var gRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            gRow.ContextMenu = GroupMenu(group);
            gRow.Children.Add(GroupFace(group, 38));
            var gWords = new StackPanel { Margin = new Thickness(11, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            gWords.Children.Add(new TextBlock { Text = group.Name, FontSize = 15, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            string memberNames = string.Join(", ", group.Members.Select(m => m.Key == _social.MyKey() ? "You" : m.Name));
            gWords.Children.Add(new TextBlock { Text = $"{group.Members.Count} members · {memberNames}", FontSize = 11.5, Foreground = HubTheme.Dim, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) });
            Grid.SetColumn(gWords, 1);
            gRow.Children.Add(gWords);
            var gActions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var gLock = RoundButton(HubTheme.Glyph("\uE72E", 13, HubTheme.Green), "End-to-end encrypted · Click to verify", 34, Brushes.Transparent, HubTheme.CardStrong);
            gLock.PointerReleased += (_, e) => { e.Handled = true; ShowEncryptionDialog(); };
            gActions.Children.Add(gLock);
            var membersBtn = RoundButton(HubTheme.Glyph("\uE716", 14), "Group members & settings", 34, Brushes.Transparent, HubTheme.CardStrong);
            membersBtn.Margin = new Thickness(4, 0, 0, 0);
            membersBtn.PointerReleased += (_, e) => { e.Handled = true; ShowGroupMembersFlyout(membersBtn, group); };
            gActions.Children.Add(membersBtn);
            Grid.SetColumn(gActions, 2);
            gRow.Children.Add(gActions);
            _header.Child = gRow;
            return;
        }
        if (_friend == null) return;
        var friend = _friend;
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        row.ContextMenu = FriendMenu(friend);
        row.Children.Add(FaceWithDot(friend, 38));
        var words = new StackPanel { Margin = new Thickness(11, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(WithMigrationBadge(friend, new TextBlock { Text = friend.Name, FontSize = 15, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis }));
        bool online = _social.IsOnline(friend);
        bool retired = FriendIdentity.IsRetired(friend.Server);
        string line = retired ? "Unreachable: their inbox is on Holm's server. Ask them for a new invite code."
                    : online ? "Active now" : "Offline · messages wait about 12 hours";
        if (friend.Status != null && !retired) line += " · " + friend.Status;
        words.Children.Add(new TextBlock { Text = line, FontSize = 11.5, Foreground = online ? HubTheme.Green : HubTheme.Dim, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) });
        Grid.SetColumn(words, 1);
        row.Children.Add(words);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var e2eLock = RoundButton(HubTheme.Glyph("\uE72E", 13, HubTheme.Green), "End-to-end encrypted · Click to verify safety number", 34, Brushes.Transparent, HubTheme.CardStrong);
        e2eLock.PointerReleased += (_, e) => { e.Handled = true; ShowEncryptionDialog(); };
        actions.Children.Add(e2eLock);
        var wave = RoundButton(new TextBlock { Text = "\U0001F44B", FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                               $"Wave at {friend.Name}", 34, Brushes.Transparent, HubTheme.CardStrong);
        wave.Margin = new Thickness(4, 0, 0, 0);
        wave.PointerReleased += (_, e) => { e.Handled = true; _social.Wave(friend); };
        actions.Children.Add(wave);
        if (_social.Stream is { } stream)
        {
            var share = RoundButton(HubTheme.Glyph("\uE7F4", 14), $"Share your screen with {friend.Name}", 34, Brushes.Transparent, HubTheme.CardStrong);
            share.Margin = new Thickness(4, 0, 0, 0);
            share.PointerReleased += (_, e) => { e.Handled = true; stream(friend); };
            actions.Children.Add(share);
        }
        Grid.SetColumn(actions, 2);
        row.Children.Add(actions);
        _header.Child = row;
    }

    void ShowMessages(bool scrollToEnd = true)
    {
        _messages.Children.Clear();
        if (CurrentKey is not { } chatKey) return;
        var messages = _social.Chats.With(chatKey);
        if (messages.Count == 0)
        {
            _messages.Children.Add(_group != null ? GroupWelcome(_group) : Welcome(_friend!));
            return;
        }
        int first = Math.Max(0, messages.Count - _shown);
        if (first > 0)
        {
            var earlier = Chip("Show earlier messages", $"{first} more");
            earlier.HorizontalAlignment = HorizontalAlignment.Center;
            earlier.Margin = new Thickness(0, 14, 0, 4);
            earlier.PointerReleased += (_, e) =>
            {
                e.Handled = true;
                // Keep the message you were looking at where it is while the earlier ones go in above it.
                double fromEnd = _scroll.Extent.Height - _scroll.Offset.Y;
                _shown += ShownAtFirst * 2;
                ShowMessages(scrollToEnd: false);
                _scroll.UpdateLayout();
                _scroll.Offset = new Vector(0, _scroll.Extent.Height - fromEnd);
            };
            _messages.Children.Add(earlier);
        }
        // The face and the friend's/group's name at the top, as messengers start a conversation.
        else _messages.Children.Add(_group != null ? GroupWelcome(_group, small: true) : Welcome(_friend!, small: true));
        var used = new HashSet<string>();
        for (int i = first; i < messages.Count; i++)
        {
            var message = messages[i];
            var previous = i > 0 ? messages[i - 1] : null;
            var next = i + 1 < messages.Count ? messages[i + 1] : null;
            bool newTime = previous == null || message.Sent - previous.Sent >= TimeGapSeconds
                           || DateTimeOffset.FromUnixTimeSeconds(previous.Sent).LocalDateTime.Date != DateTimeOffset.FromUnixTimeSeconds(message.Sent).LocalDateTime.Date;
            if (newTime) _messages.Children.Add(TimeLabel(DateTimeOffset.FromUnixTimeSeconds(message.Sent).LocalDateTime));
            bool joinsPrevious = !newTime && Together(previous, message);
            bool joinsNext = next != null && Together(message, next) && next.Sent - message.Sent < TimeGapSeconds;
            bool lastOfMine = message.Mine && next == null;
            string look = BubbleLook(message, joinsPrevious, joinsNext, lastOfMine);
            if (_justReacted == message.Id || !_bubbles.TryGetValue(message.Id, out var kept) || kept.Look != look)
                _bubbles[message.Id] = kept = (look, Bubble(message, joinsPrevious, joinsNext, lastOfMine));
            used.Add(message.Id);
            _messages.Children.Add(kept.View);
        }
        foreach (string gone in _bubbles.Keys.Where(id => !used.Contains(id)).ToList()) _bubbles.Remove(gone);
        _justReacted = null;
        if (scrollToEnd) Dispatcher.UIThread.Post(() => _scroll.ScrollToEnd(), DispatcherPriority.Loaded);
    }

    internal static string BubbleLook(FriendMessage message, bool joinsPrevious, bool joinsNext, bool lastOfMine)
    {
        string grpReacts = message.GroupReactions is { Count: > 0 } gr
            ? string.Join(",", gr.OrderBy(p => p.Key).Select(p => p.Key + "=" + p.Value))
            : "";
        return string.Join("\u001F", message.State, message.MyReaction, message.TheirReaction, grpReacts, message.Gif, message.Text, message.Edited, message.Sent, message.SenderName, joinsPrevious, joinsNext, lastOfMine);
    }

    static bool Together(FriendMessage? a, FriendMessage? b)
        => a != null && b != null && a.Mine == b.Mine && a.SenderKey == b.SenderKey && b.Sent - a.Sent < GroupSeconds
           && a.MyReaction == null && a.TheirReaction == null && (a.GroupReactions == null || a.GroupReactions.Count == 0); // a reaction pill needs room under its bubble

    /// <summary>The top of a conversation: their face, name and a word on how private it is (or a way to start).</summary>
    Control Welcome(Friend friend, bool small = false)
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(20, small ? 18 : 40, 20, small ? 10 : 0) };
        var face = FaceWithDot(friend, small ? 56 : 84);
        face.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(face);
        stack.Children.Add(new TextBlock { Text = friend.Name, FontSize = small ? 15 : 18, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) });
        var e2eNote = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Spacing = 5, Margin = new Thickness(0, 4, 0, 0) };
        e2eNote.Children.Add(HubTheme.Glyph("\uE72E", 11, HubTheme.Green));
        e2eNote.Children.Add(new TextBlock
        {
            Text = "End-to-end encrypted · Only you and " + friend.Name + " can read these messages.", FontSize = 11.5, Foreground = HubTheme.Faint,
            TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
        });
        stack.Children.Add(e2eNote);
        if (!small)
        {
            var hi = Chip("Say hi 👋", $"Send {friend.Name} a wave");
            hi.HorizontalAlignment = HorizontalAlignment.Center;
            hi.Margin = new Thickness(0, 16, 0, 0);
            hi.PointerReleased += (_, e) => { e.Handled = true; _social.Send(friend, "👋"); };
            stack.Children.Add(hi);
        }
        return stack;
    }

    Control GroupWelcome(ChatGroup group, bool small = false)
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(20, small ? 18 : 40, 20, small ? 10 : 0) };
        var face = GroupFace(group, small ? 56 : 84);
        face.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(face);
        stack.Children.Add(new TextBlock { Text = group.Name, FontSize = small ? 15 : 18, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) });
        var e2eNote = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Spacing = 5, Margin = new Thickness(0, 4, 0, 0) };
        e2eNote.Children.Add(HubTheme.Glyph("\uE72E", 11, HubTheme.Green));
        e2eNote.Children.Add(new TextBlock
        {
            Text = $"End-to-end encrypted group · {group.Members.Count} members", FontSize = 11.5, Foreground = HubTheme.Faint,
            TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
        });
        stack.Children.Add(e2eNote);
        return stack;
    }

    void React(FriendMessage message, string? emoji)
    {
        if (_group is { } grp)
        {
            _justReacted = emoji == null ? null : message.Id;
            _social.ReactGroup(grp, message, emoji);
            return;
        }
        if (_friend == null) return;
        _justReacted = emoji == null ? null : message.Id;
        _social.React(_friend, message, emoji);
    }

    /// <summary>The reactions under a bubble: a small pill over its lower edge with emoji. A click takes ours away.</summary>
    Control? Reactions(FriendMessage message)
    {
        List<string> emoji;
        int count;
        var who = new List<string>();
        if (_group != null && message.GroupReactions is { Count: > 0 } gr)
        {
            emoji = gr.Values.Distinct().ToList();
            count = gr.Count;
            string? myKey = _social.MyKey();
            foreach (var (k, e) in gr)
            {
                string name = k == myKey ? "You" : _group.Members.FirstOrDefault(m => m.Key == k)?.Name ?? "Member";
                who.Add(k == myKey ? $"You {e} · click to remove" : $"{name} {e}");
            }
        }
        else
        {
            emoji = new[] { message.TheirReaction, message.MyReaction }.Where(e => e != null).Distinct().Select(e => e!).ToList();
            count = (message.TheirReaction != null ? 1 : 0) + (message.MyReaction != null ? 1 : 0);
            if (message.MyReaction != null) who.Add($"You {message.MyReaction} · click to remove");
            if (message.TheirReaction != null) who.Add($"{_friend?.Name} {message.TheirReaction}");
        }
        if (emoji.Count == 0) return null;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (string e in emoji) row.Children.Add(new TextBlock { Text = e, FontSize = 13 });
        if (count > 1) row.Children.Add(new TextBlock { Text = count.ToString(CultureInfo.CurrentCulture), FontSize = 11, Foreground = HubTheme.Dim, Margin = new Thickness(3, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        var pill = new Border
        {
            Background = TheirBubble, BorderBrush = B(0xFF18181A), BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(11),
            Padding = new Thickness(5, 1, 6, 2), Child = row,
            HorizontalAlignment = message.Mine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Margin = message.Mine ? new Thickness(0, -8, 10, 0) : new Thickness(10, -8, 0, 0),
            Cursor = new Cursor(message.MyReaction != null ? StandardCursorType.Hand : StandardCursorType.Arrow),
        };
        ToolTip.SetTip(pill, string.Join("\n", who));
        pill.PointerReleased += (_, e) =>
        {
            if (message.MyReaction == null) return;
            e.Handled = true;
            React(message, null);
        };
        return pill;
    }

    /// <summary>One to three emoji and nothing else: shown big, without a bubble, as messengers do.</summary>
    internal static bool IsBigEmoji(string text)
    {
        string trimmed = text.Trim();
        if (trimmed.Length == 0 || trimmed.Length > 40) return false;
        var parts = new List<string>();
        var elements = StringInfo.GetTextElementEnumerator(trimmed);
        while (elements.MoveNext())
        {
            string element = (string)elements.Current;
            if (element.Trim().Length == 0) continue; // spaces between emoji are fine
            parts.Add(element);
        }
        return parts.Count is >= 1 and <= 3
            && parts.All(p => ChatReaction.Clean(p) != null && (char.IsSurrogate(p[0]) || p[0] >= '←'));
    }

    Control Bubble(FriendMessage message, bool joinsPrevious, bool joinsNext, bool lastOfMine)
    {
        bool gif = message.Gif != null;
        bool big = !gif && IsBigEmoji(message.Text);
        Control content = gif
            ? GifPlayer.View(message.Gif!, 220, 160, 0, _social.Animate(), maxSide: 320, fitHeight: true)
            : big ? new TextBlock { Text = message.Text.Trim(), FontSize = 40 }
            : new SelectableTextBlock { Text = message.Text, TextWrapping = TextWrapping.Wrap, FontSize = 13.5, Foreground = Look.Ink };

        // Bubbles of one group run together: the corners where they meet are small, like in every messenger.
        const double round = 18, joined = 5;
        var corners = message.Mine
            ? new CornerRadius(round, joinsPrevious ? joined : round, joinsNext ? joined : round, round)
            : new CornerRadius(joinsPrevious ? joined : round, round, round, joinsNext ? joined : round);
        var bubble = new Border
        {
            Background = gif || big ? Brushes.Transparent : message.Mine ? MyBubble : TheirBubble,
            CornerRadius = corners, Padding = gif || big ? new Thickness(0) : new Thickness(13, 8, 13, 9), MaxWidth = 380, Child = content,
            Opacity = message.State == ChatState.Sending ? 0.6 : 1, ClipToBounds = gif,
        };
        ToolTip.SetTip(bubble, DateTimeOffset.FromUnixTimeSeconds(message.Sent).LocalDateTime.ToString("g", CultureInfo.CurrentCulture));
        // A double click gives a heart (or takes it away), like a double tap on Instagram.
        bubble.DoubleTapped += (_, e) =>
        {
            e.Handled = true;
            React(message, message.MyReaction == ChatReaction.DoubleClick ? null : ChatReaction.DoubleClick);
        };
        // Copy, as from any chat, and React.
        var copy = new MenuItem { Header = gif ? "Copy GIF link" : "Copy" };
        copy.Click += async (_, _) => { if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) try { await clipboard.SetTextAsync(message.Gif ?? message.Text); } catch { } };
        var reactItem = new MenuItem { Header = "React…" };
        reactItem.Click += (_, _) => ChatExtras.ShowReactionBar(bubble, _social, message.MyReaction, emoji => React(message, emoji));
        var menu = new ContextMenu();
        menu.Items.Add(reactItem);
        if (message.Mine && !gif)
        {
            var editItem = new MenuItem { Header = "Edit" };
            editItem.Click += (_, _) => BeginEdit(message);
            menu.Items.Add(editItem);
        }
        menu.Items.Add(copy);
        bubble.ContextMenu = menu;

        // Hover actions beside the bubble: react (and edit for our own text messages).
        var hoverActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0), Opacity = 0 };
        var smiley = RoundButton(HubTheme.Glyph("", 13, HubTheme.Dim), "React", 26, HubTheme.Card, HubTheme.CardStrong);
        smiley.PointerReleased += (_, e) => { e.Handled = true; ChatExtras.ShowReactionBar(bubble, _social, message.MyReaction, emoji => React(message, emoji)); };
        hoverActions.Children.Add(smiley);
        if (message.Mine && !gif)
        {
            var editBtn = RoundButton(HubTheme.Glyph("\uE70F", 12, HubTheme.Dim), "Edit message", 26, HubTheme.Card, HubTheme.CardStrong);
            editBtn.PointerReleased += (_, e) => { e.Handled = true; BeginEdit(message); };
            hoverActions.Children.Add(editBtn);
        }

        var withReactions = new StackPanel();
        withReactions.Children.Add(bubble);
        var pill = Reactions(message);
        if (pill != null) withReactions.Children.Add(pill);

        // Dock the accessories first so the bubble wraps within the remaining conversation width.
        var line = new DockPanel
        {
            HorizontalAlignment = message.Mine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Background = Brushes.Transparent,
        };
        if (!message.Mine)
        {
            // Their face sits beside the last bubble of their group; the others keep its room.
            string faceName = _group != null ? (message.SenderName ?? "?") : (_friend?.Name ?? "?");
            Control face = joinsNext ? new Border { Width = 28 } : SmallFaceByName(faceName, 28);
            face.VerticalAlignment = VerticalAlignment.Bottom;
            face.Margin = new Thickness(0, 0, 8, pill != null ? 14 : 0);
            DockPanel.SetDock(face, Dock.Left);
            line.Children.Add(face);
        }
        DockPanel.SetDock(hoverActions, message.Mine ? Dock.Left : Dock.Right);
        line.Children.Add(hoverActions);
        line.Children.Add(withReactions);
        line.PointerEntered += (_, _) => hoverActions.Opacity = 1;
        line.PointerExited += (_, _) => hoverActions.Opacity = 0;

        var stack = new StackPanel
        {
            HorizontalAlignment = message.Mine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Margin = new Thickness(0, joinsPrevious ? 2 : 8, 0, 0),
        };
        if (_group != null && !message.Mine && !joinsPrevious && message.SenderName is { Length: > 0 } senderLabel)
            stack.Children.Add(new TextBlock { Text = senderLabel, FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = HubTheme.Dim, Margin = new Thickness(38, 0, 0, 2) });
        stack.Children.Add(line);
        if (message.State == ChatState.Failed)
        {
            var retry = new TextBlock
            {
                Text = "Not sent · Click to try again", FontSize = 11, Foreground = HubTheme.Red, Cursor = new Cursor(StandardCursorType.Hand),
                HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 3, 6, 0),
            };
            retry.PointerReleased += (_, _) =>
            {
                if (_group != null) _social.RetryGroup(_group, message);
                else if (_friend != null) _social.Retry(_friend, message);
            };
            stack.Children.Add(retry);
        }
        else if (message.State == ChatState.Sending || lastOfMine || message.Edited)
        {
            string status = message.State == ChatState.Sending ? "Sending…"
                          : message.Edited && lastOfMine ? "Edited · Sent"
                          : message.Edited ? "Edited"
                          : "Sent";
            stack.Children.Add(new TextBlock
            {
                Text = status, FontSize = 10.5, Foreground = HubTheme.Faint,
                HorizontalAlignment = message.Mine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                Margin = message.Mine ? new Thickness(0, 3, 6, 0) : new Thickness(38, 3, 0, 0),
            });
        }
        return stack;
    }

    // ───────────── small pieces ─────────────

    static Border RoundButton(Control content, string tip, double size, IBrush rest, IBrush hover)
    {
        var button = new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Cursor = new Cursor(StandardCursorType.Hand),
            Background = rest, Child = content,
        };
        ToolTip.SetTip(button, tip);
        button.PointerEntered += (_, _) => button.Background = hover;
        button.PointerExited += (_, _) => button.Background = rest;
        return button;
    }

    /// <summary>A small rounded label that works as a button ("Say hi").</summary>
    static Border Chip(string text, string tip)
    {
        var chip = new Border
        {
            Background = HubTheme.CardStrong, CornerRadius = new CornerRadius(13), Padding = new Thickness(10, 4, 10, 5), Cursor = new Cursor(StandardCursorType.Hand),
            Child = new TextBlock { Text = text, FontSize = 11.5, FontWeight = FontWeight.Bold, Foreground = Look.Ink },
        };
        ToolTip.SetTip(chip, tip);
        chip.PointerEntered += (_, _) => chip.Background = HubTheme.Selected;
        chip.PointerExited += (_, _) => chip.Background = HubTheme.CardStrong;
        return chip;
    }

    Control SmallFaceByName(string name, double size)
    {
        var (text, fill) = _social.Face(name);
        return HubTheme.Face(text, fill, size);
    }

    Control GroupFace(ChatGroup group, double size)
    {
        var (text, fill) = _social.Face(group.Name);
        if (group.Color != null && Color.TryParse(group.Color, out var c))
            fill = new SolidColorBrush(c).ToImmutable();
        var grid = new Grid { Width = size, Height = size };
        grid.Children.Add(HubTheme.Face(text, fill, size));
        grid.Children.Add(new Border
        {
            Width = size * 0.42, Height = size * 0.42, CornerRadius = new CornerRadius(size * 0.21),
            Background = HubTheme.Raised, BorderBrush = DotEdge, BorderThickness = new Thickness(1.5),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            Child = HubTheme.Glyph("\uE716", Math.Max(8, size * 0.21), Look.Ink),
        });
        return grid;
    }

    static TextBlock TimeLabel(DateTime at) => new()
    {
        Text = (at.Date == DateTime.Today ? "Today" : at.Date == DateTime.Today.AddDays(-1) ? "Yesterday" : at.ToString("ddd d MMM", CultureInfo.CurrentCulture))
               + " " + at.ToString("t", CultureInfo.CurrentCulture),
        FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = HubTheme.Faint,
        HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 6),
    };

    Control FaceWithDot(Friend friend, double size)
    {
        var (text, fill) = _social.Face(friend.Name);
        var grid = new Grid { Width = size, Height = size };
        grid.Children.Add(HubTheme.Face(text, fill, size));
        if (_social.IsOnline(friend))
            grid.Children.Add(new Border
            {
                Width = size * 0.3, Height = size * 0.3, CornerRadius = new CornerRadius(size * 0.15), Background = HubTheme.Green,
                BorderBrush = DotEdge, BorderThickness = new Thickness(Math.Max(2, size / 22)),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            });
        return grid;
    }

    /// <summary>A page with nothing in it yet: a big icon and a line of what to do.</summary>
    public static StackPanel Empty(string glyph, string title, string text)
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 380 };
        stack.Children.Add(new Border
        {
            Width = 64, Height = 64, CornerRadius = new CornerRadius(32), Background = HubTheme.Card, Child = HubTheme.Glyph(glyph, 26, HubTheme.Dim),
            Margin = new Thickness(0, 0, 0, 16),
        });
        stack.Children.Add(new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
        stack.Children.Add(new TextBlock { Text = text, Foreground = HubTheme.Dim, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 6, 0, 0) });
        return stack;
    }
}
