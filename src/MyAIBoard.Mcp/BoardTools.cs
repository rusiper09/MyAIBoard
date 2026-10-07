using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using MyAIBoard.Core;

namespace MyAIBoard.Mcp;

[McpServerToolType]
public static class BoardTools
{
    [McpServerTool(Name = "add_memo", Destructive = false, Idempotent = false)]
    [Description(
        "Add a memo to the user's MyAIBoard, a memo board widget on their Windows desktop. " +
        "Use this when the user asks to save something for later, e.g. \"aiboard에 추가해\", \"보드에 메모해둬\", " +
        "\"add this to aiboard\". Write a short, scannable title and put details (context, file paths, next steps) " +
        "in the body so the user can pick the task up later without this conversation. " +
        "Wrap file paths and URLs in single quotes, e.g. 'E:\\Project\\app\\Program.cs' or 'https://example.com': " +
        "only quoted ones become clickable links on the board, and quotes keep paths with spaces in one piece. " +
        "Hashtags like #bug in the title or body tag the memo; the folder this session works in is added as a tag " +
        "automatically (e.g. #MyAIBoard), so don't add that one yourself.")]
    public static Memo AddMemo(
        MemoStore store,
        McpServer server,
        [Description("Short title shown on the board, ideally under 60 characters.")] string title,
        [Description("Details of the memo. Plain text; may be multi-line.")] string body = "") =>
        AddMemo(store, title, body, server.ClientInfo?.Name, Environment.CurrentDirectory);

    /// <summary>add_memo without the live connection: the client name and session folder are passed in.</summary>
    internal static Memo AddMemo(MemoStore store, string title, string body, string? clientName, string folder)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new McpException("title must not be empty.");

        return store.Add(title.Trim(), WithFolderTag(title, body, folder), SourceName(clientName));
    }

    [McpServerTool(Name = "list_memos", ReadOnly = true)]
    [Description(
        "List memos on the user's MyAIBoard in board order. Optionally filter by text in the title or body, " +
        "and/or by hashtag (e.g. tag \"MyAIBoard\" for memos about that project folder).")]
    public static IReadOnlyList<Memo> ListMemos(
        MemoStore store,
        [Description("Optional text to search for in titles and bodies.")] string? query = null,
        [Description("Optional hashtag, with or without '#'. Matches the whole tag, case-insensitively.")] string? tag = null)
    {
        var memos = string.IsNullOrWhiteSpace(query) ? store.GetAll() : store.Search(query.Trim());
        return string.IsNullOrWhiteSpace(tag) ? memos : memos.Where(m => MemoTags.Has(m, tag.Trim())).ToList();
    }

    [McpServerTool(Name = "get_memo", ReadOnly = true)]
    [Description("Get a single memo from MyAIBoard by id.")]
    public static Memo GetMemo(MemoStore store, [Description("Memo id.")] long id) =>
        store.Get(id) ?? throw new McpException($"Memo {id} is not on the board.");

    [McpServerTool(Name = "update_memo", Idempotent = true)]
    [Description("Edit the title and/or body of a memo on MyAIBoard. Omitted fields are left unchanged.")]
    public static Memo UpdateMemo(
        MemoStore store,
        [Description("Memo id.")] long id,
        [Description("New title, or omit to keep the current one.")] string? title = null,
        [Description("New body, or omit to keep the current one. Replaces the whole body.")] string? body = null)
    {
        if (title is not null && string.IsNullOrWhiteSpace(title))
            throw new McpException("title must not be empty.");

        return store.Update(id, title?.Trim(), body) ?? throw new McpException($"Memo {id} is not on the board.");
    }

    [McpServerTool(Name = "delete_memo", Destructive = true, Idempotent = true)]
    [Description(
        "Move a memo on MyAIBoard to the trash, e.g. when the user says the task is done. " +
        "It can be brought back with restore_memo or from the widget's trash; the trash empties itself after 30 days.")]
    public static string DeleteMemo(MemoStore store, [Description("Memo id.")] long id) =>
        store.Delete(id) ? $"Moved memo {id} to the trash." : throw new McpException($"Memo {id} is not on the board.");

    [McpServerTool(Name = "list_trash", ReadOnly = true)]
    [Description("List memos in MyAIBoard's trash, most recently deleted first.")]
    public static IReadOnlyList<Memo> ListTrash(MemoStore store) => store.GetDeleted();

    [McpServerTool(Name = "restore_memo", Idempotent = true)]
    [Description("Bring a memo back from MyAIBoard's trash to its old place on the board.")]
    public static Memo RestoreMemo(MemoStore store, [Description("Memo id.")] long id) =>
        store.Restore(id)
            ? store.Get(id) ?? throw new McpException($"Memo {id} was deleted again.")
            : throw new McpException($"Memo {id} is not in the trash.");

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
