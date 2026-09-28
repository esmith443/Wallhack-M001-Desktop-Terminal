using System.Windows.Threading;
using WallhackTerminal.Protocol;
using WallhackTerminal.UI;

namespace WallhackTerminal.Services;

public static class Presets
{
    public static readonly string[] DefaultDpi = ["400", "800", "1600", "3200", "6400", "12800", "25600"];
    public static readonly string[] DefaultPoll = ["125", "250", "500", "1000", "2000", "4000", "8000"];

    public static string Dpi(MouseState state, AppSettings settings) =>
        state.DpiRank is not int rank ? "----"
        : rank == M001.CustomRank ? (state.Get(SettingId.Dpi) is int dpi ? $"{dpi} DPI" : "CUSTOM")
        : Label(settings.DpiPresetLabels, DefaultDpi, rank, "DPI");

    public static string Poll(MouseState state, AppSettings settings) =>
        state.PollRank is not int rank ? "----"
        : rank == M001.CustomRank ? (state.Get(SettingId.PollRate) is int hz ? $"{hz} HZ" : "CUSTOM")
        : Label(settings.PollPresetLabels, DefaultPoll, rank, "HZ");

    public static string Position(int? rank) =>
        rank is not int r ? "UNKNOWN" : r == M001.CustomRank ? "CUSTOM (RED)" : $"POSITION {r + 1}";

    static string Label(string[] labels, string[] defaults, int rank, string unit)
    {
        if (rank < 0 || rank >= defaults.Length) return $"POSITION {rank + 1}";
        string label = (rank < labels.Length && !string.IsNullOrWhiteSpace(labels[rank]) ? labels[rank] : defaults[rank])
            .Trim().ToUpperInvariant();
        return label.All(char.IsDigit) ? $"{label} {unit}" : label;
    }
}

public sealed class NotificationService
{
    readonly AppSettings _settings;
    readonly DeviceService _device;
    readonly DispatcherTimer _debounce;
    OsdWindow? _osd;
    bool _dpiPending, _pollPending;
    bool _hadConnection;

    public TrayService? Tray { get; set; }

    public NotificationService(AppSettings settings, DeviceService device)
    {
        _settings = settings;
        _device = device;
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Flush();
        };
        device.HardwareChanged += OnHardwareChanged;
        device.ConnectionChanged += OnConnectionChanged;
    }

    void OnHardwareChanged(HardwareChange change)
    {
        if (change.DpiRank is not null && _settings.NotifyDpi) _dpiPending = true;
        if (change.PollRank is not null && _settings.NotifyPollRate) _pollPending = true;
        if (!_dpiPending && !_pollPending) return;
        _debounce.Stop();
        _debounce.Start();
    }

    void OnConnectionChanged(bool connected)
    {
        bool firstConnection = connected && !_hadConnection;
        if (connected) _hadConnection = true;
        if (!_settings.NotifyConnection || firstConnection) return;
        if (!connected && _device.Paused) return;
        Show(connected ? "RECEIVER CONNECTED" : "RECEIVER DISCONNECTED",
            connected ? "WALLHACK M-001 is ready." : "Plug the M-001 receiver back in to reconnect.",
            [("RECEIVER", connected ? "CONNECTED" : "REMOVED")]);
    }

    void Flush()
    {
        var state = _device.State;
        var rows = new List<(string, string)>();
        var parts = new List<string>();
        var detail = new List<string>();
        if (_dpiPending)
        {
            string dpi = Presets.Dpi(state, _settings);
            rows.Add(("DPI", dpi));
            parts.Add(dpi.EndsWith("DPI") ? dpi : "DPI " + dpi);
            detail.Add($"DPI selector: {Presets.Position(state.DpiRank)}");
        }
        if (_pollPending)
        {
            string poll = Presets.Poll(state, _settings);
            rows.Add(("POLLING RATE", poll));
            parts.Add(poll.EndsWith("HZ") ? poll : "HZ " + poll);
            detail.Add($"Polling-rate selector: {Presets.Position(state.PollRank)}");
        }
        _dpiPending = _pollPending = false;
        if (rows.Count > 0) Show(string.Join("  ·  ", parts), string.Join("\n", detail), rows);
    }

    public void Show(string title, string body, IReadOnlyList<(string Label, string Value)> rows, bool forceAll = false)
    {
        if (_settings.WindowsNotifications || forceAll) Tray?.ShowBalloon(title, body);
        if (_settings.OverlayNotifications || forceAll)
        {
            _osd ??= new OsdWindow();
            _osd.Present(rows, _settings.OverlayPosition);
        }
    }

    public void ShowTest(bool forceAll = false)
    {
        var state = _device.State;
        Show($"{Presets.Dpi(state, _settings)}  ·  {Presets.Poll(state, _settings)}",
            "Test — this is how receiver dial changes are announced.",
            [("DPI", Presets.Dpi(state, _settings)), ("POLLING RATE", Presets.Poll(state, _settings))], forceAll);
    }
}
