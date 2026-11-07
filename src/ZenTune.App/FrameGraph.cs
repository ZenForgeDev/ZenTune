using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace ZenTune.App;

/// <summary>
/// Frame times of two recordings on top of each other (time on the bottom, milliseconds per frame up the side).
/// Low is good. A spike is a frame that took long, which you feel as a stutter.
/// </summary>
internal sealed class FrameGraph : StackPanel
{
    private readonly IReadOnlyList<double> _a;
    private readonly IReadOnlyList<double> _b;
    private readonly Canvas _canvas = new() { Height = 220 };
    private readonly Brush _brushA = new SolidColorBrush(Color.FromArgb(170, 170, 182, 205));
    private readonly Brush _brushB = Ui.Res("AccentFillColorDefaultBrush");
    private readonly TextBlock _note = Ui.Secondary("");

    public FrameGraph(IReadOnlyList<double> a, IReadOnlyList<double> b, string labelA, string labelB)
    {
        _a = a;
        _b = b;
        Spacing = 8;

        Children.Add(new TextBlock { Text = "Frame times", FontWeight = FontWeights.SemiBold });

        var legend = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        legend.Children.Add(LegendItem(_brushA, labelA));
        legend.Children.Add(LegendItem(_brushB, labelB));
        Children.Add(legend);

        Children.Add(_canvas);
        Children.Add(_note);

        _canvas.SizeChanged += (_, _) => Draw();
    }

    private static UIElement LegendItem(Brush brush, string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(new Border { Width = 14, Height = 4, CornerRadius = new CornerRadius(2), Background = brush, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = text });
        return row;
    }

    private void Draw()
    {
        double w = _canvas.ActualWidth;
        double h = _canvas.ActualHeight;
        if (w < 160 || h < 80) return;

        _canvas.Children.Clear();

        const double left = 52, right = 8, top = 8, bottom = 24;
        double pw = w - left - right;
        double ph = h - top - bottom;

        double seconds = Math.Max(_a.Sum(), _b.Sum()) / 1000.0;
        if (seconds <= 0) return;

        // The top of the graph: the slowest frame, so the worst stutter is seen in full height. At least 20 ms, at most 100 ms
        // (one freak frame of several seconds must not flatten everything else).
        double slowest = Math.Max(_a.Max(), _b.Max());
        double yMax = Math.Ceiling(Math.Clamp(slowest, 20, 100) / 10) * 10;

        var gridBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
        var textBrush = Ui.Res("TextFillColorTertiaryBrush");

        // Horizontal lines with milliseconds
        for (int i = 0; i <= 4; i++)
        {
            double value = yMax * i / 4.0;
            double y = top + ph * (1 - value / yMax);

            _canvas.Children.Add(new Line { X1 = left, X2 = left + pw, Y1 = y, Y2 = y, Stroke = gridBrush, StrokeThickness = 1 });

            var label = new TextBlock { Text = $"{value:0} ms", FontSize = 11, Foreground = textBrush };
            Canvas.SetLeft(label, 4);
            Canvas.SetTop(label, y - 8);
            _canvas.Children.Add(label);
        }

        // Time along the bottom
        for (int i = 0; i <= 2; i++)
        {
            double t = seconds * i / 2.0;
            var label = new TextBlock { Text = $"{t:0} s", FontSize = 11, Foreground = textBrush };
            Canvas.SetLeft(label, left + pw * i / 2.0 - (i == 0 ? 0 : i == 2 ? 24 : 12));
            Canvas.SetTop(label, h - bottom + 5);
            _canvas.Children.Add(label);
        }

        AddLine(_a, _brushA, 1.0, seconds, yMax, left, top, pw, ph);
        AddLine(_b, _brushB, 1.6, seconds, yMax, left, top, pw, ph);

        _note.Text = $"Lower is better. Each spike is a stutter. The older recording is the thin grey line.";
    }

    /// <summary>One line. Many frames share one pixel, so each pixel column shows the slowest frame in it: spikes never disappear.</summary>
    private void AddLine(IReadOnlyList<double> frames, Brush brush, double thickness, double seconds, double yMax, double left, double top, double pw, double ph)
    {
        int buckets = Math.Max(2, (int)Math.Min(pw, 700));
        var worst = new double[buckets];
        Array.Fill(worst, double.NaN);

        double t = 0;
        foreach (var ms in frames)
        {
            int k = Math.Min(buckets - 1, (int)(t / 1000.0 / seconds * buckets));
            if (k >= 0 && (double.IsNaN(worst[k]) || ms > worst[k])) worst[k] = ms;
            t += ms;
        }

        var points = new PointCollection();
        for (int k = 0; k < buckets; k++)
        {
            if (double.IsNaN(worst[k])) continue;
            double x = left + (k + 0.5) / buckets * pw;
            double y = top + ph * (1 - Math.Min(worst[k], yMax) / yMax);
            points.Add(new Windows.Foundation.Point(x, y));
        }
        if (points.Count < 2) return;

        _canvas.Children.Add(new Polyline { Points = points, Stroke = brush, StrokeThickness = thickness, StrokeLineJoin = PenLineJoin.Round });
    }
}
