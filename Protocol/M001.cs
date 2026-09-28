namespace WallhackTerminal.Protocol;

public static class M001
{
    public const ushort VendorId = 0x3879;
    public const ushort ProductId = 0x1110;
    public const ushort CommandUsagePage = 0xFF1C;
    public const ushort CommandUsage = 0x92;

    public const byte ReportCommand = 4;
    public const byte ReportStatus = 3;
    public const int PayloadLength = 63;

    public const int CustomRank = 7;
    public const int MaxRank = 7;

    public const int DpiMin = 100, DpiMax = 30000, DpiStep = 50;
    public const int SleepMinMinutes = 1, SleepMaxMinutes = 30;
    public const int AngleMin = -30, AngleMax = 30;

    public static readonly int[] PollRates =
        [125, 250, 500, 1000, 1500, 2000, 2500, 3000, 3500, 4000, 4500, 5000, 5500, 6000, 6500, 7000, 7500, 8000];

    public static readonly double[] LodMillimetres = [0.7, 1, 2];

    public static class Cmd
    {
        public const byte CheckConnection = 0xA0;
        public const byte FastBegin = 0xA1;
        public const byte FastEnd = 0xA2;
        public const byte GetBasicInfo = 0xA3;
        public const byte ReadArea = 0xA4;
        public const byte WriteArea = 0xA5;
        public const byte GetDefaultKeys = 0xA6;
        public const byte GetKeys = 0xA7;
        public const byte SetKeys = 0xA8;
        public const byte FactoryReset = 0xAB;
        public const byte GetMacro = 0xAC;
        public const byte SetMacro = 0xAD;
        public const byte ReadMouseChipId = 0xB8;
        public const byte ReadDongleChipId = 0xB9;
        public const byte Battery = 0xBA;
        public const byte ReadVersion = 0xBC;
    }

    public static class Addr
    {
        public const int ProfileIndex = 0;
        public const int ReportEsb = 10;
        public const int ReportUsb = 11;
        public const int DpiRank = 12;
        public const int Dpi8Block = 77;
        public const int ReportUser = 104;
        public const int SleepTime = 105;
        public const int DeepSleepTime = 107;
        public const int KeyDebounceTime = 109;
        public const int SilentHeight = 110;
        public const int AngleSnapEnable = 111;
        public const int RippleControlEnable = 112;
        public const int MotionSyncEnable = 113;
        public const int TurnOffAutomaticSleep = 114;
        public const int AngleTuneValue = 115;
        public const int GameMode = 116;
        public const int DynamicDpiEnable = 117;
        public const int DynamicDpiMode = 118;
        public const int DynamicDpiSpeedReport = 119;
        public const int CombinedLength = 16;

        public const int CurveClassic = 640, CurveNatural = 650, CurveJump = 660, CurveCustom = 670;
    }

    static byte[] Frame(byte cmd, int length = 0, int address = 0, ReadOnlySpan<byte> data = default)
    {
        var p = new byte[PayloadLength];
        p[2] = cmd;
        p[3] = (byte)length;
        p[4] = (byte)address;
        p[5] = (byte)(address >> 8);
        data.CopyTo(p.AsSpan(7));
        return p;
    }

    public static byte[] Simple(byte cmd) => Frame(cmd);
    public static byte[] ReadArea(int address, int length) => Frame(Cmd.ReadArea, length, address);
    public static byte[] WriteArea(int address, ReadOnlySpan<byte> data) => Frame(Cmd.WriteArea, data.Length, address, data);
    public static byte[] ReadKeys(bool defaults) => Frame(defaults ? Cmd.GetDefaultKeys : Cmd.GetKeys, KeySlots * 3);
    public static byte[] SetKey(int slot, KeyTriplet t) => Frame(Cmd.SetKeys, 3, slot * 3, [t.Type, t.CodeL, t.CodeH]);
    public static byte[] ReadMacroArea(int address, int length) => Frame(Cmd.GetMacro, length, address);
    public static byte[] WriteMacroArea(int address, ReadOnlySpan<byte> data) => Frame(Cmd.SetMacro, data.Length, address, data);

    public static int Offset(byte[] r) => r.Length >= 6 ? r[4] | (r[5] << 8) : -1;
    public static byte Status(byte[] r) => r.Length > 6 ? r[6] : (byte)0;
    public static byte[] Data(byte[] r) => r.Length < 8 ? [] : r.AsSpan(7, Math.Min(r[3], r.Length - 7)).ToArray();

    public static string DescribeStatus(byte status) => status switch
    {
        1 => "address or length rejected by the mouse",
        2 => "the mouse rejected the custom curve",
        3 => "that setting is read-only",
        _ => $"mouse returned status {status}",
    };

    public static double? ParseMotionSpeed(byte a, byte b)
    {
        int v;
        if (a >> 4 == 1) v = (a << 8) | b;
        else if (b >> 4 == 1) v = (b << 8) | a;
        else return null;
        return (v & 0x0FFF) / 10.0;
    }

    public static int PollRateCode(int hz)
    {
        int best = 0;
        for (int i = 1; i < PollRates.Length; i++)
            if (Math.Abs(PollRates[i] - hz) < Math.Abs(PollRates[best] - hz)) best = i;
        return best;
    }

    static byte Flag(int v) => (byte)(v != 0 ? 1 : 0);

    public static byte[] EncodeSetting(SettingId id, int v) => id switch
    {
        SettingId.Dpi => WriteArea(Addr.Dpi8Block,
            [1, 0, (byte)v, (byte)(v >> 8), 0x90, 0x01, 0xFF, 0xFF, 0]),
        SettingId.PollRate => WriteArea(Addr.ReportUser, [(byte)PollRateCode(v)]),
        SettingId.SleepMinutes => WriteArea(Addr.SleepTime, [(byte)(v * 60), (byte)((v * 60) >> 8)]),
        SettingId.SleepEnabled => WriteArea(Addr.TurnOffAutomaticSleep, [(byte)(v != 0 ? 0 : 1)]),
        SettingId.Lod => WriteArea(Addr.SilentHeight, [(byte)v]),
        SettingId.Ripple => WriteArea(Addr.RippleControlEnable, [Flag(v)]),
        SettingId.MotionSync => WriteArea(Addr.MotionSyncEnable, [Flag(v)]),
        SettingId.GameMode => WriteArea(Addr.GameMode, [Flag(v)]),
        SettingId.SensorAngle => WriteArea(Addr.AngleTuneValue, [(byte)(v + 30)]),
        SettingId.DynEnabled => WriteArea(Addr.DynamicDpiEnable, [Flag(v)]),
        SettingId.DynMode => WriteArea(Addr.DynamicDpiMode, [(byte)v]),
        SettingId.DynSpeedReporting => WriteArea(Addr.DynamicDpiSpeedReport, [Flag(v)]),
        _ => throw new ArgumentOutOfRangeException(nameof(id)),
    };

    public static (int Address, int Length) SettingLocation(SettingId id) => id switch
    {
        SettingId.Dpi => (Addr.Dpi8Block, 9),
        SettingId.PollRate => (Addr.ReportUser, 1),
        SettingId.SleepMinutes => (Addr.SleepTime, 2),
        SettingId.SleepEnabled => (Addr.TurnOffAutomaticSleep, 1),
        SettingId.Lod => (Addr.SilentHeight, 1),
        SettingId.Ripple => (Addr.RippleControlEnable, 1),
        SettingId.MotionSync => (Addr.MotionSyncEnable, 1),
        SettingId.GameMode => (Addr.GameMode, 1),
        SettingId.SensorAngle => (Addr.AngleTuneValue, 1),
        SettingId.DynEnabled => (Addr.DynamicDpiEnable, 1),
        SettingId.DynMode => (Addr.DynamicDpiMode, 1),
        SettingId.DynSpeedReporting => (Addr.DynamicDpiSpeedReport, 1),
        _ => throw new ArgumentOutOfRangeException(nameof(id)),
    };

    public static int? DecodeSetting(SettingId id, byte[] d)
    {
        if (d.Length == 0) return null;
        int angle = d[0] - 30;
        return id switch
        {
            SettingId.Dpi => d.Length >= 4 ? d[2] | (d[3] << 8) : (int?)null,
            SettingId.PollRate => d[0] < PollRates.Length ? PollRates[d[0]] : (int?)null,
            SettingId.SleepMinutes => d.Length >= 2 ? SleepMinutesFromSeconds(d[0] | (d[1] << 8)) : (int?)null,
            SettingId.SleepEnabled => d[0] == 0 ? 1 : 0,
            SettingId.Lod => d[0] <= 2 ? d[0] : (int?)null,
            SettingId.SensorAngle => angle is >= AngleMin and <= AngleMax ? angle : (int?)null,
            SettingId.DynMode => d[0] <= 3 ? d[0] : (int?)null,
            _ => d[0] != 0 ? 1 : 0,
        };
    }

    static int? SleepMinutesFromSeconds(int seconds) =>
        seconds is >= 60 and <= 1800 ? (int)Math.Round(seconds / 60.0, MidpointRounding.AwayFromZero) : null;

    public static Dictionary<SettingId, int> ParseCombined(byte[] d)
    {
        var s = new Dictionary<SettingId, int>();
        if (d.Length < Addr.CombinedLength) return s;
        if (d[0] < PollRates.Length) s[SettingId.PollRate] = PollRates[d[0]];
        if (SleepMinutesFromSeconds(d[1] | (d[2] << 8)) is int minutes) s[SettingId.SleepMinutes] = minutes;
        if (d[6] <= 2) s[SettingId.Lod] = d[6];
        s[SettingId.Ripple] = d[8] != 0 ? 1 : 0;
        s[SettingId.MotionSync] = d[9] != 0 ? 1 : 0;
        s[SettingId.SleepEnabled] = d[10] == 0 ? 1 : 0;
        if (d[11] - 30 is var angle && angle is >= AngleMin and <= AngleMax) s[SettingId.SensorAngle] = angle;
        s[SettingId.GameMode] = d[12] != 0 ? 1 : 0;
        s[SettingId.DynEnabled] = d[13] != 0 ? 1 : 0;
        if (d[14] <= 3) s[SettingId.DynMode] = d[14];
        s[SettingId.DynSpeedReporting] = d[15] != 0 ? 1 : 0;
        return s;
    }

    public static FirmwareVersions? ParseVersions(byte[] r) =>
        r.Length < 13 ? null : new FirmwareVersions((r[7] << 8) | r[8], (r[9] << 8) | r[10], (r[11] << 8) | r[12]);

    public static string? ParseChipId(byte[] r)
    {
        if (r.Length < 8) return null;
        var bytes = r.AsSpan(7, Math.Min(16, r.Length - 7));
        int n = bytes.Length;
        while (n > 0 && bytes[n - 1] == 0) n--;
        if (n == 0) return null;
        return (r[2] == Cmd.ReadMouseChipId ? "28" : "29") + Convert.ToHexString(bytes[..n]);
    }

    public static readonly FirmwareRelease[] Releases =
    [
        new("1.17.0", "2026-09-16", 57, 41, 55),
        new("1.16.0", "2026-09-04", 57, 40, 53),
        new("1.15.0", null, 55, 39, 52),
        new("1.14.0", null, 53, 37, 52),
        new("1.13.0", null, 52, 36, 48),
        new("1.11.0", null, 51, 36, 48),
        new("1.10.0", null, 50, 36, 41),
        new("1.7.0", null, 41, 33, 39),
    ];

    public const int ButtonSlots = 5;
    public const int KeySlots = 8;

    public static KeyTriplet[] ParseKeys(byte[] r)
    {
        var d = Data(r);
        var keys = new KeyTriplet[d.Length / 3];
        for (int i = 0; i < keys.Length; i++) keys[i] = new KeyTriplet(d[i * 3], d[i * 3 + 1], d[i * 3 + 2]);
        return keys;
    }
}

public enum SettingId
{
    Dpi,
    PollRate,
    MotionSync,
    GameMode,
    SleepEnabled,
    SleepMinutes,
    Lod,
    SensorAngle,
    Ripple,
    DynEnabled,
    DynMode,
    DynSpeedReporting,
}

public sealed record FirmwareVersions(int Mouse, int Receiver, int ReceiverNxp)
{
    public FirmwareRelease? Release =>
        M001.Releases.FirstOrDefault(r => r.Mouse == Mouse && r.Receiver == Receiver && r.ReceiverNxp == ReceiverNxp);
}

public sealed record FirmwareRelease(string Version, string? Date, int Mouse, int Receiver, int ReceiverNxp);
