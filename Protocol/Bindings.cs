namespace WallhackTerminal.Protocol;

public readonly record struct KeyTriplet(byte Type, byte CodeL, byte CodeH)
{
    public override string ToString() => $"{Type:X2} {CodeL:X2} {CodeH:X2}";
}

public sealed record BindingChoice(string Label, KeyTriplet Value);

public static class Bindings
{
    public const byte TypeMouse = 16, TypeMouseLegacy = 1, TypeKey = 32, TypeKeyLegacy = 7, TypeDpi = 19, TypeDpiLegacy = 4,
        TypeSystem = 64, TypeMedia = 48, TypeRapidFire = 20, TypeMacro = 112, TypeMacroLegacy = 11;

    public static readonly string[] SlotNames = ["Left click", "Right click", "Middle click", "Back", "Forward"];

    static readonly byte[] ButtonMasks = [1, 2, 4, 8, 16];
    static readonly string[] ButtonLabels = ["LEFT CLICK", "RIGHT CLICK", "MIDDLE CLICK", "BACK", "FORWARD"];
    static readonly string[] LegacyButtonOrder = ["LEFT CLICK", "MIDDLE CLICK", "RIGHT CLICK", "BACK", "FORWARD"];

    public static readonly KeyTriplet Disabled = new(0, 0, 0);
    public static KeyTriplet MouseButton(int slot) => new(TypeMouse, ButtonMasks[slot], 0);
    public static KeyTriplet Key(byte modifiers, byte usage) => new(TypeKey, modifiers, usage);
    public static KeyTriplet Macro(int index) => new(TypeMacro, (byte)index, 0);

    static readonly (string Label, byte Code)[] DpiOps = [("DPI +", 1), ("DPI −", 2), ("DPI CYCLE", 3)];
    static readonly (string Label, byte Code)[] SystemOps = [("POWER", 1), ("SLEEP", 2), ("WAKE", 4)];
    static readonly (string Label, byte Code)[] MediaOps =
    [
        ("PLAY / PAUSE", 205), ("NEXT TRACK", 181), ("PREVIOUS TRACK", 182), ("STOP MEDIA", 183),
        ("MUTE", 226), ("VOLUME UP", 233), ("VOLUME DOWN", 234),
    ];
    static readonly string[] MacroStopModes = ["", "FINISH ON RELEASE", "TOGGLE", "STOP ON RELEASE"];

    public static IReadOnlyList<(string Category, IReadOnlyList<BindingChoice> Choices)> Catalog(int slot)
    {
        var mouse = new List<BindingChoice>
        {
            new("DEFAULT", MouseButton(slot)),
            new("DISABLED", Disabled),
        };
        for (int i = 0; i < ButtonMasks.Length; i++) mouse.Add(new(ButtonLabels[i], MouseButton(i)));
        mouse.Add(new("RAPID FIRE (5 × 100 MS)", new KeyTriplet(TypeRapidFire, 100, 5)));

        return
        [
            ("MOUSE BUTTONS", mouse),
            ("DPI", DpiOps.Select(o => new BindingChoice(o.Label, new KeyTriplet(TypeDpi, o.Code, 0))).ToList()),
            ("KEYBOARD", [new BindingChoice("PRESS A KEY COMBINATION…", default)]),
            ("MEDIA PLAYER", MediaOps.Select(o => new BindingChoice(o.Label, new KeyTriplet(TypeMedia, o.Code, 0))).ToList()),
            ("SYSTEM", SystemOps.Select(o => new BindingChoice(o.Label, new KeyTriplet(TypeSystem, o.Code, 0))).ToList()),
            ("MACROS", Enumerable.Range(0, Macros.Slots).Select(i => new BindingChoice($"MACRO {i + 1}", Macro(i))).ToList()),
        ];
    }

    public static string Describe(KeyTriplet t)
    {
        if (t.Type == 0 && t.CodeL == 0 && t.CodeH == 0) return "DISABLED";
        switch (t.Type)
        {
            case TypeMouse:
                int slot = Array.IndexOf(ButtonMasks, t.CodeL);
                if (slot >= 0) return ButtonLabels[slot];
                break;
            case TypeMouseLegacy:
                if (t.CodeL is >= 1 and <= 5) return LegacyButtonOrder[t.CodeL - 1];
                break;
            case TypeKey:
            case TypeKeyLegacy:
                return "KEY " + HidKeys.DescribeCombo(t.CodeL, t.CodeH);
            case TypeDpi:
            case TypeDpiLegacy:
                foreach (var o in DpiOps) if (o.Code == t.CodeL) return o.Label;
                break;
            case TypeSystem:
                foreach (var o in SystemOps) if (o.Code == t.CodeL) return o.Label;
                break;
            case TypeMedia:
                foreach (var o in MediaOps) if (o.Code == t.CodeL) return o.Label;
                break;
            case TypeRapidFire:
                if (t.CodeH > 0) return $"RAPID FIRE ({t.CodeH} × {t.CodeL} MS)";
                break;
            case TypeMacro:
            case TypeMacroLegacy:
                string mode = t.CodeH < MacroStopModes.Length ? MacroStopModes[t.CodeH] : "";
                return $"MACRO {t.CodeL + 1}" + (mode.Length > 0 ? $" ({mode})" : "");
        }
        return $"UNKNOWN ({t})";
    }
}
