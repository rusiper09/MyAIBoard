using MyAIBoard.Core;

namespace MyAIBoard.Core.Tests;

public sealed class MemoChecklistTests
{
    private const string Body = "설명\n- [ ] 첫째\n  * [x] 둘째\n- [X] 셋째\n-[ ] 아님\n- [ ]";

    [Fact]
    public void Parse_FindsItemsWithLineIndexes()
    {
        var items = MemoChecklist.Parse(Body);

        Assert.Equal([1, 2, 3, 5], items.Select(i => i.LineIndex));
        Assert.Equal([false, true, true, false], items.Select(i => i.IsChecked));
        Assert.Equal(["첫째", "둘째", "셋째", ""], items.Select(i => i.Text));
        Assert.Equal("  ", items[1].Indent);
    }

    [Fact]
    public void Progress_CountsCheckedItems()
    {
        Assert.Equal((2, 4), MemoChecklist.Progress(Body));
        Assert.Equal((0, 0), MemoChecklist.Progress("체크리스트 없음"));
    }

    [Fact]
    public void Toggle_FlipsOnlyThatLine_AndKeepsLineEndings()
    {
        var body = "a\r\n- [ ] b\r\n- [x] c";

        Assert.Equal("a\r\n- [x] b\r\n- [x] c", MemoChecklist.Toggle(body, 1));
        Assert.Equal("a\r\n- [ ] b\r\n- [ ] c", MemoChecklist.Toggle(body, 2));
        Assert.Null(MemoChecklist.Toggle(body, 0));
        Assert.Null(MemoChecklist.Toggle(body, 9));
    }
}
