using MyAIBoard.Core;

namespace MyAIBoard.Core.Tests;

public sealed class ReminderTimeTests
{
    // Thursday 12 November 2026, 14:20:45
    private static readonly DateTime Now = new(2026, 11, 12, 14, 20, 45);

    [Theory]
    [InlineData("+30m", "2026-11-12 14:50")]
    [InlineData("30분 후", "2026-11-12 14:50")]
    [InlineData("2시간", "2026-11-12 16:20")]
    [InlineData("+1d", "2026-11-13 14:20")]
    [InlineData("3일 뒤", "2026-11-15 14:20")]
    [InlineData("15:30", "2026-11-12 15:30")]
    [InlineData("9:00", "2026-11-13 09:00")]        // already passed today → tomorrow
    [InlineData("오후 3시", "2026-11-12 15:00")]
    [InlineData("오전 9시 30분", "2026-11-13 09:30")]
    [InlineData("오후 12시", "2026-11-13 12:00")]
    [InlineData("오전 12시", "2026-11-13 00:00")]
    [InlineData("내일", "2026-11-13 09:00")]
    [InlineData("내일 18:00", "2026-11-13 18:00")]
    [InlineData("모레 오후 2시", "2026-11-14 14:00")]
    [InlineData("오늘 23:59", "2026-11-12 23:59")]
    [InlineData("11/20 14:00", "2026-11-20 14:00")]
    [InlineData("11.20.", "2026-11-20 09:00")]
    [InlineData("1/5", "2027-01-05 09:00")]          // date already passed this year → next year
    [InlineData("2026-12-01 08:15", "2026-12-01 08:15")]
    public void TryParse_Understands(string text, string expected)
    {
        Assert.True(ReminderTime.TryParse(text, Now, out var result));
        Assert.Equal(DateTime.Parse(expected), result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("언젠가")]
    [InlineData("15")]                  // a bare number is not a time
    [InlineData("25:00")]
    [InlineData("오후 13시")]
    [InlineData("2/30")]
    [InlineData("오늘 09:00")]          // today, but already passed
    [InlineData("2025-01-01 09:00")]    // explicit date in the past
    [InlineData("+0m")]
    public void TryParse_Rejects(string text)
    {
        Assert.False(ReminderTime.TryParse(text, Now, out _));
    }

    [Fact]
    public void Presets_AreAllInTheFuture_AndSkipThisEveningWhenLate()
    {
        var presets = ReminderTime.Presets(Now);
        Assert.All(presets, p => Assert.True(p.At > Now));
        Assert.Contains(presets, p => p.Label == "오늘 오후 6시");
        Assert.Equal(new DateTime(2026, 11, 16, 9, 0, 0), presets.Single(p => p.Label.StartsWith("다음 주")).At);

        Assert.DoesNotContain(ReminderTime.Presets(Now.Date.AddHours(19)), p => p.Label == "오늘 오후 6시");
    }

    [Theory]
    [InlineData("2026-11-12 18:00", "오늘 18:00")]
    [InlineData("2026-11-13 09:00", "내일 09:00")]
    [InlineData("2026-11-14 09:00", "모레 09:00")]
    [InlineData("2026-11-11 09:00", "어제 09:00")]
    [InlineData("2026-11-20 14:00", "11월 20일 (금) 14:00")]
    [InlineData("2027-01-05 09:00", "2027년 1월 5일 09:00")]
    public void Describe_IsShortAndRelativeToToday(string at, string expected)
    {
        Assert.Equal(expected, ReminderTime.Describe(DateTime.Parse(at), Now));
    }
}
