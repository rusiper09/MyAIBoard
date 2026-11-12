using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
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
    private static readonly TimeSpan ReminderCheckInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan HighlightDuration = TimeSpan.FromSeconds(2.5);

    /// <summary>The board (or the trash, while it is shown).</summary>
    private readonly ObservableCollection<MemoItem> _items = [];
    /// <summary>Done memos, in the folding section under the board; most recently completed first.</summary>
    private readonly ObservableCollection<MemoItem> _doneItems = [];
    private readonly DispatcherTimer _reminderTimer;
    private readonly DispatcherTimer _pollTimer;
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _undoTimer;
    private readonly DispatcherTimer _noticeTimer;
    private readonly DispatcherTimer _purgeTimer;
    private readonly TrayIcon? _tray;
    private DesktopPin? _desktopPin;
    private GlobalHotkey? _newMemoHotkey;
    private bool _exiting;
    private long _lastRevision = -1;
    private DateTime _lastReload;

    private bool _showingTrash;
    /// <summary>Memos deleted since the undo toast appeared; "되돌리기" restores all of them.</summary>
    private readonly List<long> _recentlyDeleted = [];
    /// <summary>What "되돌리기" does: restore the recent deletes, or reopen a memo just completed.</summary>
    private Action? _undo;
    /// <summary>The memo whose reminder the "직접 입력" popup is setting.</summary>
    private MemoItem? _reminderTarget;
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
        DoneList.ItemsSource = _doneItems;
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

        // Reminders fire within this interval of their time (or at the next start, if the widget was off).
        _reminderTimer = new DispatcherTimer { Interval = ReminderCheckInterval };
        _reminderTimer.Tick += (_, _) => CheckReminders();

        SourceInitialized += (_, _) =>
        {
            GlassBackdrop.Apply(this, (double)FindResource("WidgetCornerRadiusValue"));
            _desktopPin = new DesktopPin(this);
            ApplyMode(_settings.Mode);
            RegisterNewMemoHotkey();
        };
        Loaded += (_, _) =>
        {
            PurgeOldTrash();
            ReloadIfChanged();
            CheckReminders();
            _pollTimer.Start();
            _purgeTimer.Start();
            _reminderTimer.Start();
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
            _newMemoHotkey?.Dispose();
            _tray?.Dispose();
        };
        _tray = new TrayIcon(BoardMenu, ToggleVisibility);
        LinkText.OpenFailed += ShowNotice;
        LinkText.TagClicked += FilterByTag;
        LinkText.ChecklistToggled += ToggleChecklistItem;

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

            var memos = ApplySearch(_showingTrash ? _store.GetDeleted() : Sort(_store.GetAll()));
            var allIds = memos.Select(m => m.Id).ToHashSet();
            var now = DateTimeOffset.UtcNow;
            if (_showingTrash)
            {
                SyncItems(_items, memos, allIds, now);
                SyncItems(_doneItems, [], allIds, now);
            }
            else
            {
                SyncItems(_items, memos.Where(m => !m.IsCompleted).ToList(), allIds, now);
                SyncItems(_doneItems, memos.Where(m => m.IsCompleted)
                    .OrderByDescending(m => m.CompletedAt).ThenByDescending(m => m.Id).ToList(), allIds, now);
            }
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
    /// Makes a bound collection match the store, reusing existing items so their cards stay put.
    /// An unsaved draft, if any, stays pinned at the top. Changes made elsewhere (by an AI) win
    /// over an open editor: the edit is dropped and the user gets a notice.
    /// </summary>
    /// <param name="allIds">Every memo being shown, in either list: one that left this list but is
    /// still among them moved to the other list (e.g. was completed) rather than being deleted.</param>
    private void SyncItems(ObservableCollection<MemoItem> items, IReadOnlyList<Memo> memos, HashSet<long> allIds, DateTimeOffset now)
    {
        var ids = memos.Select(m => m.Id).ToHashSet();
        for (var i = items.Count - 1; i >= 0; i--)
        {
            if (items[i].IsDraft || ids.Contains(items[i].Id)) continue;

            if (items[i].IsEditing)
                ShowNotice(allIds.Contains(items[i].Id) ? "AI가 이 메모를 완료 상태로 바꿔서 편집을 취소했어요"
                    : "편집 중이던 메모를 AI가 삭제했어요");
            items.RemoveAt(i);
        }

        var offset = items.Count > 0 && items[0].IsDraft ? 1 : 0;
        for (var i = 0; i < memos.Count; i++)
        {
            var memo = memos[i];
            var target = i + offset;
            var existing = items.FirstOrDefault(item => !item.IsDraft && item.Id == memo.Id);
            if (existing is null)
            {
                items.Insert(target, new MemoItem(memo, now));
                continue;
            }

            if (existing.IsEditing && memo.UpdatedAt != existing.EditBaseUpdatedAt)
            {
                existing.EndEdit();
                ShowNotice("AI가 이 메모를 수정해서 편집을 취소했어요");
            }
            existing.Update(memo, now);
            var currentIndex = items.IndexOf(existing);
            if (currentIndex != target)
                items.Move(currentIndex, target);
        }
    }

    private int DraftOffset => _items.Count > 0 && _items[0].IsDraft ? 1 : 0;

    private IEnumerable<MemoItem> AllItems => _items.Concat(_doneItems);

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
        var editing = AllItems.Where(i => i.IsEditing && !i.IsDraft).Select(i => i.Id).ToHashSet();

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
        // The pill counts what is left to do; done memos are counted in their own section.
        var count = _items.Count(item => !item.IsDraft);
        CountText.Text = count.ToString();
        CountPill.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Visibility = _items.Count == 0 && _doneItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var hasDone = !_showingTrash && _doneItems.Count > 0;
        DoneHeader.Visibility = hasDone ? Visibility.Visible : Visibility.Collapsed;
        DoneCountText.Text = _doneItems.Count.ToString();
        DoneList.Visibility = hasDone && _settings.ShowCompleted ? Visibility.Visible : Visibility.Collapsed;
        DoneChevron.Text = _settings.ShowCompleted ? "" : ""; // ChevronDown / ChevronRight
        DoneHeader.ToolTip = _settings.ShowCompleted ? "완료한 메모 접기" : "완료한 메모 보기";
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
        foreach (var item in AllItems.Where(i => !i.IsDraft && i.IsCollapsed != collapsed).ToList())
            _store.SetCollapsed(item.Id, collapsed);
    });

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { IsDraft: false, IsDeleted: false } item) return;

        Write(() =>
        {
            if (!_store.Delete(item.Id)) return;

            // Deletes in a row pile up under one "되돌리기"; anything else on the toast is replaced.
            if (_recentlyDeleted.Count == 0) _undo = null;
            _recentlyDeleted.Add(item.Id);
            ShowUndoToast(_recentlyDeleted.Count == 1
                    ? "휴지통으로 옮겼어요"
                    : $"메모 {_recentlyDeleted.Count}개를 휴지통으로 옮겼어요",
                RestoreRecentlyDeleted);
        });
    }

    private void RestoreRecentlyDeleted()
    {
        foreach (var id in _recentlyDeleted.ToList())
            _store.Restore(id);
    }

    private void ShowUndoToast(string text, Action undo)
    {
        UndoText.Text = text;
        _undo = undo;
        UndoToast.Visibility = Visibility.Visible;
        _undoTimer.Stop();
        _undoTimer.Start();
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_undo is { } undo)
            Write(undo);
        HideUndoToast();
    }

    private void HideUndoToast()
    {
        _undoTimer.Stop();
        _recentlyDeleted.Clear();
        _undo = null;
        UndoToast.Visibility = Visibility.Collapsed;
    }

    // ───────────── Done and checklists ─────────────

    private void Complete_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { IsDraft: false, IsDeleted: false } item) return;

        var completing = !item.IsCompleted;
        Write(() =>
        {
            if (!_store.SetCompleted(item.Id, completing) || !completing) return;

            // The card leaves for the (possibly folded) done section, so offer a way back.
            _recentlyDeleted.Clear();
            ShowUndoToast("완료했어요", () => _store.SetCompleted(item.Id, false));
        });
    }

    private void DoneHeader_Click(object sender, RoutedEventArgs e)
    {
        _settings.ShowCompleted = !_settings.ShowCompleted;
        _settings.Save();
        UpdateChrome();
    }

    /// <summary>A "- [ ]" box in a memo body was clicked: tick that line off (or back on) in the stored body.</summary>
    private void ToggleChecklistItem(TextBlock block, int lineIndex)
    {
        if (block.DataContext is not MemoItem { IsDraft: false, IsDeleted: false, IsEditing: false } item) return;
        if (MemoChecklist.Toggle(item.Body, lineIndex) is not { } body) return;

        Write(() =>
        {
            // Same rule as editing: if an AI changed the memo meanwhile, its version wins.
            if (_store.Update(item.Id, body: body, expectedUpdatedAt: item.UpdatedAt) is null)
                ShowNotice("AI가 먼저 수정해서 체크하지 못했어요");
        });
    }

    // ───────────── 이어서 하기 ─────────────

    /// <summary>Copies a prompt for a new AI session that picks the memo up where it was left.</summary>
    private void Resume_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { IsDraft: false } item) return;

        try
        {
            Clipboard.SetText(item.ResumePrompt());
            ShowNotice("이어서 하기 프롬프트를 복사했어요\nAI 세션에 붙여 넣으세요");
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Another program is holding the clipboard.
            ShowNotice("클립보드를 쓸 수 없어요. 잠시 후 다시 눌러 주세요");
        }
    }

    // ───────────── Reminders ─────────────

    private void Reminder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || ItemOf(sender) is not { IsDraft: false, IsDeleted: false } item) return;

        var now = DateTime.Now;
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        menu.Items.Add(new MenuItem { Header = "알림", IsEnabled = false });
        foreach (var (label, at) in ReminderTime.Presets(now))
        {
            var preset = new MenuItem { Header = label, InputGestureText = ReminderTime.Describe(at, now) };
            preset.Click += (_, _) => SetReminder(item, at);
            menu.Items.Add(preset);
        }

        var custom = new MenuItem { Header = "직접 입력…" };
        custom.Click += (_, _) =>
        {
            _reminderTarget = item;
            ReminderInput.Text = "";
            ReminderPopup.PlacementTarget = button;
            ReminderPopup.IsOpen = true;
        };
        menu.Items.Add(custom);

        if (item.RemindAt is not null)
        {
            menu.Items.Add(new Separator());
            var clear = new MenuItem { Header = "알림 해제", InputGestureText = item.HasReminder ? item.ReminderText : "" };
            clear.Click += (_, _) => SetReminder(item, null);
            menu.Items.Add(clear);
        }
        menu.IsOpen = true;
    }

    private void SetReminder(MemoItem item, DateTime? at)
    {
        Write(() =>
        {
            if (!_store.SetReminder(item.Id, at is { } local ? new DateTimeOffset(local) : null)) return;
            ShowNotice(at is { } time ? $"{ReminderTime.Describe(time, DateTime.Now)}에 알려드릴게요" : "알림을 해제했어요");
        });
    }

    private void ReminderPopup_Opened(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => ReminderInput.Focus());

    private void ReminderInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        var text = ReminderInput.Text.Trim();
        if (text.Length == 0)
        {
            ReminderPreview.Text = "15:30 · 오후 3시 · 내일 9:00 · 11/20 14:00 · +2h · 30분 후";
            ReminderPreview.Foreground = (Brush)FindResource("TextSecondary");
        }
        else if (ReminderTime.TryParse(text, DateTime.Now, out var at))
        {
            ReminderPreview.Text = $"→ {ReminderTime.Describe(at, DateTime.Now)} · Enter로 설정";
            ReminderPreview.Foreground = (Brush)FindResource("ReminderUpcoming");
        }
        else
        {
            ReminderPreview.Text = "알 수 없는 시각이에요 (지난 시각도 안 돼요)";
            ReminderPreview.Foreground = (Brush)FindResource("ReminderOverdue");
        }
    }

    private void ReminderInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            ReminderPopup.IsOpen = false;
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if (_reminderTarget is not { } item || !ReminderTime.TryParse(ReminderInput.Text, DateTime.Now, out var at)) return;
            ReminderPopup.IsOpen = false;
            SetReminder(item, at);
        }
    }

    /// <summary>Shows a Windows notification for every reminder that is due, once each.</summary>
    private void CheckReminders()
    {
        IReadOnlyList<Memo> due;
        try
        {
            due = _store.GetDueReminders(DateTimeOffset.UtcNow);
            if (due.Count == 0) return;
            foreach (var memo in due)
                _store.MarkReminded(memo.Id, DateTimeOffset.UtcNow);
        }
        catch (SqliteException)
        {
            return; // busy; the next check tries again
        }

        var title = due.Count == 1 ? $"⏰ {due[0].Title}" : $"⏰ 알림 {due.Count}개";
        var text = due.Count == 1
            ? FirstLine(due[0].Body) ?? "AI Board 메모 알림"
            : string.Join("\n", due.Take(4).Select(m => $"• {m.Title}")) + (due.Count > 4 ? $"\n외 {due.Count - 4}개" : "");
        var first = due[0].Id;
        _tray?.ShowNotification(title, text, () =>
        {
            ShowWidget();
            RevealMemo(first);
        });
        ReloadIfChanged(force: true);
    }

    /// <summary>First line of a body that is not just hashtags, for notification text.</summary>
    private static string? FirstLine(string body) =>
        body.Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(word => !word.StartsWith('#')));

    /// <summary>Scrolls to a memo (out of the trash, search or folded done section if needed) and outlines it briefly.</summary>
    private void RevealMemo(long id)
    {
        ShowTrash(false);
        if (IsSearching) CloseSearch();
        ReloadIfChanged(force: true);

        var item = AllItems.FirstOrDefault(i => i.Id == id && !i.IsDraft);
        if (item is null) return;
        if (_doneItems.Contains(item) && !_settings.ShowCompleted)
        {
            _settings.ShowCompleted = true;
            _settings.Save();
            UpdateChrome();
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            var list = _doneItems.Contains(item) ? DoneList : MemoList;
            (list.ItemContainerGenerator.ContainerFromItem(item) as FrameworkElement)?.BringIntoView();
            item.IsHighlighted = true;
            var timer = new DispatcherTimer { Interval = HighlightDuration };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                item.IsHighlighted = false;
            };
            timer.Start();
        });
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
        _doneItems.Clear();
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
                         && ItemOf(sender) is { IsEditing: false, IsDraft: false, IsCompleted: false } item
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

    /// <summary>Shows the widget and brings it forward; a desktop-pinned widget sinks back once it loses focus.</summary>
    public void ShowWidget()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        _desktopPin?.RaiseUntilDeactivated();
        Activate();
    }

    // ───────────── Export and import ─────────────

    // The dialogs get no owner: a desktop-pinned widget sits under other windows, and an owned
    // dialog would sink with it.

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "메모 내보내기",
            FileName = $"AIBoard-{DateTime.Now:yyyy-MM-dd}",
            Filter = "JSON - 다시 가져올 수 있음 (*.json)|*.json|Markdown - 읽기용 (*.md)|*.md",
            AddExtension = true,
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var memos = _store.GetAll();
            var now = DateTimeOffset.Now;
            var markdown = dialog.FileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
            File.WriteAllText(dialog.FileName, markdown ? MemoExport.ToMarkdown(memos, now) : MemoExport.ToJson(memos, now));
            ShowNotice($"메모 {memos.Count}개를 내보냈어요");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            ShowNotice("내보내지 못했어요");
        }
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "메모 가져오기",
            Filter = "MyAIBoard 내보내기 파일 (*.json)|*.json",
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var (added, skipped) = _store.Import(MemoExport.FromJson(File.ReadAllText(dialog.FileName)));
            ShowTrash(false);
            ReloadIfChanged(force: true);
            ShowNotice(skipped == 0 ? $"메모 {added}개를 가져왔어요"
                : $"메모 {added}개를 가져왔어요 (이미 있는 {skipped}개는 건너뜀)");
        }
        catch (FormatException)
        {
            ShowNotice("MyAIBoard에서 내보낸 JSON 파일이 아니에요");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            ShowNotice("가져오지 못했어요");
        }
    }

    // ───────────── Global shortcut ─────────────

    /// <summary>The system-wide shortcut from settings (Ctrl+Alt+N by default) opens a new memo from anywhere.</summary>
    private void RegisterNewMemoHotkey()
    {
        if (string.IsNullOrWhiteSpace(_settings.NewMemoHotkey)) return; // turned off in settings.json

        _newMemoHotkey = GlobalHotkey.TryRegister(this, _settings.NewMemoHotkey, NewMemoFromHotkey, out var error);
        // The startup notice is easy to miss, so a failed shortcut also shows in the menu and tooltip.
        NewMemoMenuItem.InputGestureText = _newMemoHotkey?.Gesture ?? $"{_settings.NewMemoHotkey} 사용 불가";
        NewMemoButton.ToolTip = _newMemoHotkey is not null ? $"새 메모 ({_newMemoHotkey.Gesture})"
            : $"새 메모 ({error}, settings.json의 NewMemoHotkey로 바꿀 수 있어요)";
        if (error is not null)
            ShowNotice(error);
    }

    private void NewMemoFromHotkey()
    {
        ShowWidget();
        NewMemo_Click(this, new RoutedEventArgs());
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
