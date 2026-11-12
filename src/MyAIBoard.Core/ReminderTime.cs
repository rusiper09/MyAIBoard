using System.Globalization;
using System.Text.RegularExpressions;

namespace MyAIBoard.Core;

/// <summary>
/// Reminder times as people type and read them, in local time. Everything takes "now" as a
/// parameter so it can be tested; callers pass <see cref="DateTime.Now"/>.
/// </summary>
public static partial class ReminderTime
{
    private static readonly CultureInfo Korean = CultureInfo.GetCultureInfo("ko-KR");

    /// <summary>Hour used when only a day is given ("내일", "11/20").</summary>
    public const int DefaultHour = 9;

    // "+30m", "30분", "2시간 후", "3일 뒤"
    [GeneratedRegex(@"^\+?\s*(?<n>\d{1,4})\s*(?<unit>m|min|h|d|분|시간|일)\s*(?:후|뒤)?$", RegexOptions.IgnoreCase)]
    private static partial Regex RelativeRegex();

    // Optional day ("오늘" / "내일" / "모레" / "11/20" / "2026-11-20"), then an optional time
    // ("15:30", "오후 3시", "오전 9시 30분").
    [GeneratedRegex(
        @"^(?:(?<day>오늘|내일|모레)|(?:(?<year>\d{4})[-/.])?(?<month>\d{1,2})[-/.](?<date>\d{1,2})\.?)?\s*" +
        @"(?:(?<ampm>오전|오후|am|pm)\s*)?" +
        @"(?:(?<hour>\d{1,2})(?::(?<minute>\d{2})|\s*시(?:\s*(?<minute2>\d{1,2})\s*분)?))?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex AbsoluteRegex();

    /// <summary>
    /// Parses "15:30", "내일 9:00", "오후 3시", "11/20 14:00", "2026-11-20", "+2h", "30분 후" and the like.
    /// A time without a day means its next occurrence; a date without a year, its next occurrence.
    /// Fails for text it does not understand and for times that are not in the future.
    /// </summary>
    public static bool TryParse(string text, DateTime now, out DateTime result)
    {
        result = default;
        text = text.Trim();
        if (text.Length == 0) return false;

        if (RelativeRegex().Match(text) is { Success: true } relative)
        {
            var n = int.Parse(relative.Groups["n"].Value, CultureInfo.InvariantCulture);
            if (n == 0) return false;
            result = relative.Groups["unit"].Value.ToLowerInvariant() switch
            {
                "m" or "min" or "분" => now.AddMinutes(n),
                "h" or "시간" => now.AddHours(n),
                _ => now.AddDays(n),
            };
            result = TrimSeconds(result);
            return true;
        }

        var match = AbsoluteRegex().Match(text);
        var hasDay = match.Groups["day"].Success || match.Groups["month"].Success;
        var hasTime = match.Groups["hour"].Success;
        if (!match.Success || (!hasDay && !hasTime)) return false;
        if (match.Groups["ampm"].Success && !hasTime) return false;

        var hour = DefaultHour;
        var minute = 0;
        if (hasTime)
        {
            hour = int.Parse(match.Groups["hour"].Value, CultureInfo.InvariantCulture);
            var minuteGroup = match.Groups["minute"].Success ? match.Groups["minute"] : match.Groups["minute2"];
            minute = minuteGroup.Success ? int.Parse(minuteGroup.Value, CultureInfo.InvariantCulture) : 0;
            if (match.Groups["ampm"].Success)
            {
                if (hour is < 1 or > 12) return false;
                var pm = match.Groups["ampm"].Value is "오후" || match.Groups["ampm"].Value.Equals("pm", StringComparison.OrdinalIgnoreCase);
                hour = hour % 12 + (pm ? 12 : 0);
            }
            if (hour > 23 || minute > 59) return false;
        }
        var time = new TimeSpan(hour, minute, 0);

        if (match.Groups["day"].Success)
        {
            var offset = match.Groups["day"].Value switch { "오늘" => 0, "내일" => 1, _ => 2 };
            result = now.Date.AddDays(offset) + time;
            return result > now;
        }

        if (match.Groups["month"].Success)
        {
            var month = int.Parse(match.Groups["month"].Value, CultureInfo.InvariantCulture);
            var date = int.Parse(match.Groups["date"].Value, CultureInfo.InvariantCulture);
            var explicitYear = match.Groups["year"].Success;
            var year = explicitYear ? int.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture) : now.Year;
            if (!TryDate(year, month, date, out var day)) return false;

            result = day + time;
            if (result <= now && !explicitYear && TryDate(year + 1, month, date, out var nextYear))
                result = nextYear + time;
            return result > now;
        }

        // A time alone: today, or tomorrow if it has already passed.
        result = now.Date + time;
        if (result <= now) result = result.AddDays(1);
        return true;
    }

    /// <summary>Quick choices for the reminder menu, in order.</summary>
    public static IReadOnlyList<(string Label, DateTime At)> Presets(DateTime now)
    {
        var presets = new List<(string, DateTime)>
        {
            ("30분 후", TrimSeconds(now.AddMinutes(30))),
            ("1시간 후", TrimSeconds(now.AddHours(1))),
        };
        if (now.TimeOfDay < new TimeSpan(17, 30, 0))
            presets.Add(("오늘 오후 6시", now.Date.AddHours(18)));
        presets.Add(("내일 오전 9시", now.Date.AddDays(1).AddHours(DefaultHour)));

        var daysToMonday = ((int)DayOfWeek.Monday - (int)now.DayOfWeek + 7) % 7;
        presets.Add(("다음 주 월요일 오전 9시", now.Date.AddDays(daysToMonday == 0 ? 7 : daysToMonday).AddHours(DefaultHour)));
        return presets;
    }

    /// <summary>"오늘 18:00", "내일 09:00", "어제 09:00", "11월 20일 (목) 09:00", "2027년 1월 5일 09:00".</summary>
    public static string Describe(DateTime at, DateTime now)
    {
        var clock = at.ToString("HH:mm", CultureInfo.InvariantCulture);
        var days = (at.Date - now.Date).Days;
        return days switch
        {
            0 => $"오늘 {clock}",
            1 => $"내일 {clock}",
            2 => $"모레 {clock}",
            -1 => $"어제 {clock}",
            _ when at.Year == now.Year => $"{at.ToString("M월 d일 (ddd)", Korean)} {clock}",
            _ => $"{at.ToString("yyyy년 M월 d일", Korean)} {clock}",
        };
    }

    private static bool TryDate(int year, int month, int day, out DateTime date)
    {
        date = default;
        if (year is < 1 or > 9999 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
            return false;
        date = new DateTime(year, month, day);
        return true;
    }

    private static DateTime TrimSeconds(DateTime value) =>
        new(value.Year, value.Month, value.Day, value.Hour, value.Minute, 0, value.Kind);
}
