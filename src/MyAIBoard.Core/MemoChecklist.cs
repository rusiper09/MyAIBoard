using System.Text.RegularExpressions;

namespace MyAIBoard.Core;

/// <summary>
/// Markdown-style checklist lines in a memo body: "- [ ] to do" and "- [x] done"
/// ("*" works as the bullet too, and items may be indented). Lines are counted from 0,
/// split on '\n' exactly as the body is stored.
/// </summary>
public static partial class MemoChecklist
{
    [GeneratedRegex(@"^(?<indent>[ \t]*)[-*] \[(?<mark>[ xX])\](?: (?<text>.*?))?\r?$")]
    private static partial Regex ItemRegex();

    public sealed record Item(int LineIndex, bool IsChecked, string Indent, string Text);

    /// <summary>The line as a checklist item, or null if it is not one.</summary>
    public static Item? ParseLine(string line, int lineIndex = 0)
    {
        var match = ItemRegex().Match(line);
        return match.Success
            ? new Item(lineIndex, match.Groups["mark"].Value != " ", match.Groups["indent"].Value, match.Groups["text"].Value)
            : null;
    }

    public static IReadOnlyList<Item> Parse(string body) =>
        body.Split('\n').Select(ParseLine).OfType<Item>().ToList();

    /// <summary>How many checklist items are checked, out of how many; (0, 0) if there are none.</summary>
    public static (int Done, int Total) Progress(string body)
    {
        var items = Parse(body);
        return (items.Count(i => i.IsChecked), items.Count);
    }

    /// <summary>The body with the item on that line checked or unchecked; null if that line is not an item.</summary>
    public static string? Toggle(string body, int lineIndex)
    {
        var lines = body.Split('\n');
        if (lineIndex < 0 || lineIndex >= lines.Length) return null;

        var match = ItemRegex().Match(lines[lineIndex]);
        if (!match.Success) return null;

        var mark = match.Groups["mark"];
        var line = lines[lineIndex];
        lines[lineIndex] = line[..mark.Index] + (mark.Value == " " ? "x" : " ") + line[(mark.Index + 1)..];
        return string.Join('\n', lines);
    }
}
