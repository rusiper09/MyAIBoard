using Microsoft.Win32;

namespace MyAIBoard.App;

/// <summary>"Run at Windows sign-in" via the per-user Run registry key.</summary>
internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MyAIBoard";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string command && command.Contains(ExecutablePath, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
            key.SetValue(ValueName, $"\"{ExecutablePath}\"");
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    private static string ExecutablePath => Environment.ProcessPath!;
}
