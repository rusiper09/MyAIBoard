using System.ComponentModel;
using System.Runtime.CompilerServices;
using MyAIBoard.Core;

namespace MyAIBoard.App;

/// <summary>
/// Display-ready view of a memo, plus its UI state (editing, collapsed, dragging).
/// Updated in place on refresh so its card is kept, and only genuinely new memos
/// play the entrance animation.
/// </summary>
public sealed class MemoItem : INotifyPropertyChanged
{
    /// <summary>Id of a draft that has not been saved yet.</summary>
    public const long DraftId = 0;

    public MemoItem(Memo memo, DateTimeOffset now)
    {
        Id = memo.Id;
        Update(memo, now);
    }

    private MemoItem()
    {
        Id = DraftId;
        Source = "User";
        IsEditing = true;
    }

    /// <summary>An empty memo opened in edit mode, not yet in the store.</summary>
    public static MemoItem CreateDraft() => new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public long Id { get; }
    public bool IsDraft => Id == DraftId;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>UpdatedAt of the version the open editor started from; a newer one means someone else changed the memo.</summary>
    public DateTimeOffset EditBaseUpdatedAt { get; private set; }

    public string Title { get; private set => SetField(ref field, value); } = "";
    public string Body { get; private set { if (SetField(ref field, value)) OnPropertyChanged(nameof(HasBody)); } } = "";
    public string Source { get; private set { if (SetField(ref field, value)) OnPropertyChanged(nameof(SourceLabel)); } } = "";
    public string TimeText { get; private set => SetField(ref field, value); } = "";
    /// <summary>Exact created / edited / deleted times, shown when hovering the time.</summary>
    public string TimeToolTip { get; private set => SetField(ref field, value); } = "";
    public bool HasBody => !string.IsNullOrWhiteSpace(Body);
    public string SourceLabel => Source == "User" ? "나" : Source;

    public bool IsCollapsed { get; set => SetField(ref field, value); }
    /// <summary>The memo is in the trash (shown only in the trash view).</summary>
    public bool IsDeleted { get; private set => SetField(ref field, value); }
    public bool IsEditing { get; private set => SetField(ref field, value); }
    public bool IsDragging { get; set => SetField(ref field, value); }

    /// <summary>Edit buffers, bound two-way to the editor. Kept apart from Title/Body so refreshes do not clobber typing.</summary>
    public string EditTitle { get; set => SetField(ref field, value); } = "";
    public string EditBody { get; set => SetField(ref field, value); } = "";

    public void Update(Memo memo, DateTimeOffset now)
    {
        Title = memo.Title;
        Body = memo.Body.Trim();
        Source = memo.Source;
        CreatedAt = memo.CreatedAt;
        UpdatedAt = memo.UpdatedAt;
        IsDeleted = memo.DeletedAt is not null;
        // The trash view has no collapse button, so show trashed memos in full.
        IsCollapsed = memo.IsCollapsed && !IsDeleted;
        TimeText = memo.DeletedAt is { } deletedAt
            ? $"{RelativeTime(deletedAt, now)} 삭제"
            : CreatedAndEdited(memo.CreatedAt, memo.UpdatedAt, now);
        TimeToolTip = $"작성 {Exact(memo.CreatedAt)}"
                      + (memo.UpdatedAt > memo.CreatedAt ? $"\n수정 {Exact(memo.UpdatedAt)}" : "")
                      + (memo.DeletedAt is { } deleted ? $"\n삭제 {Exact(deleted)}" : "");
    }

    public void BeginEdit()
    {
        EditTitle = Title;
        EditBody = Body;
        EditBaseUpdatedAt = UpdatedAt;
        IsEditing = true;
    }

    public void EndEdit() => IsEditing = false;

    /// <summary>"2일 전", or "2일 전 · 수정 5분 전" once the title or body was edited.</summary>
    internal static string CreatedAndEdited(DateTimeOffset created, DateTimeOffset updated, DateTimeOffset now)
    {
        var createdText = RelativeTime(created, now);
        if (updated <= created) return createdText;

        var updatedText = RelativeTime(updated, now);
        return updatedText == createdText ? $"{createdText} · 수정됨" : $"{createdText} · 수정 {updatedText}";
    }

    private static string Exact(DateTimeOffset time) => time.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    internal static string RelativeTime(DateTimeOffset time, DateTimeOffset now)
    {
        var elapsed = now - time;
        if (elapsed < TimeSpan.FromMinutes(1)) return "방금";
        if (elapsed < TimeSpan.FromHours(1)) return $"{(int)elapsed.TotalMinutes}분 전";
        if (elapsed < TimeSpan.FromDays(1)) return $"{(int)elapsed.TotalHours}시간 전";
        if (elapsed < TimeSpan.FromDays(7)) return $"{(int)elapsed.TotalDays}일 전";

        var local = time.ToLocalTime();
        return local.Year == now.ToLocalTime().Year ? local.ToString("M월 d일") : local.ToString("yyyy년 M월 d일");
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
