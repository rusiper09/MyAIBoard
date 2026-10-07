using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace MyAIBoard.App;

/// <summary>
/// Attached property that fills a TextBlock with text whose web links and Windows paths are clickable.
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

    /// <summary>Raised when a clicked link cannot be opened, with a message for the user.</summary>
    public static event Action<string>? OpenFailed;

    // Only text in single quotes becomes a link, so paths may contain spaces:
    // 'https://...', 'C:\Program Files\x', '\\server\share', '%LOCALAPPDATA%\x'.
    [GeneratedRegex("""'(?<link>(?:https?://|[A-Za-z]:\\|\\\\|%[A-Za-z_][A-Za-z0-9_]*%\\)[^'\r\n]+)'""")]
    private static partial Regex LinkPattern();

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block) return;

        block.Inlines.Clear();
        var text = (string?)e.NewValue ?? "";
        var position = 0;
        foreach (Match match in LinkPattern().Matches(text))
        {
            // The link is the part inside the quotes; the quotes stay plain text.
            var group = match.Groups["link"];
            var link = group.Value.Trim();
            if (link.Length == 0) continue;

            if (group.Index > position)
                block.Inlines.Add(new Run(text[position..group.Index]));

            var hyperlink = new Hyperlink(new Run(group.Value)) { ToolTip = link };
            hyperlink.Click += (_, _) => Open(link);
            block.Inlines.Add(hyperlink);
            position = group.Index + group.Value.Length;
        }
        if (position < text.Length)
            block.Inlines.Add(new Run(text[position..]));
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
