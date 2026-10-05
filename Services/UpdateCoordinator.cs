using System.Windows.Threading;

namespace WallhackTerminal.Services;

public sealed class UpdateCoordinator
{
    static readonly TimeSpan AppInterval = TimeSpan.FromHours(6);
    static readonly TimeSpan FirmwareInterval = TimeSpan.FromHours(12);

    readonly AppSettings _settings;
    readonly DeviceService _device;
    readonly NotificationService _notifications;
    readonly DispatcherTimer _timer;
    DateTime _nextApp = DateTime.Now.AddSeconds(15);
    DateTime _nextFirmware = DateTime.Now.AddSeconds(30);

    public UpdateService App { get; } = new();
    public FirmwareService Firmware { get; } = new();
    public FirmwareStatus FirmwareStatus => Firmware.StatusFor(_device.State.Versions);

    public event Action? Changed;

    public UpdateCoordinator(AppSettings settings, DeviceService device, NotificationService notifications)
    {
        _settings = settings;
        _device = device;
        _notifications = notifications;
        App.Changed += () =>
        {
            Changed?.Invoke();
            AnnounceApp();
        };
        Firmware.Changed += () =>
        {
            Changed?.Invoke();
            AnnounceFirmware();
        };
        device.StateChanged += AnnounceFirmware;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    void Tick()
    {
        if (!_settings.CheckForUpdates) return;
        var now = DateTime.Now;
        if (now >= _nextApp)
        {
            _nextApp = now + AppInterval;
            _ = App.CheckAsync();
        }
        if (now >= _nextFirmware)
        {
            _nextFirmware = now + FirmwareInterval;
            _ = Firmware.CheckAsync();
        }
    }

    void AnnounceApp()
    {
        if (!App.Available || App.Latest is not { } latest) return;
        string version = latest.Version.ToString(3);
        if (_settings.AnnouncedAppVersion == version) return;
        _settings.AnnouncedAppVersion = version;
        _settings.Save();
        _notifications.Show($"WH_TERMINAL V{version} AVAILABLE", "Open the Updates tab to install it.",
            [("APP UPDATE", "V" + version)], tab: "updates");
    }

    void AnnounceFirmware()
    {
        if (FirmwareStatus != FirmwareStatus.Available || Firmware.Latest is not { } latest) return;
        if (_settings.AnnouncedFirmwareVersion == latest.Version) return;
        _settings.AnnouncedFirmwareVersion = latest.Version;
        _settings.Save();
        _notifications.Show($"MOUSE FIRMWARE V{latest.Version} AVAILABLE", "Install it with Wallhack's official web terminal. See the Updates tab.",
            [("FIRMWARE", "V" + latest.Version)], tab: "updates");
    }
}
