using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MyAIBoard.App.Interop;

/// <summary>
/// Pins a window to the desktop: it stays below all other windows and,
/// because it is owned by the desktop's Progman window, it stays visible
/// when the user presses Win+D ("Show desktop").
/// </summary>
internal sealed class DesktopPin
{
    private const int GWLP_HWNDPARENT = -8;
    private const int WM_WINDOWPOSCHANGING = 0x0046;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private static readonly IntPtr HWND_BOTTOM = new(1);

    private readonly IntPtr _hwnd;
    private IntPtr _originalOwner;

    public bool IsPinned { get; private set; }

    public DesktopPin(Window window)
    {
        _hwnd = new WindowInteropHelper(window).EnsureHandle();
        HwndSource.FromHwnd(_hwnd)!.AddHook(WndProc);
    }

    public void Pin()
    {
        if (IsPinned) return;

        var progman = FindWindow("Progman", null);
        if (progman != IntPtr.Zero)
            _originalOwner = SetWindowLongPtr(_hwnd, GWLP_HWNDPARENT, progman);

        IsPinned = true;
        SendToBottom();
    }

    public void Unpin()
    {
        if (!IsPinned) return;

        IsPinned = false;
        SetWindowLongPtr(_hwnd, GWLP_HWNDPARENT, _originalOwner);
        _originalOwner = IntPtr.Zero;
    }

    public void SendToBottom() =>
        SetWindowPos(_hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Whenever Windows tries to raise the window (click, activation), keep it at the bottom.
        if (msg == WM_WINDOWPOSCHANGING && IsPinned)
        {
            var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
            if ((pos.flags & SWP_NOZORDER) == 0)
            {
                pos.hwndInsertAfter = HWND_BOTTOM;
                Marshal.StructureToPtr(pos, lParam, fDeleteOld: false);
            }
        }
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPOS
    {
        public IntPtr hwnd;
        public IntPtr hwndInsertAfter;
        public int x;
        public int y;
        public int cx;
        public int cy;
        public uint flags;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
}
