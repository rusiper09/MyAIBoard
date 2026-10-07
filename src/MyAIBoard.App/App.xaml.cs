using System.Windows;
using MyAIBoard.Core;

namespace MyAIBoard.App;

public partial class App : Application
{
    private const string InstanceMutexName = @"Local\MyAIBoard.Widget";
    private const string ShowEventName = @"Local\MyAIBoard.Show";

    private Mutex? _singleInstance;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showWait;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Only one widget per user session. Launching again (e.g. from the Start menu)
        // asks the running widget to show itself instead.
        _singleInstance = new Mutex(initiallyOwned: true, InstanceMutexName, out var isFirstInstance);
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        if (!isFirstInstance)
        {
            _showEvent.Set();
            Shutdown();
            return;
        }

        var window = new MainWindow(new MemoStore(BoardPaths.DefaultDatabasePath), WidgetSettings.Load());
        MainWindow = window;
        window.Show();

        _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent,
            (_, _) => Dispatcher.BeginInvoke(window.ShowWidget), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showWait?.Unregister(null);
        _showEvent?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
