using System.Runtime.InteropServices;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WallhackTerminal.Services;

public sealed class TrayService : IDisposable
{
    readonly App _app;
    readonly DeviceService _device;
    readonly AppSettings _settings;
    readonly WF.NotifyIcon _icon;
    readonly SD.Icon _iconConnected, _iconIdle, _iconLow;
    readonly WF.ToolStripMenuItem _header, _dpi, _poll, _battery, _update, _notifications, _autostart;
    readonly UpdateCoordinator _updates;
    readonly SD.Font _font;

    public NotificationService? Notifications { get; set; }

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);

    public TrayService(App app, DeviceService device, AppSettings settings, UpdateCoordinator updates)
    {
        _app = app;
        _device = device;
        _settings = settings;
        _updates = updates;
        _iconConnected = MakeIcon(SD.Color.FromArgb(0x00, 0xC9, 0x50));
        _iconIdle = MakeIcon(SD.Color.FromArgb(0x71, 0x71, 0x7A));
        _iconLow = MakeIcon(SD.Color.FromArgb(0xFB, 0x2C, 0x36));
        _font = new SD.Font("Consolas", 9.5f);

        _header = Item("", null, enabled: false);
        _dpi = Item("", null, enabled: false);
        _poll = Item("", null, enabled: false);
        _battery = Item("", null, enabled: false);
        _update = Item("", () => _app.ShowMainWindow("updates"));
        _notifications = Item("NOTIFICATIONS", () =>
        {
            _settings.WindowsNotifications = !_settings.WindowsNotifications;
            _settings.Save();
            Update();
        });
        _autostart = Item("START WITH WINDOWS", () =>
        {
            try { Autostart.Set(!Autostart.IsEnabled); }
            catch (Exception ex) { Log.Error("autostart toggle failed", ex); }
            Update();
        });
        var open = Item("OPEN WH_TERMINAL", () => _app.ShowMainWindow());
        open.Font = new SD.Font(_font, SD.FontStyle.Bold);

        var menu = new WF.ContextMenuStrip
        {
            Renderer = new TerminalRenderer(),
            ShowImageMargin = false,
            ShowCheckMargin = true,
            Font = _font,
            BackColor = SD.Color.Black,
            ForeColor = SD.Color.White,
        };
        menu.Items.AddRange([
            _header, _dpi, _poll, _battery,
            new WF.ToolStripSeparator(),
            _update, open, _notifications, _autostart,
            new WF.ToolStripSeparator(),
            Item("EXIT", _app.ExitApp),
        ]);
        menu.Opening += (_, _) =>
        {
            _autostart.Checked = Autostart.IsEnabled;
            Update();
        };

        _icon = new WF.NotifyIcon { ContextMenuStrip = menu, Visible = true };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == WF.MouseButtons.Left) _app.ShowMainWindow();
        };
        _icon.BalloonTipClicked += (_, _) => _app.ShowMainWindow(Notifications?.ClickTab);

        _device.StateChanged += Update;
        _updates.Changed += Update;
        Update();
    }

    WF.ToolStripMenuItem Item(string text, Action? onClick, bool enabled = true)
    {
        var item = new WF.ToolStripMenuItem(text) { Enabled = enabled };
        if (onClick is not null) item.Click += (_, _) => onClick();
        return item;
    }

    void Update()
    {
        var s = _device.State;
        string dpi = Presets.Dpi(s, _settings), poll = Presets.Poll(s, _settings);

        _header.Text = s.ReceiverConnected
            ? s.MouseResponding == false ? "◆ M-001 — MOUSE ASLEEP" : "◆ WALLHACK M-001"
            : "◇ " + s.ReceiverStatus;
        _dpi.Text = $"DPI    {dpi}";
        _poll.Text = $"HZ     {poll}";
        _dpi.Visible = _poll.Visible = s.ReceiverConnected;
        bool hasBattery = s.ReceiverConnected && s.BatteryUpdated is not null;
        string battery = $"MOUSE {Level(s.MouseBattery)}{(s.MouseLinked == false ? " OFF" : "")} · DOCK {(s.DockBattery is null ? "NONE" : Level(s.DockBattery))}";
        _battery.Text = $"BAT    {battery}";
        _battery.Visible = hasBattery;
        _notifications.Checked = _settings.WindowsNotifications;
        bool appUpdate = _updates.App.Available;
        bool firmwareUpdate = _updates.FirmwareStatus == FirmwareStatus.Available;
        _update.Text = appUpdate ? $"▲ UPDATE TO V{_updates.App.Latest!.Version.ToString(3)}"
            : firmwareUpdate ? $"▲ FIRMWARE V{_updates.Firmware.Latest!.Version} AVAILABLE" : "";
        _update.Visible = appUpdate || firmwareUpdate;

        _icon.Icon = !s.ReceiverConnected ? _iconIdle : s.MouseBattery is <= 15 ? _iconLow : _iconConnected;
        string tip = s.ReceiverConnected
            ? $"WALLHACK M-001\n{dpi} · {poll}" + (hasBattery ? $"\n{battery}" : "")
            : $"WH_TERMINAL\n{s.ReceiverStatus}";
        _icon.Text = tip.Length > 127 ? tip[..127] : tip;
    }

    public void ShowBalloon(string title, string text)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = string.IsNullOrWhiteSpace(text) ? " " : text;
        _icon.BalloonTipIcon = WF.ToolTipIcon.None;
        _icon.ShowBalloonTip(3000);
    }

    static string Level(int? percent) => percent is int p ? $"{p}%" : "--";

    static SD.Icon MakeIcon(SD.Color status)
    {
        using var bitmap = new SD.Bitmap(32, 32);
        using (var g = SD.Graphics.FromImage(bitmap))
        {
            g.Clear(SD.Color.Black);
            g.FillRectangle(SD.Brushes.White, 2, 2, 28, 2);
            g.FillRectangle(SD.Brushes.White, 2, 28, 28, 2);
            g.FillRectangle(SD.Brushes.White, 2, 2, 2, 28);
            g.FillRectangle(SD.Brushes.White, 28, 2, 2, 28);
            for (int i = 0; i < 5; i++)
            {
                g.FillRectangle(SD.Brushes.White, 7 + i, 9 + i, 2, 2);
                g.FillRectangle(SD.Brushes.White, 7 + i, 19 - i, 2, 2);
            }
            g.FillRectangle(SD.Brushes.White, 14, 18, 5, 3);
            using var light = new SD.SolidBrush(status);
            g.FillRectangle(SD.Brushes.Black, 19, 19, 13, 13);
            g.FillRectangle(light, 21, 21, 10, 10);
        }
        IntPtr handle = bitmap.GetHicon();
        var icon = (SD.Icon)SD.Icon.FromHandle(handle).Clone();
        DestroyIcon(handle);
        return icon;
    }

    public void Dispose()
    {
        _device.StateChanged -= Update;
        _updates.Changed -= Update;
        _icon.Visible = false;
        _icon.Dispose();
        _iconConnected.Dispose();
        _iconIdle.Dispose();
        _iconLow.Dispose();
    }

    sealed class TerminalRenderer() : WF.ToolStripProfessionalRenderer(new TerminalColors())
    {
        protected override void OnRenderItemText(WF.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Selected && e.Item.Enabled ? SD.Color.Black
                : e.Item.Enabled ? SD.Color.White : SD.Color.FromArgb(0xB3, 0xB3, 0xB3);
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(WF.ToolStripItemImageRenderEventArgs e)
        {
            var r = e.ImageRectangle;
            using var brush = new SD.SolidBrush(e.Item.Selected ? SD.Color.Black : SD.Color.White);
            e.Graphics.FillRectangle(brush, r.X + r.Width / 2 - 3, r.Y + r.Height / 2 - 3, 7, 7);
        }

        protected override void OnRenderSeparator(WF.ToolStripSeparatorRenderEventArgs e)
        {
            using var pen = new SD.Pen(SD.Color.FromArgb(0x55, 0x55, 0x55));
            int y = e.Item.Height / 2;
            e.Graphics.DrawLine(pen, 4, y, e.Item.Width - 4, y);
        }
    }

    sealed class TerminalColors : WF.ProfessionalColorTable
    {
        static readonly SD.Color Black = SD.Color.Black, White = SD.Color.White;
        public override SD.Color ToolStripDropDownBackground => Black;
        public override SD.Color MenuBorder => White;
        public override SD.Color MenuItemBorder => White;
        public override SD.Color MenuItemSelected => White;
        public override SD.Color MenuItemSelectedGradientBegin => White;
        public override SD.Color MenuItemSelectedGradientEnd => White;
        public override SD.Color ImageMarginGradientBegin => Black;
        public override SD.Color ImageMarginGradientMiddle => Black;
        public override SD.Color ImageMarginGradientEnd => Black;
        public override SD.Color CheckBackground => Black;
        public override SD.Color CheckSelectedBackground => White;
        public override SD.Color CheckPressedBackground => White;
    }
}
