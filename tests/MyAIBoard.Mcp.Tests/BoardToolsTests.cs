using Microsoft.Data.Sqlite;
using ModelContextProtocol;
using MyAIBoard.Core;

namespace MyAIBoard.Mcp.Tests;

/// <summary>The tool methods called directly, against a throwaway database.</summary>
public sealed class BoardToolsTests : IDisposable
{
    private const string Folder = @"E:\Project\MyAIBoard";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "MyAIBoardTests", Guid.NewGuid().ToString("N"));
    private readonly MemoStore _store;

    public BoardToolsTests() => _store = new MemoStore(Path.Combine(_directory, "board.db"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    private Memo Add(string title, string body = "") => BoardTools.AddMemo(_store, title, body, "claude-code", Folder);

    // ───────────── add_memo ─────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AddMemo_RejectsEmptyTitle(string title)
    {
        Assert.Throws<McpException>(() => Add(title));
        Assert.Empty(_store.GetAll());
    }

    [Fact]
    public void AddMemo_TrimsTitle_AndRecordsSource()
    {
        var memo = Add("  할 일  ");

        Assert.Equal("할 일", memo.Title);
        Assert.Equal("Claude", memo.Source);
    }

    [Fact]
    public void AddMemo_AppendsFolderTag()
    {
        Assert.Equal("본문\n\n#MyAIBoard", Add("제목", "본문\n").Body);
        Assert.Equal("#MyAIBoard", Add("빈 본문").Body);
    }

    [Theory]
    [InlineData("제목 #myaiboard", "")]
    [InlineData("제목", "본문 #MYAIBOARD")]
    public void AddMemo_DoesNotRepeatFolderTag(string title, string body)
    {
        Assert.Equal(body, Add(title, body).Body);
    }

    [Fact]
    public void AddMemo_SkipsFolderTagForMeaninglessFolders()
    {
        Assert.Equal("본문", BoardTools.AddMemo(_store, "제목", "본문", "codex", @"E:\").Body);
    }

    [Theory]
    [InlineData("claude-code", "Claude")]
    [InlineData("Claude Desktop", "Claude")]
    [InlineData("codex-mcp-client", "Codex")]
    [InlineData("cursor", "cursor")]
    [InlineData("", "AI")]
    [InlineData(null, "AI")]
    public void SourceName_MapsClientNames(string? clientName, string expected)
    {
        Assert.Equal(expected, BoardTools.SourceName(clientName));
    }

    // ───────────── list_memos ─────────────

    [Fact]
    public void ListMemos_FiltersByQueryAndWholeTag()
    {
        Add("UI 정리 #ui");
        Add("UIX 실험 #uix");
        Add("버그 #ui", "로그인 실패");

        Assert.Equal(3, BoardTools.ListMemos(_store).Count);
        Assert.Equal(["버그 #ui", "UI 정리 #ui"], BoardTools.ListMemos(_store, tag: "#UI").Select(m => m.Title));
        Assert.Equal(["버그 #ui"], BoardTools.ListMemos(_store, query: "로그인", tag: "ui").Select(m => m.Title));
        Assert.Equal(3, BoardTools.ListMemos(_store, tag: "MyAIBoard").Count); // the folder tag
        Assert.Empty(BoardTools.ListMemos(_store, tag: "없는태그"));
    }

    // ───────────── get / update ─────────────

    [Fact]
    public void GetMemo_FailsForMissingOrTrashedMemo()
    {
        var memo = Add("a");
        BoardTools.DeleteMemo(_store, memo.Id);

        Assert.Throws<McpException>(() => BoardTools.GetMemo(_store, memo.Id));
        Assert.Throws<McpException>(() => BoardTools.GetMemo(_store, 999));
    }

    [Fact]
    public void UpdateMemo_ChangesGivenFields_AndRejectsEmptyTitle()
    {
        var memo = Add("제목", "본문");

        var updated = BoardTools.UpdateMemo(_store, memo.Id, title: "  새 제목 ");
        Assert.Equal("새 제목", updated.Title);
        Assert.Equal(memo.Body, updated.Body);

        Assert.Throws<McpException>(() => BoardTools.UpdateMemo(_store, memo.Id, title: " "));
        Assert.Throws<McpException>(() => BoardTools.UpdateMemo(_store, 999, body: "x"));
    }

    [Fact]
    public void UpdateMemo_FailsForTrashedMemo()
    {
        var memo = Add("a");
        BoardTools.DeleteMemo(_store, memo.Id);

        Assert.Throws<McpException>(() => BoardTools.UpdateMemo(_store, memo.Id, body: "x"));
    }

    // ───────────── origin, done, reminders ─────────────

    [Fact]
    public void AddMemo_RecordsSessionFolder_OnlyWhenItIsMeaningful()
    {
        Assert.Equal(Folder, Add("a").Folder);
        Assert.Null(BoardTools.AddMemo(_store, "b", "", "codex", @"E:\").Folder);
    }

    [Fact]
    public void AddMemo_ReturnsLocalTimes()
    {
        var memo = Add("a");
        Assert.Equal(TimeZoneInfo.Local.GetUtcOffset(memo.CreatedAt), memo.CreatedAt.Offset);
    }

    [Fact]
    public void CompleteMemo_MovesBetweenOpenAndDoneLists()
    {
        var a = Add("a");
        Add("b");

        Assert.True(BoardTools.CompleteMemo(_store, a.Id).IsCompleted);
        Assert.Equal(["b"], BoardTools.ListMemos(_store).Select(m => m.Title));
        Assert.Equal(["a"], BoardTools.ListMemos(_store, status: "done").Select(m => m.Title));
        Assert.Equal(2, BoardTools.ListMemos(_store, status: "all").Count);

        Assert.False(BoardTools.CompleteMemo(_store, a.Id, completed: false).IsCompleted);
        Assert.Equal(2, BoardTools.ListMemos(_store).Count);

        Assert.Throws<McpException>(() => BoardTools.CompleteMemo(_store, 999));
        Assert.Throws<McpException>(() => BoardTools.ListMemos(_store, status: "soon"));
    }

    [Fact]
    public void Reminders_AcceptIsoAndPhrases_AndRejectThePast()
    {
        var now = new DateTimeOffset(2026, 11, 12, 14, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 11, 12)));

        var memo = BoardTools.AddMemo(_store, "a", "", "claude-code", Folder, "2026-11-13T10:00", now);
        Assert.Equal(new DateTime(2026, 11, 13, 10, 0, 0), memo.RemindAt!.Value.LocalDateTime);

        Assert.Equal(new DateTime(2026, 11, 12, 16, 0, 0),
            BoardTools.SetReminder(_store, memo.Id, "+2h", now).RemindAt!.Value.LocalDateTime);
        Assert.Null(BoardTools.SetReminder(_store, memo.Id, null, now).RemindAt);

        var past = Assert.Throws<McpException>(() => BoardTools.SetReminder(_store, memo.Id, "2026-11-12T09:00", now));
        Assert.Contains("2026-11-12 14:00", past.Message); // tells the AI what time it is
        Assert.Throws<McpException>(() => BoardTools.SetReminder(_store, memo.Id, "언젠가", now));
        Assert.Throws<McpException>(() => BoardTools.SetReminder(_store, 999, "+1h", now));
    }

    // ───────────── trash ─────────────

    [Fact]
    public void DeleteAndRestore_RoundTripThroughTrash()
    {
        var memo = Add("a");

        BoardTools.DeleteMemo(_store, memo.Id);
        Assert.Empty(BoardTools.ListMemos(_store));
        Assert.Equal([memo.Id], BoardTools.ListTrash(_store).Select(m => m.Id));
        Assert.Throws<McpException>(() => BoardTools.DeleteMemo(_store, memo.Id));

        var restored = BoardTools.RestoreMemo(_store, memo.Id);
        Assert.Equal(memo.Title, restored.Title);
        Assert.Null(restored.DeletedAt);
        Assert.Empty(BoardTools.ListTrash(_store));
        Assert.Throws<McpException>(() => BoardTools.RestoreMemo(_store, memo.Id));
    }
}
