using System.Diagnostics;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ZenTune.Core.Advanced;
using ZenTune.Core.Pulse;

namespace ZenTune.App.Pages;

/// <summary>
/// "Pulse": how busy the PC is right now, in plain words, with colours that show how each part is doing,
/// a graph of the last minute, every processor thread, the programs that use the most, a small always-on-top
/// popout, and logging to text files in Documents\Pulse.
/// Measuring only runs while this page (or the popout) is open or a log is running.
/// Everything on the page has a fixed size, so nothing moves when the numbers change.
/// </summary>
internal sealed partial class PulsePage : UserControl
{
    private const int TopRows = 5;
    private const double ThreadTrackHeight = 64;

    private readonly PulseMonitor _monitor = PulseMonitor.Shared;
    private readonly InfoBar _status;

    // Top of the page
    private readonly Border _heroStrip = new() { Width = 4, CornerRadius = new CornerRadius(2), Margin = new Thickness(8, 14, 0, 14), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _heroTitle;
    private readonly TextBlock _heroSub;

    // Cards
    private readonly PulseCard _cpu = new(Ui.IconCpu, "Processor");
    private readonly PulseCard _gpu = new(Ui.IconGame, "Graphics card");
    private readonly PulseCard _temp = new(Ui.IconHealth, "Graphics card temperature", min: 20, max: 100, format: v => $"{v:0} °C");
    private readonly PulseCard _memory = new(Ui.IconMemory, "Memory");
    private readonly PulseCard _vram = new(Ui.IconMonitor, "Video memory");
    private readonly PulseCard _disk = new(Ui.IconSave, "Disk");
    private readonly PulseCard _network = new(Ui.IconRefresh, "Network (busiest adapter)", max: null, format: v => PulseAdvice.Rate(v));
    private readonly PulseCard _cpuTemp = new(Ui.IconHealth, "Processor temperature", min: 20, max: 110, format: v => $"{v:0} °C");
    private readonly PulseCard _cpuSpeed = new(Ui.IconCpu, "Processor speed", max: null, format: v => $"{v:0} MHz");
    private readonly PulseCard _cpuPower = new(Ui.IconCpu, "Processor power", max: null, format: v => $"{v:0} W");

    // Sensors (off by default)
    private readonly ToggleSwitch _sensorSwitch = new() { OnContent = "On", OffContent = "Off", MinWidth = 0 };
    private readonly TextBlock _sensorInfo = Ui.Secondary("");
    private readonly Button _installDriver = new() { Content = "Install sensor driver (PawnIO)" };
    private readonly Button _restartAdmin = new() { Content = "Restart as administrator" };
    private readonly Button _removeDriver = new() { Content = "Remove sensor driver" };
    private bool _sensorSwitchBusy;

    // Processor threads
    private readonly Grid _threadGrid = new() { ColumnSpacing = 3, Height = ThreadTrackHeight + 18 };
    private readonly TextBlock _threadSummary = Ui.Secondary("");
    private readonly List<(Border Bar, FrameworkElement Track)> _threads = [];

    // Programs
    private readonly List<(TextBlock Name, TextBlock Value)> _cpuRows = [];
    private readonly List<(TextBlock Name, TextBlock Value)> _gpuRows = [];
    private readonly List<(TextBlock Name, TextBlock Value)> _memoryRows = [];

    // Details and logging
    private readonly TextBlock _limits = new() { TextWrapping = TextWrapping.Wrap, Text = "Measuring…", MinHeight = 64 };
    private readonly ComboBox _interval = new() { MinWidth = 170 };
    private readonly Button _logButton;
    private readonly Button _openLog;
    private readonly TextBlock _logInfo = Ui.Secondary("");

    private string? _lastFolder;
    private bool _attached;
    private bool _loggingShown;

    public PulsePage()
    {
        var (root, body, status) = Ui.Page("Pulse",
            "How busy your PC is right now, in plain words. It only measures while this page or the popout is open, or a log is running.");
        _status = status;

        // ---- Top: overall state and the two main buttons
        _heroTitle = new TextBlock
        {
            Text = "Measuring…",
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Height = 30,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _heroSub = Ui.Secondary("");
        _heroSub.Height = 20;
        _heroSub.TextWrapping = TextWrapping.NoWrap;
        _heroSub.TextTrimming = TextTrimming.CharacterEllipsis;

        _logButton = new Button { MinWidth = 150 };
        SetLogButton(logging: false);
        _logButton.Click += (_, _) => ToggleLogging();

        var popout = new Button { MinWidth = 120 };
        var popoutContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        popoutContent.Children.Add(new FontIcon { Glyph = "", FontSize = 14 });
        popoutContent.Children.Add(new TextBlock { Text = "Pop out" });
        popout.Content = popoutContent;
        ToolTipService.SetToolTip(popout, "A small window that stays on top of your game");
        popout.Click += (_, _) => PulsePopout.Open();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 18, 0) };
        buttons.Children.Add(popout);
        buttons.Children.Add(_logButton);

        var heroText = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(26, 10, 8, 10) };
        heroText.Children.Add(_heroTitle);
        heroText.Children.Add(_heroSub);

        var heroGrid = new Grid();
        heroGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heroGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heroGrid.Children.Add(heroText);
        Grid.SetColumn(buttons, 1);
        heroGrid.Children.Add(buttons);

        var heroLayers = new Grid();
        heroLayers.Children.Add(heroGrid);
        heroLayers.Children.Add(_heroStrip);

        var hero = Ui.CardFrame(heroLayers, new Thickness(0));
        hero.Height = 78;
        body.Children.Add(hero);
        _heroStrip.Background = Ui.Res("TextFillColorDisabledBrush");

        // ---- The cards, two per row
        var grid = new Grid { ColumnSpacing = 16, RowSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 0 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 0 });

        var cards = new[] { _cpu, _gpu, _cpuTemp, _temp, _memory, _vram, _disk, _network, _cpuSpeed, _cpuPower };
        for (int i = 0; i < cards.Length; i++)
        {
            if (i % 2 == 0) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var element = cards[i].Root;
            Grid.SetRow(element, i / 2);
            Grid.SetColumn(element, i % 2);
            grid.Children.Add(element);
        }
        body.Children.Add(grid);

        // ---- Processor threads
        var threadStack = new StackPanel { Spacing = 8 };
        var threadHead = new Grid();
        threadHead.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        threadHead.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        threadHead.Children.Add(Caption("Every processor thread"));
        Grid.SetColumn(_threadSummary, 1);
        threadHead.Children.Add(_threadSummary);
        threadStack.Children.Add(threadHead);
        threadStack.Children.Add(_threadGrid);
        threadStack.Children.Add(Ui.Secondary(
            "Many games lean hard on just one or two threads. If one bar is full while the others are low, " +
            "that thread can hold the game back even though the total looks low."));
        body.Children.Add(Ui.CardFrame(threadStack));

        // ---- Programs that use the most
        var programs = new Grid { ColumnSpacing = 32 };
        for (int c = 0; c < 3; c++)
            programs.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 0 });
        var left = ProgramList("Using the most processor", _cpuRows);
        var middle = ProgramList("Using the most graphics card", _gpuRows);
        var right = ProgramList("Using the most memory", _memoryRows);
        Grid.SetColumn(middle, 1);
        Grid.SetColumn(right, 2);
        programs.Children.Add(left);
        programs.Children.Add(middle);
        programs.Children.Add(right);

        var programStack = new StackPanel { Spacing = 10 };
        programStack.Children.Add(programs);
        programStack.Children.Add(Ui.Secondary(
            "Approximate. A program with many parts, like a browser, is added together as one line, and memory can be counted more than once. " +
            "The graphics card list comes from the same Windows counters as Task Manager's GPU column."));
        body.Children.Add(Ui.CardFrame(programStack));

        // ---- Sensors
        SetSwitch(SensorSetting.Enabled);
        _sensorSwitch.Toggled += OnSensorToggled;
        _installDriver.Click += (_, _) => InstallDriver();
        _restartAdmin.Click += (_, _) => RestartAsAdmin();
        _removeDriver.Click += (_, _) => RemoveDriver();

        _sensorInfo.Height = 40;
        _sensorInfo.TextWrapping = TextWrapping.Wrap;
        _sensorInfo.TextTrimming = TextTrimming.CharacterEllipsis;
        _sensorInfo.MaxLines = 2;

        var sensorHead = new Grid();
        sensorHead.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sensorHead.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        sensorHead.Children.Add(new TextBlock
        {
            Text = "Turn on sensors (not tested with anti-cheat)",
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        Grid.SetColumn(_sensorSwitch, 1);
        sensorHead.Children.Add(_sensorSwitch);

        var sensorButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        sensorButtons.Children.Add(_installDriver);
        sensorButtons.Children.Add(_restartAdmin);
        sensorButtons.Children.Add(_removeDriver);

        var sensorStack = new StackPanel { Spacing = 8 };
        sensorStack.Children.Add(sensorHead);
        sensorStack.Children.Add(Ui.Secondary(
            "Experimental and off by default. Reads processor temperature, speed and power through a sensor driver (PawnIO) and the " +
            "LibreHardwareMonitor library. It has not been tested with anti-cheat, so do not use it in online games with strict anti-cheat. " +
            "Turn it off and nothing is loaded. Before you play online games with strict anti-cheat, press Remove sensor driver."));
        sensorStack.Children.Add(sensorButtons);
        sensorStack.Children.Add(_sensorInfo);
        body.Children.Add(Ui.CardFrame(sensorStack));
        UpdateSensorUi(null);

        // ---- Logging
        _interval.Items.Add("Every second");
        _interval.Items.Add("Every 5 seconds");
        _interval.Items.Add("Every 10 seconds");
        _interval.SelectedIndex = 0;

        _openLog = Ui.IconButton(Ui.IconFolder, "Open log folder");
        _openLog.IsEnabled = false;
        _openLog.Click += (_, _) =>
        {
            if (_lastFolder is not null) Ui.OpenFolder(_lastFolder);
        };

        _logInfo.Height = 40;
        _logInfo.TextWrapping = TextWrapping.Wrap;
        _logInfo.TextTrimming = TextTrimming.CharacterEllipsis;
        _logInfo.MaxLines = 2;

        var logRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        logRow.Children.Add(new TextBlock { Text = "Logging", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        logRow.Children.Add(_interval);
        logRow.Children.Add(_openLog);

        var logStack = new StackPanel { Spacing = 8 };
        logStack.Children.Add(logRow);
        logStack.Children.Add(Ui.Secondary(
            "Use the Start logging button at the top. The numbers are saved to text files so you can look at them later, " +
            "for example after a game session or a stutter problem. Logging keeps going when you leave this page."));
        logStack.Children.Add(_logInfo);
        body.Children.Add(Ui.CardFrame(logStack));

        // ---- Details (last, so nothing sits below it that could be pushed around)
        var detailStack = new StackPanel { Spacing = 6 };
        detailStack.Children.Add(new TextBlock { Text = "What is limiting you right now?", FontWeight = FontWeights.SemiBold });
        detailStack.Children.Add(_limits);
        detailStack.Children.Add(Ui.Secondary(
            "A guess from the numbers, not a fact. Graphics card temperature works for NVIDIA cards. " +
            "Processor and motherboard temperatures need a sensor driver and are not part of Pulse yet."));
        body.Children.Add(Ui.CardFrame(detailStack));

        Content = root;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        UpdateLoggingUi();
    }

    private static TextBlock Caption(string text) => new()
    {
        Text = text.ToUpperInvariant(),
        FontSize = 11,
        CharacterSpacing = 60,
        FontWeight = FontWeights.SemiBold,
        Foreground = Ui.Res("TextFillColorSecondaryBrush"),
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>A title and a fixed number of rows (name on the left, number on the right), filled in later.</summary>
    private static StackPanel ProgramList(string title, List<(TextBlock Name, TextBlock Value)> rows)
    {
        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(Caption(title));

        for (int i = 0; i < TopRows; i++)
        {
            var name = new TextBlock { Height = 22, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            var value = new TextBlock { Height = 22, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Right, MinWidth = 70, VerticalAlignment = VerticalAlignment.Center };

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 0 });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(value, 1);
            row.Children.Add(name);
            row.Children.Add(value);
            stack.Children.Add(row);

            rows.Add((name, value));
        }

        return stack;
    }

    // ---------------------------------------------------------------- Start and stop

    /// <summary>Tells the monitor which graphics card is the main one. Used by the tray menu, which can start Pulse before this page was ever opened.</summary>
    internal static async Task ConfigureMonitorAsync()
    {
        var hw = await AppState.HardwareAsync();
        var gpu = hw.PrimaryGpu;
        PulseMonitor.Shared.Configure(gpu?.Name, gpu?.VramBytes ?? 0, hw.ToReport());
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var hw = await AppState.HardwareAsync();
            if (!IsLoaded || _attached) return;

            var gpu = hw.PrimaryGpu;
            _monitor.Configure(gpu?.Name, gpu?.VramBytes ?? 0, hw.ToReport());

            _attached = true;
            _monitor.Updated += OnUpdated;
            _monitor.Failed += OnFailed;
            _monitor.AddViewer();

            UpdateLoggingUi();
            if (_monitor.Latest is { } latest) Render(latest);
        }
        catch (Exception ex)
        {
            _status.Show(InfoBarSeverity.Error, "Pulse could not start", ex.Message);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (!_attached) return;

        _attached = false;
        _monitor.Updated -= OnUpdated;
        _monitor.Failed -= OnFailed;
        _monitor.RemoveViewer();
    }

    // These two are called on the monitor's own thread, so hand the work to the window's thread
    private void OnUpdated(PulseSnapshot snapshot) => DispatcherQueue.TryEnqueue(() => Render(snapshot));

    private void OnFailed(string message) => DispatcherQueue.TryEnqueue(() =>
    {
        _status.Show(InfoBarSeverity.Warning, "Pulse", message);
        UpdateLoggingUi();
    });

    // ---------------------------------------------------------------- Showing a reading

    private void Render(PulseSnapshot s)
    {
        var worst = PulseLevel.Idle;

        var cpuNote = PulseAdvice.Load(s.CpuPercent);
        worst = Worse(worst, cpuNote.Level);
        _cpu.Update($"{s.CpuPercent:0} %", cpuNote, $"Busiest thread: {s.BusiestCorePercent:0} %", s.CpuPercent);

        var gpu = s.Gpu;
        if (gpu?.UsagePercent is double usage)
        {
            var parts = new List<string> { gpu.Name };
            if (gpu.PowerWatts is double watts) parts.Add($"{watts:0} W");
            if (gpu.ClockMhz is double mhz) parts.Add($"{mhz:0} MHz");
            if (gpu.FanPercent is double fan) parts.Add($"fan {fan:0} %");

            var note = PulseAdvice.GpuLoad(usage);
            worst = Worse(worst, note.Level);
            _gpu.Update($"{usage:0} %", note, string.Join(" · ", parts), usage);
        }
        else
        {
            _gpu.ShowUnavailable("The graphics card usage could not be read on this PC.");
        }

        if (gpu?.TemperatureC is double celsius)
        {
            var note = PulseAdvice.Temperature(celsius);
            worst = Worse(worst, note.Level);
            _temp.Update($"{celsius:0} °C", note, null, celsius);
        }
        else
        {
            _temp.ShowUnavailable("Temperatures are only read from NVIDIA graphics cards for now.");
        }

        var memoryNote = PulseAdvice.Memory(s.MemoryPercent);
        worst = Worse(worst, memoryNote.Level);
        _memory.Update($"{s.MemoryPercent:0} %", memoryNote,
            $"{PulseAdvice.Gigabytes(s.MemoryUsedBytes)} of {PulseAdvice.Gigabytes(s.MemoryTotalBytes)} used", s.MemoryPercent);

        if (gpu?.VramPercent is double vramPercent && gpu.VramUsedBytes is double vramUsed && gpu.VramTotalBytes is double vramTotal)
        {
            var note = PulseAdvice.VideoMemory(vramPercent);
            worst = Worse(worst, note.Level);
            _vram.Update($"{vramPercent:0} %", note,
                $"{PulseAdvice.Gigabytes(vramUsed)} of {PulseAdvice.Gigabytes(vramTotal)} used", vramPercent);
        }
        else
        {
            _vram.ShowUnavailable("The video memory could not be read on this PC.");
        }

        var diskNote = PulseAdvice.Disk(s.DiskPercent);
        worst = Worse(worst, diskNote.Level);
        _disk.Update($"{s.DiskPercent:0} %", diskNote, $"Reading and writing {PulseAdvice.Rate(s.DiskBytesPerSec)}", s.DiskPercent);

        _network.Update($"Down {PulseAdvice.Rate(s.NetDownBytesPerSec)}",
            PulseAdvice.Network(s.NetDownBytesPerSec, s.NetUpBytesPerSec),
            $"Up {PulseAdvice.Rate(s.NetUpBytesPerSec)}", Math.Max(s.NetDownBytesPerSec, s.NetUpBytesPerSec));

        RenderSensors(s.Sensors, ref worst);
        UpdateSensorUi(s.Sensors);

        RenderThreads(s.CoreLoads);
        RenderPrograms(s);

        var limits = PulseAdvice.Limits(s);
        _limits.Text = string.Join("\n", limits);
        _heroSub.Text = limits.Count > 0 ? limits[0] : "";
        ToolTipService.SetToolTip(_heroSub, _heroSub.Text);

        _heroTitle.Text = worst switch
        {
            PulseLevel.Critical => "Something needs attention",
            PulseLevel.High => "Working hard",
            PulseLevel.Fine => "Everything looks fine",
            _ => "Relaxed, nothing is stressed",
        };
        _heroStrip.Background = PulseColors.Brush(worst);

        // Logging can be started or stopped from the tray menu
        if (_monitor.IsLogging != _loggingShown) UpdateLoggingUi();
    }

    private void RenderSensors(SensorReading? sensors, ref PulseLevel worst)
    {
        string why = !SensorSetting.Enabled
            ? "Turn on sensors further down to see this."
            : sensors?.Problem ?? "Starting the sensors…";

        if (sensors?.CpuTemperatureC is double temperature)
        {
            var note = PulseAdvice.CpuTemperature(temperature);
            worst = Worse(worst, note.Level);
            _cpuTemp.Update($"{temperature:0} °C", note,
                sensors.CpuHottestCoreC is double hottest ? $"Hottest core: {hottest:0} °C" : null, temperature);
        }
        else
        {
            _cpuTemp.ShowUnavailable(why);
        }

        if (sensors?.CpuClockMhz is double mhz)
            _cpuSpeed.Update($"{mhz:0} MHz", new PulseNote("Fastest core right now", PulseLevel.Fine),
                sensors.CpuClockAverageMhz is double average ? $"Average of all cores: {average:0} MHz" : null, mhz);
        else
            _cpuSpeed.ShowUnavailable(why);

        if (sensors?.CpuPowerW is double watts)
            _cpuPower.Update($"{watts:0} W", new PulseNote("Whole processor", PulseLevel.Fine), null, watts);
        else
            _cpuPower.ShowUnavailable(why);
    }

    private static PulseLevel Worse(PulseLevel a, PulseLevel b) => b > a ? b : a;

    private void RenderThreads(IReadOnlyList<double>? loads)
    {
        if (loads is null || loads.Count == 0)
        {
            _threadSummary.Text = "Not available on this PC";
            return;
        }

        // Rebuild only when the number of threads is different (the first time)
        if (_threads.Count != loads.Count)
        {
            _threadGrid.ColumnDefinitions.Clear();
            _threadGrid.Children.Clear();
            _threads.Clear();

            for (int i = 0; i < loads.Count; i++)
            {
                _threadGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 0 });

                var track = new Grid
                {
                    Height = ThreadTrackHeight,
                    Background = Ui.Res("ControlFillColorSecondaryBrush"),
                    CornerRadius = new CornerRadius(3),
                    VerticalAlignment = VerticalAlignment.Top,
                };
                var bar = new Border
                {
                    Height = 2,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    CornerRadius = new CornerRadius(3),
                };
                track.Children.Add(bar);

                var number = new TextBlock
                {
                    Text = i.ToString(),
                    FontSize = 10,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Foreground = Ui.Res("TextFillColorTertiaryBrush"),
                };

                var cell = new Grid();
                cell.Children.Add(track);
                cell.Children.Add(number);
                Grid.SetColumn(cell, i);
                _threadGrid.Children.Add(cell);

                _threads.Add((bar, track));
            }
        }

        for (int i = 0; i < loads.Count; i++)
        {
            double value = loads[i];
            var (bar, track) = _threads[i];
            bar.Height = Math.Max(2, value / 100.0 * ThreadTrackHeight);
            bar.Background = PulseColors.Brush(PulseAdvice.Load(value).Level);
            ToolTipService.SetToolTip(track, $"Thread {i}: {value:0} %");
        }

        _threadSummary.Text = $"{loads.Count} threads · busiest {loads.Max():0} % · average {loads.Average():0} %";
    }

    private void RenderPrograms(PulseSnapshot s)
    {
        for (int i = 0; i < TopRows; i++)
        {
            if (s.TopByCpu is { } byCpu && i < byCpu.Count)
            {
                _cpuRows[i].Name.Text = byCpu[i].Name;
                _cpuRows[i].Value.Text = byCpu[i].CpuPercent < 0.1 ? "<0.1 %" : $"{byCpu[i].CpuPercent:0.0} %";
            }
            else
            {
                _cpuRows[i].Name.Text = i == 0 ? "Measuring…" : "";
                _cpuRows[i].Value.Text = "";
            }

            if (s.TopByGpu is { Count: > 0 } byGpu && i < byGpu.Count)
            {
                _gpuRows[i].Name.Text = byGpu[i].Name;
                _gpuRows[i].Value.Text = byGpu[i].GpuPercent < 0.1 ? "<0.1 %" : $"{byGpu[i].GpuPercent:0.0} %";
            }
            else
            {
                _gpuRows[i].Name.Text = i == 0 ? "Nothing or not available" : "";
                _gpuRows[i].Value.Text = "";
            }

            if (s.TopByMemory is { } byMemory && i < byMemory.Count)
            {
                _memoryRows[i].Name.Text = byMemory[i].Name;
                _memoryRows[i].Value.Text = Size(byMemory[i].MemoryBytes);
            }
            else
            {
                _memoryRows[i].Name.Text = i == 0 ? "Measuring…" : "";
                _memoryRows[i].Value.Text = "";
            }
        }
    }

    private static string Size(ulong bytes)
    {
        double mb = bytes / 1048576.0;
        return mb >= 1024 ? $"{mb / 1024:0.0} GB" : $"{mb:0} MB";
    }

    // ---------------------------------------------------------------- Sensors

    private void SetSwitch(bool on)
    {
        _sensorSwitchBusy = true;
        _sensorSwitch.IsOn = on;
        _sensorSwitchBusy = false;
    }

    private void UpdateSensorUi(SensorReading? sensors)
    {
        bool on = SensorSetting.Enabled;

        _installDriver.IsEnabled = on;
        _restartAdmin.IsEnabled = on;

        _sensorInfo.Text = !on
            ? "Off. No sensor library or driver is loaded."
            : sensors is null ? "Starting the sensors…"
            : sensors.CpuTemperatureC is not null ? "Working."
            : sensors.Problem ?? "Starting the sensors…";
    }

    private async void OnSensorToggled(object sender, RoutedEventArgs e)
    {
        if (_sensorSwitchBusy) return;

        try
        {
            if (_sensorSwitch.IsOn)
            {
                bool ok = await Ui.ConfirmAsync(XamlRoot, "Turn on sensors?",
                    "ZenTune will read processor and motherboard sensors through a sensor driver. This has not been tested with anti-cheat, " +
                    "so do not use it in online games with strict anti-cheat. You can turn it off again at any time.",
                    "Turn on");
                if (!ok)
                {
                    SetSwitch(false);
                    return;
                }

                SensorSetting.Enabled = true;
                _monitor.SetSensors(true);
            }
            else
            {
                SensorSetting.Enabled = false;
                _monitor.SetSensors(false);
            }

            UpdateSensorUi(null);
        }
        catch (Exception ex)
        {
            _status.Show(InfoBarSeverity.Error, "Sensors", ex.Message);
        }
    }

    private const string DriverScript = """
        $winget = Get-Command winget.exe -ErrorAction SilentlyContinue
        if (-not $winget) {
            Fail 'winget was not found on this PC.'
        } else {
            Log 'Installing PawnIO...'
            $out = & winget.exe install --id namazso.PawnIO --exact --source winget --silent --accept-source-agreements --accept-package-agreements 2>&1 | Out-String
            $code = $LASTEXITCODE
            Log ('install exit code ' + $code)
            Log $out
            if ($code -ne 0 -and $out -notmatch 'already installed|No newer package|No available upgrade') {
                Fail ('winget install failed with exit code ' + $code)
            }
        }
        """;

    private async void InstallDriver()
    {
        try
        {
            bool ok = await Ui.ConfirmAsync(XamlRoot, "Install the sensor driver?",
                "ZenTune asks Windows (winget) to install PawnIO, a driver from a third party (namazso.PawnIO) that lets programs read " +
                "processor sensors. Windows asks for administrator permission. It can be removed again with winget (winget uninstall namazso.PawnIO).",
                "Install");
            if (!ok) return;

            _installDriver.IsEnabled = false;
            _status.Show(InfoBarSeverity.Informational, "Installing the sensor driver", "This can take a minute…");

            var result = await Task.Run(() => ScriptRunner.Run("pawnio-install", DriverScript, elevated: true, TimeSpan.FromMinutes(5)));
            if (!result.Ok)
            {
                string last = result.Log
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .LastOrDefault(l => l.StartsWith("ERROR:", StringComparison.Ordinal)) ?? "Unknown error.";
                throw new InvalidOperationException($"{last} The log is in {Path.GetDirectoryName(result.ScriptPath)}.");
            }

            _monitor.RestartSensors();
            _status.Show(InfoBarSeverity.Success, "Sensor driver installed",
                "The processor temperature should show up within a few seconds. If it does not, restart ZenTune, or use Restart as administrator.");
        }
        catch (Exception ex)
        {
            _status.Show(InfoBarSeverity.Error, "The sensor driver was not installed", ex.Message);
        }
        finally
        {
            UpdateSensorUi(_monitor.Latest?.Sensors);
        }
    }

    private const string RemoveScript = """
        $winget = Get-Command winget.exe -ErrorAction SilentlyContinue
        if (-not $winget) {
            Fail 'winget was not found on this PC.'
        } else {
            Log 'Removing PawnIO...'
            $out = & winget.exe uninstall --id namazso.PawnIO --exact --silent --accept-source-agreements 2>&1 | Out-String
            $code = $LASTEXITCODE
            Log ('uninstall exit code ' + $code)
            Log $out
            if ($code -ne 0 -and $out -notmatch 'No installed package') {
                Fail ('winget uninstall failed with exit code ' + $code)
            }
        }
        """;

    private async void RemoveDriver()
    {
        try
        {
            bool ok = await Ui.ConfirmAsync(XamlRoot, "Remove the sensor driver?",
                "ZenTune turns the sensors off and asks Windows (winget) to uninstall PawnIO (namazso.PawnIO). Windows asks for administrator " +
                "permission. Do this before you play online games with strict anti-cheat. You can install the driver again later.",
                "Remove");
            if (!ok) return;

            _removeDriver.IsEnabled = false;

            // Sensors off first, so the driver is not in use when it is removed
            SensorSetting.Enabled = false;
            _monitor.SetSensors(false);
            SetSwitch(false);
            UpdateSensorUi(null);

            _status.Show(InfoBarSeverity.Informational, "Removing the sensor driver", "This can take a minute…");
            await Task.Delay(2500); // lets the measuring loop close the sensors

            var result = await Task.Run(() => ScriptRunner.Run("pawnio-remove", RemoveScript, elevated: true, TimeSpan.FromMinutes(5)));
            if (!result.Ok)
            {
                string last = result.Log
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .LastOrDefault(l => l.StartsWith("ERROR:", StringComparison.Ordinal)) ?? "Unknown error.";
                throw new InvalidOperationException($"{last} The log is in {Path.GetDirectoryName(result.ScriptPath)}.");
            }

            _status.Show(InfoBarSeverity.Success, "Sensor driver removed",
                "The sensors are off and the driver is gone (or was not installed). You can install it again with the Install button.");
        }
        catch (Exception ex)
        {
            _status.Show(InfoBarSeverity.Error, "The sensor driver was not removed", ex.Message);
        }
        finally
        {
            _removeDriver.IsEnabled = true;
        }
    }

    private async void RestartAsAdmin()
    {
        try
        {
            bool ok = await Ui.ConfirmAsync(XamlRoot, "Restart ZenTune as administrator?",
                "ZenTune closes and opens again with administrator rights. A running Pulse log is saved first. Some sensors may need this.",
                "Restart");
            if (!ok) return;

            string? exe = Environment.ProcessPath;
            if (exe is null) throw new InvalidOperationException("ZenTune could not find its own program file.");

            _monitor.StopLogging();
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" });
            Application.Current.Exit();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            _status.Show(InfoBarSeverity.Informational, "Not restarted", "The administrator question was cancelled, so ZenTune stays as it is.");
        }
        catch (Exception ex)
        {
            _status.Show(InfoBarSeverity.Error, "ZenTune could not restart", ex.Message);
        }
    }

    // ---------------------------------------------------------------- Logging

    private void ToggleLogging()
    {
        try
        {
            if (_monitor.IsLogging)
            {
                var result = _monitor.StopLogging();
                UpdateLoggingUi();
                if (result is not null)
                    _status.Show(InfoBarSeverity.Success, "Log saved", $"{result.Readings} readings saved in {result.Folder}");
            }
            else
            {
                int seconds = _interval.SelectedIndex switch { 1 => 5, 2 => 10, _ => 1 };
                _lastFolder = _monitor.StartLogging(seconds);
                UpdateLoggingUi();
                _status.Show(InfoBarSeverity.Informational, "Logging started", $"Saving to {_lastFolder}");
            }
        }
        catch (Exception ex)
        {
            UpdateLoggingUi();
            _status.Show(InfoBarSeverity.Error, "Logging did not work", ex.Message);
        }
    }

    private void UpdateLoggingUi()
    {
        bool logging = _monitor.IsLogging;
        _loggingShown = logging;
        if (logging) _lastFolder = _monitor.LogFolder ?? _lastFolder;

        SetLogButton(logging);
        _interval.IsEnabled = !logging;
        _openLog.IsEnabled = _lastFolder is not null;
        _logInfo.Text = logging
            ? $"Logging to {_lastFolder}"
            : "Each time you press Start logging, a new folder is made in Documents\\Pulse with log.txt, perf.txt and temp.txt.";
    }

    private void SetLogButton(bool logging)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        content.Children.Add(new FontIcon { Glyph = logging ? Ui.IconUndo : Ui.IconSave, FontSize = 14 });
        content.Children.Add(new TextBlock { Text = logging ? "Stop logging" : "Start logging" });

        _logButton.Content = content;
        _logButton.Style = logging ? null : Ui.StyleRes("AccentButtonStyle");
    }
}
