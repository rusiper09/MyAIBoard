using MyAIBoard.Core;

namespace MyAIBoard.Core.Tests;

public sealed class MemoTagsTests
{
    [Fact]
    public void Parse_FindsTagsAfterWhitespaceOrBrackets()
    {
        Assert.Equal(["MyAIBoard", "버그", "ui-fix", "a_b"],
            MemoTags.Parse("#MyAIBoard 할 일 #버그\n(#ui-fix) [#a_b]"));
    }

    [Fact]
    public void Parse_IgnoresNumbersCSharpAndDuplicates()
    {
        Assert.Equal(["ui"], MemoTags.Parse("메모 #13 참고, C# 코드, #ui #UI"));
    }

    [Fact]
    public void Parse_StopsAtPunctuation()
    {
        Assert.Equal(["ui"], MemoTags.Parse("이건 #ui."));
    }

    [Fact]
    public void Has_MatchesWholeTagCaseInsensitively()
    {
        var memo = new Memo(1, "제목 #UIX", "본문 #MyAIBoard", false, 0, "Claude", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        Assert.True(MemoTags.Has(memo, "myaiboard"));
        Assert.True(MemoTags.Has(memo, "#uix"));
        Assert.False(MemoTags.Has(memo, "ui"));
    }

    [Theory]
    [InlineData(@"E:\Project\MyAIBoard", "MyAIBoard")]
    [InlineData(@"E:\Project\MyAIBoard\", "MyAIBoard")]
    [InlineData(@"E:\work\my app.v2", "my-app-v2")]
    [InlineData(@"E:\work\2026", null)]
    [InlineData(@"E:\", null)]
    [InlineData("", null)]
    public void FromFolder_UsesTheFolderName(string folder, string? expected)
    {
        Assert.Equal(expected, MemoTags.FromFolder(folder));
    }

    [Fact]
    public void FromFolder_SkipsTheUserProfile()
    {
        Assert.Null(MemoTags.FromFolder(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
    }
}
