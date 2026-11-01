using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using ZenTune.Core.Hardware;

namespace ZenTune.App.Pages;

/// <summary>"Your PC": what ZenTune detected about the hardware and screens.</summary>
internal sealed partial class PcPage : UserControl
{
    private readonly StackPanel _body;
    private readonly InfoBar _status;
    private readonly Button _copy;
    private readonly Button _rescan;
    private readonly TextBlock _subtitle;
    private readonly StackPanel _results = new() { Spacing = 16 };
    private HardwareSnapshot? _snapshot;

    public PcPage()
    {
        _copy = Ui.IconButton(Ui.IconCopy, "Copy report");
        _copy.IsEnabled = false;
        _copy.Click += (_, _) => Copy();

        _rescan = Ui.IconButton(Ui.IconRefresh, "Scan again", accent: true);
        _rescan.Click += async (_, _) => await ScanAsync(rescan: true);

        var (root, body, status) = Ui.Page("Your PC", "ZenTune uses this to pick the right settings for every game.", _copy, _rescan);
        _body = body;
        _status = status;
        _subtitle = (TextBlock)((StackPanel)((Grid)body.Children[0]).Children[0]).Children[1];
        _body.Children.Add(_results);
        Content = root;

        _ = ScanAsync(rescan: false);
    }

    private async Task ScanAsync(bool rescan)
    {
        _rescan.IsEnabled = false;
        _copy.IsEnabled = false;
        _status.IsOpen = false;
        _results.Children.Clear();
        _results.Children.Add(Ui.Busy());

        try
        {
            _snapshot = await AppState.HardwareAsync(rescan);
            Render(_snapshot);

            if (_snapshot.Warnings.Count > 0)
                _status.Show(InfoBarSeverity.Warning, "Some parts could not be read", string.Join("\n", _snapshot.Warnings));

            _copy.IsEnabled = true;
        }
        catch (Exception ex)
        {
            _results.Children.Clear();
            _status.Show(InfoBarSeverity.Error, "Scan failed", ex.Message);
        }
        finally
        {
            _rescan.IsEnabled = true;
        }
    }

    private void Copy()
    {
        if (_snapshot is null) return;
        var package = new DataPackage();
        package.SetText(_snapshot.ToReport());
        Clipboard.SetContent(package);
        _status.Show(InfoBarSeverity.Success, "Copied", "The hardware report is on your clipboard.");
    }

    private void Render(HardwareSnapshot s)
    {
        _results.Children.Clear();

        if (s.Pc.Manufacturer.Length > 0)
            _subtitle.Text = $"{s.Pc.BrandName} {s.Pc.Model}".Trim() + " · ZenTune uses this to pick the right settings for every game.";

        var gpu = s.PrimaryGpu;
        _results.Children.Add(gpu is null
            ? Ui.Card(Ui.IconGame, "Graphics card", "Not found", [])
            : Ui.Card(Ui.IconGame, "Graphics card", gpu.Name,
            [
                ("Brand", VendorName(gpu.Vendor)),
                ("Video memory", Format.Bytes(gpu.VramBytes)),
                ("Driver", gpu.DriverVersion.Length > 0 ? gpu.DriverVersion : "Unknown"),
                ("Low-latency tech", LatencyTech(gpu.Vendor)),
            ], accent: true));

        var pair = new Grid { ColumnSpacing = 16 };
        pair.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pair.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var cpu = Ui.Card(Ui.IconCpu, "Processor", s.Cpu.Name,
        [
            ("Cores / threads", s.Cpu.Cores > 0 ? $"{s.Cpu.Cores} / {s.Cpu.Threads}" : $"? / {s.Cpu.Threads}"),
            ("Base clock", s.Cpu.BaseClockMhz > 0 ? $"{s.Cpu.BaseClockMhz / 1000.0:0.0} GHz" : "Unknown"),
        ]);

        var memoryRows = new List<(string, string)>
        {
            ("Speed", s.Memory.SpeedMts > 0 ? $"{s.Memory.SpeedMts} MT/s" : "Unknown"),
            ("Modules", s.Memory.Modules > 0 ? $"{s.Memory.Modules}{(s.Memory.Modules >= 2 ? " (dual channel)" : " (single channel)")}" : "Unknown"),
        };
        if (s.Pc.IsBrandPc) memoryRows.Add(("Managed by", s.Pc.BrandName));

        var ram = Ui.Card(Ui.IconMemory, "Memory", $"{Format.Bytes(s.Memory.TotalBytes)} {s.Memory.Type}", memoryRows);

        cpu.VerticalAlignment = VerticalAlignment.Stretch;
        ram.VerticalAlignment = VerticalAlignment.Stretch;
        Grid.SetColumn(ram, 1);
        pair.Children.Add(cpu);
        pair.Children.Add(ram);
        _results.Children.Add(pair);

        foreach (var d in s.Displays)
            _results.Children.Add(DisplayCard(s, d));

        foreach (var other in s.Gpus.Where(g => g != gpu))
        {
            _results.Children.Add(Ui.Card(Ui.IconLaptop,
                other.IsIntegrated ? "Built-in graphics (not used for games)" : "Other graphics card",
                other.Name,
                [("Video memory", other.IsIntegrated ? "Shared with system memory" : Format.Bytes(other.VramBytes)), ("Driver", other.DriverVersion)]));
        }
    }

    private static UIElement DisplayCard(HardwareSnapshot s, DisplayInfo d)
    {
        string res = $"{d.Width} × {d.Height}" + (d.IsRunningNative ? " (native)" : $" (native is {d.NativeWidth} × {d.NativeHeight})");
        int best = d.MaxRefreshAtCurrentHz > 0 ? d.MaxRefreshAtCurrentHz : d.MaxRefreshHz;
        string hz = $"{d.RefreshHz:0.##} Hz" + (best > 0 ? $" (max {best} Hz)" : "");
        string hdr = !d.HdrSupported ? "Not supported" : d.HdrEnabled ? "Supported · On" : "Supported · Off";

        var gpu = s.GpuFor(d);
        string plugged = gpu is null ? "Unknown" : gpu.IsIntegrated ? $"{gpu.Name} (motherboard)" : gpu.Name;

        var notes = new List<string>();
        if (!d.IsRunningNative && d.NativeWidth > 0)
            notes.Add("Windows is running below the screen's native resolution. Games can look soft like this.");
        if (best > 0 && d.RefreshHz + 1 < best)
            notes.Add($"The screen can do {best} Hz but runs at {d.RefreshHz:0} Hz. System check can fix this with one click.");
        if (d.Connection == "HDMI")
            notes.Add("Connected over HDMI. If colors look grey or faded, check that the GPU's output color range is set to Full.");

        return Ui.Card(Ui.IconMonitor, d.IsPrimary ? "Main display" : "Display", d.Name,
        [
            ("Resolution", res),
            ("Refresh rate", hz),
            ("HDR", hdr),
            ("Color depth", d.BitsPerColor > 0 ? $"{d.BitsPerColor}-bit" : "Unknown"),
            ("Connection", d.Connection),
            ("Plugged into", plugged),
            ("G-Sync / FreeSync", "Checked in a later version"),
        ], notes: notes);
    }

    private static string VendorName(GpuVendor v) => v switch
    {
        GpuVendor.Nvidia => "NVIDIA",
        GpuVendor.Amd => "AMD",
        GpuVendor.Intel => "Intel",
        _ => "Other"
    };

    private static string LatencyTech(GpuVendor v) => v switch
    {
        GpuVendor.Nvidia => "NVIDIA Reflex",
        GpuVendor.Amd => "AMD Anti-Lag",
        GpuVendor.Intel => "Intel XeLL",
        _ => "None"
    };
}
