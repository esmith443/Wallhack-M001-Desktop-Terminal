using WallhackTerminal.Protocol;

namespace WallhackTerminal.Services;

public sealed class MouseState
{
    public bool ReceiverConnected { get; set; }
    public string ReceiverStatus { get; set; } = "SCANNING";
    public bool? MouseResponding { get; set; }
    public bool Hydrated { get; set; }
    public bool Busy { get; set; }

    public int? DpiRank { get; set; }
    public int? PollRank { get; set; }

    readonly Dictionary<SettingId, int> _settings = new();

    public FirmwareVersions? Versions { get; set; }
    public string? MouseChipId { get; set; }
    public string? ReceiverChipId { get; set; }
    public KeyTriplet[]? Buttons { get; set; }
    public KeyTriplet[]? DefaultButtons { get; set; }
    public List<MacroStep>?[]? Macros { get; set; }
    public Curve?[] Curves { get; } = new Curve?[4];
    public bool CurvesUnsupported { get; set; }

    public int? Get(SettingId id) => _settings.TryGetValue(id, out var v) ? v : null;
    public bool? Flag(SettingId id) => _settings.TryGetValue(id, out var v) ? v != 0 : null;
    public void Set(SettingId id, int value) => _settings[id] = value;

    public bool IsDpiCustom => DpiRank == M001.CustomRank;
    public bool IsPollRateCustom => PollRank == M001.CustomRank;
    public bool CanConfigure => ReceiverConnected && MouseResponding == true;

    public Curve CurveFor(int mode) => Curves[mode] ?? Protocol.Curves.Defaults[mode];

    public void Reset()
    {
        ReceiverConnected = false;
        MouseResponding = null;
        Hydrated = false;
        Busy = false;
        DpiRank = PollRank = null;
        _settings.Clear();
        Versions = null;
        MouseChipId = ReceiverChipId = null;
        Buttons = DefaultButtons = null;
        Macros = null;
        Array.Clear(Curves);
        CurvesUnsupported = false;
    }
}

public sealed record HardwareChange(int? DpiRank, int? PollRank);
