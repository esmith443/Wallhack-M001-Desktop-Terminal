using System.Diagnostics;
using System.Windows;
using WallhackTerminal.Services;
using WallhackTerminal.UI;

namespace WallhackTerminal;

public partial class App : Application
{
    const string InstanceName = "WallhackTerminal.SingleInstance";
    const string ShowEventName = "WallhackTerminal.ShowWindow";
    const string UpdatedArg = "--updated";
    const string TrayArg = "--tray";

    Mutex? _instance;
    bool _ownsInstance;
    EventWaitHandle? _showSignal;
    AppSettings _settings = null!;
    DeviceService? _device;
    NotificationService? _notifications;
    UpdateCoordinator? _updates;
    TrayService? _tray;
    MainWindow? _window;

    public bool Exiting { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool updated = HasArg(e, UpdatedArg);

        _instance = new Mutex(false, InstanceName);
        try
        {
            _ownsInstance = _instance.WaitOne(updated ? TimeSpan.FromSeconds(15) : TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            _ownsInstance = true;
        }
        if (!_ownsInstance)
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
                Dispatcher.BeginInvoke(() => ShowMainWindow());
            }
        }) { IsBackground = true, Name = "Show-window signal" }.Start();

        UpdateService.CleanUpPreviousVersion();
        Term.Initialize();
        _settings = AppSettings.Load();
        _device = new DeviceService(Dispatcher);
        _notifications = new NotificationService(_settings, _device);
        _updates = new UpdateCoordinator(_settings, _device, _notifications);
        _tray = new TrayService(this, _device, _settings, _updates) { Notifications = _notifications };
        _notifications.Tray = _tray;
        _device.Start();
        Log.Info($"started V{AppInfo.VersionText}{(updated ? " after update" : "")}");

        if (!HasArg(e, TrayArg)) ShowMainWindow();
        if (updated)
            _notifications.Show($"UPDATED TO V{AppInfo.VersionText}", "WH_TERMINAL Desktop is up to date.", [("APP", "V" + AppInfo.VersionText)]);
    }

    static bool HasArg(StartupEventArgs e, string arg) => e.Args.Any(a => a.Equals(arg, StringComparison.OrdinalIgnoreCase));

    public void ShowMainWindow(string? tab = null)
    {
        if (Exiting || _device is null || _notifications is null || _updates is null) return;
        _window ??= new MainWindow(this, _device, _settings, _notifications, _updates);
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
        if (tab is not null) _window.OpenTab(tab);
    }

    public void RestartAfterUpdate()
    {
        if (Environment.ProcessPath is not { } path) return;
        string args = _window is { IsVisible: true } ? UpdatedArg : $"{UpdatedArg} {TrayArg}";
        Quit(() =>
        {
            try
            {
                Process.Start(new ProcessStartInfo(path, args) { UseShellExecute = false });
            }
            catch (Exception ex)
            {
                Log.Error("restart after update failed", ex);
            }
        });
    }

    void ReleaseInstance()
    {
        if (!_ownsInstance) return;
        _ownsInstance = false;
        try { _instance?.ReleaseMutex(); }
        catch { }
    }

    public void ExitApp() => Quit(null);

    void Quit(Action? relaunch)
    {
        if (Exiting) return;
        Exiting = true;
        _window?.Close();
        _tray?.Dispose();
        _device?.Dispose();
        _showSignal?.Set();
        if (relaunch is not null)
        {
            ReleaseInstance();
            relaunch();
        }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _device?.Dispose();
        ReleaseInstance();
        base.OnExit(e);
    }
}
