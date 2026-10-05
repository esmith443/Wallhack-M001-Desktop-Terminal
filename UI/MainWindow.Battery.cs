using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WallhackTerminal.Services;

namespace WallhackTerminal.UI;

public sealed partial class MainWindow
{
    readonly BatteryIcon _mouseIcon = new(), _dockIcon = new();
    readonly TextBlock _mouseChipText = Term.Text(""), _dockChipText = Term.Text("");
    StackPanel? _batteryChips;

    FrameworkElement BuildBatteryChips()
    {
        _batteryChips = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(Term.Cells(4), 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = System.Windows.Media.Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = "Battery details: POWER tab",
        };
        foreach (var (name, icon, text) in new[] { ("MOUSE", _mouseIcon, _mouseChipText), ("DOCK", _dockIcon, _dockChipText) })
        {
            var label = Term.Text(name + " ", Term.Dim);
            label.VerticalAlignment = VerticalAlignment.Center;
            icon.Margin = new Thickness(0, 0, Term.Cells(0.5), 0);
            text.VerticalAlignment = VerticalAlignment.Center;
            text.Margin = new Thickness(0, 0, Term.Cells(3), 0);
            _batteryChips.Children.Add(label);
            _batteryChips.Children.Add(icon);
            _batteryChips.Children.Add(text);
        }
        _batteryChips.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            _tabs.Select("power");
        };
        UpdateBatteryChips();
        return _batteryChips;
    }

    void UpdateBatteryChips()
    {
        if (_batteryChips is null) return;
        bool known = S.ReceiverConnected && S.BatteryUpdated is not null;
        bool mouseOff = S.MouseLinked == false;
        _batteryChips.Visibility = known ? Visibility.Visible : Visibility.Collapsed;
        _mouseIcon.Percent = S.MouseBattery;
        _dockIcon.Percent = S.DockBattery;
        _mouseIcon.Opacity = mouseOff ? 0.5 : 1;
        _mouseChipText.Text = BatteryLook.Percent(S.MouseBattery) + (mouseOff ? " OFF" : "");
        _dockChipText.Text = S.DockBattery is null ? "NONE" : BatteryLook.Percent(S.DockBattery);
        _mouseChipText.Foreground = mouseOff ? Term.Dim : BatteryLook.For(S.MouseBattery);
        _dockChipText.Foreground = S.DockBattery is null ? Term.Yellow : BatteryLook.For(S.DockBattery);
    }

    FrameworkElement BuildBatterySection()
    {
        var panel = new StackPanel();
        panel.Children.Add(Heading("Battery"));
        panel.Children.Add(BatteryBlock("Mouse", mouse: true));
        panel.Children.Add(Term.Blank());
        panel.Children.Add(BatteryBlock("Dock", mouse: false));

        var graph = new BatteryGraph
        {
            Width = Term.Cells(56),
            Height = 150,
            Margin = new Thickness(0, 12, 0, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        _refreshers.Add(() => graph.Show(_device.Battery.Series(mouse: true), _device.Battery.Series(mouse: false)));
        panel.Children.Add(graph);

        var updated = Term.Text("", Term.Dim);
        updated.VerticalAlignment = VerticalAlignment.Center;
        _refreshers.Add(() => updated.Text = S.BatteryUpdated is not null
            ? "LIVE · CHECKED EVERY 3 SECONDS"
            : S.ReceiverConnected ? "WAITING FOR THE FIRST READING" : "CONNECT THE RECEIVER TO READ THE BATTERIES");
        var footer = new StackPanel { Orientation = Orientation.Horizontal };
        footer.Children.Add(updated);
        footer.Children.Add(new Border { Width = Term.Cells(3) });
        footer.Children.Add(new TermButton("Refresh", () => _ = _device.RefreshBatteryAsync(), framed: true)
        {
            HelpTitle = Help["battery"].Title,
            HelpText = Help["battery"].Text,
        });
        panel.Children.Add(footer);
        return panel;
    }

    FrameworkElement BatteryBlock(string name, bool mouse)
    {
        var block = new StackPanel();

        var top = new Grid { Height = Term.Line };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Term.Cells(8)) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Term.Cells(30)) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Term.Cells(6)) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = Term.Text(name);
        var bar = new BatteryBar { Height = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, Term.Cells(1), 0) };
        var percent = Term.Text("");
        percent.HorizontalAlignment = HorizontalAlignment.Right;
        var trend = Term.Text("");
        trend.Margin = new Thickness(Term.Cells(2), 0, 0, 0);
        Grid.SetColumn(bar, 1);
        Grid.SetColumn(percent, 2);
        Grid.SetColumn(trend, 3);
        top.Children.Add(label);
        top.Children.Add(bar);
        top.Children.Add(percent);
        top.Children.Add(trend);
        block.Children.Add(top);

        BatteryStats? Stats() => _device.Battery.Stats(mouse);
        int? Level() => mouse ? S.MouseBattery : S.DockBattery;

        _refreshers.Add(() =>
        {
            var stats = Stats();
            bool read = S.ReceiverConnected && S.BatteryUpdated is not null;
            bool absent = read && Level() is null;
            bool stale = mouse && read && S.MouseLinked == false;
            int? level = absent ? null : Level() ?? stats?.Percent;
            bool live = Level() is not null && !stale;
            bar.Percent = level;
            bar.Opacity = live ? 1 : 0.5;
            percent.Text = BatteryLook.Percent(level);
            percent.Foreground = live ? BatteryLook.For(level) : Term.Dim;
            trend.Text = absent ? "NO BATTERY"
                : level is null ? "NO READING"
                : stale ? "MOUSE OFF · LAST KNOWN"
                : live ? BatteryLook.Trend(stats)
                : "LAST KNOWN";
            trend.Foreground = absent ? Term.Yellow : live ? BatteryLook.TrendBrush(stats) : Term.Dim;
        });

        var statsGrid = new Grid { Margin = new Thickness(Term.Cells(8), 4, 0, 0) };
        statsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        statsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Term.Cells(2)) });
        statsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        statsGrid.RowDefinitions.Add(new RowDefinition());
        statsGrid.RowDefinitions.Add(new RowDefinition());
        AddStat(statsGrid, 0, 0, () => "Rate", () => BatteryLook.Rate(Stats()));
        AddStat(statsGrid, 0, 2, () => Stats()?.Trend == BatteryTrend.Charging ? "To full" : "Time left", () => BatteryLook.Estimate(Stats()));
        AddStat(statsGrid, 1, 0, () => "Last full", () => BatteryLook.Ago(Stats()?.LastFull));
        AddStat(statsGrid, 1, 2, () => "24h range", () => BatteryLook.Range(Stats()));
        block.Children.Add(statsGrid);
        return block;
    }

    void AddStat(Grid grid, int row, int column, Func<string> label, Func<string> value)
    {
        var (title, text) = Help["battery"];
        var cell = new TermRow("", 23, 12, arrows: false)
        {
            Label = label,
            Value = value,
            Static = true,
            HelpTitle = title,
            HelpText = text,
        };
        Grid.SetRow(cell, row);
        Grid.SetColumn(cell, column);
        grid.Children.Add(cell);
        _refreshers.Add(cell.Refresh);
    }
}
