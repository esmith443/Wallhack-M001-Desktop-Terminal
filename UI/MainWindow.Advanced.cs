using System.Windows;
using System.Windows.Controls;
using WallhackTerminal.Protocol;
using WallhackTerminal.Services;

namespace WallhackTerminal.UI;

public sealed partial class MainWindow
{
    static readonly (string Id, string Label)[] AdvancedTabs = [("general", "General"), ("device", "Device"), ("receiver", "Receiver")];

    static readonly OverlayPosition[] OverlayPositions = Enum.GetValues<OverlayPosition>();

    string _advancedTab = "general";

    FrameworkElement BuildAdvanced()
    {
        _structureKey = () => _advancedTab;
        var panel = new StackPanel();
        var tabs = new TermTabs(AdvancedTabs, _advancedTab);
        tabs.SelectionChanged += id =>
        {
            _advancedTab = id;
            RebuildTab(keepFocus: true);
        };
        panel.Children.Add(tabs);
        panel.Children.Add(Term.Blank(2));
        panel.Children.Add(_advancedTab switch
        {
            "device" => BuildDeviceInfo(),
            "receiver" => BuildPresetLabels(),
            _ => BuildAppSettings(),
        });
        return panel;
    }

    TermRow AppToggle(string label, Func<bool> get, Action<bool> set) =>
        Row(label, () => get() ? "ON" : "OFF", _ =>
        {
            set(!get());
            _settings.Save();
            foreach (var refresh in _refreshers) refresh();
        }, help: "app", requiresMouse: false);

    FrameworkElement BuildAppSettings()
    {
        var panel = new StackPanel();
        panel.Children.Add(Heading("Desktop app"));
        panel.Children.Add(AppToggle("Start with Windows", () => Autostart.IsEnabled, on =>
        {
            try { Autostart.Set(on); }
            catch (Exception ex) { ShowStatus("COULD NOT CHANGE AUTOSTART — " + ex.Message, true); }
        }));
        panel.Children.Add(Row("Close button", () => _settings.CloseToTray ? "TO TRAY" : "EXIT", _ =>
        {
            _settings.CloseToTray = !_settings.CloseToTray;
            _settings.Save();
            foreach (var refresh in _refreshers) refresh();
        }, help: "app", requiresMouse: false));
        panel.Children.Add(AppToggle("Grain effect", () => _settings.GrainEffect, on =>
        {
            _settings.GrainEffect = on;
            ApplyGrain();
        }));
        panel.Children.Add(Term.Blank());

        panel.Children.Add(Heading("Notifications"));
        panel.Children.Add(AppToggle("Windows notifications", () => _settings.WindowsNotifications, on => _settings.WindowsNotifications = on));
        panel.Children.Add(AppToggle("On-screen overlay", () => _settings.OverlayNotifications, on => _settings.OverlayNotifications = on));
        panel.Children.Add(Row("Overlay position", () => _settings.OverlayPosition switch
            {
                OverlayPosition.BottomCenter => "BOTTOM",
                OverlayPosition.TopCenter => "TOP",
                OverlayPosition.BottomRight => "BOTTOM RIGHT",
                OverlayPosition.TopRight => "TOP RIGHT",
                OverlayPosition.BottomLeft => "BOTTOM LEFT",
                _ => "TOP LEFT",
            },
            d =>
            {
                int i = Array.IndexOf(OverlayPositions, _settings.OverlayPosition);
                _settings.OverlayPosition = OverlayPositions[((i + Math.Sign(d)) % OverlayPositions.Length + OverlayPositions.Length) % OverlayPositions.Length];
                _settings.Save();
                foreach (var refresh in _refreshers) refresh();
            }, enabled: () => _settings.OverlayNotifications, help: "app", requiresMouse: false));
        panel.Children.Add(AppToggle("Announce DPI changes", () => _settings.NotifyDpi, on => _settings.NotifyDpi = on));
        panel.Children.Add(AppToggle("Announce HZ changes", () => _settings.NotifyPollRate, on => _settings.NotifyPollRate = on));
        panel.Children.Add(AppToggle("Announce plug / unplug", () => _settings.NotifyConnection, on => _settings.NotifyConnection = on));
        panel.Children.Add(Term.Blank());

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(new TermButton("Test notification", () => _notifications.ShowTest(), framed: true));
        buttons.Children.Add(new Border { Width = Term.Cells(2) });
        buttons.Children.Add(new TermButton("Open settings folder", () => Open(AppSettings.Directory), framed: true));
        buttons.Children.Add(new Border { Width = Term.Cells(2) });
        buttons.Children.Add(new TermButton("Quit app", _app.ExitApp, framed: true));
        panel.Children.Add(buttons);
        return panel;
    }

    TermRow InfoRow(string label, Func<string> value)
    {
        var row = new TermRow(label, 56, 30, arrows: false) { Value = value, Static = true };
        _refreshers.Add(row.Refresh);
        return row;
    }

    FrameworkElement BuildDeviceInfo()
    {
        var panel = new StackPanel();
        panel.Children.Add(InfoRow("Name", () => S.ReceiverConnected ? "WALLHACK M-001" : "--"));
        panel.Children.Add(InfoRow("Serial number", () => S.MouseChipId ?? "--"));
        panel.Children.Add(InfoRow("Firmware version", () => S.Versions is not { } v ? "--"
            : v.Release is { } r ? $"V{r.Version}" + (r.Date is { } date ? $" ({date})" : "") : "UNKNOWN"));
        panel.Children.Add(InfoRow("Mouse firmware", () => S.Versions?.Mouse.ToString() ?? "--"));
        panel.Children.Add(InfoRow("Receiver firmware", () => S.Versions?.Receiver.ToString() ?? "--"));
        panel.Children.Add(InfoRow("Receiver NXP firmware", () => S.Versions?.ReceiverNxp.ToString() ?? "--"));
        panel.Children.Add(InfoRow("Receiver ID", () => S.ReceiverChipId ?? "--"));
        panel.Children.Add(InfoRow("DPI dial", () => S.DpiRank is null ? "--" : $"{Presets.Dpi(S, _settings)} · {Presets.Position(S.DpiRank)}"));
        panel.Children.Add(InfoRow("HZ dial", () => S.PollRank is null ? "--" : $"{Presets.Poll(S, _settings)} · {Presets.Position(S.PollRank)}"));
        panel.Children.Add(InfoRow("Latest known firmware", () => "V" + M001.Releases[0].Version));
        panel.Children.Add(Term.Blank());

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(new TermButton("Re-read device", () => _ = _device.RefreshAsync(), framed: true));
        buttons.Children.Add(new Border { Width = Term.Cells(2) });
        buttons.Children.Add(new TermButton("Open web terminal", () => Open("https://terminal.wallhack.com/"), framed: true));
        panel.Children.Add(buttons);
        panel.Children.Add(Term.Blank());
        panel.Children.Add(Term.Wrapped(
            "Firmware updates are not done here — use the web terminal. Press X next to the device name first so " +
            "the two apps do not talk to the receiver at the same time.", Term.Dim, 70));
        return panel;
    }

    FrameworkElement BuildPresetLabels()
    {
        var panel = new StackPanel();
        panel.Children.Add(Term.Wrapped(
            "The receiver only reports which dial position is active; these are the values printed on the dials. " +
            "Turn a dial and the active position is marked ◆. Press Enter on a row to rename it; an empty name restores the default.",
            Term.Dim, 70));
        panel.Children.Add(Term.Blank());
        AddPresetRows(panel, "DPI dial", _settings.DpiPresetLabels, Presets.DefaultDpi, () => S.DpiRank, "DPI");
        panel.Children.Add(Term.Blank());
        AddPresetRows(panel, "HZ dial", _settings.PollPresetLabels, Presets.DefaultPoll, () => S.PollRank, "HZ");
        return panel;
    }

    void AddPresetRows(Panel panel, string heading, string[] labels, string[] defaults, Func<int?> activeRank, string symbol)
    {
        panel.Children.Add(Heading(heading));
        for (int i = 0; i < AppSettings.PresetCount; i++)
        {
            int rank = i;
            panel.Children.Add(Row($"Position {rank + 1}",
                () => string.IsNullOrWhiteSpace(labels[rank]) ? $"{defaults[rank]} {symbol}" : labels[rank],
                dynamicLabel: () => $"{(activeRank() == rank ? "◆" : " ")} Position {rank + 1}",
                commit: text =>
                {
                    labels[rank] = text.Trim().Length > 14 ? text.Trim()[..14] : text.Trim();
                    _settings.Save();
                    foreach (var refresh in _refreshers) refresh();
                    return true;
                },
                editText: () => string.IsNullOrWhiteSpace(labels[rank]) ? defaults[rank] : labels[rank],
                help: "preset-label", requiresMouse: false));
        }
        panel.Children.Add(InfoRowWithLabel(() => $"{(activeRank() == M001.CustomRank ? "◆" : " ")} Position 8 (red {symbol})",
            () => "CUSTOM"));
    }

    TermRow InfoRowWithLabel(Func<string> label, Func<string> value)
    {
        var row = new TermRow("", 56, 16, arrows: false) { Label = label, Value = value, Static = true };
        _refreshers.Add(row.Refresh);
        return row;
    }
}
