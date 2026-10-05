using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using WallhackTerminal.Protocol;

namespace WallhackTerminal.UI;

public sealed partial class MainWindow
{
    enum MappingView { List, Picker, MacroEditor }

    MappingView _mappingView;
    int _mappingSlot;
    int _pickerCategory;
    TextBlock? _captureHint;

    List<MacroStep> _macroDraft = [];
    int _macroDraftSlot = -1;
    bool _macroDirty, _recording;
    readonly Stopwatch _recordClock = new();
    long _lastRecordedMs;
    TermList? _stepList;
    TextBlock? _macroInfo;
    TermButton? _recordButton;
    TextBlock? _padText;

    FrameworkElement BuildMapping()
    {
        _structureKey = () => $"{_mappingView}|{_mappingSlot}";
        return _mappingView switch
        {
            MappingView.Picker => BuildPicker(),
            MappingView.MacroEditor => BuildMacroEditor(),
            _ => BuildMappingList(),
        };
    }

    FrameworkElement BuildMappingList()
    {
        var panel = new StackPanel();
        panel.Children.Add(Heading("Buttons"));
        for (int i = 0; i < M001.ButtonSlots; i++)
        {
            int slot = i;
            panel.Children.Add(Row(Bindings.SlotNames[slot],
                () => S.Buttons is { } b && slot < b.Length ? Bindings.Describe(b[slot]) : "----",
                activate: () => OpenSubView(MappingView.Picker, slot), help: "buttons", valueCh: 28));
        }
        panel.Children.Add(Term.Blank());
        panel.Children.Add(Heading("Macros"));
        for (int i = 0; i < Macros.Slots; i++)
        {
            int slot = i;
            panel.Children.Add(Row($"Macro {slot + 1}",
                () => _macroDirty && _macroDraftSlot == slot ? $"{_macroDraft.Count} steps · unsaved"
                    : S.Macros is null ? "----" : S.Macros[slot] is { Count: > 0 } m ? $"{m.Count} steps" : "empty",
                activate: () => OpenMacroEditor(slot), help: "macros", valueCh: 28));
        }
        return panel;
    }

    async void OpenMacroEditor(int slot)
    {
        if (_macroDirty && _macroDraftSlot != slot)
        {
            if (!await ConfirmAsync($"Discard macro {_macroDraftSlot + 1} changes",
                    "Those edits have not been saved to the mouse.", "Discard", "Keep")) return;
            _macroDraftSlot = -1;
            _macroDirty = false;
        }
        OpenSubView(MappingView.MacroEditor, slot);
    }

    void OpenSubView(MappingView view, int slot)
    {
        _mappingView = view;
        _mappingSlot = slot;
        if (view == MappingView.Picker) _pickerCategory = 0;
        RebuildTab(keepFocus: false);
    }

    bool CloseSubView()
    {
        if (_keyCapture is not null)
        {
            if (_recording) StopRecording();
            _keyCapture = null;
            if (_captureHint is not null) _captureHint.Text = "";
            return true;
        }
        if (_tab != "mapping" || _mappingView == MappingView.List) return false;
        int slot = _mappingSlot;
        var view = _mappingView;
        LeaveSubViews();
        RebuildTab(keepFocus: false);
        Dispatcher.BeginInvoke(() => FocusAt(2 + (view == MappingView.MacroEditor ? M001.ButtonSlots + slot : slot)),
            DispatcherPriority.Loaded);
        return true;
    }

    void LeaveSubViews()
    {
        if (_recording) StopRecording();
        _keyCapture = null;
        _mappingView = MappingView.List;
        if (!_macroDirty) _macroDraftSlot = -1;
    }

    FrameworkElement BuildPicker()
    {
        int slot = _mappingSlot;
        var catalog = Bindings.Catalog(slot);
        var panel = new StackPanel();
        var header = Term.Text("");
        _refreshers.Add(() =>
        {
            string current = S.Buttons is { } b && slot < b.Length ? Bindings.Describe(b[slot]) : "----";
            header.Inlines.Clear();
            header.Inlines.Add(new System.Windows.Documents.Run("ASSIGN  ") { Foreground = Term.Dim });
            header.Inlines.Add(new System.Windows.Documents.Run(Bindings.SlotNames[slot].ToUpperInvariant()));
            header.Inlines.Add(new System.Windows.Documents.Run("    CURRENT  ") { Foreground = Term.Dim });
            header.Inlines.Add(new System.Windows.Documents.Run(current));
        });
        panel.Children.Add(header);
        panel.Children.Add(Term.Blank());

        var categories = new TermList(20) { ActivateOnClick = false };
        var choices = new TermList(34);
        categories.SetItems(catalog.Select(c => c.Category), _pickerCategory);
        choices.SetItems(catalog[categories.Index].Choices.Select(c => c.Label));
        categories.IndexChanged += i =>
        {
            _pickerCategory = i;
            choices.SetItems(catalog[i].Choices.Select(c => c.Label));
        };
        categories.Activated += _ => choices.Focus();
        categories.OtherKey += e =>
        {
            if (e.Key != Key.Right) return;
            choices.Focus();
            e.Handled = true;
        };
        choices.OtherKey += e =>
        {
            if (e.Key != Key.Left) return;
            categories.Focus();
            e.Handled = true;
        };
        choices.Activated += i => ChooseBinding(slot, catalog[categories.Index], i);

        var columns = new StackPanel { Orientation = Orientation.Horizontal };
        columns.Children.Add(categories);
        columns.Children.Add(new Border { Width = Term.Cells(4) });
        columns.Children.Add(choices);
        panel.Children.Add(columns);
        panel.Children.Add(Term.Blank());

        _captureHint = Term.Text("", Term.Yellow);
        panel.Children.Add(_captureHint);
        panel.Children.Add(Term.Text("↑↓ SELECT · ←→ COLUMN · ENTER ASSIGN · ESC BACK", Term.Dim));
        panel.Children.Add(Term.Blank());
        panel.Children.Add(new TermButton("Back", () => CloseSubView(), framed: true));

        Dispatcher.BeginInvoke(() => categories.Focus(), DispatcherPriority.Loaded);
        return panel;
    }

    async void ChooseBinding(int slot, (string Category, IReadOnlyList<BindingChoice> Choices) category, int index)
    {
        if (!S.CanConfigure)
        {
            ShowStatus("MOUSE NOT RESPONDING — MOVE IT TO WAKE IT", true);
            return;
        }
        if (category.Category == "KEYBOARD")
        {
            StartKeyCapture(slot);
            return;
        }
        if (await _device.WriteButtonAsync(slot, category.Choices[index].Value)) CloseSubView();
    }

    void StartKeyCapture(int slot)
    {
        if (_captureHint is not null) _captureHint.Text = "PRESS THE KEY OR COMBINATION TO ASSIGN — ESC CANCELS";
        byte heldModifier = 0;
        _keyCapture = (e, down) =>
        {
            var key = HidKeys.RealKey(e);
            if (key == Key.Escape)
            {
                if (down) CloseSubView();
                return;
            }
            if (e.IsRepeat) return;
            if (HidKeys.Usage(key) is not byte usage)
            {
                if (down && _captureHint is not null)
                    _captureHint.Text = $"{key.ToString().ToUpperInvariant()} CANNOT BE ASSIGNED — TRY ANOTHER KEY";
                return;
            }
            if (HidKeys.IsModifier(usage))
            {
                if (down)
                {
                    heldModifier = usage;
                    if (_captureHint is not null)
                        _captureHint.Text = $"HOLDING {HidKeys.DescribeCombo(HidKeys.CurrentModifiers(), 0)} + …";
                }
                else if (heldModifier == usage)
                {
                    _keyCapture = null;
                    _ = AssignKey(slot, HidKeys.CurrentModifiers(), usage);
                }
                return;
            }
            if (!down) return;
            _keyCapture = null;
            _ = AssignKey(slot, HidKeys.CurrentModifiers(), usage);
        };
    }

    async Task AssignKey(int slot, byte modifiers, byte usage)
    {
        if (_captureHint is not null) _captureHint.Text = "";
        if (await _device.WriteButtonAsync(slot, Bindings.Key(modifiers, usage))) CloseSubView();
    }

    FrameworkElement BuildMacroEditor()
    {
        int slot = _mappingSlot;
        if (_macroDraftSlot != slot)
        {
            _macroDraft = S.Macros?[slot]?.ToList() ?? [];
            _macroDraftSlot = slot;
            _macroDirty = false;
        }

        var panel = new StackPanel();
        _macroInfo = Term.Text("");
        panel.Children.Add(_macroInfo);
        panel.Children.Add(Term.Blank());
        panel.Children.Add(Term.Text("  #   ACTION                                 DELAY AFTER", Term.Dim));

        _stepList = new TermList(56) { ActivateOnClick = false };
        _stepList.OtherKey += OnStepKey;
        panel.Children.Add(_stepList);
        panel.Children.Add(Term.Blank());

        _padText = Term.Text("");
        _padText.HorizontalAlignment = HorizontalAlignment.Center;
        _padText.VerticalAlignment = VerticalAlignment.Center;
        var pad = new Border
        {
            Width = Term.Cells(56),
            Height = 56,
            HorizontalAlignment = HorizontalAlignment.Left,
            BorderThickness = new Thickness(1),
            Background = System.Windows.Media.Brushes.Transparent,
            Child = _padText,
        };
        pad.MouseDown += (_, e) =>
        {
            if (!_recording) return;
            e.Handled = true;
            if (MouseMask(e.ChangedButton) is int mask) Record(MacroEvent.ButtonDown, mask);
        };
        pad.MouseUp += (_, e) =>
        {
            if (!_recording) return;
            e.Handled = true;
            if (MouseMask(e.ChangedButton) is int mask) Record(MacroEvent.ButtonUp, mask);
        };
        pad.MouseWheel += (_, e) =>
        {
            if (!_recording) return;
            e.Handled = true;
            Record(MacroEvent.Wheel, e.Delta > 0 ? 1 : -1);
            Record(MacroEvent.WheelReset, e.Delta > 0 ? 1 : -1);
        };
        panel.Children.Add(pad);
        panel.Children.Add(Term.Blank());

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        _recordButton = new TermButton("Record", ToggleRecording, framed: true);
        buttons.Children.Add(_recordButton);
        foreach (var (label, action) in new (string, Action)[]
                 {
                     ("Clear", () =>
                     {
                         _macroDraft.Clear();
                         _macroDirty = true;
                         RefreshMacroEditor();
                     }),
                     ("Save to mouse", SaveMacro),
                     ("Back", () => CloseSubView()),
                 })
        {
            buttons.Children.Add(new Border { Width = Term.Cells(2) });
            buttons.Children.Add(new TermButton(label, action, framed: true));
        }
        panel.Children.Add(buttons);
        panel.Children.Add(Term.Blank());
        panel.Children.Add(Term.Text("←→ DELAY ±10 MS (SHIFT ±100) · DEL REMOVE STEP · ESC BACK", Term.Dim));

        _refreshers.Add(() => pad.BorderBrush = _recording ? Term.Red : Term.Dim40);
        RefreshMacroEditor();
        Dispatcher.BeginInvoke(() => (_macroDraft.Count > 0 ? (UIElement)_stepList : _recordButton).Focus(),
            DispatcherPriority.Loaded);
        return panel;
    }

    static int? MouseMask(MouseButton button) => button switch
    {
        MouseButton.Left => 1,
        MouseButton.Right => 2,
        MouseButton.Middle => 4,
        MouseButton.XButton1 => 8,
        MouseButton.XButton2 => 16,
        _ => null,
    };

    void RefreshMacroEditor()
    {
        if (_macroInfo is null || _stepList is null) return;
        _macroInfo.Text = $"MACRO {_mappingSlot + 1}   {_macroDraft.Count}/{Macros.MaxSteps} STEPS" +
                          (_recording ? "   ● RECORDING" : _macroDirty ? "   UNSAVED CHANGES" : "");
        _macroInfo.Foreground = _recording ? Term.Red : Term.Fg;
        var lines = _macroDraft.Select((s, i) => $" {i + 1:00}   {s.Describe(),-38} {s.DelayMs,5} MS").ToList();
        if (lines.Count == 0) lines.Add("  NO STEPS — PRESS RECORD");
        int index = _stepList.Index;
        _stepList.SetItems(lines, _recording ? lines.Count - 1 : index);
        _stepList.Focusable = _macroDraft.Count > 0;
        if (_recordButton is not null) _recordButton.Label = _recording ? "Stop" : "Record";
        if (_padText is not null)
        {
            _padText.Text = _recording ? "CLICK / SCROLL HERE FOR MOUSE BUTTONS · KEYS RECORD ANYWHERE · ESC STOPS" : "RECORD PAD";
            _padText.Foreground = _recording ? Term.Fg : Term.Dim40;
        }
        foreach (var refresh in _refreshers) refresh();
    }

    void OnStepKey(KeyEventArgs e)
    {
        if (_stepList is null || _macroDraft.Count == 0) return;
        int i = _stepList.Index;
        int big = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 100 : 10;
        switch (e.Key)
        {
            case Key.Left:
            case Key.Right:
                int delay = Math.Clamp(_macroDraft[i].DelayMs + (e.Key == Key.Right ? big : -big), 0, 65535);
                _macroDraft[i] = _macroDraft[i] with { DelayMs = delay };
                break;
            case Key.Delete:
            case Key.Back:
                _macroDraft.RemoveAt(i);
                break;
            default:
                return;
        }
        e.Handled = true;
        _macroDirty = true;
        RefreshMacroEditor();
    }

    void ToggleRecording()
    {
        if (_recording) StopRecording();
        else StartRecording();
    }

    void StartRecording()
    {
        if (_macroDraft.Count >= Macros.MaxSteps)
        {
            ShowStatus($"MACRO IS FULL — {Macros.MaxSteps} STEPS MAX", true);
            return;
        }
        _recording = true;
        _recordClock.Restart();
        _lastRecordedMs = -1;
        _keyCapture = (e, down) =>
        {
            var key = HidKeys.RealKey(e);
            if (key == Key.Escape)
            {
                if (down) StopRecording();
                return;
            }
            if (e.IsRepeat || HidKeys.Usage(key) is not byte usage) return;
            Record(down ? MacroEvent.KeyDown : MacroEvent.KeyUp, usage);
        };
        RefreshMacroEditor();
    }

    void StopRecording()
    {
        _recording = false;
        _keyCapture = null;
        _recordClock.Stop();
        RefreshMacroEditor();
        _recordButton?.Focus();
    }

    void Record(MacroEvent kind, int code)
    {
        if (!_recording) return;
        long now = _recordClock.ElapsedMilliseconds;
        if (_lastRecordedMs >= 0 && _macroDraft.Count > 0)
        {
            int gap = (int)Math.Min(65535, now - _lastRecordedMs);
            _macroDraft[^1] = _macroDraft[^1] with { DelayMs = gap };
        }
        _macroDraft.Add(new MacroStep(0, kind, code));
        _lastRecordedMs = now;
        _macroDirty = true;
        if (_macroDraft.Count >= Macros.MaxSteps) StopRecording();
        else RefreshMacroEditor();
    }

    async void SaveMacro()
    {
        if (_recording) StopRecording();
        if (!S.CanConfigure)
        {
            ShowStatus("MOUSE NOT RESPONDING — MOVE IT TO WAKE IT", true);
            return;
        }
        if (await _device.WriteMacroAsync(_mappingSlot, _macroDraft.ToList()))
        {
            _macroDirty = false;
            RefreshMacroEditor();
        }
    }
}
