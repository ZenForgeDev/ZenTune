using System.Runtime.InteropServices;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ZenTune.Core.Pulse;

namespace ZenTune.App;

/// <summary>
/// A small window that stays on top of everything (also games that run in borderless window mode) and shows the most
/// important numbers. Only one can be open. It measures while it is open, like the Pulse page does.
/// </summary>
internal sealed class PulsePopout : Window
{
    public static PulsePopout? Current { get; private set; }

    /// <summary>Opens the popout, or brings it to the front if it is already open.</summary>
    public static void Open()
    {
        if (Current is { } existing)
        {
            existing.Activate();
            return;
        }

        Current = new PulsePopout();
        Current.Activate();
    }

    /// <summary>Opens the popout from the tray icon. Makes sure Pulse knows the graphics card first, as the Pulse page does.</summary>
    public static async void OpenFromTray()
    {
        try { await Pages.PulsePage.ConfigureMonitorAsync(); }
        catch { /* the popout works without the extra information */ }

        try { Open(); }
        catch { /* nothing more to do */ }
    }

    /// <summary>Called when the main window closes, so the program does not stay running because of the popout.</summary>
    public static void CloseIfOpen()
    {
        try { Current?.Close(); }
        catch { /* already closing */ }
    }

    private sealed class Row
    {
        public readonly TextBlock Value = new() { FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right };
        public readonly ProgressBar Bar = new() { Minimum = 0, Maximum = 100, Value = 0, Height = 6 };
        public readonly Grid Root = new() { RowSpacing = 4 };

        public Row(string label)
        {
            Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var name = new TextBlock
            {
                Text = label.ToUpperInvariant(),
                FontSize = 11,
                CharacterSpacing = 60,
                FontWeight = FontWeights.SemiBold,
                Foreground = Ui.Res("TextFillColorSecondaryBrush"),
                VerticalAlignment = VerticalAlignment.Bottom,
            };
            Value.MinWidth = 90;
            Value.TextAlignment = TextAlignment.Right;

            Grid.SetColumn(Value, 1);
            Grid.SetRow(Bar, 1);
            Grid.SetColumnSpan(Bar, 2);

            Root.Children.Add(name);
            Root.Children.Add(Value);
            Root.Children.Add(Bar);
        }

        public void Show(string text, double percent, PulseLevel level)
        {
            Value.Text = text;
            Bar.Value = Math.Clamp(percent, 0, 100);
            Bar.Foreground = PulseColors.Brush(level);
        }

        public void ShowNothing()
        {
            Value.Text = "n/a";
            Bar.Value = 0;
        }
    }

    private readonly PulseMonitor _monitor = PulseMonitor.Shared;
    private readonly Row _cpu = new("Processor");
    private readonly Row _gpu = new("Graphics card");
    private readonly Row _cpuTemp = new("CPU temperature");
    private readonly Row _temp = new("GPU temperature");
    private readonly Row _memory = new("Memory");
    private readonly Row _vram = new("Video memory");
    private bool _closed;

    private PulsePopout()
    {
        Title = "ZenTune Pulse";
        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();

        var stack = new StackPanel { Spacing = 14, Padding = new Thickness(18, 14, 18, 14) };
        foreach (var row in new[] { _cpu, _cpuTemp, _gpu, _temp, _memory, _vram })
        {
            row.ShowNothing();
            stack.Children.Add(row.Root);
        }

        Content = stack;

        if (!RestorePlacement()) PlaceInCorner(300, 390);

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsMaximizable = false;
        }

        Closed += OnClosed;
        _monitor.Updated += OnUpdated;
        _monitor.AddViewer();

        if (_monitor.Latest is { } latest) Render(latest);
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        if (_closed) return;
        _closed = true;

        SavePlacement();

        _monitor.Updated -= OnUpdated;
        _monitor.RemoveViewer();
        if (ReferenceEquals(Current, this)) Current = null;
    }

    private void OnUpdated(PulseSnapshot snapshot) => DispatcherQueue.TryEnqueue(() =>
    {
        if (!_closed) Render(snapshot);
    });

    private void Render(PulseSnapshot s)
    {
        _cpu.Show($"{s.CpuPercent:0} %", s.CpuPercent, PulseAdvice.Load(s.CpuPercent).Level);
        _memory.Show($"{s.MemoryPercent:0} %", s.MemoryPercent, PulseAdvice.Memory(s.MemoryPercent).Level);

        if (s.Sensors?.CpuTemperatureC is double cpuCelsius) _cpuTemp.Show($"{cpuCelsius:0} °C", cpuCelsius, PulseAdvice.CpuTemperature(cpuCelsius).Level);
        else _cpuTemp.ShowNothing();

        var gpu = s.Gpu;

        if (gpu?.UsagePercent is double usage) _gpu.Show($"{usage:0} %", usage, PulseAdvice.GpuLoad(usage).Level);
        else _gpu.ShowNothing();

        if (gpu?.TemperatureC is double celsius) _temp.Show($"{celsius:0} °C", celsius, PulseAdvice.Temperature(celsius).Level);
        else _temp.ShowNothing();

        if (gpu?.VramPercent is double vram) _vram.Show($"{vram:0} %", vram, PulseAdvice.VideoMemory(vram).Level);
        else _vram.ShowNothing();
    }

    private static string PlacementFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZenTune", "pulse-popout-v2.txt");

    /// <summary>Opens the window where it was when it was closed last time, if that place is still on a screen.</summary>
    private bool RestorePlacement()
    {
        try
        {
            var parts = File.ReadAllText(PlacementFile).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 4) return false;

            int x = int.Parse(parts[0]), y = int.Parse(parts[1]), w = int.Parse(parts[2]), h = int.Parse(parts[3]);
            if (w < 150 || h < 150 || w > 4000 || h > 4000) return false;

            var area = DisplayArea.GetFromPoint(new Windows.Graphics.PointInt32(x + 20, y + 20), DisplayAreaFallback.None);
            if (area is null) return false;

            AppWindow.Resize(new Windows.Graphics.SizeInt32(w, h));
            AppWindow.Move(new Windows.Graphics.PointInt32(x, y));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void SavePlacement()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PlacementFile)!);
            var position = AppWindow.Position;
            var size = AppWindow.Size;
            File.WriteAllText(PlacementFile, $"{position.X} {position.Y} {size.Width} {size.Height}");
        }
        catch
        {
            // Not remembering the place is not a problem
        }
    }

    /// <summary>Puts the window in the bottom right corner of the main screen, above the taskbar.</summary>
    private void PlaceInCorner(int width, int height)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        double scale = GetDpiForWindow(hwnd) / 96.0;
        if (scale <= 0) scale = 1;

        int w = (int)(width * scale);
        int h = (int)(height * scale);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(w, h));

        try
        {
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            int margin = (int)(16 * scale);
            AppWindow.Move(new Windows.Graphics.PointInt32(area.X + area.Width - w - margin, area.Y + area.Height - h - margin));
        }
        catch
        {
            // The window just opens where Windows puts it
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
