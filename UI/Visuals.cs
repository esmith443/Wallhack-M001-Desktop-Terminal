using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WallhackTerminal.Protocol;
using WallhackTerminal.Services;

namespace WallhackTerminal.UI;

public sealed class MouseArt : Border
{
    static readonly string[] Lines =
    [
        "     ",
        "     ",
        "          /---------------------\\",
        "         /           |           \\",
        "        /            |            \\",
        "       /            /-\\            \\",
        "      |             |-|             |",
        "      |             |-|             |",
        "      |             |-|             |",
        "      |             \\-/             |",
        "     [|              |              |",
        "     [|              |              |",
        "     [|              |              |",
        "      |--------------|--------------|",
        "     [|                             |",
        "     [|                             |",
        "     [|                             |",
        "      |                             |",
        "      |                             |",
        "      |                             |",
        "      |                             |",
        "      |                             |",
        "      |                             |",
        "       \\                           /",
        "        \\          M-001          /",
        "         \\                       /",
        "          \\                     /",
        "           \\-------------------/",
    ];

    enum Region { None, Left, Right, Wheel, Forward, Back }

    readonly Dictionary<Region, List<Run>> _runs = new();
    readonly RotateTransform _rotation = new();

    public MouseArt()
    {
        var stack = new StackPanel();
        for (int line = 0; line < Lines.Length; line++)
        {
            var block = Term.Text("");
            block.Inlines.Clear();
            var regions = Classify(line, Lines[line]);
            int start = 0;
            for (int i = 1; i <= Lines[line].Length; i++)
            {
                if (i < Lines[line].Length && regions[i] == regions[start]) continue;
                var run = new Run(Lines[line][start..i]);
                block.Inlines.Add(run);
                if (regions[start] != Region.None)
                {
                    if (!_runs.TryGetValue(regions[start], out var list)) _runs[regions[start]] = list = [];
                    list.Add(run);
                }
                start = i;
            }
            stack.Children.Add(block);
        }
        Child = stack;
        Width = Term.Cells(42);
        RenderTransformOrigin = new Point(0.5, 0.5);
        RenderTransform = _rotation;
        IsHitTestVisible = false;
    }

    static Region[] Classify(int line, string s)
    {
        var regions = new Region[s.Length];
        if (line is >= 3 and <= 12)
        {
            int a = 0;
            while (a < s.Length && s[a] == ' ') a++;
            if (a < s.Length && s[a] == '[')
            {
                regions[a] = Region.Forward;
                a++;
            }
            int b = s.Length - 1;
            int m0 = a + 1;
            while (m0 < b && s[m0] == ' ') m0++;
            int m1 = b - 1;
            while (m1 > a && s[m1] == ' ') m1--;
            for (int i = a + 1; i < m0; i++) regions[i] = Region.Left;
            for (int i = m1 + 1; i < b; i++) regions[i] = Region.Right;
            if (line is >= 6 and <= 8) regions[m0 + 1] = Region.Wheel;
        }
        else if (line is >= 14 and <= 16)
        {
            int bracket = s.IndexOf('[');
            if (bracket >= 0) regions[bracket] = Region.Back;
        }
        return regions;
    }

    public void SetPressed(bool left, bool right, bool middle, bool back, bool forward)
    {
        Fill(Region.Left, left);
        Fill(Region.Right, right);
        Invert(Region.Wheel, middle);
        Invert(Region.Back, back);
        Invert(Region.Forward, forward);
    }

    void Fill(Region region, bool on)
    {
        if (!_runs.TryGetValue(region, out var runs)) return;
        foreach (var run in runs) run.Background = on ? Term.PressFill : null;
    }

    void Invert(Region region, bool on)
    {
        if (!_runs.TryGetValue(region, out var runs)) return;
        foreach (var run in runs)
        {
            run.Background = on ? Term.Fg : null;
            run.Foreground = on ? Term.Bg : Term.Fg;
        }
    }

    public double Angle
    {
        set => _rotation.Angle = value;
    }
}

public sealed class InputPad : FrameworkElement
{
    static readonly Pen GridPen = MakePen(Term.Accent, 0.5);
    static readonly Pen FramePen = MakePen(Term.Secondary, 1);
    Point _position = new(0.5, 0.5);

    static Pen MakePen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        return pen;
    }

    public Point Position
    {
        get => _position;
        set
        {
            _position = new Point(Math.Clamp(value.X, 0, 1), Math.Clamp(value.Y, 0, 1));
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Term.Bg, FramePen, new Rect(0.5, 0.5, Math.Max(0, w - 1), Math.Max(0, h - 1)));
        for (int i = 1; i < 10; i++)
        {
            dc.DrawLine(GridPen, new Point(w * i / 10, 0), new Point(w * i / 10, h));
            dc.DrawLine(GridPen, new Point(0, h * i / 10), new Point(w, h * i / 10));
        }
        dc.DrawEllipse(Term.Fg, null, new Point(3 + _position.X * (w - 6), 3 + _position.Y * (h - 6)), 3, 3);
    }
}

public sealed class CurveGraph : FrameworkElement
{
    static readonly Pen GridPen = MakePen(Term.Zinc800, 1);
    static readonly Pen AxisPen = MakePen(Term.Accent, 1);
    static readonly Pen CurvePen = MakePen(Term.Fg, 1.5);
    static readonly Pen LivePen = MakePen(Term.Red, 1);
    Curve? _curve;
    double? _live;
    bool _dimmed;

    static Pen MakePen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        return pen;
    }

    public void Show(Curve? curve, bool dimmed)
    {
        _curve = curve;
        _dimmed = dimmed;
        InvalidateVisual();
    }

    public double? LiveSpeed
    {
        set
        {
            _live = value;
            InvalidateVisual();
        }
    }

    FormattedText Label(string text, Brush brush) => new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
        new Typeface(Term.Font, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 10, brush,
        VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected override void OnRender(DrawingContext dc)
    {
        const double left = 34, bottom = 18, top = 6, right = 16;
        double w = ActualWidth - left - right, h = ActualHeight - top - bottom;
        if (w <= 0 || h <= 0) return;
        double maxGain = Math.Max(2, Math.Ceiling((_curve?.Points.Max(p => p.Gain) ?? 1) + 0.25));

        double X(double speed) => left + speed / Curves.MaxSpeed * w;
        double Y(double gain) => top + h - gain / maxGain * h;

        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        for (int s = 0; s <= Curves.MaxSpeed; s += 40)
        {
            dc.DrawLine(GridPen, new Point(X(s), top), new Point(X(s), top + h));
            var t = Label(s.ToString(), Term.Dim);
            dc.DrawText(t, new Point(X(s) - t.Width / 2, top + h + 3));
        }
        for (double g = 0; g <= maxGain + 0.001; g += maxGain > 3 ? 1 : 0.5)
        {
            dc.DrawLine(GridPen, new Point(left, Y(g)), new Point(left + w, Y(g)));
            var t = Label(g.ToString("0.0", CultureInfo.InvariantCulture), Term.Dim);
            dc.DrawText(t, new Point(left - t.Width - 4, Y(g) - t.Height / 2));
        }
        dc.DrawLine(AxisPen, new Point(left, top), new Point(left, top + h));
        dc.DrawLine(AxisPen, new Point(left, top + h), new Point(left + w, top + h));

        if (_curve is { } curve)
        {
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(new Point(X(0), Y(curve.Points[0].Gain)), false, false);
                foreach (var p in curve.Points) ctx.LineTo(new Point(X(p.Speed), Y(p.Gain)), true, false);
                ctx.LineTo(new Point(X(Curves.MaxSpeed), Y(curve.Points[^1].Gain)), true, false);
            }
            geometry.Freeze();
            dc.PushOpacity(_dimmed ? 0.35 : 1);
            dc.DrawGeometry(null, CurvePen, geometry);
            foreach (var p in curve.Points) dc.DrawRectangle(Term.Fg, null, new Rect(X(p.Speed) - 2.5, Y(p.Gain) - 2.5, 5, 5));
            dc.Pop();
        }

        if (_live is double speed && !_dimmed)
        {
            double x = X(Math.Min(speed, Curves.MaxSpeed));
            dc.DrawLine(LivePen, new Point(x, top), new Point(x, top + h));
            string text = _curve is null ? $"{speed:0.0}" : $"{speed:0.0} → ×{_curve.GainAt(speed):0.00}";
            var t = Label(text, Term.Red);
            dc.DrawText(t, new Point(Math.Min(x + 4, left + w - t.Width), top));
        }
    }
}

public sealed class OsdWindow : Window
{
    const int GWL_EXSTYLE = -20;
    const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_LAYERED = 0x80000;

    [DllImport("user32.dll")]
    static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    readonly StackPanel _lines = new();
    readonly DispatcherTimer _hold;

    public OsdWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        FontFamily = Term.Font;
        FontSize = Term.Size;
        Foreground = Term.Fg;
        Content = new Border
        {
            Background = Term.Bg,
            BorderBrush = Term.Fg,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(Term.Cells(1.5), 8, Term.Cells(1.5), 8),
            Child = _lines,
        };
        _hold = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1800) };
        _hold.Tick += (_, _) =>
        {
            _hold.Stop();
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(250));
            fade.Completed += (_, _) =>
            {
                if (!_hold.IsEnabled) Hide();
            };
            BeginAnimation(OpacityProperty, fade);
        };
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowLong(hwnd, GWL_EXSTYLE,
                GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED);
        };
    }

    public void Present(IReadOnlyList<(string Label, string Value)> rows, OverlayPosition position)
    {
        _lines.Children.Clear();
        _lines.Children.Add(Term.Rich(("◆ ", Term.Green), ("WALLHACK M-001", Term.Fg)));
        _lines.Children.Add(Term.Blank());
        foreach (var (label, value) in rows)
        {
            var row = new TermRow(label, widthCh: 34, valueCh: 14, arrows: false) { Value = () => value, Focusable = false };
            row.Refresh();
            row.Focusable = false;
            _lines.Children.Add(row);
        }

        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
        if (!IsVisible) Show();
        UpdateLayout();
        var area = SystemParameters.WorkArea;
        const double margin = 32;
        Left = position switch
        {
            OverlayPosition.BottomLeft or OverlayPosition.TopLeft => area.Left + margin,
            OverlayPosition.BottomRight or OverlayPosition.TopRight => area.Right - ActualWidth - margin,
            _ => area.Left + (area.Width - ActualWidth) / 2,
        };
        Top = position is OverlayPosition.TopCenter or OverlayPosition.TopLeft or OverlayPosition.TopRight
            ? area.Top + margin
            : area.Bottom - ActualHeight - margin * 2;
        _hold.Stop();
        _hold.Start();
    }
}
