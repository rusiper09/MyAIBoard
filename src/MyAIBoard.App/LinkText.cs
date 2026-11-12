using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using MyAIBoard.Core;

namespace MyAIBoard.App;

/// <summary>
/// Attached property that fills a TextBlock with text whose quoted web links and Windows paths,
/// and hashtags, are clickable.
/// Use <c>local:LinkText.Text="{Binding Body}"</c> in place of <c>Text</c>.
/// </summary>
/// <remarks>
/// Memo bodies are often written by an AI, so a click never runs anything: web links must be
/// http(s), folders open in Explorer, and files are only selected in Explorer, not opened.
/// </remarks>
public static partial class LinkText
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(LinkText), new PropertyMetadata(null, OnTextChanged));

    public static string? GetText(DependencyObject element) => (string?)element.GetValue(TextProperty);
    public static void SetText(DependencyObject element, string? value) => element.SetValue(TextProperty, value);

    /// <summary>When true, "- [ ]" / "- [x]" lines show a clickable box (see <see cref="ChecklistToggled"/>).</summary>
    public static readonly DependencyProperty ChecklistProperty = DependencyProperty.RegisterAttached(
        "Checklist", typeof(bool), typeof(LinkText), new PropertyMetadata(false, (d, _) => Render(d)));

    public static bool GetChecklist(DependencyObject element) => (bool)element.GetValue(ChecklistProperty);
    public static void SetChecklist(DependencyObject element, bool value) => element.SetValue(ChecklistProperty, value);

    /// <summary>Raised when a checklist box is clicked, with the TextBlock and the line (counted from 0) of the item.</summary>
    public static event Action<TextBlock, int>? ChecklistToggled;

    /// <summary>Raised when a clicked link cannot be opened, with a message for the user.</summary>
    public static event Action<string>? OpenFailed;

    /// <summary>Raised when a hashtag is clicked, with the tag (without "#").</summary>
    public static event Action<string>? TagClicked;

    // Only text in single quotes becomes a link, so paths may contain spaces:
    // 'https://...', 'C:\Program Files\x', '\\server\share', '%LOCALAPPDATA%\x'.
    // Hashtags (see MemoTags) are links too, and filter the board when clicked.
    [GeneratedRegex("""'(?<link>(?:https?://|[A-Za-z]:\\|\\\\|%[A-Za-z_][A-Za-z0-9_]*%\\)[^'\r\n]+)'|""" + MemoTags.Pattern)]
    private static partial Regex LinkPattern();

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => Render(d);

    private static void Render(DependencyObject d)
    {
        if (d is not TextBlock block) return;

        block.Inlines.Clear();
        var text = GetText(block) ?? "";
        if (!GetChecklist(block))
        {
            AddLinked(block, block.Inlines, text);
            return;
        }

        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0) block.Inlines.Add(new LineBreak());
            var line = lines[i].TrimEnd('\r');
            if (MemoChecklist.ParseLine(line, i) is not { } item)
            {
                AddLinked(block, block.Inlines, line);
                continue;
            }

            if (item.Indent.Length > 0)
                block.Inlines.Add(new Run(item.Indent));

            var glyph = new Run(item.IsChecked ? "" : "") // CheckboxComposite / Checkbox
            {
                FontSize = block.FontSize,
                BaselineAlignment = BaselineAlignment.Center,
            };
            if (block.TryFindResource("IconFont") is FontFamily iconFont)
                glyph.FontFamily = iconFont;
            var box = new Hyperlink(glyph) { ToolTip = item.IsChecked ? "체크 해제" : "체크" };
            if (block.TryFindResource(item.IsChecked ? "ChecklistBoxChecked" : "ChecklistBox") is Style boxStyle)
                box.Style = boxStyle;
            var lineIndex = i;
            box.Click += (_, _) => ChecklistToggled?.Invoke(block, lineIndex);
            block.Inlines.Add(box);
            block.Inlines.Add(new Run(" "));

            if (item.IsChecked)
            {
                // Done steps read as done: struck through and dimmed.
                var done = new Span { TextDecorations = TextDecorations.Strikethrough };
                if (block.TryFindResource("CardTextTertiary") is Brush dim)
                    done.Foreground = dim;
                AddLinked(block, done.Inlines, item.Text);
                block.Inlines.Add(done);
            }
            else
            {
                AddLinked(block, block.Inlines, item.Text);
            }
        }
    }

    /// <summary>Adds the text to <paramref name="inlines"/>, turning quoted links and hashtags into hyperlinks.</summary>
    private static void AddLinked(TextBlock block, InlineCollection inlines, string text)
    {
        var position = 0;
        foreach (Match match in LinkPattern().Matches(text))
        {
            Hyperlink hyperlink;
            Group shown;
            if (match.Groups["tag"] is { Success: true } tagGroup)
            {
                var tag = tagGroup.Value;
                if (!MemoTags.IsTag(tag)) continue;

                shown = match.Groups[0]; // "#tag", including the "#"
                hyperlink = new Hyperlink(new Run(shown.Value)) { ToolTip = $"#{tag} 메모만 보기" };
                if (block.TryFindResource("TagLink") is Style tagStyle)
                    hyperlink.Style = tagStyle;
                hyperlink.Click += (_, _) => TagClicked?.Invoke(tag);
            }
            else
            {
                // The link is the part inside the quotes; the quotes stay plain text.
                shown = match.Groups["link"];
                var link = shown.Value.Trim();
                if (link.Length == 0) continue;

                hyperlink = new Hyperlink(new Run(shown.Value)) { ToolTip = link };
                hyperlink.Click += (_, _) => Open(link);
            }

            if (shown.Index > position)
                inlines.Add(new Run(text[position..shown.Index]));
            inlines.Add(hyperlink);
            position = shown.Index + shown.Length;
        }
        if (position < text.Length)
            inlines.Add(new Run(text[position..]));
    }

    private static void Open(string link)
    {
        try
        {
            if (link.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                link.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                Process.Start(new ProcessStartInfo(link) { UseShellExecute = true });
                return;
            }

            var path = ResolvePath(Environment.ExpandEnvironmentVariables(link));
            if (path is null)
                OpenFailed?.Invoke("경로를 찾을 수 없어요");
            else if (Directory.Exists(path))
                Process.Start("explorer.exe", $"\"{path}\"");
            else
                Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            OpenFailed?.Invoke("링크를 열지 못했어요");
        }
    }

    /// <summary>
    /// The longest existing file or folder that the text starts with, so trailing words and
    /// line numbers are ignored ("E:\Project\MyAIBoard에서" → "E:\Project\MyAIBoard",
    /// "Foo.cs:137" → "Foo.cs"). A bare drive root does not count as a match.
    /// </summary>
    internal static string? ResolvePath(string text)
    {
        // Probing a network path can block for seconds, so UNC paths are checked once, as written.
        if (text.StartsWith(@"\\"))
            return File.Exists(text) || Directory.Exists(text) ? text : null;

        for (var length = text.Length; length > 3; length--)
        {
            var candidate = text[..length].TrimEnd('\\');
            if (candidate.Length > 2 && (File.Exists(candidate) || Directory.Exists(candidate)))
                return candidate;
        }
        return null;
    }
}
