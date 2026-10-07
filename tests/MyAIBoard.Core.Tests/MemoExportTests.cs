using MyAIBoard.Core;

namespace MyAIBoard.Core.Tests;

public sealed class MemoExportTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 6, 0, 0, TimeSpan.Zero);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "MyAIBoardTests", Guid.NewGuid().ToString("N"));
    private readonly MemoStore _source;
    private readonly MemoStore _target;

    public MemoExportTests()
    {
        _source = new MemoStore(Path.Combine(_directory, "source.db"));
        _target = new MemoStore(Path.Combine(_directory, "target.db"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void JsonRoundTrip_KeepsMemosOrderSourceAndTimes()
    {
        _source.Add("오래된 메모", "", "User");
        var edited = _source.Add("새 메모 #ui", "본문\n둘째 줄", "Claude");
        _source.Update(edited.Id, body: "고친 본문");
        _source.SetCollapsed(edited.Id, true);

        var json = MemoExport.ToJson(_source.GetAll(), Now);
        Assert.Contains("새 메모", json); // Korean stays readable

        var (added, skipped) = _target.Import(MemoExport.FromJson(json));

        Assert.Equal((2, 0), (added, skipped));
        var original = _source.GetAll();
        var imported = _target.GetAll();
        Assert.Equal(original.Select(m => (m.Title, m.Body, m.Source, m.IsCollapsed, m.CreatedAt, m.UpdatedAt)),
            imported.Select(m => (m.Title, m.Body, m.Source, m.IsCollapsed, m.CreatedAt, m.UpdatedAt)));
    }

    [Fact]
    public void Import_AddsBelowExistingMemos_AndSkipsDuplicates()
    {
        _target.Add("원래 있던 메모", "", "User");
        _source.Add("가져올 메모", "", "Codex");
        var json = MemoExport.ToJson(_source.GetAll(), Now);

        Assert.Equal((1, 0), _target.Import(MemoExport.FromJson(json)));
        Assert.Equal((0, 1), _target.Import(MemoExport.FromJson(json)));

        Assert.Equal(["원래 있던 메모", "가져올 메모"], _target.GetAll().Select(m => m.Title));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"app":"Other","version":1,"memos":[]}""")]
    [InlineData("""{"app":"MyAIBoard","version":99,"exportedAt":"2026-10-07T00:00:00+00:00","memos":[]}""")]
    public void FromJson_RejectsOtherFiles(string json)
    {
        Assert.Throws<FormatException>(() => MemoExport.FromJson(json));
    }

    [Fact]
    public void ToMarkdown_ListsEachMemoWithSourceAndTimes()
    {
        var memo = _source.Add("할 일", "본문", "User");
        _source.Update(memo.Id, body: "고친 본문");
        _source.Add("본문 없는 메모", "", "Claude");

        var markdown = MemoExport.ToMarkdown(_source.GetAll(), Now);

        Assert.StartsWith("# AI Board\n", markdown);
        Assert.Contains("메모 2개", markdown);
        Assert.Contains("## 할 일\n\n고친 본문\n\n_나 · 작성 ", markdown);
        Assert.Contains(" · 수정 ", markdown);
        Assert.Contains("## 본문 없는 메모\n\n_Claude · 작성 ", markdown);
    }
}
