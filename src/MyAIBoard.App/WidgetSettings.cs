using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MyAIBoard.App;

public enum WindowMode
{
    /// <summary>Sits on the desktop below other windows and survives Win+D.</summary>
    Desktop,
    AlwaysOnTop,
    Normal,
}

public enum SortMode
{
    /// <summary>The order the user arranged by dragging (new memos go on top).</summary>
    Manual,
    Newest,
    Oldest,
    Title,
}

/// <summary>Widget placement and behavior, persisted next to the database.</summary>
public sealed class WidgetSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    /// <summary>NaN means "not placed yet": the window picks a default spot.</summary>
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    public double Width { get; set; } = 320;
    public double Height { get; set; } = 480;
    public WindowMode Mode { get; set; } = WindowMode.Desktop;
    public SortMode Sort { get; set; } = SortMode.Manual;

    /// <summary>System-wide shortcut that opens a new memo, e.g. "Ctrl+Alt+M"; empty turns it off.</summary>
    public string NewMemoHotkey { get; set; } = "Ctrl+Alt+M";

    public static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyAIBoard", "settings.json");

    public static WidgetSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<WidgetSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A broken settings file should never keep the widget from starting.
        }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing the window position is not worth crashing over.
        }
    }
}
