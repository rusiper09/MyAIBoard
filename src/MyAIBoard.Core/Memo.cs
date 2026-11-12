namespace MyAIBoard.Core;

/// <summary>A single memo on the board.</summary>
/// <param name="SortOrder">Position in manual ordering; lower values come first.</param>
/// <param name="Source">Who created the memo, e.g. "User", "Claude", "Codex".</param>
/// <param name="DeletedAt">When the memo was moved to the trash; null while it is on the board.</param>
/// <param name="CompletedAt">When the memo was marked done; null while it is still to do.</param>
/// <param name="RemindAt">When the widget should remind the user; null for no reminder.</param>
/// <param name="RemindedAt">When that reminder was shown; null until it fires. Cleared when the reminder changes.</param>
/// <param name="Folder">Working folder of the AI session that added the memo, for picking the task up again.</param>
/// <param name="Branch">Git branch checked out in <paramref name="Folder"/> when the memo was added.</param>
public sealed record Memo(
    long Id,
    string Title,
    string Body,
    bool IsCollapsed,
    long SortOrder,
    string Source,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? DeletedAt = null,
    DateTimeOffset? CompletedAt = null,
    DateTimeOffset? RemindAt = null,
    DateTimeOffset? RemindedAt = null,
    string? Folder = null,
    string? Branch = null)
{
    public bool IsCompleted => CompletedAt is not null;
}
