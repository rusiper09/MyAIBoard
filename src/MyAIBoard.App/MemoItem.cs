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

    public bool IsCompleted { get; private set => SetField(ref field, value); }
    public DateTimeOffset? RemindAt { get; private set; }
    /// <summary>"내일 10:00"; empty when there is no reminder (or the memo is done).</summary>
    public string ReminderText { get; private set { if (SetField(ref field, value)) OnPropertyChanged(nameof(HasReminder)); } } = "";
    public bool HasReminder => ReminderText.Length > 0;
    /// <summary>The reminder time has passed and the memo is still to do.</summary>
    public bool IsOverdue { get; private set => SetField(ref field, value); }
    /// <summary>"2/5" for a body with checklist lines; empty otherwise.</summary>
    public string ChecklistText { get; private set { if (SetField(ref field, value)) OnPropertyChanged(nameof(HasChecklist)); } } = "";
    public bool HasChecklist => ChecklistText.Length > 0;
    /// <summary>Folder and branch of the AI session that added the memo, for "이어서 하기".</summary>
    public string? Folder { get; private set; }
    public string? Branch { get; private set; }
    public string ResumeToolTip { get; private set => SetField(ref field, value); } = "";
    /// <summary>Briefly true to outline the card, e.g. after jumping to it from a notification.</summary>
    public bool IsHighlighted { get; set => SetField(ref field, value); }

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
                      + (memo.CompletedAt is { } completed ? $"\n완료 {Exact(completed)}" : "")
                      + (memo.DeletedAt is { } deleted ? $"\n삭제 {Exact(deleted)}" : "");

        IsCompleted = memo.IsCompleted;
        RemindAt = memo.RemindAt;
        var showReminder = memo.RemindAt is not null && !memo.IsCompleted && !IsDeleted;
        ReminderText = showReminder ? ReminderTime.Describe(memo.RemindAt!.Value.LocalDateTime, now.LocalDateTime) : "";
        IsOverdue = showReminder && memo.RemindAt <= now;

        var (done, total) = MemoChecklist.Progress(Body);
        ChecklistText = total > 0 ? $"{done}/{total}" : "";

        Folder = memo.Folder;
        Branch = memo.Branch;
        ResumeToolTip = memo.Folder is null
            ? "이어서 하기: AI에게 줄 프롬프트 복사"
            : $"이어서 하기: AI에게 줄 프롬프트 복사\n{memo.Folder}{(memo.Branch is null ? "" : $" ({memo.Branch})")}";
    }

    /// <summary>
    /// Text to paste into a new AI session to pick this memo up again. It names the memo id so an AI
    /// with the board connected can open it, and carries the content for one that cannot.
    /// </summary>
    public string ResumePrompt()
    {
        var prompt = new System.Text.StringBuilder();
        prompt.Append($"aiboard 메모 #{Id} \"{Title}\" 이어서 해줘.\n");
        if (Folder is not null)
            prompt.Append($"작업 폴더: '{Folder}'{(Branch is null ? "" : $" (브랜치 {Branch})")}\n");
        prompt.Append("끝나면 aiboard에서 이 메모를 완료 처리해줘.\n");
        if (HasBody)
            prompt.Append("\n--- 메모 내용 ---\n").Append(Body).Append('\n');
        return prompt.ToString();
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
