using System.ComponentModel;
using System.Globalization;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using MyAIBoard.Core;

namespace MyAIBoard.Mcp;

/// <remarks>
/// Memos go out with times in the PC's local time zone (the store keeps UTC), so an AI can
/// tell the user "내일 10:00" rather than an hour in UTC.
/// </remarks>
[McpServerToolType]
public static class BoardTools
{
    private const string RemindAtHelp =
        "Local date and time on the user's PC, ISO 8601 without an offset, e.g. \"2026-11-13T10:00\" " +
        "(an explicit offset like \"+09:00\" also works). Must be in the future.";

    [McpServerTool(Name = "add_memo", Destructive = false, Idempotent = false)]
    [Description(
        "Add a memo to the user's MyAIBoard, a memo board widget on their Windows desktop. " +
        "Use this when the user asks to save something for later, e.g. \"aiboard에 추가해\", \"보드에 메모해둬\", " +
        "\"add this to aiboard\". Write a short, scannable title and put details (context, file paths, next steps) " +
        "in the body so the user can pick the task up later without this conversation. " +
        "For work with several steps, write each step as a checklist line \"- [ ] step\"; the user ticks them off on the board. " +
        "Wrap file paths and URLs in single quotes, e.g. 'E:\\Project\\app\\Program.cs' or 'https://example.com': " +
        "only quoted ones become clickable links on the board, and quotes keep paths with spaces in one piece. " +
        "Hashtags like #bug in the title or body tag the memo; the folder this session works in is added as a tag " +
        "automatically (e.g. #MyAIBoard), so don't add that one yourself. The session's folder and git branch are " +
        "also recorded so the task can be resumed later. " +
        "If the user wants to be reminded (\"내일 10시에 알려줘\"), pass remind_at; the widget shows a Windows notification then.")]
    public static Memo AddMemo(
        MemoStore store,
        McpServer server,
        [Description("Short title shown on the board, ideally under 60 characters.")] string title,
        [Description("Details of the memo. Plain text; may be multi-line.")] string body = "",
        [Description("Optional reminder time. " + RemindAtHelp)] string? remind_at = null) =>
        AddMemo(store, title, body, server.ClientInfo?.Name, Environment.CurrentDirectory, remind_at, DateTimeOffset.Now);

    /// <summary>add_memo without the live connection: the client name, session folder and current time are passed in.</summary>
    internal static Memo AddMemo(MemoStore store, string title, string body, string? clientName, string folder,
        string? remindAt = null, DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new McpException("title must not be empty.");
        var remind = string.IsNullOrWhiteSpace(remindAt) ? (DateTimeOffset?)null : ParseRemindAt(remindAt, now ?? DateTimeOffset.Now);

        // Only folders that say something about the task (the same ones that get a tag) are worth resuming from.
        var origin = MemoTags.FromFolder(folder) is null ? null : Path.GetFullPath(folder);
        return Local(store.Add(title.Trim(), WithFolderTag(title, body, folder), SourceName(clientName),
            origin, GitBranch.Find(origin), remind));
    }

    [McpServerTool(Name = "list_memos", ReadOnly = true)]
    [Description(
        "List memos on the user's MyAIBoard in board order. By default only memos still to do; " +
        "status \"done\" lists finished ones, \"all\" both. Optionally filter by text in the title or body, " +
        "and/or by hashtag (e.g. tag \"MyAIBoard\" for memos about that project folder).")]
    public static IReadOnlyList<Memo> ListMemos(
        MemoStore store,
        [Description("Optional text to search for in titles and bodies.")] string? query = null,
        [Description("Optional hashtag, with or without '#'. Matches the whole tag, case-insensitively.")] string? tag = null,
        [Description("\"open\" (default): still to do. \"done\": marked complete. \"all\": both.")] string status = "open")
    {
        Func<Memo, bool> statusFilter = status.Trim().ToLowerInvariant() switch
        {
            "open" or "" => m => !m.IsCompleted,
            "done" or "completed" => m => m.IsCompleted,
            "all" => _ => true,
            _ => throw new McpException("status must be \"open\", \"done\" or \"all\"."),
        };

        var memos = string.IsNullOrWhiteSpace(query) ? store.GetAll() : store.Search(query.Trim());
        return memos
            .Where(statusFilter)
            .Where(m => string.IsNullOrWhiteSpace(tag) || MemoTags.Has(m, tag.Trim()))
            .Select(Local)
            .ToList();
    }

    [McpServerTool(Name = "get_memo", ReadOnly = true)]
    [Description(
        "Get a single memo from MyAIBoard by id. Use this when the user asks to continue a memo " +
        "(e.g. \"aiboard 메모 #12 이어서 해줘\"); its folder and branch say where that work happened.")]
    public static Memo GetMemo(MemoStore store, [Description("Memo id.")] long id) =>
        Local(store.Get(id) ?? throw new McpException($"Memo {id} is not on the board."));

    [McpServerTool(Name = "update_memo", Idempotent = true)]
    [Description(
        "Edit the title and/or body of a memo on MyAIBoard. Omitted fields are left unchanged. " +
        "To tick off checklist steps, rewrite the body with \"- [x]\" for the finished ones.")]
    public static Memo UpdateMemo(
        MemoStore store,
        [Description("Memo id.")] long id,
        [Description("New title, or omit to keep the current one.")] string? title = null,
        [Description("New body, or omit to keep the current one. Replaces the whole body.")] string? body = null)
    {
        if (title is not null && string.IsNullOrWhiteSpace(title))
            throw new McpException("title must not be empty.");

        return Local(store.Update(id, title?.Trim(), body) ?? throw new McpException($"Memo {id} is not on the board."));
    }

    [McpServerTool(Name = "complete_memo", Idempotent = true)]
    [Description(
        "Mark a memo on MyAIBoard as done, e.g. when the user says the task is finished (\"그거 끝났어\", " +
        "\"완료 처리해줘\"). Done memos move to the board's \"완료\" section and stop reminding. " +
        "Pass completed=false to put it back on the to-do list. Prefer this over delete_memo for finished work.")]
    public static Memo CompleteMemo(
        MemoStore store,
        [Description("Memo id.")] long id,
        [Description("true (default) marks it done; false makes it a to-do again.")] bool completed = true) =>
        store.SetCompleted(id, completed)
            ? Local(store.Get(id)!)
            : throw new McpException($"Memo {id} is not on the board.");

    [McpServerTool(Name = "set_reminder", Idempotent = true)]
    [Description(
        "Set, change or clear the reminder of a memo on MyAIBoard. At that time the widget shows a Windows " +
        "notification. Use for requests like \"그거 금요일 오후 2시에 알려줘\".")]
    public static Memo SetReminder(
        MemoStore store,
        [Description("Memo id.")] long id,
        [Description("Reminder time, or omit/null to remove the reminder. " + RemindAtHelp)] string? remind_at = null) =>
        SetReminder(store, id, remind_at, DateTimeOffset.Now);

    internal static Memo SetReminder(MemoStore store, long id, string? remindAt, DateTimeOffset now)
    {
        var remind = string.IsNullOrWhiteSpace(remindAt) ? (DateTimeOffset?)null : ParseRemindAt(remindAt, now);
        return store.SetReminder(id, remind)
            ? Local(store.Get(id)!)
            : throw new McpException($"Memo {id} is not on the board.");
    }

    [McpServerTool(Name = "delete_memo", Destructive = true, Idempotent = true)]
    [Description(
        "Move a memo on MyAIBoard to the trash, when the user wants it gone (for finished tasks use complete_memo). " +
        "It can be brought back with restore_memo or from the widget's trash; the trash empties itself after 30 days.")]
    public static string DeleteMemo(MemoStore store, [Description("Memo id.")] long id) =>
        store.Delete(id) ? $"Moved memo {id} to the trash." : throw new McpException($"Memo {id} is not on the board.");

    [McpServerTool(Name = "list_trash", ReadOnly = true)]
    [Description("List memos in MyAIBoard's trash, most recently deleted first.")]
    public static IReadOnlyList<Memo> ListTrash(MemoStore store) => store.GetDeleted().Select(Local).ToList();

    [McpServerTool(Name = "restore_memo", Idempotent = true)]
    [Description("Bring a memo back from MyAIBoard's trash to its old place on the board.")]
    public static Memo RestoreMemo(MemoStore store, [Description("Memo id.")] long id) =>
        store.Restore(id)
            ? Local(store.Get(id) ?? throw new McpException($"Memo {id} was deleted again."))
            : throw new McpException($"Memo {id} is not in the trash.");

    /// <summary>
    /// Reads remind_at: ISO 8601 (local unless it has an offset), or a phrase the widget understands
    /// ("내일 10:00", "+2h"). Errors say what time it is, so the AI can correct itself.
    /// </summary>
    internal static DateTimeOffset ParseRemindAt(string text, DateTimeOffset now)
    {
        var localNow = now.ToLocalTime();
        DateTimeOffset at;
        // Phrases first: the general parser would read "11/20" as midnight rather than 09:00.
        if (ReminderTime.TryParse(text, localNow.DateTime, out var phrase))
            at = new DateTimeOffset(phrase, TimeZoneInfo.Local.GetUtcOffset(phrase));
        else if (DateTimeOffset.TryParse(text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
            at = parsed;
        else
            throw new McpException(
                $"remind_at \"{text}\" is not a date and time. Use local time like \"{localNow.AddDays(1):yyyy-MM-dd}T10:00\".");

        if (at <= now)
            throw new McpException(
                $"remind_at {at.ToLocalTime():yyyy-MM-dd HH:mm} is not in the future; it is now {localNow:yyyy-MM-dd HH:mm} " +
                $"({TimeZoneInfo.Local.StandardName}).");
        return at;
    }

    /// <summary>The memo with its times in the PC's time zone.</summary>
    internal static Memo Local(Memo memo) => memo with
    {
        CreatedAt = memo.CreatedAt.ToLocalTime(),
        UpdatedAt = memo.UpdatedAt.ToLocalTime(),
        DeletedAt = memo.DeletedAt?.ToLocalTime(),
        CompletedAt = memo.CompletedAt?.ToLocalTime(),
        RemindAt = memo.RemindAt?.ToLocalTime(),
        RemindedAt = memo.RemindedAt?.ToLocalTime(),
    };

    /// <summary>
    /// Appends the session folder's tag to the body unless the memo already has it.
    /// A stdio MCP server runs in the folder of the AI session that started it.
    /// </summary>
    internal static string WithFolderTag(string title, string body, string folder)
    {
        if (MemoTags.FromFolder(folder) is not { } tag) return body;
        if (MemoTags.Parse(title + "\n" + body).Contains(tag, MemoTags.Comparer)) return body;

        return string.IsNullOrWhiteSpace(body) ? $"#{tag}" : $"{body.TrimEnd()}\n\n#{tag}";
    }

    /// <summary>Turns the connecting client's name into a friendly source label, e.g. "claude-code" → "Claude".</summary>
    internal static string SourceName(string? clientName)
    {
        var name = clientName ?? "";
        if (name.Contains("claude", StringComparison.OrdinalIgnoreCase)) return "Claude";
        if (name.Contains("codex", StringComparison.OrdinalIgnoreCase)) return "Codex";
        return string.IsNullOrWhiteSpace(name) ? "AI" : name;
    }
}
