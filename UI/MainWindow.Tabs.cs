using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using WallhackTerminal.Protocol;

namespace WallhackTerminal.UI;

public sealed partial class MainWindow
{
    static readonly Dictionary<string, (string Title, string Text)> Help = new()
    {
        ["custom-dpi"] = ("Custom DPI",
            "Sensitivity in dots per inch, 100–30000 in steps of 50. Applies when the receiver's selector is on the red DPI symbol. Enter or click the value to type a number; Shift+←/→ jumps 500."),
        ["custom-polling-rate"] = ("Custom polling rate",
            "How often the mouse reports to the PC. Applies when the receiver's selector is on the red HZ symbol. Higher rates lower latency but use more battery."),
        ["motion-sync"] = ("Motion sync",
            "Lines sensor readings up with the polling interval for more even cursor motion."),
        ["sensor-scanning-mode"] = ("Sensor scanning mode",
            "ACCEL scans the sensor faster for more reliable tracking at high speed, at the cost of battery life. HIGH is the standard mode."),
        ["auto-sleep"] = ("Auto sleep",
            "When on, the mouse sleeps after the idle time below. When off it never sleeps and is always instantly ready."),
        ["sleep-after"] = ("Sleep after",
            "Minutes without movement before the mouse goes to sleep to save battery."),
        ["lod"] = ("Lift-off distance",
            "Height at which the sensor stops tracking when you lift the mouse. Lower values mean less drift while repositioning; the right value depends on your pad."),
        ["sensor-rotation"] = ("Sensor rotation",
            "Rotates the sensor axes to compensate for an angled grip, so a straight hand motion draws a straight line. −30° to +30°. The mouse on the right turns with it."),
        ["dpi-acceleration"] = ("DPI acceleration",
            "Computed on the mouse: slow, precise moves keep your sensitivity while fast flicks get more. Pick a preset curve or shape your own."),
        ["curve-point"] = ("Custom curve point",
            "Speed is in counts per millisecond (0–280, strictly increasing). Gain multiplies sensitivity (0.10–6.00). Press APPLY CURVE to save it to the mouse."),
        ["speed-readout"] = ("Live speed readout",
            "Makes the mouse stream its current speed, drawn as a red line on the curve. Turn it off when you are done tuning."),
        ["buttons"] = ("Button mapping",
            "Enter opens the assignment list: mouse buttons, DPI controls, keyboard keys and combos, media keys, system actions or one of the on-board macros."),
        ["macros"] = ("Macros",
            "Up to four macros live on the mouse, 30 steps each. Record key and button sequences with their timing, then bind a macro to any button."),
        ["battery"] = ("Battery",
            "Charge of the mouse and dock batteries, read from the receiver every few seconds. Rate, time left and the graph are worked out from the readings this PC has collected, so they sharpen over time. The mouse reports its own level; it can sit at 100% for a while before the first drop."),
        ["app"] = ("Desktop app",
            "Settings for this app. They are stored on this PC, not on the mouse."),
        ["preset-label"] = ("Receiver dials",
            "The values printed on the receiver's dials, used for notifications and the tray. Rename a position only if your receiver is printed differently."),
    };

    CurveGraph? _curveGraph;
    List<CurvePoint>? _curveDraft;

    TermRow Row(string label, Func<string> value, Action<int>? step = null, Func<bool>? enabled = null, Action? activate = null,
        string? help = null, Func<string, bool>? commit = null, Func<string>? editText = null, Func<string>? dynamicLabel = null,
        bool requiresMouse = true, double widthCh = 56, double valueCh = 16)
    {
        var (title, text) = help is not null && Help.TryGetValue(help, out var h) ? h : (null, null);
        var row = new TermRow(label, widthCh, valueCh, arrows: step is not null)
        {
            Label = dynamicLabel,
            Value = value,
            Step = step,
            Activate = activate,
            Commit = commit,
            EditText = editText,
            Enabled = () => (!requiresMouse || S.CanConfigure) && (enabled?.Invoke() ?? true),
            HelpTitle = title,
            HelpText = text,
        };
        _refreshers.Add(row.Refresh);
        return row;
    }

    string OnOff(SettingId id) => S.Flag(id) switch { true => "ON", false => "OFF", null => "--" };

    Action<int> Toggle(SettingId id) => _ =>
    {
        if (S.Flag(id) is bool on) _device.SetSetting(id, on ? 0 : 1);
    };

    Action<int> Range(SettingId id, int min, int max, int fallback) => delta =>
    {
        int current = S.Get(id) ?? fallback;
        int next = Math.Clamp(current + delta, min, max);
        if (next != current) _device.SetSetting(id, next);
    };

    FrameworkElement WithNote(TermRow row, string setting, Func<bool> visible)
    {
        var note = Term.Rich(("select ", Term.Dim), (setting, Term.Red), (" on receiver to enable", Term.Dim));
        note.Margin = new Thickness(Term.Cells(1.5), 0, 0, 0);
        note.VerticalAlignment = VerticalAlignment.Center;
        _refreshers.Add(() => note.Visibility = visible() ? Visibility.Visible : Visibility.Collapsed);
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(row);
        line.Children.Add(note);
        return line;
    }

    static TextBlock Heading(string text) => Term.Text(text.ToUpperInvariant(), Term.Dim);

    FrameworkElement BuildPerformance()
    {
        var panel = new StackPanel();
        var dpi = Row("Custom DPI", () => S.Get(SettingId.Dpi) is int v ? $"{v} dpi" : "----",
            step: d => SetDpi((S.Get(SettingId.Dpi) ?? 1600) + d * M001.DpiStep),
            enabled: () => S.IsDpiCustom, help: "custom-dpi",
            commit: text => int.TryParse(new string(text.Where(char.IsDigit).ToArray()), out int v) && SetDpi(v),
            editText: () => S.Get(SettingId.Dpi)?.ToString() ?? "");
        panel.Children.Add(WithNote(dpi, "DPI", () => S.CanConfigure && !S.IsDpiCustom));

        var poll = Row("Custom polling rate", () => S.Get(SettingId.PollRate) is int hz ? $"{hz} hz" : "----",
            step: StepPollRate, enabled: () => S.IsPollRateCustom, help: "custom-polling-rate");
        panel.Children.Add(WithNote(poll, "HZ", () => S.CanConfigure && !S.IsPollRateCustom));

        panel.Children.Add(Term.Blank());
        panel.Children.Add(Row("Motion sync", () => OnOff(SettingId.MotionSync), Toggle(SettingId.MotionSync), help: "motion-sync"));
        panel.Children.Add(Row("Sensor scanning mode",
            () => S.Flag(SettingId.GameMode) switch { true => "ACCEL", false => "HIGH", null => "--" },
            _ => ToggleScanningMode(), help: "sensor-scanning-mode"));

        panel.Children.Add(Term.Blank());
        var receiver = Term.Text("", Term.Dim);
        _refreshers.Add(() => receiver.Text = S.ReceiverConnected
            ? $"RECEIVER DIALS   {Services.Presets.Dpi(S, _settings)}   ·   {Services.Presets.Poll(S, _settings)}"
            : "");
        panel.Children.Add(receiver);
        return panel;
    }

    bool SetDpi(int value)
    {
        value = Math.Clamp((int)Math.Round(value / (double)M001.DpiStep) * M001.DpiStep, M001.DpiMin, M001.DpiMax);
        if (value != S.Get(SettingId.Dpi)) _device.SetSetting(SettingId.Dpi, value);
        return true;
    }

    void StepPollRate(int delta)
    {
        int current = Array.IndexOf(M001.PollRates, S.Get(SettingId.PollRate) ?? 1000);
        if (current < 0) current = 3;
        int next = Math.Clamp(current + Math.Sign(delta) * (Math.Abs(delta) >= 10 ? 4 : 1), 0, M001.PollRates.Length - 1);
        if (next != current) _device.SetSetting(SettingId.PollRate, M001.PollRates[next]);
    }

    async void ToggleScanningMode()
    {
        if (S.Flag(SettingId.GameMode) is not bool accel) return;
        if (!accel && !await ConfirmAsync("Sensor scanning mode", "Enabling ACCEL results in higher battery consumption.")) return;
        _device.SetSetting(SettingId.GameMode, accel ? 0 : 1);
    }

    FrameworkElement BuildPower()
    {
        var panel = new StackPanel();
        panel.Children.Add(BuildBatterySection());
        panel.Children.Add(Term.Blank());
        panel.Children.Add(Heading("Sleep"));
        panel.Children.Add(Row("Auto sleep", () => OnOff(SettingId.SleepEnabled), Toggle(SettingId.SleepEnabled), help: "auto-sleep"));
        panel.Children.Add(Row("Sleep after", () => S.Get(SettingId.SleepMinutes) is int m ? $"{m} min" : "--",
            Range(SettingId.SleepMinutes, M001.SleepMinMinutes, M001.SleepMaxMinutes, 5),
            enabled: () => S.Flag(SettingId.SleepEnabled) == true, help: "sleep-after"));
        return panel;
    }

    int AccelerationIndex => S.Flag(SettingId.DynEnabled) == true ? (S.Get(SettingId.DynMode) ?? 0) + 1 : 0;
    bool EditingCustomCurve => S.CanConfigure && AccelerationIndex == Curves.CustomMode + 1;

    FrameworkElement BuildCalibration()
    {
        _structureKey = () => $"{S.CanConfigure}|{AccelerationIndex}";
        var panel = new StackPanel();
        panel.Children.Add(Row("Lift-off distance",
            () => S.Get(SettingId.Lod) is int i ? $"{M001.LodMillimetres[i]:0.#} mm" : "--",
            d =>
            {
                int current = S.Get(SettingId.Lod) ?? 1;
                int next = Math.Clamp(current + Math.Sign(d), 0, M001.LodMillimetres.Length - 1);
                if (next != current) _device.SetSetting(SettingId.Lod, next);
            }, help: "lod"));
        panel.Children.Add(Row("Sensor rotation", () => S.Get(SettingId.SensorAngle) is int a ? $"{a}°" : "--",
            Range(SettingId.SensorAngle, M001.AngleMin, M001.AngleMax, 0), help: "sensor-rotation"));
        panel.Children.Add(Term.Blank());

        panel.Children.Add(Row("DPI Acceleration",
            () => AccelerationIndex == 0 ? "OFF" : Curves.ModeNames[AccelerationIndex - 1],
            StepAcceleration, enabled: () => S.Flag(SettingId.DynEnabled) is not null && !S.CurvesUnsupported,
            help: "dpi-acceleration"));

        _curveGraph = new CurveGraph { Width = Term.Cells(56), Height = 170, Margin = new Thickness(0, 8, 0, 4), HorizontalAlignment = HorizontalAlignment.Left };
        _curveGraph.LiveSpeed = S.Flag(SettingId.DynSpeedReporting) == true ? _liveSpeed : null;
        _refreshers.Add(() =>
        {
            int index = AccelerationIndex;
            var curve = EditingCustomCurve && _curveDraft is not null ? new Curve(_curveDraft)
                : S.CurveFor(index == 0 ? 0 : index - 1);
            _curveGraph?.Show(curve, dimmed: index == 0 || !S.CanConfigure);
        });
        panel.Children.Add(_curveGraph);
        var axis = Term.Text("SPEED (COUNTS/MS) →   GAIN ↑", Term.Dim40);
        panel.Children.Add(axis);

        if (EditingCustomCurve) panel.Children.Add(BuildCurveEditor());
        else _curveDraft = null;

        panel.Children.Add(Term.Blank());
        panel.Children.Add(Row("Live speed readout", () => OnOff(SettingId.DynSpeedReporting),
            d =>
            {
                Toggle(SettingId.DynSpeedReporting)(d);
                if (_curveGraph is not null) _curveGraph.LiveSpeed = null;
            }, help: "speed-readout"));
        if (S.CurvesUnsupported) panel.Children.Add(Term.Text("THIS MOUSE FIRMWARE HAS NO ACCELERATION CURVES.", Term.Dim));
        return panel;
    }

    void StepAcceleration(int delta)
    {
        const int options = 5;
        int next = ((AccelerationIndex + Math.Sign(delta)) % options + options) % options;
        if (next == 0)
        {
            _device.SetSetting(SettingId.DynEnabled, 0);
            return;
        }
        _device.SetSetting(SettingId.DynMode, next - 1);
        if (S.Flag(SettingId.DynEnabled) != true) _device.SetSetting(SettingId.DynEnabled, 1);
    }

    FrameworkElement BuildCurveEditor()
    {
        _curveDraft ??= S.CurveFor(Curves.CustomMode).Points.ToList();
        var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(Heading("Custom curve"));

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Term.Cells(2)) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int i = 0; i < Curves.PointCount; i++)
        {
            int point = i;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var speedRow = Row($"P{point + 1} speed", () => _curveDraft is { } d ? d[point].Speed.ToString() : "--",
                delta => EditPoint(point, speed: delta), help: "curve-point", widthCh: 27, valueCh: 9);
            var gainRow = Row($"P{point + 1} gain", () => _curveDraft is { } d ? $"×{d[point].Gain:0.00}" : "--",
                delta => EditPoint(point, gain: delta), help: "curve-point", widthCh: 27, valueCh: 9);
            Grid.SetRow(speedRow, point);
            Grid.SetRow(gainRow, point);
            Grid.SetColumn(gainRow, 2);
            grid.Children.Add(speedRow);
            grid.Children.Add(gainRow);
        }
        panel.Children.Add(grid);
        panel.Children.Add(Term.Blank());

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(new TermButton("Apply curve", ApplyCurve, framed: true));
        buttons.Children.Add(new Border { Width = Term.Cells(2) });
        buttons.Children.Add(new TermButton("Reset", () =>
        {
            _curveDraft = S.CurveFor(Curves.CustomMode).Points.ToList();
            foreach (var refresh in _refreshers) refresh();
        }, framed: true));
        panel.Children.Add(buttons);
        return panel;
    }

    void EditPoint(int index, int speed = 0, int gain = 0)
    {
        if (_curveDraft is null) return;
        var p = _curveDraft[index];
        int newSpeed = Math.Clamp(p.Speed + speed, 0, Curves.MaxSpeed);
        double newGain = Math.Clamp(Math.Round(p.Gain + gain * 0.01, 2), Curves.MinGain, Curves.MaxGain);
        _curveDraft[index] = new CurvePoint(newSpeed, newGain);
        foreach (var refresh in _refreshers) refresh();
    }

    async void ApplyCurve()
    {
        if (_curveDraft is null) return;
        var curve = new Curve(_curveDraft.ToList());
        if (Curves.Validate(curve) is string error)
        {
            ShowStatus("CURVE NOT SAVED — " + error.ToUpperInvariant(), true);
            return;
        }
        await _device.WriteCustomCurveAsync(curve);
    }
}
