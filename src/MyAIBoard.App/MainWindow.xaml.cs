using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using MyAIBoard.App.Interop;
using MyAIBoard.Core;

namespace MyAIBoard.App;

public partial class MainWindow : Window
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RelativeTimeRefresh = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan UndoWindow = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan NoticeDuration = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan TrashRetention = TimeSpan.FromDays(30);
    private static readonly TimeSpan TrashPurgeInterval = TimeSpan.FromHours(1);
    private static readonly StringComparer TitleComparer =
        StringComparer.Create(CultureInfo.GetCultureInfo("ko-KR"), ignoreCase: true);

    private readonly MemoStore _store;
    private readonly WidgetSettings _settings;
    private readonly ObservableCollection<MemoItem> _items = [];
    private readonly DispatcherTimer _pollTimer;
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _undoTimer;
    private readonly DispatcherTimer _noticeTimer;
    private readonly DispatcherTimer _purgeTimer;
    private readonly TrayIcon? _tray;
    private DesktopPin? _desktopPin;
    private bool _exiting;
    private long _lastRevision = -1;
    private DateTime _lastReload;

    private bool _showingTrash;
    /// <summary>Memos deleted since the undo toast appeared; "되돌리기" restores all of them.</summary>
    private readonly List<long> _recentlyDeleted = [];
    private Point _dragStart;
    private MemoItem? _dragCandidate;
    private bool _isDragging;
    private int _dropIndex;

    public MainWindow(MemoStore store, WidgetSettings settings)
    {
        _store = store;
        _settings = settings;
        InitializeComponent();
        MemoList.ItemsSource = _items;
        RestorePlacement();
        UpdateSortChecks();

        // Poll the shared revision counter so memos added by AIs show up within a second.
        _pollTimer = new DispatcherTimer { Interval = PollInterval };
        _pollTimer.Tick += (_, _) => ReloadIfChanged();

        // Save placement shortly after the user stops moving/resizing.
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SavePlacement(); };
        LocationChanged += (_, _) => RestartSaveTimer();
        SizeChanged += (_, _) => RestartSaveTimer();

        _undoTimer = new DispatcherTimer { Interval = UndoWindow };
        _undoTimer.Tick += (_, _) => HideUndoToast();

        _noticeTimer = new DispatcherTimer { Interval = NoticeDuration };
        _noticeTimer.Tick += (_, _) => { _noticeTimer.Stop(); NoticeToast.Visibility = Visibility.Collapsed; };

        // Memos older than the retention period leave the trash for good.
        _purgeTimer = new DispatcherTimer { Interval = TrashPurgeInterval };
        _purgeTimer.Tick += (_, _) => PurgeOldTrash();

        SourceInitialized += (_, _) =>
        {
            GlassBackdrop.Apply(this, (double)FindResource("WidgetCornerRadiusValue"));
            _desktopPin = new DesktopPin(this);
            ApplyMode(_settings.Mode);
        };
        Loaded += (_, _) =>
        {
            PurgeOldTrash();
            ReloadIfChanged();
            _pollTimer.Start();
            _purgeTimer.Start();
        };
        Closing += (_, e) =>
        {
            // Alt+F4 and friends only hide the widget; "종료" in the menu really exits.
            if (!_exiting)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            SavePlacement();
            _tray?.Dispose();
        };
        _tray = new TrayIcon(BoardMenu, ToggleVisibility);
        LinkText.OpenFailed += ShowNotice;
        LinkText.TagClicked += FilterByTag;

        // The menu button anchors the menu under itself; right-click elsewhere opens it at the cursor.
        BoardMenu.Closed += (_, _) =>
        {
            BoardMenu.PlacementTarget = null;
            BoardMenu.Placement = PlacementMode.MousePoint;
        };
    }

    // ───────────── Loading ─────────────

    private void ReloadIfChanged(bool force = false)
    {
        if (_isDragging) return;

        try
        {
            var revision = _store.GetRevision();
            if (!force && revision == _lastRevision && DateTime.UtcNow - _lastReload < RelativeTimeRefresh)
                return;

            var memos = _showingTrash ? _store.GetDeleted() : Sort(_store.GetAll());
            SyncItems(ApplySearch(memos), DateTimeOffset.UtcNow);
            UpdateChrome();

            _lastRevision = revision;
            _lastReload = DateTime.UtcNow;
        }
        catch (SqliteException)
        {
            // Database busy or briefly unavailable; try again on the next tick.
        }
    }

    private IReadOnlyList<Memo> Sort(IReadOnlyList<Memo> memos) => _settings.Sort switch
    {
        SortMode.Newest => memos.OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id).ToList(),
        SortMode.Oldest => memos.OrderBy(m => m.CreatedAt).ThenBy(m => m.Id).ToList(),
        SortMode.Title => memos.OrderBy(m => m.Title, TitleComparer).ThenBy(m => m.Id).ToList(),
        _ => memos, // Manual: the store already returns the user's order.
    };

    /// <summary>
    /// Makes the bound collection match the store, reusing existing items so their cards stay put.
    /// An unsaved draft, if any, stays pinned at the top. Changes made elsewhere (by an AI) win
    /// over an open editor: the edit is dropped and the user gets a notice.
    /// </summary>
    private void SyncItems(IReadOnlyList<Memo> memos, DateTimeOffset now)
    {
        var ids = memos.Select(m => m.Id).ToHashSet();
        for (var i = _items.Count - 1; i >= 0; i--)
        {
            if (_items[i].IsDraft || ids.Contains(_items[i].Id)) continue;

            if (_items[i].IsEditing)
                ShowNotice("편집 중이던 메모를 AI가 삭제했어요");
            _items.RemoveAt(i);
        }

        var offset = DraftOffset;
        for (var i = 0; i < memos.Count; i++)
        {
            var memo = memos[i];
            var target = i + offset;
            var existing = _items.FirstOrDefault(item => !item.IsDraft && item.Id == memo.Id);
            if (existing is null)
            {
                _items.Insert(target, new MemoItem(memo, now));
                continue;
            }

            if (existing.IsEditing && memo.UpdatedAt != existing.EditBaseUpdatedAt)
            {
                existing.EndEdit();
                ShowNotice("AI가 이 메모를 수정해서 편집을 취소했어요");
            }
            existing.Update(memo, now);
            var currentIndex = _items.IndexOf(existing);
            if (currentIndex != target)
                _items.Move(currentIndex, target);
        }
    }

    private int DraftOffset => _items.Count > 0 && _items[0].IsDraft ? 1 : 0;

    private bool IsSearching => SearchBox.Text.Trim().Length > 0;

    /// <summary>
    /// Keeps the memos matching the search box: every "#tag" must be on the memo and every other word
    /// must appear in its title or body. A memo being edited stays visible so the edit is not lost.
    /// </summary>
    private IReadOnlyList<Memo> ApplySearch(IReadOnlyList<Memo> memos)
    {
        var terms = SearchBox.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0) return memos;

        var tags = terms.Where(t => t.Length > 1 && t[0] == '#').Select(t => t[1..]).ToList();
        var words = terms.Where(t => t[0] != '#').ToList();
        var editing = _items.Where(i => i.IsEditing && !i.IsDraft).Select(i => i.Id).ToHashSet();

        return memos.Where(m => editing.Contains(m.Id) || Matches(m)).ToList();

        bool Matches(Memo memo)
        {
            var memoTags = MemoTags.Of(memo);
            return tags.All(tag => memoTags.Contains(tag, MemoTags.Comparer))
                   && words.All(word => memo.Title.Contains(word, StringComparison.CurrentCultureIgnoreCase)
                                        || memo.Body.Contains(word, StringComparison.CurrentCultureIgnoreCase));
        }
    }

    private void UpdateChrome()
    {
        var count = _items.Count(item => !item.IsDraft);
        CountText.Text = count.ToString();
        CountPill.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = IsSearching ? $"\"{SearchBox.Text.Trim()}\"에 맞는 메모가 없어요."
            : _showingTrash ? $"휴지통이 비어 있어요.\n삭제한 메모는 {TrashRetention.Days}일 동안 여기 보관돼요."
            : "아직 메모가 없어요.\nAI에게 \"aiboard에 추가해\"라고 말해보세요.";

        HeaderTitle.Text = _showingTrash ? "휴지통" : "AI Board";
        BackButton.Visibility = _showingTrash ? Visibility.Visible : Visibility.Collapsed;
        NewMemoButton.Visibility = _showingTrash ? Visibility.Collapsed : Visibility.Visible;
        EmptyTrashButton.Visibility = _showingTrash && count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ───────────── Search ─────────────

    private void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        if (SearchBar.Visibility == Visibility.Visible && !IsSearching) CloseSearch();
        else OpenSearch();
    }

    private void OpenSearch(string? text = null)
    {
        SearchBar.Visibility = Visibility.Visible;
        if (text is not null)
            SearchBox.Text = text; // TextChanged reloads the list
        SearchBox.Focus();
        SearchBox.CaretIndex = SearchBox.Text.Length;
    }

    private void CloseSearch()
    {
        SearchBar.Visibility = Visibility.Collapsed;
        SearchBox.Text = "";
    }

    private void CloseSearch_Click(object sender, RoutedEventArgs e) => CloseSearch();

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        MemoScroller.ScrollToTop();
        ReloadIfChanged(force: true);
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        CloseSearch();
        e.Handled = true;
    }

    /// <summary>Clicking a #tag on a card shows only memos with that tag.</summary>
    private void FilterByTag(string tag) => OpenSearch($"#{tag}");

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            OpenSearch();
            e.Handled = true;
        }
    }

    /// <summary>Runs a store write; on failure (e.g. database locked) the board simply resyncs.</summary>
    private void Write(Action write)
    {
        try
        {
            write();
        }
        catch (SqliteException)
        {
        }
        ReloadIfChanged(force: true);
    }

    // ───────────── Adding and editing ─────────────

    private void NewMemo_Click(object sender, RoutedEventArgs e)
    {
        ShowTrash(false);
        if (DraftOffset == 0)
        {
            _items.Insert(0, MemoItem.CreateDraft());
            UpdateChrome();
        }
        MemoScroller.ScrollToTop();
        Activate();
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { IsEditing: false } item)
            item.BeginEdit();
    }

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Double-click a card to edit it (but not a link in it).
        if (e.ClickCount == 2 && FindAncestor<Hyperlink>(e.OriginalSource) is null && ItemOf(sender) is { IsEditing: false, IsDeleted: false } item)
        {
            item.BeginEdit();
            e.Handled = true;
        }
    }

    // Focus the title when an editor appears: IsVisibleChanged covers existing cards entering
    // edit mode, Loaded covers a brand-new draft card whose editor is visible from the start.
    private void EditTitleBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true) FocusEditor((TextBox)sender);
    }

    private void EditTitleBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox { IsVisible: true } box) FocusEditor(box);
    }

    private void FocusEditor(TextBox box) =>
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
        {
            if (!box.IsVisible) return;
            box.Focus();
            box.CaretIndex = box.Text.Length;
        });

    private void Editor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;

        if (e.Key == Key.Escape)
        {
            CancelEdit(item);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            SaveEdit(item);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && e.OriginalSource is TextBox { Name: "EditTitleBox" } && sender is Panel panel)
        {
            // Enter in the title jumps to the body.
            panel.Children.OfType<TextBox>().Last().Focus();
            e.Handled = true;
        }
    }

    private void SaveEdit_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) SaveEdit(item);
    }

    private void CancelEdit_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) CancelEdit(item);
    }

    private void SaveEdit(MemoItem item)
    {
        var body = item.EditBody.Trim();
        var title = item.EditTitle.Trim();
        if (title.Length == 0)
        {
            // No title: borrow the first line of the body.
            var firstLine = body.Split('\n', 2)[0].Trim();
            title = firstLine.Length > 60 ? firstLine[..60] : firstLine;
        }
        if (title.Length == 0)
        {
            CancelEdit(item);
            return;
        }

        if (item.IsDraft)
        {
            _items.Remove(item);
            Write(() => _store.Add(title, body, "User"));
        }
        else
        {
            item.EndEdit();
            Write(() =>
            {
                // Only save over the version the editor started from; if an AI changed it
                // in the meantime, its change wins.
                if (_store.Update(item.Id, title, body, item.EditBaseUpdatedAt) is null)
                    ShowNotice("AI가 먼저 수정해서 내 편집은 저장하지 않았어요");
            });
        }
    }

    private void ShowNotice(string text)
    {
        NoticeText.Text = text;
        NoticeToast.Visibility = Visibility.Visible;
        _noticeTimer.Stop();
        _noticeTimer.Start();
    }

    private void CancelEdit(MemoItem item)
    {
        if (item.IsDraft)
        {
            _items.Remove(item);
            UpdateChrome();
        }
        else
        {
            item.EndEdit();
        }
    }

    // ───────────── Collapse, delete, undo ─────────────

    private void ToggleCollapse_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { IsDraft: false } item)
            Write(() => _store.SetCollapsed(item.Id, !item.IsCollapsed));
    }

    private void CollapseAll_Click(object sender, RoutedEventArgs e) => SetAllCollapsed(true);
    private void ExpandAll_Click(object sender, RoutedEventArgs e) => SetAllCollapsed(false);

    private void SetAllCollapsed(bool collapsed) => Write(() =>
    {
        foreach (var item in _items.Where(i => !i.IsDraft && i.IsCollapsed != collapsed).ToList())
            _store.SetCollapsed(item.Id, collapsed);
    });

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { IsDraft: false, IsDeleted: false } item) return;

        Write(() =>
        {
            if (!_store.Delete(item.Id)) return;

            _recentlyDeleted.Add(item.Id);
            UndoText.Text = _recentlyDeleted.Count == 1
                ? "휴지통으로 옮겼어요"
                : $"메모 {_recentlyDeleted.Count}개를 휴지통으로 옮겼어요";
            UndoToast.Visibility = Visibility.Visible;
            _undoTimer.Stop();
            _undoTimer.Start();
        });
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        var ids = _recentlyDeleted.ToList();
        Write(() =>
        {
            foreach (var id in ids)
                _store.Restore(id);
        });
        HideUndoToast();
    }

    private void HideUndoToast()
    {
        _undoTimer.Stop();
        _recentlyDeleted.Clear();
        UndoToast.Visibility = Visibility.Collapsed;
    }

    // ───────────── Trash ─────────────

    private void TrashItem_Click(object sender, RoutedEventArgs e)
    {
        ShowWidget();
        ShowTrash(true);
    }

    private void BackToBoard_Click(object sender, RoutedEventArgs e) => ShowTrash(false);

    /// <summary>Switches between the board and the trash. Unsaved drafts and edits are dropped.</summary>
    private void ShowTrash(bool show)
    {
        if (_showingTrash == show) return;

        _showingTrash = show;
        _items.Clear();
        MemoScroller.ScrollToTop();
        ReloadIfChanged(force: true);
    }

    private void RestoreFromTrash_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { IsDeleted: true } item)
            Write(() => _store.Restore(item.Id));
    }

    private void Purge_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { IsDeleted: true } item)
            Write(() => _store.Purge(item.Id));
    }

    private void EmptyTrash_Click(object sender, RoutedEventArgs e)
    {
        int count;
        try
        {
            count = _store.GetDeleted().Count; // all of the trash, even while a search hides some
        }
        catch (SqliteException)
        {
            return;
        }
        if (count == 0) return;

        var answer = MessageBox.Show(this, $"휴지통의 메모 {count}개를 영구 삭제할까요?\n되돌릴 수 없어요.",
            "휴지통 비우기", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (answer == MessageBoxResult.OK)
            Write(() => _store.EmptyTrash());
    }

    private void PurgeOldTrash()
    {
        try
        {
            _store.PurgeDeletedBefore(DateTimeOffset.UtcNow - TrashRetention);
        }
        catch (SqliteException)
        {
            // Busy; the next hourly tick tries again.
        }
    }

    // ───────────── Drag to reorder (manual sort only) ─────────────

    private void Card_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        var startedOnControl = FindAncestor<ButtonBase>(e.OriginalSource) is not null
                               || FindAncestor<TextBoxBase>(e.OriginalSource) is not null
                               || FindAncestor<Hyperlink>(e.OriginalSource) is not null;
        _dragCandidate = _settings.Sort == SortMode.Manual && !_showingTrash && !IsSearching && !startedOnControl
                         && ItemOf(sender) is { IsEditing: false, IsDraft: false } item
            ? item
            : null;
    }

    private void Card_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragCandidate is null || e.LeftButton != MouseButtonState.Pressed) return;

        var delta = e.GetPosition(this) - _dragStart;
        if (Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance &&
            Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance)
            return;

        var item = _dragCandidate;
        _dragCandidate = null;
        _isDragging = true;
        item.IsDragging = true;
        try
        {
            DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(typeof(MemoItem), item), DragDropEffects.Move);
        }
        finally
        {
            item.IsDragging = false;
            _isDragging = false;
            DropIndicator.Visibility = Visibility.Collapsed;
        }
    }

    private void MemoScroller_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!e.Data.GetDataPresent(typeof(MemoItem)))
        {
            e.Effects = DragDropEffects.None;
            return;
        }
        e.Effects = DragDropEffects.Move;

        // Find the gap the cursor is closest to.
        var cursor = e.GetPosition(MemoList);
        _dropIndex = _items.Count;
        double lineY = 0;
        for (var i = 0; i < _items.Count; i++)
        {
            if (MemoList.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container) continue;
            var top = container.TranslatePoint(new Point(), MemoList).Y;
            if (cursor.Y < top + container.ActualHeight / 2)
            {
                _dropIndex = i;
                lineY = top - 4;
                break;
            }
            lineY = top + container.ActualHeight - 4;
        }
        _dropIndex = Math.Max(_dropIndex, DraftOffset);

        var linePosition = MemoList.TranslatePoint(new Point(0, lineY), DropLayer);
        Canvas.SetLeft(DropIndicator, 6);
        Canvas.SetTop(DropIndicator, linePosition.Y - 1);
        DropIndicator.Width = Math.Max(0, MemoList.ActualWidth - 12);
        DropIndicator.Visibility = Visibility.Visible;

        // Auto-scroll near the edges.
        var inScroller = e.GetPosition(MemoScroller).Y;
        if (inScroller < 28) MemoScroller.ScrollToVerticalOffset(MemoScroller.VerticalOffset - 8);
        else if (inScroller > MemoScroller.ActualHeight - 28) MemoScroller.ScrollToVerticalOffset(MemoScroller.VerticalOffset + 8);
    }

    private void MemoScroller_DragLeave(object sender, DragEventArgs e) =>
        DropIndicator.Visibility = Visibility.Collapsed;

    private void MemoScroller_Drop(object sender, DragEventArgs e)
    {
        DropIndicator.Visibility = Visibility.Collapsed;
        if (e.Data.GetData(typeof(MemoItem)) is not MemoItem item) return;

        var from = _items.IndexOf(item);
        var to = _dropIndex > from ? _dropIndex - 1 : _dropIndex;
        if (from < 0 || to == from) return;

        _items.Move(from, to);
        var order = _items.Where(i => !i.IsDraft).Select(i => i.Id).ToList();
        _isDragging = false;
        Write(() => _store.Reorder(order));
    }

    // ───────────── Menu: sort and window mode ─────────────

    private void MenuButton_Click(object sender, RoutedEventArgs e)
    {
        BoardMenu.PlacementTarget = MenuButton;
        BoardMenu.Placement = PlacementMode.Bottom;
        BoardMenu.IsOpen = true;
    }

    private void Sort_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: SortMode sort }) return;

        _settings.Sort = sort;
        _settings.Save();
        UpdateSortChecks();
        ReloadIfChanged(force: true);
    }

    private void UpdateSortChecks()
    {
        SortManualItem.IsChecked = _settings.Sort == SortMode.Manual;
        SortNewestItem.IsChecked = _settings.Sort == SortMode.Newest;
        SortOldestItem.IsChecked = _settings.Sort == SortMode.Oldest;
        SortTitleItem.IsChecked = _settings.Sort == SortMode.Title;
    }

    private void ApplyMode(WindowMode mode)
    {
        _settings.Mode = mode;

        if (mode == WindowMode.Desktop)
        {
            Topmost = false;
            _desktopPin?.Pin();
        }
        else
        {
            _desktopPin?.Unpin();
            Topmost = mode == WindowMode.AlwaysOnTop;
        }

        DesktopModeItem.IsChecked = mode == WindowMode.Desktop;
        TopmostModeItem.IsChecked = mode == WindowMode.AlwaysOnTop;
        NormalModeItem.IsChecked = mode == WindowMode.Normal;
        _settings.Save();
    }

    private void DesktopMode_Click(object sender, RoutedEventArgs e) => ApplyMode(WindowMode.Desktop);
    private void TopmostMode_Click(object sender, RoutedEventArgs e) => ApplyMode(WindowMode.AlwaysOnTop);
    private void NormalMode_Click(object sender, RoutedEventArgs e) => ApplyMode(WindowMode.Normal);

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        _exiting = true;
        Close();
    }

    // ───────────── Tray, visibility, autostart ─────────────

    private void BoardMenu_Opened(object sender, RoutedEventArgs e)
    {
        VisibilityItem.Header = IsVisible ? "위젯 숨기기" : "위젯 보이기";
        try
        {
            var trashCount = _store.GetDeleted().Count;
            TrashItem.Header = trashCount > 0 ? $"휴지통 ({trashCount})" : "휴지통";
        }
        catch (SqliteException)
        {
            TrashItem.Header = "휴지통";
        }
        AutostartItem.IsChecked = Autostart.IsEnabled;
    }

    private void ToggleVisibility_Click(object sender, RoutedEventArgs e) => ToggleVisibility();

    private void ToggleVisibility()
    {
        if (IsVisible) Hide();
        else ShowWidget();
    }

    /// <summary>Shows the widget and brings it forward (a desktop-pinned widget stays under other windows).</summary>
    public void ShowWidget()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    private void Autostart_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Autostart.SetEnabled(!Autostart.IsEnabled);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Registry locked down by policy; the check mark below shows the real state.
        }
        AutostartItem.IsChecked = Autostart.IsEnabled;
    }

    // ───────────── Window placement ─────────────

    private void RestorePlacement()
    {
        Width = Math.Max(MinWidth, _settings.Width);
        Height = Math.Max(MinHeight, _settings.Height);

        var bounds = new Rect(_settings.Left, _settings.Top, Width, Height);
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);

        // First run, or the saved spot is no longer on any monitor: top-right of the primary work area.
        if (double.IsNaN(bounds.X) || double.IsNaN(bounds.Y) || !screen.IntersectsWith(bounds))
        {
            var workArea = SystemParameters.WorkArea;
            bounds.X = workArea.Right - Width - 24;
            bounds.Y = workArea.Top + 24;
        }

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = bounds.X;
        Top = bounds.Y;
    }

    private void SavePlacement()
    {
        if (WindowState != WindowState.Normal) return;

        _settings.Left = Left;
        _settings.Top = Top;
        _settings.Width = Width;
        _settings.Height = Height;
        _settings.Save();
    }

    private void RestartSaveTimer()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    // ───────────── Helpers ─────────────

    private static MemoItem? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as MemoItem;

    private static T? FindAncestor<T>(object? source) where T : DependencyObject
    {
        var current = source as DependencyObject;
        while (current is not null and not T)
            current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);
        return current as T;
    }
}
