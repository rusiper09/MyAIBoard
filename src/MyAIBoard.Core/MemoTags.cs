using System.Text;
using System.Text.RegularExpressions;

namespace MyAIBoard.Core;

/// <summary>
/// Hashtags written in a memo's title or body, e.g. "#MyAIBoard" or "#버그".
/// A tag starts after whitespace, "(" or "[" (so "C#" is not one), and is letters, digits, "_" or "-";
/// digits alone ("#13", usually a memo number) do not count. Tags compare case-insensitively.
/// </summary>
public static partial class MemoTags
{
    /// <summary>Regex matching one hashtag; the tag without "#" is in the group "tag".</summary>
    public const string Pattern = @"(?<![^\s(\[])#(?<tag>[\p{L}\p{N}_-]+)";

    public static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    [GeneratedRegex(Pattern)]
    private static partial Regex TagRegex();

    /// <summary>Whether a "#..." match is a real tag rather than a number like "#13".</summary>
    public static bool IsTag(string tag) => tag.Any(c => !char.IsDigit(c));

    /// <summary>The distinct tags in the text, without "#", in order of first appearance.</summary>
    public static IReadOnlyList<string> Parse(string text) =>
        TagRegex().Matches(text)
            .Select(m => m.Groups["tag"].Value)
            .Where(IsTag)
            .Distinct(Comparer)
            .ToList();

    public static IReadOnlyList<string> Of(Memo memo) => Parse(memo.Title + "\n" + memo.Body);

    public static bool Has(Memo memo, string tag) => Of(memo).Contains(tag.TrimStart('#'), Comparer);

    /// <summary>
    /// A tag for the folder an AI session works in ("E:\Project\MyAIBoard" → "MyAIBoard"),
    /// or null for folders that say nothing about the task (drive roots, the user profile, Windows).
    /// Characters a tag cannot hold become "-".
    /// </summary>
    public static string? FromFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        string[] meaningless =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.System),
        ];
        if (Path.GetPathRoot(full) is { } root && Path.TrimEndingDirectorySeparator(root) == full) return null;
        if (meaningless.Any(path => path.Length > 0 && string.Equals(path, full, StringComparison.OrdinalIgnoreCase)))
            return null;

        var tag = new StringBuilder();
        foreach (var c in Path.GetFileName(full))
            tag.Append(char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '-');
        var result = Regex.Replace(tag.ToString(), "-{2,}", "-").Trim('-');
        return result.Length > 0 && IsTag(result) ? result : null;
    }
}
