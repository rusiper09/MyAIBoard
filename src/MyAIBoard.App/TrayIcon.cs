using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace MyAIBoard.App;

/// <summary>
/// Notification-area icon. Left click shows/hides the widget; right click opens the
/// widget's own glass menu at the cursor so both menus look the same.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly ContextMenu _menu;

    public TrayIcon(ContextMenu menu, Action toggleWidget)
    {
        _menu = menu;
        using var iconStream = typeof(TrayIcon).Assembly.GetManifestResourceStream("MyAIBoard.ico")!;
        _icon = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(iconStream, Forms.SystemInformation.SmallIconSize),
            Text = "AI Board",
            Visible = true,
        };
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) toggleWidget();
            else if (e.Button == Forms.MouseButtons.Right) ShowMenu();
        };
    }

    private void ShowMenu()
    {
        _menu.PlacementTarget = null;
        _menu.Placement = PlacementMode.MousePoint;
        _menu.IsOpen = true;

        // A popup only closes on outside clicks while its app is in the foreground.
        if (PresentationSource.FromVisual(_menu) is HwndSource source)
            SetForegroundWindow(source.Handle);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
