using System.Globalization;
using System.Windows;
using System.Windows.Media;
using WallhackTerminal.Services;

namespace WallhackTerminal.UI;

public static class BatteryLook
{
    public static Brush For(int? percent) => percent switch
    {
        null => Term.Dim40,
        <= 15 => Term.Red,
        <= 30 => Term.Yellow,
        _ => Term.Fg,
    };

    public static string Percent(int? percent) => percent is int p ? $"{p}%" : "--";

    public static string Trend(BatteryStats? stats) => stats?.Trend switch
    {
        BatteryTrend.Full => "FULL",
        BatteryTrend.Charging => "CHARGING ↑",
        BatteryTrend.Discharging => "DISCHARGING ↓",
        BatteryTrend.Steady => "STEADY",
        BatteryTrend.Settling => "SETTLING AFTER INSERT",
        _ => "COLLECTING DATA",
    };

    public static Brush TrendBrush(BatteryStats? stats) => stats?.Trend switch
    {
        BatteryTrend.Full or BatteryTrend.Charging => Term.Green,
        BatteryTrend.Discharging => For(stats?.Percent),
        _ => Term.Dim,
    };

    public static string Rate(BatteryStats? stats) => stats?.RatePerHour is double rate
        ? $"{(stats.Trend == BatteryTrend.Charging ? "+" : "−")}{rate:0.0}%/H"
        : "--";

    public static string Estimate(BatteryStats? stats) => stats?.Estimate is TimeSpan t
        ? (stats.Trend == BatteryTrend.Charging ? "FULL IN " : "") + Duration(t)
        : "--";

    public static string Duration(TimeSpan t) =>
        t.TotalMinutes < 60 ? $"~{Math.Max(1, (int)t.TotalMinutes)}M"
        : t.TotalHours < 48 ? $"~{(int)t.TotalHours}H {t.Minutes:00}M"
        : $"~{(int)t.TotalDays}D {t.Hours}H";

    public static string Ago(DateTime? time)
    {
        if (time is not DateTime t) return "--";
        var ago = DateTime.Now - t;
        return ago.TotalMinutes < 2 ? "NOW"
            : ago.TotalMinutes < 60 ? $"{(int)ago.TotalMinutes}M AGO"
            : ago.TotalHours < 48 ? $"{(int)ago.TotalHours}H AGO"
            : $"{(int)ago.TotalDays}D AGO";
    }

    public static string Range(BatteryStats? stats) => stats is null ? "--"
        : stats.Low24h == stats.High24h ? $"{stats.Low24h}%" : $"{stats.Low24h}–{stats.High24h}%";
}

public sealed class BatteryBar : FrameworkElement
{
    const int Segments = 20;
    static readonly Pen EmptyPen = MakePen(Term.Zinc800);
    int? _percent;

    static Pen MakePen(Brush brush)
    {
        var pen = new Pen(brush, 1);
        pen.Freeze();
        return pen;
    }

    public int? Percent
    {
        get => _percent;
        set
        {
            if (_percent == value) return;
            _percent = value;
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        const double gap = 2;
        double w = ActualWidth, h = ActualHeight;
        double cell = (w - gap * (Segments - 1)) / Segments;
        if (cell <= 0) return;
        int filled = _percent is int p ? (int)Math.Round(p / (100.0 / Segments)) : 0;
        if (_percent is > 0 && filled == 0) filled = 1;
        var brush = BatteryLook.For(_percent);
        for (int i = 0; i < Segments; i++)
        {
            var rect = new Rect(Math.Round(i * (cell + gap)) + 0.5, 0.5, Math.Max(1, Math.Round(cell) - 1), h - 1);
            if (i < filled) dc.DrawRectangle(brush, null, rect);
            else dc.DrawRectangle(null, EmptyPen, rect);
        }
    }
}

public sealed class BatteryIcon : FrameworkElement
{
    int? _percent;

    public BatteryIcon()
    {
        Width = 22;
        Height = 11;
        VerticalAlignment = VerticalAlignment.Center;
    }

    public int? Percent
    {
        get => _percent;
        set
        {
            if (_percent == value) return;
            _percent = value;
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        var brush = BatteryLook.For(_percent);
        var pen = new Pen(brush, 1);
        double w = ActualWidth - 3, h = ActualHeight;
        dc.DrawRectangle(null, pen, new Rect(0.5, 0.5, w - 1, h - 1));
        dc.DrawRectangle(brush, null, new Rect(w, h / 2 - 2.5, 2, 5));
        if (_percent is int p && p > 0)
            dc.DrawRectangle(brush, null, new Rect(2, 2, Math.Max(1, (w - 4) * p / 100.0), h - 4));
    }
}

public sealed class BatteryGraph : FrameworkElement
{
    static readonly Pen GridPen = MakePen(Term.Zinc800, 1, null);
    static readonly Pen AxisPen = MakePen(Term.Accent, 1, null);
    static readonly Pen MousePen = MakePen(Term.Fg, 1.5, null);
    static readonly Pen DockPen = MakePen(Term.Accent, 1.5, DashStyles.Dash);
    static readonly TimeSpan Window = TimeSpan.FromHours(24);
    IReadOnlyList<(DateTime Time, int? Level)> _mouse = [];
    IReadOnlyList<(DateTime Time, int? Level)> _dock = [];

    static Pen MakePen(Brush brush, double thickness, DashStyle? dash)
    {
        var pen = new Pen(brush, thickness);
        if (dash is not null) pen.DashStyle = dash;
        pen.Freeze();
        return pen;
    }

    public void Show(IEnumerable<(DateTime Time, int? Level)> mouse, IEnumerable<(DateTime Time, int? Level)> dock)
    {
        _mouse = mouse.ToList();
        _dock = dock.ToList();
        InvalidateVisual();
    }

    FormattedText Label(string text, Brush brush) => new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
        new Typeface(Term.Font, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 10, brush,
        VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected override void OnRender(DrawingContext dc)
    {
        const double left = 34, bottom = 18, top = 18, right = 16;
        double w = ActualWidth - left - right, h = ActualHeight - top - bottom;
        if (w <= 0 || h <= 0) return;
        var now = DateTime.Now;
        var from = now - Window;

        double X(DateTime t) => left + Math.Clamp((t - from).TotalHours / Window.TotalHours, 0, 1) * w;
        double Y(int level) => top + h - level / 100.0 * h;

        for (int level = 0; level <= 100; level += 25)
        {
            dc.DrawLine(GridPen, new Point(left, Y(level)), new Point(left + w, Y(level)));
            var t = Label($"{level}%", Term.Dim);
            dc.DrawText(t, new Point(left - t.Width - 4, Y(level) - t.Height / 2));
        }
        for (int hours = 24; hours >= 0; hours -= 6)
        {
            double x = X(now - TimeSpan.FromHours(hours));
            dc.DrawLine(GridPen, new Point(x, top), new Point(x, top + h));
            var t = Label(hours == 0 ? "NOW" : $"-{hours}H", Term.Dim);
            double tx = hours == 24 ? x : hours == 0 ? x - t.Width : x - t.Width / 2;
            dc.DrawText(t, new Point(tx, top + h + 3));
        }
        dc.DrawLine(AxisPen, new Point(left, top), new Point(left, top + h));
        dc.DrawLine(AxisPen, new Point(left, top + h), new Point(left + w, top + h));

        DrawSeries(dc, _dock, DockPen, X, Y, from, now);
        DrawSeries(dc, _mouse, MousePen, X, Y, from, now);

        var mouseLabel = Label("━ MOUSE", Term.Fg);
        dc.DrawText(mouseLabel, new Point(left, 2));
        dc.DrawText(Label("┅ DOCK", Term.Accent), new Point(left + mouseLabel.Width + 12, 2));

        if (!_mouse.Any(p => p.Level is not null) && !_dock.Any(p => p.Level is not null))
        {
            var t = Label("NO READINGS YET", Term.Dim);
            dc.DrawText(t, new Point(left + (w - t.Width) / 2, top + (h - t.Height) / 2));
        }
    }

    static void DrawSeries(DrawingContext dc, IReadOnlyList<(DateTime Time, int? Level)> points, Pen pen,
        Func<DateTime, double> x, Func<int, double> y, DateTime from, DateTime now)
    {
        if (points.Count == 0) return;
        var visible = points.Where(p => p.Time >= from).ToList();
        var before = points.LastOrDefault(p => p.Time < from);
        if (before.Time != default) visible.Insert(0, (from, before.Level));
        if (visible.Count == 0) return;

        var geometry = new StreamGeometry();
        int? last = null;
        using (var ctx = geometry.Open())
        {
            foreach (var (time, level) in visible)
            {
                if (level is not int value)
                {
                    last = null;
                    continue;
                }
                if (last is int previous)
                {
                    ctx.LineTo(new Point(x(time), y(previous)), true, false);
                    ctx.LineTo(new Point(x(time), y(value)), true, false);
                }
                else
                {
                    ctx.BeginFigure(new Point(x(time), y(value)), false, false);
                }
                last = value;
            }
            if (last is int end) ctx.LineTo(new Point(x(now), y(end)), true, false);
        }
        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
        if (last is int final) dc.DrawRectangle(pen.Brush, null, new Rect(x(now) - 3, y(final) - 3, 6, 6));
    }
}
