using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace MyAIBoard.App.Interop;

/// <summary>
/// Asks the Windows compositor to blur whatever is behind the window, and clips
/// the window to a rounded rectangle so the blur does not show square corners.
/// Uses plain blur-behind rather than acrylic: acrylic lags while dragging on Windows 10.
/// </summary>
internal static class GlassBackdrop
{
    private const int WCA_ACCENT_POLICY = 19;
    private const int ACCENT_ENABLE_BLURBEHIND = 3;

    public static void Apply(Window window, double cornerRadius)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        EnableBlur(hwnd);

        window.SizeChanged += (_, _) => ClipToRoundedRect(window, hwnd, cornerRadius);
        window.DpiChanged += (_, _) => ClipToRoundedRect(window, hwnd, cornerRadius);
        ClipToRoundedRect(window, hwnd, cornerRadius);
    }

    private static void EnableBlur(IntPtr hwnd)
    {
        var accent = new AccentPolicy { AccentState = ACCENT_ENABLE_BLURBEHIND };
        var size = Marshal.SizeOf<AccentPolicy>();
        var accentPtr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, accentPtr, fDeleteOld: false);
            var data = new WindowCompositionAttributeData
            {
                Attribute = WCA_ACCENT_POLICY,
                Data = accentPtr,
                SizeOfData = size,
            };
            SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(accentPtr);
        }
    }

    private static void ClipToRoundedRect(Window window, IntPtr hwnd, double cornerRadius)
    {
        var dpi = VisualTreeHelper.GetDpi(window);
        var width = (int)Math.Round(window.ActualWidth * dpi.DpiScaleX);
        var height = (int)Math.Round(window.ActualHeight * dpi.DpiScaleY);
        var diameter = (int)Math.Round(cornerRadius * 2 * dpi.DpiScaleX);
        if (width <= 0 || height <= 0) return;

        // The window takes ownership of the region; it must not be deleted here.
        var region = CreateRoundRectRgn(0, 0, width + 1, height + 1, diameter, diameter);
        SetWindowRgn(hwnd, region, bRedraw: true);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public uint GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int widthEllipse, int heightEllipse);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, [MarshalAs(UnmanagedType.Bool)] bool bRedraw);
}
