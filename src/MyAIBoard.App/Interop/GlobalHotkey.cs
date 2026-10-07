using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace MyAIBoard.App.Interop;

/// <summary>
/// A system-wide keyboard shortcut (RegisterHotKey) delivered to a window, so it works
/// while another application has focus.
/// </summary>
internal sealed class GlobalHotkey : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;
    private const int HotkeyId = 0x4D41; // any id unique within this window

    private readonly IntPtr _hwnd;
    private readonly HwndSource _source;
    private readonly Action _pressed;

    /// <summary>The gesture that was registered, e.g. "Ctrl+Alt+M".</summary>
    public string Gesture { get; }

    private GlobalHotkey(Window window, KeyGesture gesture, string text, Action pressed)
    {
        _hwnd = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(_hwnd)!;
        _pressed = pressed;
        Gesture = text;

        uint modifiers = MOD_NOREPEAT;
        if (gesture.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= MOD_ALT;
        if (gesture.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= MOD_CONTROL;
        if (gesture.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= MOD_SHIFT;
        if (gesture.Modifiers.HasFlag(ModifierKeys.Windows)) modifiers |= MOD_WIN;

        if (!RegisterHotKey(_hwnd, HotkeyId, modifiers, (uint)KeyInterop.VirtualKeyFromKey(gesture.Key)))
            throw new InvalidOperationException($"{text} is already in use.");
        _source.AddHook(WndProc);
    }

    /// <summary>
    /// Registers a gesture such as "Ctrl+Alt+M". Returns null, with the reason, if the text is not a valid
    /// gesture or another program already owns the shortcut.
    /// </summary>
    public static GlobalHotkey? TryRegister(Window window, string text, Action pressed, out string? error)
    {
        error = null;
        KeyGesture gesture;
        try
        {
            gesture = (KeyGesture)new KeyGestureConverter().ConvertFromInvariantString(text)!;
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException or FormatException)
        {
            error = $"단축키 \"{text}\"를 이해하지 못했어요";
            return null;
        }

        try
        {
            return new GlobalHotkey(window, gesture, text, pressed);
        }
        catch (InvalidOperationException)
        {
            error = $"{text}는 다른 프로그램이 쓰고 있어요";
            return null;
        }
    }

    public void Dispose()
    {
        _source.RemoveHook(WndProc);
        UnregisterHotKey(_hwnd, HotkeyId);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            _pressed();
            handled = true;
        }
        return IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
