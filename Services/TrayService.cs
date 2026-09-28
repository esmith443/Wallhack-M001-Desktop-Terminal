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
    readonly SD.Icon _iconConnected, _iconIdle;
    readonly WF.ToolStripMenuItem _header, _dpi, _poll, _notifications, _autostart;
    readonly SD.Font _font;

    public NotificationService? Notifications { get; set; }

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);

    public TrayService(App app, DeviceService device, AppSettings settings)
    {
        _app = app;
        _device = device;
        _settings = settings;
        _iconConnected = MakeIcon(connected: true);
        _iconIdle = MakeIcon(connected: false);
        _font = new SD.Font("Consolas", 9.5f);

        _header = Item("", null, enabled: false);
        _dpi = Item("", null, enabled: false);
        _poll = Item("", null, enabled: false);
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
        var open = Item("OPEN WH_TERMINAL", _app.ShowMainWindow);
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
            _header, _dpi, _poll,
            new WF.ToolStripSeparator(),
            open, _notifications, _autostart,
            new WF.ToolStripSeparator(),
            Item("EXIT", _app.ExitApp),
        ]);
        menu.Opening += (_, _) => Update();

        _icon = new WF.NotifyIcon { ContextMenuStrip = menu, Visible = true };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == WF.MouseButtons.Left) _app.ShowMainWindow();
        };
        _icon.BalloonTipClicked += (_, _) => _app.ShowMainWindow();

        _device.StateChanged += Update;
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
        _notifications.Checked = _settings.WindowsNotifications;
        _autostart.Checked = Autostart.IsEnabled;

        _icon.Icon = s.ReceiverConnected ? _iconConnected : _iconIdle;
        string tip = s.ReceiverConnected ? $"WALLHACK M-001\n{dpi} · {poll}" : $"WH_TERMINAL\n{s.ReceiverStatus}";
        _icon.Text = tip.Length > 127 ? tip[..127] : tip;
    }

    public void ShowBalloon(string title, string text)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = string.IsNullOrWhiteSpace(text) ? " " : text;
        _icon.BalloonTipIcon = WF.ToolTipIcon.None;
        _icon.ShowBalloonTip(3000);
    }

    static SD.Icon MakeIcon(bool connected)
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
            using var light = new SD.SolidBrush(connected ? SD.Color.FromArgb(0x00, 0xC9, 0x50) : SD.Color.FromArgb(0x71, 0x71, 0x7A));
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
        _icon.Visible = false;
        _icon.Dispose();
        _iconConnected.Dispose();
        _iconIdle.Dispose();
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
