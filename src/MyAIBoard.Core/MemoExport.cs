using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace MyAIBoard.Core;

/// <summary>
/// Board backups. JSON keeps everything needed to import the memos again (see
/// <see cref="MemoStore.Import"/>); Markdown is for reading or pasting elsewhere.
/// Ids and positions are not exported: the order of the list is the board order.
/// </summary>
public static class MemoExport
{
    public const int FormatVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keep Korean readable
    };

    public sealed record ExportedMemo(
        string Title,
        string Body,
        string Source,
        bool IsCollapsed,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);

    public sealed record ExportFile(string App, int Version, DateTimeOffset ExportedAt, IReadOnlyList<ExportedMemo> Memos);

    public static string ToJson(IEnumerable<Memo> memos, DateTimeOffset exportedAt) =>
        JsonSerializer.Serialize(new ExportFile("MyAIBoard", FormatVersion, exportedAt,
            memos.Select(m => new ExportedMemo(m.Title, m.Body, m.Source, m.IsCollapsed, m.CreatedAt, m.UpdatedAt)).ToList()),
            JsonOptions);

    /// <summary>Reads a file written by <see cref="ToJson"/>. Throws <see cref="FormatException"/> if it is not one.</summary>
    public static IReadOnlyList<ExportedMemo> FromJson(string json)
    {
        ExportFile? file;
        try
        {
            file = JsonSerializer.Deserialize<ExportFile>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new FormatException("Not a MyAIBoard export file.", ex);
        }

        if (file is not { App: "MyAIBoard", Memos: not null })
            throw new FormatException("Not a MyAIBoard export file.");
        if (file.Version > FormatVersion)
            throw new FormatException($"Export format {file.Version} is newer than this version of MyAIBoard.");

        return file.Memos
            .Where(m => !string.IsNullOrWhiteSpace(m.Title))
            .Select(m => m with { Body = m.Body ?? "", Source = string.IsNullOrWhiteSpace(m.Source) ? "User" : m.Source })
            .ToList();
    }

    public static string ToMarkdown(IReadOnlyList<Memo> memos, DateTimeOffset exportedAt)
    {
        var text = new StringBuilder();
        text.Append("# AI Board\n\n");
        text.Append(CultureInfo.InvariantCulture, $"_{Local(exportedAt)}에 내보냄 · 메모 {memos.Count}개_\n");

        foreach (var memo in memos)
        {
            text.Append(CultureInfo.InvariantCulture, $"\n## {memo.Title}\n\n");
            if (!string.IsNullOrWhiteSpace(memo.Body))
                text.Append(memo.Body.Trim()).Append("\n\n");

            var source = memo.Source == "User" ? "나" : memo.Source;
            text.Append(CultureInfo.InvariantCulture, $"_{source} · 작성 {Local(memo.CreatedAt)}");
            if (memo.UpdatedAt > memo.CreatedAt)
                text.Append(CultureInfo.InvariantCulture, $" · 수정 {Local(memo.UpdatedAt)}");
            text.Append("_\n");
        }
        return text.ToString();
    }

    private static string Local(DateTimeOffset time) =>
        time.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}
