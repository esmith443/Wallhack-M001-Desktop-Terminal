using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using WallhackTerminal.Protocol;
using WallhackTerminal.Services;

namespace WallhackTerminal.UI;

public sealed partial class MainWindow : Window
{
    static readonly string[] Logo =
    [
        "00000000000000000000000",
        "0000000000000000000000000",
        "000000000000000000000000000",
        "00000000000000000000000000000",
        "000000               0000000000",
        "000000   000000000     00000000",
        "000000   00000000000     000000",
        "000000     00000000000     0000",
        "00000000     00000000000     00",
        " 000000000     00000000000",
        "   000000000     00000000000",
        "     000000000     00000000000",
        "00     000000000     0000000000",
        "0000     000000000     00000000",
        "000000     000000000     000000",
        "00000000     000000000     0000",
        " 000000000     00000000      00",
    ];

    static readonly (string Id, string Label)[] Tabs =
    [
        ("performance", "Performance"),
        ("power", "Power"),
        ("calibration", "Calibration"),
        ("mapping", "Mapping"),
        ("advanced", "Advanced"),
        ("updates", "Updates"),
    ];

    [StructLayout(LayoutKind.Sequential)]
    struct POINT
    {
        public int X, Y;
    }

    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out POINT point);

    readonly App _app;
    readonly DeviceService _device;
    readonly AppSettings _settings;
    readonly NotificationService _notifications;
    readonly UpdateCoordinator _updates;
    readonly StackPanel _page;
    readonly StackPanel _updateBadges = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(Term.Cells(4), 0, 0, 0) };
    readonly Border _scrollThumb = new() { Width = 3, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false, Visibility = Visibility.Collapsed };

    readonly StackPanel _navRoot = new();
    readonly Border _contentHost = new();
    readonly TermTabs _tabs;
    readonly TextBlock _deviceLine = Term.Text("");
    readonly TermButton _deviceButton;
    readonly TextBlock _statusLine = Term.Text("");
    readonly MouseArt _art = new();
    readonly TermDrawer _helpDrawer, _loggerDrawer;
    readonly TextBlock _helpTitle = Term.Text("", Term.Fg);
    readonly TextBlock _helpText = Term.Wrapped("", Term.Dim70, 40);
    readonly LoggerView _logger = new();
    readonly TermButton _loggerMode;
    readonly Grid _dialogLayer = new();
    readonly System.Windows.Shapes.Rectangle _grain;
    readonly ImageBrush? _grainBrush;
    readonly DispatcherTimer _statusTimer, _frameTimer, _grainTimer;
    readonly RawMouse _raw = new();
    readonly Random _random = new();

    readonly List<Action> _refreshers = [];
    string _tab = "performance";
    string _structure = "";
    Func<string>? _structureKey;
    Action<KeyEventArgs, bool>? _keyCapture;
    Action? _dialogCancel;
    FrameworkElement? _hovered;
    bool _hidMode;
    Point _lastPointer;
    long _lastMoves, _rateMoves;
    DateTime _rateSince = DateTime.UtcNow;
    double? _liveSpeed;

    MouseState S => _device.State;

    public MainWindow(App app, DeviceService device, AppSettings settings, NotificationService notifications, UpdateCoordinator updates)
    {
        _app = app;
        _device = device;
        _settings = settings;
        _notifications = notifications;
        _updates = updates;

        Title = "WH_TERMINAL";
        var area = SystemParameters.WorkArea;
        Width = Math.Min(1500, area.Width - 40);
        Height = Math.Min(980, area.Height - 40);
        MinWidth = 1120;
        MinHeight = 640;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        Background = Term.Bg;
        FontFamily = Term.Font;
        FontSize = Term.Size;
        Foreground = Term.Fg;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });
        try
        {
            Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/app.ico"));
        }
        catch
        {
        }

        var page = _page = new StackPanel { Margin = PageMargin(_settings.LoggerExpanded) };
        var logo = Term.Text(string.Join("\n", Logo));
        logo.LineHeight = Term.Size;
        page.Children.Add(logo);
        page.Children.Add(Term.Blank());
        var versionLine = new StackPanel { Orientation = Orientation.Horizontal, Height = Term.Line };
        versionLine.Children.Add(Term.Text($"WH_TERMINAL V{AppInfo.VersionText} - WALLHACK 2026 - DESKTOP"));
        versionLine.Children.Add(_updateBadges);
        page.Children.Add(versionLine);
        page.Children.Add(Term.Blank());
        page.Children.Add(Term.Text("Navigate with mouse or arrow & enter keys"));
        page.Children.Add(Term.Blank());

        _deviceButton = new TermButton("X", ToggleConnection, framed: true)
        {
            HelpTitle = "Connection",
            HelpText = "Release the receiver (for example to use the web terminal), or connect to it again.",
        };
        var deviceRow = new StackPanel { Orientation = Orientation.Horizontal, Height = Term.Line };
        _deviceLine.Margin = new Thickness(Term.Cells(1), 0, 0, 0);
        _deviceLine.VerticalAlignment = VerticalAlignment.Center;
        deviceRow.Children.Add(_deviceButton);
        deviceRow.Children.Add(_deviceLine);
        deviceRow.Children.Add(BuildBatteryChips());

        _tabs = new TermTabs(Tabs, _tab);
        _tabs.SelectionChanged += id =>
        {
            LeaveSubViews();
            _tab = id;
            RebuildTab(keepFocus: false);
        };

        var tabArea = new StackPanel();
        tabArea.Children.Add(_tabs);
        tabArea.Children.Add(Term.Blank(2));
        tabArea.Children.Add(Term.Indent(_contentHost));
        tabArea.Children.Add(Term.Blank());
        tabArea.Children.Add(Term.Indent(_statusLine));

        _navRoot.Margin = new Thickness(Term.Cells(4), 0, 0, 0);
        _navRoot.Children.Add(deviceRow);
        _navRoot.Children.Add(Term.Blank());
        _navRoot.Children.Add(Term.Indent(tabArea));

        _navRoot.MinWidth = Term.Cells(100);
        page.Children.Add(_navRoot);

        var artBox = new Border
        {
            Width = Term.Cells(58),
            Height = 500,
            Margin = new Thickness(0, 48, 0, 0),
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = _art,
        };
        _art.HorizontalAlignment = HorizontalAlignment.Center;
        _art.VerticalAlignment = VerticalAlignment.Center;
        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.Children.Add(page);
        Grid.SetColumn(artBox, 1);
        layout.Children.Add(artBox);

        var scroller = new QuietScrollViewer
        {
            Content = layout,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
        };

        var titleBar = new Grid { Height = 40, VerticalAlignment = VerticalAlignment.Top, Background = Brushes.Transparent };
        titleBar.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) ToggleMaximize();
            else DragMove();
        };
        var windowButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, Term.Cells(2), 0),
        };
        foreach (var (label, action) in new (string, Action)[] { ("_", () => WindowState = WindowState.Minimized), ("□", ToggleMaximize), ("X", Close) })
        {
            windowButtons.Children.Add(new TermButton(label, action) { Focusable = false, Margin = new Thickness(Term.Cells(1), 0, 0, 0) });
        }
        titleBar.Children.Add(windowButtons);

        var helpBody = new StackPanel { Margin = new Thickness(10, 8, 10, 8) };
        helpBody.Children.Add(_helpTitle);
        helpBody.Children.Add(Term.Blank());
        helpBody.Children.Add(_helpText);
        _helpDrawer = new TermDrawer("Help", helpBody, 380, 200, expanded: false);

        var support = Term.Text("support@wallhack.com", Term.Dim40);
        support.TextDecorations = TextDecorations.Underline;
        support.Cursor = Cursors.Hand;
        support.VerticalAlignment = VerticalAlignment.Center;
        support.Margin = new Thickness(Term.Cells(4), 0, 0, 4);
        support.MouseLeftButtonDown += (_, _) => Open("mailto:support@wallhack.com");
        var bottomLeft = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(16, 0, 0, 0),
        };
        bottomLeft.Children.Add(_helpDrawer);
        bottomLeft.Children.Add(support);

        _loggerMode = new TermButton("Mouse", ToggleLoggerMode)
        {
            Focusable = false,
            HelpTitle = "Logger mode",
            HelpText = "MOUSE logs pointer moves and clicks. HID shows the raw reports exchanged with the receiver.",
        };
        _loggerDrawer = new TermDrawer("Input logger", _logger, 660, 300, _settings.LoggerExpanded)
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, Term.Cells(1), 0),
        };
        _loggerDrawer.HeaderExtra = _loggerMode;
        _loggerDrawer.ExpandedChanged += expanded =>
        {
            _page.Margin = PageMargin(expanded);
            _settings.LoggerExpanded = expanded;
            _settings.Save();
        };

        _grain = new System.Windows.Shapes.Rectangle { IsHitTestVisible = false, Opacity = 0.08 };
        var noise = LoadNoise();
        _grainBrush = new ImageBrush(noise)
        {
            TileMode = TileMode.Tile,
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, noise.PixelWidth, noise.PixelHeight),
            Stretch = Stretch.Fill,
            Transform = new TranslateTransform(),
        };
        _grain.Fill = _grainBrush;
        var vignette = new System.Windows.Shapes.Rectangle
        {
            IsHitTestVisible = false,
            Fill = new RadialGradientBrush
            {
                RadiusX = 0.75,
                RadiusY = 0.75,
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.633),
                    new GradientStop(Color.FromArgb(0x33, 0, 0, 0), 1.0),
                },
            },
        };

        _dialogLayer.Background = new SolidColorBrush(Color.FromArgb(0xB3, 0, 0, 0));
        _dialogLayer.Visibility = Visibility.Collapsed;

        var root = new Grid();
        root.Children.Add(scroller);
        _scrollThumb.Background = Term.Dim40;
        _scrollThumb.Margin = new Thickness(0, 0, 4, 0);
        root.Children.Add(_scrollThumb);
        scroller.ScrollChanged += (_, _) => UpdateScrollThumb(scroller);
        root.Children.Add(titleBar);
        root.Children.Add(bottomLeft);
        root.Children.Add(_loggerDrawer);
        root.Children.Add(_grain);
        root.Children.Add(vignette);
        root.Children.Add(_dialogLayer);
        Content = new Border { BorderBrush = Term.Zinc800, BorderThickness = new Thickness(1), Child = root };

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _statusTimer.Tick += (_, _) =>
        {
            _statusTimer.Stop();
            _statusLine.Text = "";
        };
        _frameTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _frameTimer.Tick += (_, _) => OnFrame();
        _grainTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _grainTimer.Tick += (_, _) =>
        {
            if (_grainBrush?.Transform is TranslateTransform t)
            {
                t.X = -_random.Next(0, 1024);
                t.Y = -_random.Next(0, 1024);
            }
        };

        _device.StateChanged += OnStateChanged;
        _updates.Changed += OnStateChanged;
        _device.Status += ShowStatus;
        _device.MotionSpeed += speed =>
        {
            _liveSpeed = speed;
            if (_curveGraph is not null) _curveGraph.LiveSpeed = speed;
        };
        _device.Traffic += (line, outgoing) =>
        {
            if (_hidMode) _logger.Add(line, outgoing ? Term.Dim70 : Term.Fg, outgoing ? "TX" : "RX");
        };

        IsVisibleChanged += (_, _) => OnVisibilityChanged();
        ApplyGrain();
        UpdateHeader();
        RebuildTab(keepFocus: false);
        Loaded += (_, _) => _tabs.Focus();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        source?.AddHook(WndProc);
        OnVisibilityChanged();
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == RawMouse.WM_INPUT) _raw.Process(lParam);
        return IntPtr.Zero;
    }

    void OnVisibilityChanged()
    {
        bool visible = IsVisible;
        _device.WindowVisible = visible;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (visible && hwnd != IntPtr.Zero)
        {
            _raw.Register(hwnd);
            _frameTimer.Start();
            ApplyGrain();
        }
        else
        {
            _raw.Unregister();
            _frameTimer.Stop();
            _grainTimer.Stop();
        }
    }

    public void ApplyGrain()
    {
        _grain.Opacity = _settings.GrainEffect ? 0.08 : 0;
        if (_settings.GrainEffect && IsVisible) _grainTimer.Start();
        else _grainTimer.Stop();
    }

    static BitmapSource LoadNoise()
    {
        try
        {
            return new BitmapImage(new Uri("pack://application:,,,/Assets/noise.png"));
        }
        catch
        {
            const int size = 512;
            var pixels = new byte[size * size * 4];
            var random = new Random(2026);
            for (int i = 0; i < pixels.Length; i += 4)
            {
                byte v = (byte)random.Next(256);
                pixels[i] = pixels[i + 1] = pixels[i + 2] = v;
                pixels[i + 3] = 255;
            }
            var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
            bitmap.Freeze();
            return bitmap;
        }
    }

    static Thickness PageMargin(bool loggerExpanded) => new(Term.Cells(2), Term.Cells(2), Term.Cells(2), loggerExpanded ? 320 : 56);

    void UpdateScrollThumb(ScrollViewer scroller)
    {
        if (scroller.ExtentHeight <= scroller.ViewportHeight + 1)
        {
            _scrollThumb.Visibility = Visibility.Collapsed;
            return;
        }
        double height = Math.Max(24, scroller.ViewportHeight * scroller.ViewportHeight / scroller.ExtentHeight);
        double top = (scroller.ViewportHeight - height) * scroller.VerticalOffset / (scroller.ExtentHeight - scroller.ViewportHeight);
        _scrollThumb.Height = height;
        _scrollThumb.Margin = new Thickness(0, top, 4, 0);
        _scrollThumb.Visibility = Visibility.Visible;
    }

    void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_app.Exiting && _settings.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _device.StateChanged -= OnStateChanged;
        _updates.Changed -= OnStateChanged;
        _device.Status -= ShowStatus;
        _device.TrafficEnabled = false;
        _raw.Unregister();
        if (!_app.Exiting) _app.ExitApp();
    }

    static void Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("open " + target, ex);
        }
    }

    void OnFrame()
    {
        _art.SetPressed(_raw.Left, _raw.Right, _raw.Middle, _raw.Back, _raw.Forward);
        while (_raw.Events.TryDequeue(out var click))
        {
            if (!_hidMode) _logger.Add(click);
        }

        if (_raw.Moves != _lastMoves && GetCursorPos(out var cursor))
        {
            _lastMoves = _raw.Moves;
            var p = PointFromScreen(new Point(cursor.X, cursor.Y));
            if (p != _lastPointer)
            {
                _lastPointer = p;
                if (!_hidMode) _logger.Add($"X: {(int)p.X} Y: {(int)p.Y}");
                if (ActualWidth > 0 && ActualHeight > 0) _logger.PadPosition = new Point(p.X / ActualWidth, p.Y / ActualHeight);
            }
        }

        var elapsed = DateTime.UtcNow - _rateSince;
        if (elapsed >= TimeSpan.FromMilliseconds(500))
        {
            long moves = _raw.Moves - _rateMoves;
            double rate = moves / elapsed.TotalSeconds;
            _loggerDrawer.Title = rate > 60 ? $"Input logger · ~{Math.Round(rate / 10) * 10:0} hz" : "Input logger";
            _rateMoves = _raw.Moves;
            _rateSince = DateTime.UtcNow;
        }
    }

    void ToggleLoggerMode()
    {
        _hidMode = !_hidMode;
        _device.TrafficEnabled = _hidMode;
        _loggerMode.Label = _hidMode ? "HID" : "Mouse";
        _logger.Clear();
        _logger.Add(_hidMode ? "HID TRAFFIC — TX = SENT, RX = RECEIVED" : "MOUSE INPUT", Term.Dim);
    }

    void OnStateChanged()
    {
        UpdateHeader();
        UpdateBadges();
        UpdateBatteryChips();
        _art.Angle = S.Get(SettingId.SensorAngle) ?? 0;
        if ((_structureKey?.Invoke() ?? "") != _structure) RebuildTab(keepFocus: true);
        else foreach (var refresh in _refreshers) refresh();
    }

    void UpdateHeader()
    {
        _deviceLine.Inlines.Clear();
        _deviceButton.Label = _device.Paused ? "+" : "X";
        if (!S.ReceiverConnected)
        {
            string text = _device.Paused
                ? "RECEIVER RELEASED — PRESS + TO CONNECT"
                : $"{S.ReceiverStatus} — PLUG IN THE M-001 RECEIVER";
            _deviceLine.Inlines.Add(new System.Windows.Documents.Run("◇ ") { Foreground = Term.Dim });
            _deviceLine.Inlines.Add(new System.Windows.Documents.Run(text) { Foreground = Term.Dim });
            return;
        }

        var diamond = S.MouseResponding switch { true => Term.Green, false => Term.Yellow, null => Term.Dim };
        string version = S.Versions?.Release is { } release ? "V" + release.Version
            : S.Versions is { } v ? $"FW {v.Mouse}/{v.Receiver}/{v.ReceiverNxp}" : "…";
        string line = S.MouseResponding == false
            ? "WALLHACK M-001 – MOUSE ASLEEP OR OUT OF RANGE — MOVE IT TO WAKE IT"
            : $"WALLHACK M-001 – {S.MouseChipId ?? "…"} – {version}" + (S.Busy ? "   [READING]" : "");
        _deviceLine.Inlines.Add(new System.Windows.Documents.Run("◆ ") { Foreground = diamond });
        _deviceLine.Inlines.Add(new System.Windows.Documents.Run(line));
    }

    void ShowStatus(string message, bool error)
    {
        _statusLine.Text = "> " + message.ToUpperInvariant();
        _statusLine.Foreground = error ? Term.Red : Term.Dim;
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    void ToggleConnection()
    {
        if (_device.Paused) _device.Reconnect();
        else _device.Disconnect();
        UpdateHeader();
    }

    public void OpenTab(string id)
    {
        if (Tabs.Any(t => t.Id == id)) _tabs.Select(id);
    }

    void RebuildTab(bool keepFocus)
    {
        int focusIndex = keepFocus ? FocusIndex() : -1;
        _refreshers.Clear();
        _structureKey = null;
        _curveGraph = null;
        _contentHost.Child = _tab switch
        {
            "performance" => BuildPerformance(),
            "power" => BuildPower(),
            "calibration" => BuildCalibration(),
            "mapping" => BuildMapping(),
            "updates" => BuildUpdates(),
            _ => BuildAdvanced(),
        };
        _structure = _structureKey?.Invoke() ?? "";
        foreach (var refresh in _refreshers) refresh();
        if (focusIndex >= 0) Dispatcher.BeginInvoke(() => FocusAt(focusIndex), DispatcherPriority.Loaded);
    }

    List<FrameworkElement> Navigables()
    {
        var list = new List<FrameworkElement>();
        void Walk(DependencyObject node)
        {
            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                if (child is TermRow or TermTabs or TermButton or TermList)
                {
                    var element = (FrameworkElement)child;
                    if (element.Focusable && element.IsVisible) list.Add(element);
                    continue;
                }
                Walk(child);
            }
        }
        Walk(_navRoot);
        return list;
    }

    int FocusIndex()
    {
        var focused = Keyboard.FocusedElement as DependencyObject;
        if (focused is null) return -1;
        return Navigables().FindIndex(e => e == focused || e.IsAncestorOf(focused));
    }

    void FocusAt(int index)
    {
        var items = Navigables();
        if (items.Count == 0) return;
        var target = items[Math.Clamp(index, 0, items.Count - 1)];
        target.Focus();
        target.BringIntoView();
    }

    void MoveFocusBy(int direction)
    {
        var items = Navigables();
        if (items.Count == 0) return;
        int index = FocusIndex();
        FocusAt(index < 0 ? 0 : index + direction);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (_dialogCancel is not null)
        {
            switch (e.Key)
            {
                case Key.Escape:
                    _dialogCancel();
                    e.Handled = true;
                    return;
                case Key.Left:
                case Key.Right:
                    (Keyboard.FocusedElement as UIElement)?.MoveFocus(new TraversalRequest(
                        e.Key == Key.Left ? FocusNavigationDirection.Previous : FocusNavigationDirection.Next));
                    e.Handled = true;
                    return;
                case Key.Up:
                case Key.Down:
                    e.Handled = true;
                    return;
            }
            base.OnPreviewKeyDown(e);
            return;
        }
        if (_keyCapture is not null && Keyboard.FocusedElement is not TextBox)
        {
            _keyCapture(e, true);
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        if (_keyCapture is not null && _dialogCancel is null && Keyboard.FocusedElement is not TextBox)
        {
            _keyCapture(e, false);
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyUp(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || _dialogCancel is not null) return;
        switch (e.Key)
        {
            case Key.Up:
            case Key.Down:
                MoveFocusBy(e.Key == Key.Down ? 1 : -1);
                e.Handled = true;
                break;
            case Key.Escape:
                if (CloseSubView()) e.Handled = true;
                break;
        }
    }

    protected override void OnPreviewGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnPreviewGotKeyboardFocus(e);
        if (_hovered is null) ShowHelp(e.NewFocus as FrameworkElement);
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        base.OnPreviewMouseMove(e);
        var element = e.OriginalSource as DependencyObject;
        while (element is not null and not TermRow and not TermButton)
            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        var hovered = element as FrameworkElement;
        if (hovered == _hovered) return;
        _hovered = hovered;
        ShowHelp(hovered ?? Keyboard.FocusedElement as FrameworkElement);
    }

    void ShowHelp(FrameworkElement? element)
    {
        var (title, text) = element switch
        {
            TermRow row => (row.HelpTitle, row.HelpText),
            TermButton button => (button.HelpTitle, button.HelpText),
            _ => (null, null),
        };
        if (title is null) return;
        _helpTitle.Text = title.ToUpperInvariant();
        _helpText.Text = (text ?? "").ToUpperInvariant();
    }

    Task<bool> ConfirmAsync(string title, string subtitle, string ok = "OK", string cancel = "Cancel")
    {
        _dialogCancel?.Invoke();
        var result = new TaskCompletionSource<bool>();
        var previous = Keyboard.FocusedElement as IInputElement;
        void Finish(bool value)
        {
            _dialogCancel = null;
            _dialogLayer.Children.Clear();
            _dialogLayer.Visibility = Visibility.Collapsed;
            previous?.Focus();
            result.TrySetResult(value);
        }

        var okButton = new TermButton(ok, () => Finish(true), framed: true);
        var cancelButton = new TermButton(cancel, () => Finish(false), framed: true) { Margin = new Thickness(Term.Cells(2), 0, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(okButton);
        buttons.Children.Add(cancelButton);

        var body = new StackPanel { Margin = new Thickness(Term.Cells(3), 16, Term.Cells(3), 16) };
        body.Children.Add(Term.Text(title.ToUpperInvariant()));
        body.Children.Add(Term.Blank());
        body.Children.Add(Term.Wrapped(subtitle, Term.Dim70, 52));
        body.Children.Add(Term.Blank());
        body.Children.Add(buttons);

        _dialogLayer.Children.Add(new Border
        {
            Background = Term.Bg,
            BorderBrush = Term.Fg,
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = body,
        });
        _dialogLayer.Visibility = Visibility.Visible;
        _dialogCancel = () => Finish(false);
        Dispatcher.BeginInvoke(() => okButton.Focus(), DispatcherPriority.Loaded);
        return result.Task;
    }
}
