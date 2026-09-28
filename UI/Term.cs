using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace WallhackTerminal.UI;

public static class Term
{
    public const double Size = 14;
    public const double Line = 16;

    public static FontFamily Font { get; private set; } = new("Consolas");
    public static double Ch { get; private set; } = 8;

    public static readonly SolidColorBrush Bg = Solid(0x00, 0x00, 0x00);
    public static readonly SolidColorBrush Fg = Solid(0xFF, 0xFF, 0xFF);
    public static readonly SolidColorBrush Secondary = Solid(0xCC, 0xCC, 0xCC);
    public static readonly SolidColorBrush Accent = Solid(0x99, 0x99, 0x99);
    public static readonly SolidColorBrush Dim = Solid(0xFF, 0xFF, 0xFF, 0x80);
    public static readonly SolidColorBrush Dim70 = Solid(0xFF, 0xFF, 0xFF, 0xB3);
    public static readonly SolidColorBrush Dim40 = Solid(0xFF, 0xFF, 0xFF, 0x66);
    public static readonly SolidColorBrush Red = Solid(0xFB, 0x2C, 0x36);
    public static readonly SolidColorBrush Green = Solid(0x00, 0xC9, 0x50);
    public static readonly SolidColorBrush Yellow = Solid(0xFD, 0xC7, 0x00);
    public static readonly SolidColorBrush Zinc900 = Solid(0x18, 0x18, 0x1B);
    public static readonly SolidColorBrush Zinc800 = Solid(0x27, 0x27, 0x2A);
    public static readonly SolidColorBrush PressFill = Solid(0xFF, 0xFF, 0xFF, 0x55);

    static SolidColorBrush Solid(byte r, byte g, byte b, byte a = 0xFF)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    public static void Initialize()
    {
        try
        {
            var family = Fonts.GetFontFamilies(new Uri("pack://application:,,,/"), "./Assets/Fonts/").FirstOrDefault();
            if (family is not null) Font = family;
        }
        catch
        {
        }
        var sample = new FormattedText(new string('0', 20), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(Font, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), Size, Fg, 1.0);
        Ch = Math.Round(sample.WidthIncludingTrailingWhitespace / 20, 2);
    }

    public static double Cells(double n) => n * Ch;

    public static TextBlock Text(string text, Brush? brush = null) => new()
    {
        Text = text.ToUpperInvariant(),
        Foreground = brush ?? Fg,
        LineHeight = Line,
        LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        TextWrapping = TextWrapping.NoWrap,
    };

    public static TextBlock Wrapped(string text, Brush? brush = null, double widthCh = 90)
    {
        var block = Text(text, brush);
        block.TextWrapping = TextWrapping.Wrap;
        block.MaxWidth = Cells(widthCh);
        block.HorizontalAlignment = HorizontalAlignment.Left;
        return block;
    }

    public static FrameworkElement Blank(int lines = 1) => new Border { Height = Line * lines };

    public static TextBlock Rich(params (string Text, Brush? Brush)[] parts)
    {
        var block = Text("");
        block.Inlines.Clear();
        foreach (var (text, brush) in parts) block.Inlines.Add(new Run(text.ToUpperInvariant()) { Foreground = brush ?? Fg });
        return block;
    }

    public static FrameworkElement Indent(UIElement child, double cells = 4) =>
        new Border { Padding = new Thickness(Cells(cells), 0, 0, 0), Child = child };
}
