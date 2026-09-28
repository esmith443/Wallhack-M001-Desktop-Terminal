using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WallhackTerminal.UI;

public sealed unsafe class RawMouse
{
    const uint RID_INPUT = 0x10000003, RIDI_DEVICENAME = 0x20000007, RIDEV_INPUTSINK = 0x100, RIDEV_REMOVE = 0x1;
    const uint RIM_TYPEMOUSE = 0;
    public const int WM_INPUT = 0x00FF;

    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTDEVICE
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public IntPtr Target;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);

    [DllImport("user32.dll")]
    static extern uint GetRawInputData(IntPtr rawInput, uint command, void* data, ref uint size, uint headerSize);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, char* data, ref uint size);

    readonly Dictionary<IntPtr, bool> _isM001 = new();
    bool _seenM001;
    byte[] _buffer = new byte[64];
    bool _registered;

    public bool Left, Right, Middle, Back, Forward;
    public long Moves;
    public readonly Queue<string> Events = new();

    public void Register(IntPtr hwnd)
    {
        if (_registered) return;
        var device = new RAWINPUTDEVICE { UsagePage = 0x01, Usage = 0x02, Flags = RIDEV_INPUTSINK, Target = hwnd };
        _registered = RegisterRawInputDevices([device], 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
    }

    public void Unregister()
    {
        if (!_registered) return;
        var device = new RAWINPUTDEVICE { UsagePage = 0x01, Usage = 0x02, Flags = RIDEV_REMOVE, Target = IntPtr.Zero };
        RegisterRawInputDevices([device], 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        _registered = false;
        Left = Right = Middle = Back = Forward = false;
    }

    public void Process(IntPtr rawInput)
    {
        uint headerSize = (uint)(8 + 2 * IntPtr.Size);
        uint size = 0;
        GetRawInputData(rawInput, RID_INPUT, null, ref size, headerSize);
        if (size == 0) return;
        if (size > _buffer.Length) _buffer = new byte[size];
        fixed (byte* p = _buffer)
        {
            if (GetRawInputData(rawInput, RID_INPUT, p, ref size, headerSize) == uint.MaxValue) return;
            if (*(uint*)p != RIM_TYPEMOUSE) return;
            if (!Accept(*(IntPtr*)(p + 8))) return;

            byte* mouse = p + headerSize;
            ushort flags = *(ushort*)(mouse + 4);
            short wheel = *(short*)(mouse + 6);
            int dx = *(int*)(mouse + 12), dy = *(int*)(mouse + 16);
            if (dx != 0 || dy != 0) Moves++;
            if (flags == 0) return;

            Button(flags, 0x0001, 0x0002, ref Left, "LMB CLICK");
            Button(flags, 0x0004, 0x0008, ref Right, "RMB CLICK");
            Button(flags, 0x0010, 0x0020, ref Middle, "MMB CLICK");
            Button(flags, 0x0040, 0x0080, ref Back, "MB4 CLICK");
            Button(flags, 0x0100, 0x0200, ref Forward, "MB5 CLICK");
            if ((flags & 0x0400) != 0) Enqueue(wheel > 0 ? "WHEEL UP" : "WHEEL DOWN");
        }
    }

    void Button(ushort flags, ushort down, ushort up, ref bool state, string label)
    {
        if ((flags & down) != 0)
        {
            state = true;
            Enqueue(label);
        }
        if ((flags & up) != 0) state = false;
    }

    void Enqueue(string text)
    {
        if (Events.Count < 64) Events.Enqueue(text);
    }

    bool Accept(IntPtr device)
    {
        if (!_isM001.TryGetValue(device, out bool match))
        {
            match = DeviceName(device)?.Contains("VID_3879", StringComparison.OrdinalIgnoreCase) == true;
            _isM001[device] = match;
            _seenM001 |= match;
        }
        return match || !_seenM001;
    }

    static string? DeviceName(IntPtr device)
    {
        if (device == IntPtr.Zero) return null;
        uint length = 0;
        GetRawInputDeviceInfo(device, RIDI_DEVICENAME, null, ref length);
        if (length == 0 || length > 1024) return null;
        char* name = stackalloc char[(int)length + 1];
        if (GetRawInputDeviceInfo(device, RIDI_DEVICENAME, name, ref length) == uint.MaxValue) return null;
        name[length] = '\0';
        return new string(name);
    }
}

public sealed class LoggerView : Grid
{
    const int MaxLines = 15;
    readonly List<(TextBlock Stamp, TextBlock Text)> _rows = [];
    readonly InputPad _pad = new();

    public LoggerView()
    {
        Margin = new Thickness(16, 12, 16, 10);
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var lines = new StackPanel { ClipToBounds = true };
        for (int i = 0; i < MaxLines; i++)
        {
            var stamp = Term.Text("");
            var text = Term.Text("");
            text.HorizontalAlignment = HorizontalAlignment.Right;
            text.TextTrimming = TextTrimming.CharacterEllipsis;
            text.Margin = new Thickness(Term.Cells(1), 0, 0, 0);
            var row = new DockPanel { Height = Term.Line, LastChildFill = true };
            DockPanel.SetDock(stamp, Dock.Left);
            row.Children.Add(stamp);
            row.Children.Add(text);
            lines.Children.Add(row);
            _rows.Add((stamp, text));
        }
        Children.Add(lines);
        Grid.SetColumn(_pad, 2);
        Children.Add(_pad);
    }

    public void Add(string text, Brush? brush = null, string? prefix = null)
    {
        for (int i = _rows.Count - 1; i > 0; i--)
        {
            _rows[i].Stamp.Text = _rows[i - 1].Stamp.Text;
            _rows[i].Text.Text = _rows[i - 1].Text.Text;
            _rows[i].Text.Foreground = _rows[i - 1].Text.Foreground;
        }
        _rows[0].Stamp.Text = $"{prefix ?? "M001"}_{DateTime.Now:HH:mm:ss} >";
        _rows[0].Text.Text = text.ToUpperInvariant();
        _rows[0].Text.Foreground = brush ?? Term.Fg;
    }

    public void Clear()
    {
        foreach (var (stamp, text) in _rows) stamp.Text = text.Text = "";
    }

    public Point PadPosition
    {
        set => _pad.Position = value;
    }
}
