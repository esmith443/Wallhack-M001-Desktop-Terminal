using System.Windows.Input;

namespace WallhackTerminal.Protocol;

public static class HidKeys
{
    public const byte LeftCtrl = 0xE0, LeftShift = 0xE1, LeftAlt = 0xE2, LeftWin = 0xE3,
        RightCtrl = 0xE4, RightShift = 0xE5, RightAlt = 0xE6, RightWin = 0xE7;

    static readonly Dictionary<Key, byte> FromKey = new();
    static readonly Dictionary<byte, string> Names = new();

    static HidKeys()
    {
        for (int i = 0; i < 26; i++) Add(Key.A + i, (byte)(0x04 + i), ((char)('A' + i)).ToString());
        for (int i = 1; i <= 9; i++) Add(Key.D0 + i, (byte)(0x1E + i - 1), i.ToString());
        Add(Key.D0, 0x27, "0");
        Add(Key.Enter, 0x28, "ENTER");
        Add(Key.Escape, 0x29, "ESC");
        Add(Key.Back, 0x2A, "BACKSPACE");
        Add(Key.Tab, 0x2B, "TAB");
        Add(Key.Space, 0x2C, "SPACE");
        Add(Key.OemMinus, 0x2D, "-");
        Add(Key.OemPlus, 0x2E, "=");
        Add(Key.OemOpenBrackets, 0x2F, "[");
        Add(Key.OemCloseBrackets, 0x30, "]");
        Add(Key.OemPipe, 0x31, "\\");
        Add(Key.OemSemicolon, 0x33, ";");
        Add(Key.OemQuotes, 0x34, "'");
        Add(Key.OemTilde, 0x35, "`");
        Add(Key.OemComma, 0x36, ",");
        Add(Key.OemPeriod, 0x37, ".");
        Add(Key.OemQuestion, 0x38, "/");
        Add(Key.CapsLock, 0x39, "CAPS LOCK");
        for (int i = 0; i < 12; i++) Add(Key.F1 + i, (byte)(0x3A + i), $"F{i + 1}");
        Add(Key.PrintScreen, 0x46, "PRINT SCREEN");
        Add(Key.Scroll, 0x47, "SCROLL LOCK");
        Add(Key.Pause, 0x48, "PAUSE");
        Add(Key.Insert, 0x49, "INSERT");
        Add(Key.Home, 0x4A, "HOME");
        Add(Key.PageUp, 0x4B, "PAGE UP");
        Add(Key.Delete, 0x4C, "DELETE");
        Add(Key.End, 0x4D, "END");
        Add(Key.PageDown, 0x4E, "PAGE DOWN");
        Add(Key.Right, 0x4F, "RIGHT");
        Add(Key.Left, 0x50, "LEFT");
        Add(Key.Down, 0x51, "DOWN");
        Add(Key.Up, 0x52, "UP");
        Add(Key.NumLock, 0x53, "NUM LOCK");
        Add(Key.Divide, 0x54, "NUM /");
        Add(Key.Multiply, 0x55, "NUM *");
        Add(Key.Subtract, 0x56, "NUM -");
        Add(Key.Add, 0x57, "NUM +");
        Names[0x58] = "NUM ENTER";
        for (int i = 1; i <= 9; i++) Add(Key.NumPad0 + i, (byte)(0x59 + i - 1), $"NUM {i}");
        Add(Key.NumPad0, 0x62, "NUM 0");
        Add(Key.Decimal, 0x63, "NUM .");
        Add(Key.OemBackslash, 0x64, "\\ (ISO)");
        Add(Key.Apps, 0x65, "MENU");
        for (int i = 0; i < 12; i++) Add(Key.F13 + i, (byte)(0x68 + i), $"F{i + 13}");
        Add(Key.LeftCtrl, LeftCtrl, "L-CTRL");
        Add(Key.LeftShift, LeftShift, "L-SHIFT");
        Add(Key.LeftAlt, LeftAlt, "L-ALT");
        Add(Key.LWin, LeftWin, "L-WIN");
        Add(Key.RightCtrl, RightCtrl, "R-CTRL");
        Add(Key.RightShift, RightShift, "R-SHIFT");
        Add(Key.RightAlt, RightAlt, "R-ALT");
        Add(Key.RWin, RightWin, "R-WIN");
    }

    static void Add(Key key, byte usage, string name)
    {
        FromKey[key] = usage;
        Names[usage] = name;
    }

    public static Key RealKey(KeyEventArgs e) => e.Key switch
    {
        Key.System => e.SystemKey,
        Key.ImeProcessed => e.ImeProcessedKey,
        Key.DeadCharProcessed => e.DeadCharProcessedKey,
        _ => e.Key,
    };

    public static byte? Usage(Key key) => FromKey.TryGetValue(key, out var usage) ? usage : null;

    public static bool IsModifier(byte usage) => usage is >= LeftCtrl and <= RightWin;

    public static string Name(byte usage) => Names.TryGetValue(usage, out var name) ? name : $"0x{usage:X2}";

    public static byte CurrentModifiers()
    {
        byte mask = 0;
        if (Keyboard.IsKeyDown(Key.LeftCtrl)) mask |= 1;
        if (Keyboard.IsKeyDown(Key.LeftShift)) mask |= 2;
        if (Keyboard.IsKeyDown(Key.LeftAlt)) mask |= 4;
        if (Keyboard.IsKeyDown(Key.LWin)) mask |= 8;
        if (Keyboard.IsKeyDown(Key.RightCtrl)) mask |= 16;
        if (Keyboard.IsKeyDown(Key.RightShift)) mask |= 32;
        if (Keyboard.IsKeyDown(Key.RightAlt)) mask |= 64;
        if (Keyboard.IsKeyDown(Key.RWin)) mask |= 128;
        return mask;
    }

    public static string DescribeCombo(byte modifiers, byte usage)
    {
        var parts = new List<string>();
        for (int bit = 0; bit < 8; bit++)
        {
            byte modUsage = (byte)(LeftCtrl + bit);
            if ((modifiers & (1 << bit)) != 0 && modUsage != usage) parts.Add(Name(modUsage));
        }
        if (usage != 0) parts.Add(Name(usage));
        return parts.Count == 0 ? "NONE" : string.Join("+", parts);
    }
}
