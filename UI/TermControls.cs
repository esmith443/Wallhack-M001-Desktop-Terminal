using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WallhackTerminal.UI;

public sealed class TermRow : Border
{
    readonly TextBlock _label, _dots, _value, _ltText, _gtText;
    readonly Border _lt, _gt;
    readonly Grid _carousel;
    TextBox? _editor;
    bool _enabled = true;

    public Func<string>? Label { get; init; }
    public Func<string>? Value { get; init; }
    public Func<bool>? Enabled { get; init; }
    public Action<int>? Step { get; init; }
    public Action? Activate { get; init; }
    public Func<string, bool>? Commit { get; init; }
    public Func<string>? EditText { get; init; }
    public string? HelpTitle { get; init; }
    public string? HelpText { get; init; }
    public bool Static { get; init; }

    public TermRow(string label, double widthCh = 56, double valueCh = 16, bool arrows = true)
    {
        Focusable = true;
        FocusVisualStyle = null;
        Background = Brushes.Transparent;
        BorderBrush = Brushes.Transparent;
        BorderThickness = new Thickness(1);
        Width = Term.Cells(widthCh);
        Height = Term.Line;
        HorizontalAlignment = HorizontalAlignment.Left;
        SnapsToDevicePixels = true;

        _label = Cell(label);
        _dots = Cell(new string('.', 400));
        _dots.ClipToBounds = true;
        _dots.MinWidth = Term.Cells(3);
        _dots.Margin = new Thickness(0, 0, Term.Cells(1), 0);
        _value = Cell("");
        _value.HorizontalAlignment = HorizontalAlignment.Center;
        _value.Cursor = Cursors.IBeam;
        _ltText = Cell("<");
        _gtText = Cell(">");
        _lt = new Border { Child = _ltText, Background = Brushes.Transparent, Visibility = arrows ? Visibility.Visible : Visibility.Hidden };
        _gt = new Border { Child = _gtText, Background = Brushes.Transparent, Visibility = arrows ? Visibility.Visible : Visibility.Hidden };

        _carousel = new Grid { Width = Term.Cells(valueCh) };
        _carousel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _carousel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _carousel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_value, 1);
        Grid.SetColumn(_gt, 2);
        _carousel.Children.Add(_lt);
        _carousel.Children.Add(_value);
        _carousel.Children.Add(_gt);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_dots, 1);
        Grid.SetColumn(_carousel, 2);
        grid.Children.Add(_label);
        grid.Children.Add(_dots);
        grid.Children.Add(_carousel);
        Child = grid;

        foreach (var (arrow, direction) in new[] { (_lt, -1), (_gt, 1) })
        {
            arrow.MouseEnter += (_, _) => UpdateVisual();
            arrow.MouseLeave += (_, _) => UpdateVisual();
            arrow.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                if (!_enabled) return;
                Focus();
                Step?.Invoke(direction * (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1));
            };
        }
        _value.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            if (!_enabled) return;
            Focus();
            Fire(1);
        };
    }

    static TextBlock Cell(string text)
    {
        var block = Term.Text(text);
        block.LineHeight = Term.Line - 2;
        block.VerticalAlignment = VerticalAlignment.Center;
        return block;
    }

    void Fire(int direction)
    {
        if (Commit is not null) BeginEdit();
        else if (Activate is not null) Activate();
        else Step?.Invoke(direction);
    }

    public void Refresh()
    {
        if (Label is not null) _label.Text = Label().ToUpperInvariant();
        _enabled = Enabled?.Invoke() ?? true;
        Focusable = _enabled && !Static;
        if (_editor is null) _value.Text = (Value?.Invoke() ?? "").ToUpperInvariant();
        Cursor = !_enabled ? Cursors.No : Step is not null || Activate is not null || Commit is not null ? Cursors.Hand : Cursors.Arrow;
        UpdateVisual();
    }

    void UpdateVisual()
    {
        bool focused = IsKeyboardFocusWithin;
        bool hot = _enabled && !Static && (IsMouseOver || focused);
        Background = focused ? Term.Fg : Brushes.Transparent;
        BorderBrush = hot ? Term.Fg : Brushes.Transparent;
        var text = focused ? Term.Bg : Term.Fg;
        _label.Foreground = _dots.Foreground = _value.Foreground = text;
        Arrow(_lt, _ltText, focused);
        Arrow(_gt, _gtText, focused);
        Opacity = _enabled ? 1 : 0.5;
    }

    void Arrow(Border arrow, TextBlock glyph, bool focused)
    {
        bool over = _enabled && arrow.IsMouseOver;
        arrow.Background = over ? Term.Bg : Brushes.Transparent;
        glyph.Foreground = over || !focused ? Term.Fg : Term.Bg;
    }

    protected override void OnMouseEnter(MouseEventArgs e) { base.OnMouseEnter(e); UpdateVisual(); }
    protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); UpdateVisual(); }
    protected override void OnIsKeyboardFocusWithinChanged(DependencyPropertyChangedEventArgs e) { base.OnIsKeyboardFocusWithinChanged(e); UpdateVisual(); }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!_enabled) return;
        Focus();
        if (Step is null && (Activate is not null || Commit is not null)) Fire(1);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!_enabled || _editor is not null || e.Handled) return;
        int big = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
        switch (e.Key)
        {
            case Key.Left when Step is not null:
                Step(-big);
                e.Handled = true;
                break;
            case Key.Right when Step is not null:
                Step(big);
                e.Handled = true;
                break;
            case Key.Enter:
            case Key.Space:
                Fire(1);
                e.Handled = true;
                break;
        }
    }

    void BeginEdit()
    {
        if (_editor is not null || Commit is null) return;
        var editor = new TextBox
        {
            Text = EditText?.Invoke() ?? _value.Text,
            Background = Brushes.Transparent,
            Foreground = Term.Bg,
            CaretBrush = Term.Bg,
            SelectionBrush = Term.Accent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Margin = new Thickness(0, -1, 0, -1),
            FontFamily = Term.Font,
            FontSize = Term.Size,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        _editor = editor;
        Grid.SetColumn(editor, 1);
        _carousel.Children.Add(editor);
        _value.Visibility = Visibility.Hidden;
        editor.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                if (Commit(editor.Text))
                {
                    EndEdit();
                    Focus();
                }
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                EndEdit();
                Focus();
            }
        };
        editor.LostKeyboardFocus += (_, _) => EndEdit();
        editor.Focus();
        editor.SelectAll();
    }

    void EndEdit()
    {
        if (_editor is not { } editor) return;
        _editor = null;
        _carousel.Children.Remove(editor);
        _value.Visibility = Visibility.Visible;
        Refresh();
    }
}

public sealed class TermTabs : StackPanel
{
    readonly List<(string Id, Border Box, TextBlock Text)> _items = [];

    public string Selected { get; private set; }
    public event Action<string>? SelectionChanged;

    public TermTabs(IEnumerable<(string Id, string Label)> tabs, string selected, double gapCh = 4)
    {
        Orientation = Orientation.Horizontal;
        Focusable = true;
        FocusVisualStyle = null;
        Background = Brushes.Transparent;
        Height = Term.Line;
        HorizontalAlignment = HorizontalAlignment.Left;
        Selected = selected;

        foreach (var (id, label) in tabs)
        {
            var text = Term.Text(label.ToUpperInvariant());
            text.LineHeight = Term.Line - 2;
            var box = new Border
            {
                BorderThickness = new Thickness(1),
                Child = text,
                Margin = new Thickness(0, 0, Term.Cells(gapCh), 0),
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent,
            };
            box.MouseEnter += (_, _) => UpdateVisual();
            box.MouseLeave += (_, _) => UpdateVisual();
            box.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                Focus();
                Select(id);
            };
            _items.Add((id, box, text));
            Children.Add(box);
        }
        UpdateVisual();
    }

    public void Select(string id)
    {
        if (Selected == id) return;
        Selected = id;
        UpdateVisual();
        SelectionChanged?.Invoke(id);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        int i = _items.FindIndex(t => t.Id == Selected);
        if (e.Key == Key.Left && i > 0)
        {
            Select(_items[i - 1].Id);
            e.Handled = true;
        }
        else if (e.Key == Key.Right && i < _items.Count - 1)
        {
            Select(_items[i + 1].Id);
            e.Handled = true;
        }
    }

    protected override void OnIsKeyboardFocusedChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnIsKeyboardFocusedChanged(e);
        UpdateVisual();
    }

    void UpdateVisual()
    {
        bool focused = IsKeyboardFocused;
        foreach (var (id, box, text) in _items)
        {
            bool selected = id == Selected;
            box.BorderBrush = selected || box.IsMouseOver ? Term.Fg : Brushes.Transparent;
            box.Background = selected && focused ? Term.Fg : Brushes.Transparent;
            text.Foreground = selected && focused ? Term.Bg : Term.Fg;
        }
    }
}

public sealed class TermButton : Border
{
    readonly TextBlock _text;
    readonly Action _click;
    readonly bool _framed;

    public string? HelpTitle { get; init; }
    public string? HelpText { get; init; }

    public TermButton(string label, Action click, bool framed = false)
    {
        _click = click;
        _framed = framed;
        Focusable = true;
        FocusVisualStyle = null;
        BorderThickness = new Thickness(1);
        Background = Brushes.Transparent;
        Cursor = Cursors.Hand;
        Height = Term.Line;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Center;
        Padding = new Thickness(Term.Cells(0.5), 0, Term.Cells(0.5), 0);
        _text = Term.Text(label.ToUpperInvariant());
        _text.LineHeight = Term.Line - 2;
        _text.VerticalAlignment = VerticalAlignment.Center;
        Child = _text;
        UpdateVisual();
    }

    public string Label
    {
        set => _text.Text = value.ToUpperInvariant();
    }

    void UpdateVisual()
    {
        bool focused = IsKeyboardFocused;
        Background = focused ? Term.Fg : Brushes.Transparent;
        _text.Foreground = focused ? Term.Bg : Term.Fg;
        BorderBrush = _framed || focused || IsMouseOver ? Term.Fg : Brushes.Transparent;
    }

    protected override void OnMouseEnter(MouseEventArgs e) { base.OnMouseEnter(e); UpdateVisual(); }
    protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); UpdateVisual(); }
    protected override void OnIsKeyboardFocusedChanged(DependencyPropertyChangedEventArgs e) { base.OnIsKeyboardFocusedChanged(e); UpdateVisual(); }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        e.Handled = true;
        Focus();
        _click();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is Key.Enter or Key.Space)
        {
            e.Handled = true;
            _click();
        }
    }
}

public sealed class TermList : Border
{
    readonly StackPanel _stack = new();
    readonly List<(Border Row, TextBlock Text)> _rows = [];
    readonly double _widthCh;

    public int Index { get; private set; }
    public int Count => _rows.Count;
    public bool ActivateOnClick { get; init; } = true;

    public event Action<int>? IndexChanged;
    public event Action<int>? Activated;
    public event Action<KeyEventArgs>? OtherKey;

    public TermList(double widthCh)
    {
        _widthCh = widthCh;
        Focusable = true;
        FocusVisualStyle = null;
        Background = Brushes.Transparent;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        Child = _stack;
    }

    public void SetItems(IEnumerable<string> items, int index = 0)
    {
        _stack.Children.Clear();
        _rows.Clear();
        foreach (var item in items)
        {
            int i = _rows.Count;
            var text = Term.Text(item.ToUpperInvariant());
            text.LineHeight = Term.Line - 2;
            text.TextTrimming = TextTrimming.CharacterEllipsis;
            var row = new Border
            {
                Child = text,
                Width = Term.Cells(_widthCh),
                Height = Term.Line,
                BorderThickness = new Thickness(1),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
            };
            row.MouseEnter += (_, _) => UpdateVisual();
            row.MouseLeave += (_, _) => UpdateVisual();
            row.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                Focus();
                SetIndex(i);
                if (ActivateOnClick) Activated?.Invoke(i);
            };
            _rows.Add((row, text));
            _stack.Children.Add(row);
        }
        Index = _rows.Count == 0 ? 0 : Math.Clamp(index, 0, _rows.Count - 1);
        UpdateVisual();
    }

    public void SetIndex(int index)
    {
        if (_rows.Count == 0) return;
        index = Math.Clamp(index, 0, _rows.Count - 1);
        if (index == Index) return;
        Index = index;
        UpdateVisual();
        _rows[index].Row.BringIntoView();
        IndexChanged?.Invoke(index);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.Up when Index > 0:
                SetIndex(Index - 1);
                e.Handled = true;
                break;
            case Key.Down when Index < _rows.Count - 1:
                SetIndex(Index + 1);
                e.Handled = true;
                break;
            case Key.Enter:
            case Key.Space:
                if (_rows.Count > 0) Activated?.Invoke(Index);
                e.Handled = true;
                break;
            case Key.Up:
            case Key.Down:
                break;
            default:
                OtherKey?.Invoke(e);
                break;
        }
    }

    protected override void OnIsKeyboardFocusedChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnIsKeyboardFocusedChanged(e);
        UpdateVisual();
    }

    void UpdateVisual()
    {
        bool focused = IsKeyboardFocused;
        for (int i = 0; i < _rows.Count; i++)
        {
            var (row, text) = _rows[i];
            bool selected = i == Index;
            row.Background = selected && focused ? Term.Fg : Brushes.Transparent;
            text.Foreground = selected && focused ? Term.Bg : Term.Fg;
            row.BorderBrush = selected || row.IsMouseOver ? Term.Fg : Brushes.Transparent;
        }
    }
}

public sealed class TermDrawer : Border
{
    const double HeaderHeight = 24;
    readonly Border _header;
    readonly TextBlock _title, _chevron;
    readonly Border _body;
    readonly double _expandedHeight;
    bool _expanded;

    public event Action<bool>? ExpandedChanged;

    public TermDrawer(string title, UIElement content, double width, double expandedHeight, bool expanded)
    {
        _expandedHeight = expandedHeight;
        Background = Term.Bg;
        BorderBrush = Term.Fg;
        BorderThickness = new Thickness(1, 1, 1, 0);
        Width = width;
        VerticalAlignment = VerticalAlignment.Bottom;

        _title = Term.Text(title.ToUpperInvariant());
        _title.VerticalAlignment = VerticalAlignment.Center;
        _chevron = Term.Text("");
        _chevron.VerticalAlignment = VerticalAlignment.Center;
        _chevron.HorizontalAlignment = HorizontalAlignment.Right;
        var headerGrid = new Grid { Margin = new Thickness(8, 0, 8, 0) };
        headerGrid.Children.Add(_title);
        headerGrid.Children.Add(_chevron);
        _header = new Border { Height = HeaderHeight, Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = headerGrid };
        _header.MouseEnter += (_, _) => _header.Background = Term.Zinc900;
        _header.MouseLeave += (_, _) => _header.Background = Brushes.Transparent;
        _header.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            Expanded = !Expanded;
        };

        _body = new Border { Child = content };
        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_header, Dock.Top);
        dock.Children.Add(_header);
        dock.Children.Add(_body);
        Child = dock;
        _expanded = !expanded;
        Expanded = expanded;
    }

    public UIElement HeaderExtra
    {
        set
        {
            var grid = (Grid)_header.Child;
            if (value is FrameworkElement fe)
            {
                fe.HorizontalAlignment = HorizontalAlignment.Right;
                fe.Margin = new Thickness(0, 0, Term.Cells(3), 0);
                fe.VerticalAlignment = VerticalAlignment.Center;
            }
            grid.Children.Add(value);
        }
    }

    public string Title
    {
        set => _title.Text = value.ToUpperInvariant();
    }

    public bool Expanded
    {
        get => _expanded;
        set
        {
            if (_expanded == value) return;
            _expanded = value;
            Height = value ? _expandedHeight : HeaderHeight + 1;
            _body.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            _chevron.Text = value ? "⌄" : "⌃";
            ExpandedChanged?.Invoke(value);
        }
    }
}

public sealed class QuietScrollViewer : ScrollViewer
{
    protected override void OnKeyDown(KeyEventArgs e) { }
}
