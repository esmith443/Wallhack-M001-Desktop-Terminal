using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WallhackTerminal.Services;

namespace WallhackTerminal.UI;

public sealed partial class MainWindow
{
    string _badgeKey = "";

    void UpdateBadges()
    {
        var badges = new List<string>();
        if (_updates.App.Available && _updates.App.Latest is { } app) badges.Add($"▲ APP UPDATE V{app.Version.ToString(3)}");
        if (_updates.FirmwareStatus == FirmwareStatus.Available && _updates.Firmware.Latest is { } firmware) badges.Add($"▲ FIRMWARE V{firmware.Version}");
        string key = string.Join("|", badges);
        if (key == _badgeKey) return;
        _badgeKey = key;
        _updateBadges.Children.Clear();
        foreach (var text in badges)
        {
            _updateBadges.Children.Add(new TermButton(text, () => _tabs.Select("updates"), framed: true)
            {
                Focusable = false,
                Margin = new Thickness(0, 0, Term.Cells(2), 0),
                HelpTitle = "Update available",
                HelpText = "Open the Updates tab for details.",
            });
        }
    }

    FrameworkElement BuildUpdates()
    {
        var app = _updates.App;
        var firmware = _updates.Firmware;
        var panel = new StackPanel();

        panel.Children.Add(Heading("Desktop app"));
        panel.Children.Add(InfoRow("Installed", () => "V" + AppInfo.VersionText));
        panel.Children.Add(InfoRow("Latest release", () => app.Latest is { } latest
            ? $"V{latest.Version.ToString(3)}" + (latest.Published is { } published ? $" ({published:yyyy-MM-dd})" : "")
            : "--"));
        panel.Children.Add(StatusRow(() => app.Phase switch
        {
            UpdatePhase.Checking => ("CHECKING…", Term.Dim),
            UpdatePhase.Downloading => ($"DOWNLOADING {app.Progress:0}%", Term.Yellow),
            UpdatePhase.Installing => ("INSTALLING…", Term.Yellow),
            UpdatePhase.Failed => ("FAILED", Term.Red),
            _ when app.Available => ("UPDATE AVAILABLE", Term.Yellow),
            UpdatePhase.UpToDate => ("UP TO DATE", Term.Green),
            _ => ("NOT CHECKED YET", Term.Dim),
        }, () => app.Phase == UpdatePhase.Failed ? app.Error : null));
        panel.Children.Add(InfoRow("Last checked", () => app.LastChecked is null ? "NOT YET" : BatteryLook.Ago(app.LastChecked)));
        panel.Children.Add(Term.Blank());

        var install = new TermButton("Install update", InstallAppUpdate, framed: true)
        {
            HelpTitle = "Install update",
            HelpText = "Downloads the new version from GitHub, checks its size, SHA-256 checksum and product name, replaces this copy and restarts.",
        };
        panel.Children.Add(ButtonRow(
            new TermButton("Check now", () => _ = app.CheckAsync(), framed: true),
            install,
            new TermButton("Release page", () => Open(app.Latest?.PageUrl ?? AppInfo.ReleasesPage), framed: true)));
        _refreshers.Add(() => install.Visibility = app.Available && !app.Busy ? Visibility.Visible : Visibility.Collapsed);

        var notes = Term.Wrapped("", Term.Dim, 72);
        notes.Margin = new Thickness(0, Term.Line, 0, 0);
        _refreshers.Add(() =>
        {
            notes.Text = (app.Available && app.Latest is { } latest ? ReleaseNotes(latest.Notes) : "").ToUpperInvariant();
            notes.Visibility = notes.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        });
        panel.Children.Add(notes);
        panel.Children.Add(Term.Blank(2));

        panel.Children.Add(Heading("Mouse firmware"));
        panel.Children.Add(InfoRow("Installed", () => S.Versions is not { } v ? "--"
            : v.Release is { } release ? "V" + release.Version : $"UNLISTED {v.Mouse}/{v.Receiver}/{v.ReceiverNxp}"));
        panel.Children.Add(InfoRow("Latest from Wallhack", () => firmware.Latest is { } latest
            ? $"V{latest.Version}" + (string.IsNullOrEmpty(latest.Date) ? "" : $" ({latest.Date})")
            : "--"));
        panel.Children.Add(StatusRow(() => firmware.Checking ? ("CHECKING…", Term.Dim)
            : firmware.Error is not null ? ("CHECK FAILED", Term.Red)
            : _updates.FirmwareStatus switch
            {
                FirmwareStatus.Available => ("UPDATE AVAILABLE", Term.Yellow),
                FirmwareStatus.UpToDate => ("UP TO DATE", Term.Green),
                FirmwareStatus.Newer => ("NEWER THAN LIST", Term.Dim),
                _ => (S.ReceiverConnected ? "WAITING FOR MOUSE" : "NO RECEIVER", Term.Dim),
            }, () => firmware.Checking ? null : firmware.Error));
        panel.Children.Add(InfoRow("Last checked", () => firmware.LastChecked is null ? "NOT YET" : BatteryLook.Ago(firmware.LastChecked)));
        panel.Children.Add(Term.Blank());

        var site = new TermButton("Open wallhack.com", OpenFirmwareUpdate, framed: true)
        {
            HelpTitle = "Firmware update",
            HelpText = "Firmware is only installed with Wallhack's official web terminal. This releases the receiver and opens the site; press + next to the device name when you are done.",
        };
        _refreshers.Add(() => site.Label = _updates.FirmwareStatus == FirmwareStatus.Available ? "Update on wallhack.com" : "Open wallhack.com");
        panel.Children.Add(ButtonRow(new TermButton("Check now", () => _ = firmware.CheckAsync(), framed: true), site));

        var changelog = Term.Wrapped("", Term.Dim, 72);
        changelog.Margin = new Thickness(0, Term.Line, 0, 0);
        _refreshers.Add(() =>
        {
            changelog.Text = _updates.FirmwareStatus == FirmwareStatus.Available && firmware.Latest?.Changelog is { Length: > 0 } log
                ? $"V{firmware.Latest.Version}: {log}".ToUpperInvariant()
                : "";
            changelog.Visibility = changelog.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        });
        panel.Children.Add(changelog);
        panel.Children.Add(Term.Blank(2));

        panel.Children.Add(Heading("Settings"));
        panel.Children.Add(AppToggle("Check automatically", () => _settings.CheckForUpdates, on => _settings.CheckForUpdates = on));
        panel.Children.Add(Term.Text("App every 6 hours from GitHub · firmware every 12 hours from Wallhack", Term.Dim40));
        return panel;
    }

    FrameworkElement StatusRow(Func<(string Text, Brush Brush)> status, Func<string?> error)
    {
        var panel = new StackPanel();
        var row = new TermRow("Status", 56, 30, arrows: false)
        {
            Value = () => status().Text,
            ValueBrush = () => status().Brush,
            Static = true,
        };
        _refreshers.Add(row.Refresh);
        panel.Children.Add(row);
        var detail = Term.Wrapped("", Term.Red, 56);
        _refreshers.Add(() =>
        {
            detail.Text = (error() ?? "").ToUpperInvariant();
            detail.Visibility = detail.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        });
        panel.Children.Add(detail);
        return panel;
    }

    static FrameworkElement ButtonRow(params TermButton[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var button in buttons)
        {
            button.Margin = new Thickness(0, 0, Term.Cells(2), 0);
            row.Children.Add(button);
        }
        return row;
    }

    static string ReleaseNotes(string markdown) => string.Join("\n", markdown.Replace("\r", "").Split('\n')
        .Select(l => l.Trim())
        .Where(l => l.Length > 0 && !l.StartsWith("```"))
        .Select(l => l.TrimStart('#', '-', '*', ' '))
        .Take(8));

    async void InstallAppUpdate()
    {
        if (_updates.App.Latest is not { } latest) return;
        if (!await ConfirmAsync($"Install V{latest.Version.ToString(3)}",
                "WH_TERMINAL will download the update from GitHub, verify it, replace this copy and restart.", "Install")) return;
        if (await _updates.App.DownloadAndInstallAsync()) _app.RestartAfterUpdate();
    }

    async void OpenFirmwareUpdate()
    {
        if (!await ConfirmAsync("Wallhack web terminal",
                "This releases the receiver and opens Wallhack's official web terminal in your browser. When you are done there, press + next to the device name to reconnect.",
                "Open site")) return;
        if (!_device.Paused) _device.Disconnect();
        UpdateHeader();
        Open(FirmwareService.WebTerminal);
    }
}
