using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using ZenTune.Core.Pulse;

namespace ZenTune.App;

/// <summary>The colours Pulse uses to show how a part is doing. One place, so the page and the popout match.</summary>
internal static class PulseColors
{
    private static readonly Dictionary<PulseLevel, SolidColorBrush> Solid = [];
    private static readonly Dictionary<PulseLevel, LinearGradientBrush> Fades = [];

    public static Windows.UI.Color Of(PulseLevel level) => level switch
    {
        PulseLevel.Idle => Windows.UI.Color.FromArgb(255, 108, 152, 214),
        PulseLevel.Fine => Windows.UI.Color.FromArgb(255, 72, 196, 128),
        PulseLevel.High => Windows.UI.Color.FromArgb(255, 245, 172, 52),
        _ => Windows.UI.Color.FromArgb(255, 236, 84, 92),
    };

    public static SolidColorBrush Brush(PulseLevel level)
    {
        if (!Solid.TryGetValue(level, out var brush))
        {
            brush = new SolidColorBrush(Of(level));
            Solid[level] = brush;
        }
        return brush;
    }

    /// <summary>The colour fading out towards the bottom, for the area under a graph line.</summary>
    public static LinearGradientBrush Fade(PulseLevel level)
    {
        if (!Fades.TryGetValue(level, out var brush))
        {
            var c = Of(level);
            brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            brush.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(120, c.R, c.G, c.B), Offset = 0 });
            brush.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(6, c.R, c.G, c.B), Offset = 1 });
            Fades[level] = brush;
        }
        return brush;
    }
}

/// <summary>
/// One card on the Pulse page: a title, a big number, a plain-words comment, a graph of the last minute and its
/// lowest / average / highest. Every text has a fixed height and the card has a fixed height, so nothing moves when numbers change.
/// The colour (side strip, icon and graph) follows how the part is doing.
/// </summary>
internal sealed class PulseCard
{
    private const int Capacity = 60; // one reading per second, so one minute
    public const double CardHeight = 262;

    private readonly double _min;
    private readonly double? _max;
    private readonly Func<double, string> _format;
    private readonly List<double> _history = [];

    private readonly FontIcon _icon;
    private readonly Border _strip;
    private readonly TextBlock _value;
    private readonly TextBlock _note;
    private readonly TextBlock _extra;
    private readonly TextBlock _stats;
    private readonly Canvas _canvas;
    private readonly Polygon _area;
    private readonly Polyline _line;

    private PulseLevel? _level;

    public Border Root { get; }

    /// <param name="min">The bottom of the graph.</param>
    /// <param name="max">The top of the graph (100 for percent). Null lets the graph scale itself, for example for network speed.</param>
    /// <param name="format">How a number from the graph is written in the "lowest / average / highest" line.</param>
    public PulseCard(string glyph, string caption, double min = 0, double? max = 100, Func<double, string>? format = null)
    {
        _min = min;
        _max = max;
        _format = format ?? (v => $"{v:0} %");

        _icon = new FontIcon { Glyph = glyph, FontSize = 14 };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Height = 20 };
        head.Children.Add(_icon);
        head.Children.Add(new TextBlock
        {
            Text = caption.ToUpperInvariant(),
            FontSize = 11,
            CharacterSpacing = 60,
            FontWeight = FontWeights.SemiBold,
            Foreground = Ui.Res("TextFillColorSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        });

        _value = new TextBlock
        {
            Text = "…",
            Style = Ui.StyleRes("TitleTextBlockStyle"),
            Height = 40,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        // Two lines are reserved for the comment, used or not
        _note = new TextBlock
        {
            Text = "Measuring…",
            Height = 42,
            MaxLines = 2,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontWeight = FontWeights.SemiBold,
            Foreground = Ui.Res("TextFillColorSecondaryBrush"),
        };

        _extra = Ui.Secondary("");
        _extra.Height = 20;
        _extra.TextWrapping = TextWrapping.NoWrap;
        _extra.TextTrimming = TextTrimming.CharacterEllipsis;

        _area = new Polygon();
        _line = new Polyline { StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
        _canvas = new Canvas { MinHeight = 40, Margin = new Thickness(0, 4, 0, 4) };
        _canvas.Children.Add(_area);
        _canvas.Children.Add(_line);
        _canvas.SizeChanged += (_, _) => Redraw();

        _stats = Ui.Secondary("");
        _stats.Height = 18;
        _stats.FontSize = 11;
        _stats.TextWrapping = TextWrapping.NoWrap;
        _stats.TextTrimming = TextTrimming.CharacterEllipsis;

        var grid = new Grid { Margin = new Thickness(20, 14, 18, 12) };
        for (int i = 0; i < 6; i++)
            grid.RowDefinitions.Add(new RowDefinition { Height = i == 4 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });

        Put(grid, head, 0);
        Put(grid, _value, 1);
        Put(grid, _note, 2);
        Put(grid, _extra, 3);
        Put(grid, _canvas, 4);
        Put(grid, _stats, 5);

        _strip = new Border
        {
            Width = 4,
            CornerRadius = new CornerRadius(2),
            Margin = new Thickness(8, 14, 0, 14),
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        var layers = new Grid();
        layers.Children.Add(grid);
        layers.Children.Add(_strip);

        Root = Ui.CardFrame(layers, new Thickness(0));
        Root.Height = CardHeight;
        Root.MinWidth = 0;

        ShowNothingYet();
    }

    private static void Put(Grid grid, UIElement element, int row)
    {
        Grid.SetRow((FrameworkElement)element, row);
        grid.Children.Add(element);
    }

    /// <summary>Shows a new reading. "sample" is the number the graph draws.</summary>
    public void Update(string value, PulseNote note, string? extra, double sample)
    {
        SetLevel(note.Level);

        _value.Text = value;
        _note.Text = note.Text;
        ToolTipService.SetToolTip(_note, note.Text);
        _extra.Text = extra ?? "";

        _history.Add(double.IsFinite(sample) ? sample : 0);
        if (_history.Count > Capacity) _history.RemoveAt(0);

        _stats.Text = $"Last minute   lowest {_format(_history.Min())}  ·  average {_format(_history.Average())}  ·  highest {_format(_history.Max())}";
        Redraw();
    }

    /// <summary>For a number that cannot be read on this PC.</summary>
    public void ShowUnavailable(string message)
    {
        _history.Clear();
        _level = null;
        ShowNothingYet();

        _value.Text = "Not available";
        _note.Text = message;
        ToolTipService.SetToolTip(_note, message);
    }

    private void ShowNothingYet()
    {
        var grey = Ui.Res("TextFillColorDisabledBrush");
        _icon.Foreground = grey;
        _strip.Background = grey;
        _note.Foreground = Ui.Res("TextFillColorSecondaryBrush");
        _extra.Text = "";
        _stats.Text = "";
        _line.Points = new PointCollection();
        _area.Points = new PointCollection();
    }

    private void SetLevel(PulseLevel level)
    {
        if (_level == level) return;
        _level = level;

        var brush = PulseColors.Brush(level);
        _icon.Foreground = brush;
        _strip.Background = brush;
        _note.Foreground = brush;
        _line.Stroke = brush;
        _area.Fill = PulseColors.Fade(level);
    }

    private void Redraw()
    {
        double width = _canvas.ActualWidth;
        double height = _canvas.ActualHeight;
        if (width <= 1 || height <= 1 || _history.Count < 2) return;

        double top = _max ?? Math.Max(1, _history.Max() * 1.15);
        double range = Math.Max(0.0001, top - _min);
        double step = width / (Capacity - 1);

        var line = new PointCollection();
        for (int i = 0; i < _history.Count; i++)
        {
            // The newest reading sits at the right edge
            double x = width - (_history.Count - 1 - i) * step;
            double fraction = Math.Clamp((_history[i] - _min) / range, 0, 1);
            line.Add(new Point(x, 2 + (height - 4) * (1 - fraction)));
        }

        var area = new PointCollection();
        foreach (var point in line) area.Add(point);
        area.Add(new Point(line[line.Count - 1].X, height));
        area.Add(new Point(line[0].X, height));

        _line.Points = line;
        _area.Points = area;
    }
}
