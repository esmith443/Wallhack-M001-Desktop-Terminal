using System.Windows;
using WallhackTerminal.Services;
using WallhackTerminal.UI;

namespace WallhackTerminal;

public partial class App : Application
{
    const string InstanceName = "WallhackTerminal.SingleInstance";
    const string ShowEventName = "WallhackTerminal.ShowWindow";

    Mutex? _instance;
    EventWaitHandle? _showSignal;
    AppSettings _settings = null!;
    DeviceService? _device;
    NotificationService? _notifications;
    TrayService? _tray;
    MainWindow? _window;

    public bool Exiting { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instance = new Mutex(true, InstanceName, out bool firstInstance);
        if (!firstInstance)
        {
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); }
            catch { }
            Exiting = true;
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("unhandled UI exception", args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error("unhandled exception", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("unobserved task exception", args.Exception);
            args.SetObserved();
        };

        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        new Thread(() =>
        {
            while (_showSignal.WaitOne())
            {
                if (Exiting) return;
                Dispatcher.BeginInvoke(ShowMainWindow);
            }
        }) { IsBackground = true, Name = "Show-window signal" }.Start();

        Term.Initialize();
        _settings = AppSettings.Load();
        _device = new DeviceService(Dispatcher);
        _notifications = new NotificationService(_settings, _device);
        _tray = new TrayService(this, _device, _settings) { Notifications = _notifications };
        _notifications.Tray = _tray;
        _device.Start();
        Log.Info("started");

        if (!e.Args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase))) ShowMainWindow();
        if (e.Args.FirstOrDefault(a => a.StartsWith("--tab=", StringComparison.OrdinalIgnoreCase)) is { } tab)
            _window?.OpenTab(tab["--tab=".Length..]);
        if (e.Args.Any(a => a.Equals("--test-notify", StringComparison.OrdinalIgnoreCase)))
        {
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                _notifications?.ShowTest(forceAll: true);
            };
            timer.Start();
        }
    }

    public void ShowMainWindow()
    {
        if (Exiting || _device is null || _notifications is null) return;
        _window ??= new MainWindow(this, _device, _settings, _notifications);
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    public void ExitApp()
    {
        if (Exiting) return;
        Exiting = true;
        _window?.Close();
        _tray?.Dispose();
        _device?.Dispose();
        _showSignal?.Set();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _device?.Dispose();
        try { _instance?.ReleaseMutex(); }
        catch { }
        base.OnExit(e);
    }
}
