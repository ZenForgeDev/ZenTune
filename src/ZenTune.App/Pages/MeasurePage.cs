using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ZenTune.Core.Ai;
using ZenTune.Core.Measure;

namespace ZenTune.App.Pages;

/// <summary>
/// "Measure": records FPS and frame times of a running game with PresentMon (no overlay, nothing added to the game)
/// and compares two recordings, for example before and after a preset.
/// </summary>
internal sealed partial class MeasurePage : UserControl
{
    private sealed class ProcItem
    {
        public ProcItem(string exe, string title)
        {
            Exe = exe;
            Title = title;
        }

        public string Exe { get; }
        public string Title { get; }

        public override string ToString() => Title.Length > 60 ? $"{Exe}  ·  {Title[..60]}…" : $"{Exe}  ·  {Title}";
    }

    private readonly InfoBar _status;
    private readonly ComboBox _process = new() { MinWidth = 360 };
    private readonly TextBox _label = new() { Text = "Before", MinWidth = 160, PlaceholderText = "Name, like Before" };
    private readonly ComboBox _duration = new() { MinWidth = 130 };
    private readonly Button _record;
    private readonly Button _refresh;
    private readonly CheckBox _withPulse = new() { Content = "Also save Pulse numbers (processor and graphics card load, temperatures)", IsChecked = true };
    private readonly TextBlock _countdown = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold };
    private readonly StackPanel _list = new() { Spacing = 8 };
    private readonly StackPanel _compare = new() { Spacing = 8 };
    private readonly HashSet<string> _selected = new();
    private List<CaptureRecord> _records = [];

    // The recording name suggests itself: Before, then After, then Test 3... until the user types their own
    private bool _labelAuto = true;
    private bool _settingLabel;

    // AI answers for a pair of recordings, so ticking the same two again does not use another request
    private readonly Dictionary<string, MeasureAnalysis> _aiCache = new();

    public MeasurePage()
    {
        _refresh = Ui.IconButton(Ui.IconRefresh, "Refresh");
        _refresh.Click += async (_, _) => await LoadProcessesAsync();

        var (root, body, status) = Ui.Page("Measure",
            "Record the FPS and frame times of a game, change something, record again and compare. " +
            "ZenTune adds no overlay and nothing inside the game: it reads Windows' own graphics events with PresentMon.");
        _status = status;

        _label.TextChanged += (_, _) =>
        {
            if (!_settingLabel) _labelAuto = false;
        };
        _process.SelectionChanged += (_, _) => UpdateAutoLabel();

        _duration.Items.Add("30 seconds");
        _duration.Items.Add("60 seconds");
        _duration.Items.Add("120 seconds");
        _duration.SelectedIndex = 1;

        _record = Ui.IconButton(Ui.IconHealth, "Record", accent: true);
        _record.Click += async (_, _) => await RecordAsync();

        // Record card
        var form = new StackPanel { Spacing = 12 };
        form.Children.Add(new TextBlock { Text = "Record a game", Style = Ui.StyleRes("SubtitleTextBlockStyle") });
        var steps = new StackPanel { Spacing = 2 };
        steps.Children.Add(Ui.Secondary("1. Start your game and go to the spot you want to measure. Use the same spot every time."));
        steps.Children.Add(Ui.Secondary("2. Press Refresh and choose the game below."));
        steps.Children.Add(Ui.Secondary("3. Press Record and switch to the game. Recording starts 10 seconds later."));
        steps.Children.Add(Ui.Secondary("Record once, change a setting, record again. ZenTune names them Before and After and compares them."));
        form.Children.Add(steps);

        var pick = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        pick.Children.Add(_process);
        pick.Children.Add(_refresh);
        form.Children.Add(pick);

        var options = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        options.Children.Add(_label);
        options.Children.Add(_duration);
        options.Children.Add(_record);
        form.Children.Add(options);
        form.Children.Add(_withPulse);
        form.Children.Add(Ui.Secondary(
            "Pulse measures the PC in the background while ZenTune records, so a comparison can show what held the FPS back. " +
            "Only numbers like percentages and temperatures are saved. Untick it to record with PresentMon alone."));
        form.Children.Add(_countdown);

        body.Children.Add(Ui.CardFrame(form));

        body.Children.Add(Ui.SectionHeader("Compare"));
        body.Children.Add(_compare);

        body.Children.Add(Ui.SectionHeader("Recordings"));
        body.Children.Add(_list);

        var help = new StackPanel { Spacing = 8 };
        help.Children.Add(Ui.Secondary(
            "How to read the numbers: Average FPS is frames divided by time. 1% low is the average FPS of the slowest 1% of frames (0.1% low: the slowest 0.1%); " +
            "the closer it is to the average, the smoother the game feels. 99th percentile means 99% of frames were faster than that. " +
            "A stutter is a frame that took more than twice the median frame time."));
        help.Children.Add(Ui.Secondary(
            "Good to know: PresentMon counts the frames the game hands to Windows. With frame generation (DLSS, FSR, AFMF) different games count real or generated frames, " +
            "so compare recordings made with the same settings. ZenTune does not record Valorant or Delta Force because of their anti-cheat. " +
            "PresentMon reads Windows' events and does not touch the game, but ZenTune has not tested it against every anti-cheat."));
        body.Children.Add(AdvancedPage.Section("How to read the numbers", "What 1% low, 99th percentile and stutters mean.", help));

        Content = root;

        _ = LoadProcessesAsync();
        _ = LoadListAsync();
    }

    // ---------------------------------------------------------------- Programs

    private async Task LoadProcessesAsync()
    {
        try
        {
            var running = await Task.Run(Captures.RunningWindows);
            string? previous = (_process.SelectedItem as ProcItem)?.Exe;

            _process.Items.Clear();
            int select = 0;
            foreach (var (exe, title) in running)
            {
                if (Captures.IsBlocked(exe)) continue;
                if (exe.Equals(previous, StringComparison.OrdinalIgnoreCase)) select = _process.Items.Count;
                _process.Items.Add(new ProcItem(exe, title));
            }
            if (_process.Items.Count > 0) _process.SelectedIndex = select;
        }
        catch (Exception ex)
        {
            _status.Show(InfoBarSeverity.Error, "Could not list the running programs", ex.Message);
        }
    }

    // ---------------------------------------------------------------- Record

    private async Task RecordAsync()
    {
        if (_process.SelectedItem is not ProcItem item)
        {
            _status.Show(InfoBarSeverity.Warning, "Choose a game first",
                "Start the game, press Refresh and pick it from the list.");
            return;
        }

        int seconds = _duration.SelectedIndex switch { 0 => 30, 2 => 120, _ => 60 };
        string label = string.IsNullOrWhiteSpace(_label.Text) ? "Recording" : _label.Text.Trim();
        const int delay = 10;

        _record.IsEnabled = false;
        try
        {
            if (!PresentMonTool.IsInstalled)
            {
                bool ok = await Ui.ConfirmAsync(XamlRoot, "Download PresentMon?",
                    "ZenTune measures frames with PresentMon, a free tool from Intel (MIT license). " +
                    $"It is downloaded once from github.com/GameTechDev/PresentMon (version {PresentMonTool.Version}, about 1 MB), " +
                    "and ZenTune checks its checksum before it will run it. It reads Windows' graphics events and does not touch your games.",
                    "Download");
                if (!ok) return;

                _countdown.Text = "Downloading PresentMon…";
                await PresentMonTool.EnsureInstalledAsync();
            }

            bool withPulse = _withPulse.IsChecked == true;
            if (withPulse)
            {
                try { await PulsePage.ConfigureMonitorAsync(); }
                catch { /* Pulse numbers are an extra; the recording works without the graphics card details */ }
            }

            var task = Task.Run(() => Captures.Record(item.Exe, label, delay, seconds, withPulse));

            int total = delay + seconds + 5;
            for (int t = 0; t < total && !task.IsCompleted; t++)
            {
                _countdown.Text = t < delay
                    ? $"Recording starts in {delay - t} s. Switch to {item.Exe} now."
                    : $"Recording {item.Exe}… {Math.Max(0, seconds - (t - delay))} s left.";
                await Task.Delay(1000);
            }
            _countdown.Text = "Working out the numbers…";

            var record = await task;
            _status.Show(InfoBarSeverity.Success, $"Recorded \"{record.Label}\"",
                $"{record.Stats.Frames} frames · {record.Stats.AvgFps:0} FPS on average · 1% low {record.Stats.Low1Fps:0} FPS.");
        }
        catch (OperationCanceledException ex)
        {
            _status.Show(InfoBarSeverity.Informational, "Nothing was recorded", ex.Message);
        }
        catch (Exception ex)
        {
            _status.Show(InfoBarSeverity.Error, "The recording did not work", ex.Message);
        }
        finally
        {
            _countdown.Text = "";
            _record.IsEnabled = true;
        }

        _labelAuto = true;
        await LoadListAsync();
    }

    // ---------------------------------------------------------------- Recordings list

    private async Task LoadListAsync()
    {
        try
        {
            _records = await Task.Run(Captures.List);
        }
        catch
        {
            _records = [];
        }

        _selected.RemoveWhere(id => _records.All(r => r.Id != id));
        UpdateAutoLabel();
        RenderList();
        RenderCompare();
    }

    private void UpdateAutoLabel()
    {
        if (!_labelAuto) return;

        string exe = (_process.SelectedItem as ProcItem)?.Exe ?? "";
        int n = _records.Count(r => string.Equals(r.Process, exe, StringComparison.OrdinalIgnoreCase));
        string suggestion = n == 0 ? "Before" : n == 1 ? "After" : $"Test {n + 1}";

        _settingLabel = true;
        _label.Text = suggestion;
        _settingLabel = false;
    }

    private void RenderList()
    {
        _list.Children.Clear();
        if (_records.Count == 0)
        {
            _list.Children.Add(Ui.Secondary("No recordings yet."));
            return;
        }

        foreach (var group in _records.GroupBy(r => r.Process, StringComparer.OrdinalIgnoreCase))
        {
            _list.Children.Add(new TextBlock
            {
                Text = $"{group.Key} · {group.Count()}",
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 8, 0, 0),
            });
            foreach (var record in group)
                _list.Children.Add(RecordRow(record));
        }
    }

    private UIElement RecordRow(CaptureRecord record)
    {
        var s = record.Stats;

        var pick = new CheckBox
        {
            IsChecked = _selected.Contains(record.Id),
            Content = null,
            MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Center,
        };
        pick.Checked += (_, _) => { _selected.Add(record.Id); RenderCompare(); };
        pick.Unchecked += (_, _) => { _selected.Remove(record.Id); RenderCompare(); };

        var delete = Ui.IconButton(Ui.IconDelete, "Delete");
        delete.Click += async (_, _) =>
        {
            bool ok = await Ui.ConfirmAsync(XamlRoot, $"Delete \"{record.Label}\"?", "The recording and its frame data are deleted.", "Delete", destructive: true);
            if (!ok) return;
            try
            {
                await Task.Run(() => Captures.Delete(record));
            }
            catch (Exception ex)
            {
                _status.Show(InfoBarSeverity.Error, "Could not delete it", ex.Message);
            }
            await LoadListAsync();
        };

        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock
        {
            Text = $"{record.Label} · {record.Process} · {Ui.When(record.When)}",
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        text.Children.Add(Ui.Secondary($"{s.AvgFps:0} FPS · 1% low {s.Low1Fps:0} · {s.Stutters} stutters"));

        var details = new StackPanel { Spacing = 2, Visibility = Visibility.Collapsed };
        details.Children.Add(Ui.Secondary(
            $"{s.AvgFps:0} FPS average · 1% low {s.Low1Fps:0} · 0.1% low {s.Low01Fps:0} · 99th percentile {s.P99Ms:0.0} ms · " +
            $"{s.Stutters} stutters · {s.Seconds:0} s, {s.Frames} frames"));
        if (record.Pulse is { } pulse)
        {
            var parts = new List<string> { $"processor {pulse.CpuAverage:0} % (highest {pulse.CpuHighest:0} %)" };
            if (pulse.GpuAverage is double gpuAverage) parts.Add($"graphics card {gpuAverage:0} % (highest {pulse.GpuHighest ?? gpuAverage:0} %)");
            if (pulse.GpuTemperatureHighest is double gpuTemp) parts.Add($"graphics card up to {gpuTemp:0} °C");
            if (pulse.CpuTemperatureHighest is double cpuTemp) parts.Add($"processor up to {cpuTemp:0} °C");
            details.Children.Add(Ui.Secondary("While recording: " + string.Join(" · ", parts)));
        }

        var more = new HyperlinkButton { Content = "Details", Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Left };
        more.Click += (_, _) =>
        {
            bool open = details.Visibility == Visibility.Visible;
            details.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
            more.Content = open ? "Details" : "Hide details";
        };
        text.Children.Add(details);
        text.Children.Add(more);

        var rename = Ui.IconButton("\uE70F", "Rename");
        rename.Click += async (_, _) =>
        {
            var box = new TextBox { Text = record.Label, MinWidth = 280 };
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Rename recording",
                Content = box,
                PrimaryButtonText = "Save",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                RequestedTheme = ElementTheme.Dark,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            var name = box.Text.Trim();
            if (name.Length == 0) return;
            try
            {
                await Task.Run(() => Captures.Rename(record, name));
            }
            catch (Exception ex)
            {
                _status.Show(InfoBarSeverity.Error, "Could not rename it", ex.Message);
            }
            await LoadListAsync();
        };

        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(pick);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(rename);
        actions.Children.Add(delete);
        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);

        return Ui.CardFrame(grid, new Thickness(16, 10, 20, 10));
    }

    // ---------------------------------------------------------------- Compare

    private void RenderCompare()
    {
        _compare.Children.Clear();

        var chosen = _records.Where(r => _selected.Contains(r.Id)).OrderBy(r => r.When).ToList();
        if (chosen.Count != 2)
        {
            _compare.Children.Add(Ui.Secondary(chosen.Count == 0
                ? "Tick two recordings below to compare them. The older one counts as the starting point."
                : chosen.Count == 1
                    ? "Tick one more recording to compare."
                    : "Tick exactly two recordings."));
            return;
        }

        var a = chosen[0];
        var b = chosen[1];

        var grid = new Grid { ColumnSpacing = 24, RowSpacing = 8 };
        for (int c = 0; c < 4; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = c == 0 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });

        int row = 0;
        void Header(string t0, string t1, string t2, string t3)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var texts = new[] { t0, t1, t2, t3 };
            for (int c = 0; c < 4; c++)
            {
                var tb = new TextBlock { Text = texts[c], FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
                Grid.SetRow(tb, row);
                Grid.SetColumn(tb, c);
                grid.Children.Add(tb);
            }
            row++;
        }

        void Metric(string name, double va, double vb, string format, bool? higherIsBetter)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            string change;
            string brush = "TextFillColorSecondaryBrush";
            if (va <= 0)
            {
                change = "";
            }
            else
            {
                double pct = (vb - va) / va * 100.0;
                bool better = higherIsBetter == true ? pct > 0 : pct < 0;
                if (Math.Abs(pct) < 1)
                {
                    change = "about the same";
                }
                else if (higherIsBetter is null)
                {
                    change = $"{pct:+0.0;-0.0}%";
                }
                else
                {
                    change = $"{pct:+0.0;-0.0}% · {(better ? "better" : "worse")}";
                    brush = better ? "SystemFillColorSuccessBrush" : "SystemFillColorCautionBrush";
                }
            }

            var cells = new[] { name, va.ToString(format), vb.ToString(format), change };
            for (int c = 0; c < 4; c++)
            {
                var tb = new TextBlock { Text = cells[c], TextWrapping = TextWrapping.Wrap };
                if (c == 3) tb.Foreground = Ui.Res(brush);
                Grid.SetRow(tb, row);
                Grid.SetColumn(tb, c);
                grid.Children.Add(tb);
            }
            row++;
        }

        Header("", $"{a.Label} ({Ui.When(a.When)})", $"{b.Label} ({Ui.When(b.When)})", "Change");
        Metric("Average FPS", a.Stats.AvgFps, b.Stats.AvgFps, "0.0", true);
        Metric("1% low FPS", a.Stats.Low1Fps, b.Stats.Low1Fps, "0.0", true);
        Metric("0.1% low FPS", a.Stats.Low01Fps, b.Stats.Low01Fps, "0.0", true);
        Metric("Median frame time (ms)", a.Stats.MedianMs, b.Stats.MedianMs, "0.00", false);
        Metric("99th percentile frame time (ms)", a.Stats.P99Ms, b.Stats.P99Ms, "0.00", false);
        Metric("Longest frame (ms)", a.Stats.MaxMs, b.Stats.MaxMs, "0.0", false);
        Metric("Stutters per minute", a.Stats.StuttersPerMinute, b.Stats.StuttersPerMinute, "0.0", false);

        // How busy the PC was (from Pulse). Neither higher nor lower is "better" here: it shows what the FPS was limited by.
        if (a.Pulse is { } pa && b.Pulse is { } pb)
        {
            Metric("Processor load, average (%)", pa.CpuAverage, pb.CpuAverage, "0", null);
            Metric("Busiest thread, average (%)", pa.BusiestThreadAverage, pb.BusiestThreadAverage, "0", null);
            if (pa.GpuAverage is double gpuA && pb.GpuAverage is double gpuB)
                Metric("Graphics card load, average (%)", gpuA, gpuB, "0", null);
            if (pa.GpuTemperatureHighest is double tempA && pb.GpuTemperatureHighest is double tempB)
                Metric("Graphics card temperature, highest (°C)", tempA, tempB, "0", null);
            if (pa.CpuTemperatureHighest is double cpuTempA && pb.CpuTemperatureHighest is double cpuTempB)
                Metric("Processor temperature, highest (°C)", cpuTempA, cpuTempB, "0", null);
        }

        _compare.Children.Add(CompareSummary(a, b));

        var graphHost = new StackPanel();
        _compare.Children.Add(graphHost);
        _ = ShowGraphAsync(a, b, graphHost);

        _compare.Children.Add(new Expander
        {
            Header = new TextBlock { Text = "All numbers", FontWeight = FontWeights.SemiBold },
            Content = grid,
            IsExpanded = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        });

        var notes = new List<string>();
        if (!string.Equals(a.Process, b.Process, StringComparison.OrdinalIgnoreCase))
            notes.Add("These two recordings are from different programs, so the comparison says little.");
        if (Math.Abs(a.Stats.Seconds - b.Stats.Seconds) > 20)
            notes.Add("The recordings have very different lengths.");
        notes.Add("A single run can vary by a few percent. Differences under about 3% are usually noise: record twice if it matters.");
        foreach (var n in notes)
            _compare.Children.Add(Ui.Secondary(n));

        AddAiSection(a, b);
    }

    // ---------------------------------------------------------------- Frame time graph

    /// <summary>Reads the frame times of both recordings and draws them. The graph is an extra: if a file is missing nothing is shown.</summary>
    private static async Task ShowGraphAsync(CaptureRecord a, CaptureRecord b, StackPanel host)
    {
        try
        {
            var (fa, fb) = await Task.Run(() => (FrameStats.ReadFrameTimes(a.CsvPath), FrameStats.ReadFrameTimes(b.CsvPath)));

            // The comparison may have been redrawn (other recordings ticked) while the files were read
            if (host.Parent is null || fa.Count < 2 || fb.Count < 2) return;

            host.Children.Add(Ui.CardFrame(new FrameGraph(fa, fb, $"{a.Label} · {a.When:HH:mm}", $"{b.Label} · {b.When:HH:mm}")));
        }
        catch
        {
            // no graph
        }
    }

    // ---------------------------------------------------------------- Compare summary

    private static double Pct(double from, double to) => from > 0 ? (to - from) / from * 100.0 : 0;

    /// <summary>A one-line verdict and four big numbers: what changed between the two recordings.</summary>
    private static UIElement CompareSummary(CaptureRecord a, CaptureRecord b)
    {
        var good = new List<string>();
        var bad = new List<string>();

        void Judge(double pct, bool higherIsBetter, string better, string worse)
        {
            if (Math.Abs(pct) < 3) return;
            bool isBetter = higherIsBetter ? pct > 0 : pct < 0;
            string text = (isBetter ? better : worse).Replace("{p}", Math.Abs(pct).ToString("0"));
            (isBetter ? good : bad).Add(text);
        }

        Judge(Pct(a.Stats.AvgFps, b.Stats.AvgFps), true, "{p}% more FPS", "{p}% fewer FPS");
        if (Math.Max(a.Stats.Low1Fps, b.Stats.Low1Fps) >= 10)
            Judge(Pct(a.Stats.Low1Fps, b.Stats.Low1Fps), true, "{p}% higher 1% lows", "{p}% lower 1% lows");
        Judge(Pct(a.Stats.StuttersPerMinute, b.Stats.StuttersPerMinute), false, "{p}% fewer stutters", "{p}% more stutters");

        string headline;
        string brush;
        if (good.Count == 0 && bad.Count == 0)
        {
            headline = "About the same";
            brush = "TextFillColorSecondaryBrush";
        }
        else if (bad.Count == 0)
        {
            headline = $"\"{b.Label}\" is better: " + string.Join(", ", good);
            brush = "SystemFillColorSuccessBrush";
        }
        else if (good.Count == 0)
        {
            headline = $"\"{b.Label}\" is worse: " + string.Join(", ", bad);
            brush = "SystemFillColorCautionBrush";
        }
        else
        {
            headline = "Mixed: " + string.Join(", ", good) + ", but " + string.Join(", ", bad);
            brush = "AccentTextFillColorPrimaryBrush";
        }

        var stack = new StackPanel { Spacing = 12 };

        var head = new StackPanel { Spacing = 4 };
        head.Children.Add(new TextBlock
        {
            Text = headline,
            Style = Ui.StyleRes("SubtitleTextBlockStyle"),
            TextWrapping = TextWrapping.Wrap,
            Foreground = Ui.Res(brush),
        });
        head.Children.Add(Ui.Secondary($"\"{a.Label}\" compared with \"{b.Label}\" (the second one is the newer recording)."));
        stack.Children.Add(Ui.CardFrame(head));

        var tiles = new Grid { ColumnSpacing = 12 };
        for (int i = 0; i < 4; i++)
            tiles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        void Tile(int column, string title, double va, double vb, string format, bool higherIsBetter)
        {
            double pct = Pct(va, vb);
            bool same = Math.Abs(pct) < 3;
            bool isBetter = higherIsBetter ? pct > 0 : pct < 0;

            var tile = new StackPanel { Spacing = 2 };
            tile.Children.Add(Ui.Secondary(title));
            tile.Children.Add(new TextBlock { Text = vb.ToString(format), Style = Ui.StyleRes("TitleTextBlockStyle") });
            tile.Children.Add(Ui.Secondary("was " + va.ToString(format)));
            tile.Children.Add(new TextBlock
            {
                Text = same ? "about the same" : $"{pct:+0;-0}%",
                FontWeight = FontWeights.SemiBold,
                Foreground = Ui.Res(same ? "TextFillColorSecondaryBrush" : isBetter ? "SystemFillColorSuccessBrush" : "SystemFillColorCautionBrush"),
            });

            var frame = Ui.CardFrame(tile, new Thickness(16, 14, 16, 14));
            Grid.SetColumn(frame, column);
            tiles.Children.Add(frame);
        }

        Tile(0, "Average FPS", a.Stats.AvgFps, b.Stats.AvgFps, "0", true);
        Tile(1, "1% low FPS", a.Stats.Low1Fps, b.Stats.Low1Fps, "0", true);
        Tile(2, "Stutters per minute", a.Stats.StuttersPerMinute, b.Stats.StuttersPerMinute, "0.0", false);
        Tile(3, "99th percentile (ms)", a.Stats.P99Ms, b.Stats.P99Ms, "0.0", false);
        stack.Children.Add(tiles);

        return stack;
    }

    // ---------------------------------------------------------------- Compare with AI

    private void AddAiSection(CaptureRecord a, CaptureRecord b)
    {
        string cacheKey = a.Id + "|" + b.Id;
        var result = new StackPanel { Spacing = 8 };
        var button = Ui.IconButton(Ui.IconInfo, "Compare with AI");

        _compare.Children.Add(button);
        _compare.Children.Add(Ui.Secondary(
            "Gemini explains the difference in plain words. Only the numbers above, the game's program name and your two recording names are sent: " +
            "no files and nothing personal. It uses one request from your Gemini quota, and the AI can be wrong."));
        _compare.Children.Add(result);

        if (_aiCache.TryGetValue(cacheKey, out var cached))
            ShowAnalysis(result, cached);

        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            result.Children.Clear();
            result.Children.Add(Ui.Busy());
            try
            {
                bool hasKey = false;
                try { hasKey = !string.IsNullOrWhiteSpace(AiConfig.Load().ApiKey); } catch { /* ask for the key below */ }
                if (!hasKey && !await GeminiDialogs.AskForKeyAsync(XamlRoot))
                {
                    result.Children.Clear();
                    return;
                }

                var progress = new Progress<string>(m => _status.Show(InfoBarSeverity.Informational, "Gemini", m));
                var analysis = await MeasureAdvisor.CompareAsync(a, b, progress);
                _aiCache[cacheKey] = analysis;
                ShowAnalysis(result, analysis);
            }
            catch (Exception ex)
            {
                result.Children.Clear();
                _status.Show(InfoBarSeverity.Error, "The AI comparison did not work", ex.Message);
            }
            finally
            {
                button.IsEnabled = true;
            }
        };
    }

    private static void ShowAnalysis(StackPanel host, MeasureAnalysis analysis)
    {
        host.Children.Clear();

        var (headline, brush) = analysis.Verdict switch
        {
            "better" => ("The second recording is better", "SystemFillColorSuccessBrush"),
            "worse" => ("The second recording is worse", "SystemFillColorCautionBrush"),
            "mixed" => ("Mixed: some numbers got better, others worse", "AccentTextFillColorPrimaryBrush"),
            _ => ("About the same", "TextFillColorSecondaryBrush"),
        };

        var text = new StackPanel { Spacing = 6 };
        text.Children.Add(new TextBlock
        {
            Text = headline,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Ui.Res(brush),
        });
        text.Children.Add(new TextBlock { Text = analysis.Explanation, TextWrapping = TextWrapping.Wrap });
        foreach (var tip in analysis.Tips)
            text.Children.Add(Ui.Secondary("• " + tip));
        text.Children.Add(Ui.Secondary("Written by Gemini, so it may be wrong. The table above has the real numbers."));

        host.Children.Add(Ui.CardFrame(text));
    }
}
