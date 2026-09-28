namespace WallhackTerminal.Protocol;

public enum MacroEvent { KeyDown, KeyUp, ButtonDown, ButtonUp, Wheel, WheelReset, MoveX, MoveY }

public sealed record MacroStep(int DelayMs, MacroEvent Event, int Code)
{
    public string Describe() => Event switch
    {
        MacroEvent.KeyDown => $"KEY {HidKeys.Name((byte)Code)} ↓",
        MacroEvent.KeyUp => $"KEY {HidKeys.Name((byte)Code)} ↑",
        MacroEvent.ButtonDown => $"{Macros.ButtonName(Code)} ↓",
        MacroEvent.ButtonUp => $"{Macros.ButtonName(Code)} ↑",
        MacroEvent.Wheel => Code > 0 ? "WHEEL UP" : "WHEEL DOWN",
        MacroEvent.WheelReset => "WHEEL RELEASE",
        MacroEvent.MoveX => $"MOVE X {Code:+#;-#;0}",
        MacroEvent.MoveY => $"MOVE Y {Code:+#;-#;0}",
        _ => "?",
    };
}

public static class Macros
{
    public const int Slots = 4;
    public const int MaxSteps = 30;
    public const int IndexOffset = 16;
    public const int IndexLength = Slots * 2;
    public const int AreaStart = 32;
    public const int AreaEnd = AreaStart + Slots * 128;
    public const int Chunk = 24;

    const int CodeButton = 1, CodeWheel = 3, CodeX = 4, CodeY = 5, CodeModifier = 9, CodeKey = 10;
    const int FirstModifier = 0xE0, LastModifier = 0xE7;

    static readonly (int Mask, string Name)[] Buttons = [(1, "LMB"), (2, "RMB"), (4, "MMB"), (8, "BACK"), (16, "FORWARD")];

    public static string ButtonName(int mask) => Buttons.FirstOrDefault(b => b.Mask == mask).Name ?? $"BUTTON {mask}";

    public static bool IsValidAddress(int address) => address >= AreaStart && address + 4 <= AreaEnd;
    public static bool Fits(int address, int length) => length >= 4 && IsValidAddress(address) && address + length <= AreaEnd;

    public static byte[] Encode(IReadOnlyList<MacroStep> steps)
    {
        if (steps.Count > MaxSteps) throw new InvalidOperationException($"Macro has {steps.Count} steps; a slot holds {MaxSteps}");
        var e = new byte[4 + steps.Count * 4];
        e[0] = (byte)steps.Count;
        e[1] = (byte)(steps.Count >> 8);
        for (int i = 0; i < steps.Count; i++)
        {
            var s = steps[i];
            int r = 4 + i * 4;
            int delay = Math.Clamp(s.DelayMs, 0, 65535);
            e[r] = (byte)delay;
            e[r + 1] = (byte)(delay >> 8);
            switch (s.Event)
            {
                case MacroEvent.KeyDown:
                case MacroEvent.KeyUp:
                    bool modifier = s.Code is >= FirstModifier and <= LastModifier;
                    e[r + 2] = (byte)((modifier ? CodeModifier : CodeKey) | (s.Event == MacroEvent.KeyDown ? 0x80 : 0));
                    e[r + 3] = (byte)(modifier ? 1 << (s.Code - FirstModifier) : s.Code);
                    break;
                case MacroEvent.ButtonDown:
                case MacroEvent.ButtonUp:
                    e[r + 2] = (byte)(CodeButton | (s.Event == MacroEvent.ButtonDown ? 0x80 : 0));
                    e[r + 3] = (byte)s.Code;
                    break;
                case MacroEvent.Wheel:
                case MacroEvent.WheelReset:
                    e[r + 2] = (byte)(CodeWheel | (s.Event == MacroEvent.Wheel ? 0x80 : 0));
                    e[r + 3] = (byte)(s.Code > 0 ? 1 : 255);
                    break;
                case MacroEvent.MoveX:
                case MacroEvent.MoveY:
                    if (s.Code == 0 || Math.Abs(s.Code) > 255) throw new InvalidOperationException("Macro movement must be -255..255 and non-zero");
                    e[r + 2] = (byte)((s.Event == MacroEvent.MoveX ? CodeX : CodeY) | (s.Code < 0 ? 0x80 : 0));
                    e[r + 3] = (byte)Math.Abs(s.Code);
                    break;
            }
        }
        return e;
    }

    public static List<MacroStep>? Decode(byte[] t)
    {
        if (t.Length < 4) return null;
        int count = t[0] | (t[1] << 8);
        var steps = new List<MacroStep>();
        for (int a = 4; a + 4 <= t.Length && steps.Count < count; a += 4)
        {
            int delay = t[a] | (t[a + 1] << 8);
            int type = t[a + 2] & 0x7F;
            bool flag = (t[a + 2] & 0x80) != 0;
            int code = t[a + 3];
            switch (type)
            {
                case CodeKey:
                    steps.Add(new(delay, flag ? MacroEvent.KeyDown : MacroEvent.KeyUp, code));
                    break;
                case CodeModifier:
                    if (code == 0 || (code & (code - 1)) != 0) continue;
                    steps.Add(new(delay, flag ? MacroEvent.KeyDown : MacroEvent.KeyUp, FirstModifier + (int)Math.Log2(code)));
                    break;
                case CodeButton:
                    if (Buttons.Any(b => b.Mask == code))
                        steps.Add(new(delay, flag ? MacroEvent.ButtonDown : MacroEvent.ButtonUp, code));
                    break;
                case CodeWheel:
                    steps.Add(new(delay, flag ? MacroEvent.Wheel : MacroEvent.WheelReset, code == 1 ? 1 : -1));
                    break;
                case CodeX:
                case CodeY:
                    if (code != 0) steps.Add(new(delay, type == CodeX ? MacroEvent.MoveX : MacroEvent.MoveY, flag ? -code : code));
                    break;
            }
        }
        return steps;
    }

    public sealed record Allocation(int Slot, int Address, int Length);

    public static List<(int Offset, byte[] Bytes)> PlanWrite(int slot, IReadOnlyList<MacroStep> steps, byte[] index,
        IReadOnlyList<Allocation> allocations)
    {
        var writes = new List<(int, byte[])>();
        var bySlot = allocations.ToDictionary(a => a.Slot, a => a.Address);

        for (int p = 0; p < Slots; p++)
        {
            if (p == slot || index.Length < (p + 1) * 2) continue;
            int address = index[p * 2] | (index[p * 2 + 1] << 8);
            bool empty = address == 0 || address == 0xFFFF;
            bool stale = !bySlot.TryGetValue(p, out int known) || known != address;
            if (!empty && stale) writes.Add((IndexOffset + p * 2, [0, 0]));
        }

        var others = allocations.Where(a => a.Slot != slot).ToList();
        int? current = allocations.FirstOrDefault(a => a.Slot == slot)?.Address;
        var content = Encode(steps);
        int target = steps.Count > 0 ? Allocate(content.Length, others, current) : 0;

        writes.Add((IndexOffset + slot * 2, [(byte)target, (byte)(target >> 8)]));
        if (steps.Count == 0) return writes;
        for (int h = 0; h < content.Length; h += Chunk)
            writes.Add((target + h, content.AsSpan(h, Math.Min(Chunk, content.Length - h)).ToArray()));
        return writes;
    }

    static int Allocate(int length, List<Allocation> others, int? preferred)
    {
        if (!Fits(AreaStart, length)) throw new InvalidOperationException($"Macro content length {length} exceeds storage");
        bool Overlaps(int u) => others.Any(d => u < d.Address + d.Length && d.Address < u + length);
        if (preferred is int p && Fits(p, length) && !Overlaps(p)) return p;

        int next = AreaStart;
        foreach (var used in others.OrderBy(a => a.Address))
        {
            if (next + length <= used.Address) return next;
            next = Math.Max(next, (used.Address + used.Length + 15) & ~15);
        }
        if (Fits(next, length)) return next;
        throw new InvalidOperationException("No free macro storage range is available");
    }
}
