namespace MyAIBoard.Core;

/// <summary>A single memo on the board.</summary>
/// <param name="SortOrder">Position in manual ordering; lower values come first.</param>
/// <param name="Source">Who created the memo, e.g. "User", "Claude", "Codex".</param>
/// <param name="DeletedAt">When the memo was moved to the trash; null while it is on the board.</param>
public sealed record Memo(
    long Id,
    string Title,
    string Body,
    bool IsCollapsed,
    long SortOrder,
    string Source,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? DeletedAt = null);
