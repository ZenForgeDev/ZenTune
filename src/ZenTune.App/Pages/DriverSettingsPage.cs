using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ZenTune.Core.Driver;
using ZenTune.Core.Games;
using ZenTune.Core.Hardware;

namespace ZenTune.App.Pages;

/// <summary>
/// "Driver settings": the graphics driver options people normally set in the NVIDIA Control Panel
/// (G-Sync, V-Sync, frame limit, low latency, shader cache ...), plus two Windows graphics options
/// that work with every graphics card. NVIDIA first; AMD is planned.
/// </summary>
internal sealed partial class DriverSettingsPage : UserControl
{
    private const string DefaultLabel = "Driver default";

    private readonly StackPanel _results = new() { Spacing = 8 };
    private readonly InfoBar _status;
    private readonly Button _refresh;
    private readonly Button _resetAll;
    private bool _loading;
    private bool _populating;
    private bool _nvidiaOk;
    private Func<Task>? _reloadGame;
    private Task<GameScanResult>? _gameScan;

    public DriverSettingsPage()
    {
        _refresh = Ui.IconButton(Ui.IconRefresh, "Refresh");
        _refresh.Click += async (_, _) => await LoadAsync();

        _resetAll = Ui.IconButton(Ui.IconUndo, "Reset NVIDIA settings");
        _resetAll.IsEnabled = false;
        _resetAll.Click += async (_, _) => await ResetAllAsync();

        var (root, body, status) = Ui.Page("Driver settings",
            "Graphics driver options in one place, without the NVIDIA Control Panel or the NVIDIA app. Every change can be undone.",
            _resetAll, _refresh);
        _status = status;
        body.Children.Add(_results);
        Content = root;

        _ = LoadAsync();
    }

    public void Reload()
    {
        if (!_loading) _ = LoadAsync();
    }

    // ---------------------------------------------------------------- Loading

    private async Task LoadAsync()
    {
        _loading = true;
        _refresh.IsEnabled = false;
        if (_results.Children.Count == 0) _results.Children.Add(Ui.Busy());

        try
        {
            GpuInfo? nvGpu = null;
            bool isLaptop = false;
            try
            {
                var hw = await AppState.HardwareAsync(rescan: true); // a new scan, so the installed driver version is current
                nvGpu = hw.Gpus.Where(g => g.Vendor == GpuVendor.Nvidia)
                    .OrderBy(g => g.IsIntegrated).ThenByDescending(g => g.VramBytes).FirstOrDefault();
                isLaptop = hw.Pc.IsLaptop;
            }
            catch
            {
                // Without the hardware scan the driver check is simply not shown
            }

            var data = await Task.Run(() =>
            {
                bool? vrr = WindowsGraphics.Get(WindowsGraphics.VariableRefreshRate);
                bool? swap = WindowsGraphics.Get(WindowsGraphics.WindowedOptimizations);

                bool ok = NvidiaDrs.IsAvailable(out var reason);
                Dictionary<uint, uint?>? values = null;
                if (ok)
                {
                    try { values = NvidiaDrs.Read(NvidiaCatalog.All.Select(s => s.Id)); }
                    catch (Exception ex) { ok = false; reason = ex.Message; }
                }
                return (Vrr: vrr, Swap: swap, Ok: ok, Reason: reason, Values: values);
            });

            _nvidiaOk = data.Ok;
            Render(data.Vrr, data.Swap, data.Ok, data.Reason, data.Values, nvGpu, isLaptop);
        }
        catch (Exception ex)
        {
            _results.Children.Clear();
            _status.Show(InfoBarSeverity.Error, "Could not read the driver settings", ex.Message);
        }
        finally
        {
            _resetAll.IsEnabled = _nvidiaOk;
            _refresh.IsEnabled = true;
            _loading = false;
        }
    }

    private void Render(bool? vrr, bool? swap, bool nvidiaOk, string? reason, Dictionary<uint, uint?>? values, GpuInfo? nvGpu, bool isLaptop)
    {
        _populating = true;
        try
        {
            _results.Children.Clear();

            _results.Children.Add(new InfoBar
            {
                Severity = InfoBarSeverity.Informational,
                Title = "New and not tested on many PCs yet",
                Message = "These settings are written straight into the graphics driver. Each change is saved in the undo list on the System check page, " +
                          "and 'Reset NVIDIA settings' removes everything ZenTune set. Changes apply to the next game you start.",
                IsOpen = true,
                IsClosable = false,
            });

            // ---- Windows (every graphics card)
            _results.Children.Add(Ui.SectionHeader("Windows graphics"));
            _results.Children.Add(Ui.Secondary("Works with NVIDIA, AMD and Intel. The same switches as Settings → System → Display → Graphics."));

            _results.Children.Add(ToggleRow("Variable refresh rate for windowed games",
                "Lets G-Sync, FreeSync and Adaptive-Sync work in games that use windowed or borderless mode. Needs a monitor with variable refresh rate turned on.",
                vrr == true,
                async on => await ApplyWindowsAsync(WindowsGraphics.VariableRefreshRate, on, "Variable refresh rate for windowed games")));

            _results.Children.Add(ToggleRow("Optimizations for windowed games",
                "Lets Windows show older DirectX 10 and 11 games in windowed or borderless mode with less delay, close to fullscreen.",
                swap == true,
                async on => await ApplyWindowsAsync(WindowsGraphics.WindowedOptimizations, on, "Optimizations for windowed games")));

            // ---- NVIDIA driver (check for a newer driver)
            if (nvGpu is not null)
            {
                _results.Children.Add(Ui.SectionHeader("NVIDIA driver"));
                _results.Children.Add(Ui.CardFrame(BuildDriverSection(nvGpu, isLaptop), new Thickness(20, 14, 20, 14)));
            }

            // ---- NVIDIA
            _results.Children.Add(Ui.SectionHeader("NVIDIA"));

            if (!nvidiaOk)
            {
                _results.Children.Add(new InfoBar
                {
                    Severity = InfoBarSeverity.Warning,
                    Title = "NVIDIA settings are not available on this PC",
                    Message = reason ?? "No NVIDIA graphics card was found.",
                    IsOpen = true,
                    IsClosable = false,
                });
                return;
            }

            _results.Children.Add(Ui.Secondary("These are global settings: they apply to every game unless a game has its own profile in the driver."));

            string? group = null;
            foreach (var setting in NvidiaCatalog.All)
            {
                if (setting.Group != group)
                {
                    group = setting.Group;
                    _results.Children.Add(new TextBlock
                    {
                        Text = group,
                        FontWeight = FontWeights.SemiBold,
                        Margin = new Thickness(0, 10, 0, 0),
                    });
                }

                uint? current = values is not null && values.TryGetValue(setting.Id, out var v) ? v : null;
                _results.Children.Add(SettingRow(setting, current));
            }

            // ---- Per game
            _results.Children.Add(Ui.SectionHeader("Per-game settings"));
            _results.Children.Add(Ui.CardFrame(BuildGameSection(), new Thickness(20, 14, 20, 14)));
        }
        finally
        {
            _populating = false;
        }
    }

    // ---------------------------------------------------------------- Rows

    private UIElement ToggleRow(string title, string detail, bool isOn, Func<bool, Task> onChanged)
    {
        var toggle = new ToggleSwitch { IsOn = isOn, OnContent = "On", OffContent = "Off", VerticalAlignment = VerticalAlignment.Center };
        toggle.Toggled += async (_, _) =>
        {
            if (_populating) return;
            toggle.IsEnabled = false;
            await onChanged(toggle.IsOn);
            toggle.IsEnabled = true;
        };
        return FrameRow(title, detail, toggle);
    }

    private UIElement SettingRow(DriverSetting setting, uint? current, string? exe = null)
    {
        // In a game's own profile "default" means the game follows the global setting
        string defaultLabel = exe is null ? DefaultLabel : "Same as global";
        var combo = new ComboBox { MinWidth = 220, VerticalAlignment = VerticalAlignment.Center };
        combo.Items.Add(defaultLabel);
        foreach (var o in setting.Options) combo.Items.Add(o.Label);

        // A value that is not in our list: set by someone else (Control Panel, Profile Inspector) or typed by the user below
        string? foreign = null;
        if (current is { } cv)
        {
            var match = setting.Options.FirstOrDefault(o => o.Value == cv);
            if (match is not null)
            {
                combo.SelectedItem = match.Label;
            }
            else
            {
                foreign = setting.Custom is { } r
                    ? $"Custom ({cv} {r.Unit})"
                    : $"Other value ({cv}) - not changed by ZenTune";
                combo.Items.Add(foreign);
                combo.SelectedItem = foreign;
            }
        }
        else
        {
            combo.SelectedIndex = 0;
        }

        combo.SelectionChanged += async (_, _) =>
        {
            if (_populating || combo.SelectedItem is not string label) return;
            if (label == foreign) return; // nothing to write for a value ZenTune does not know

            DriverOption? option = label == defaultLabel ? null : setting.Options.First(o => o.Label == label);
            combo.IsEnabled = false;
            // G-Sync needs the driver's own order of steps and a redraw of the screens, so it has its own routine
            if (setting.Id == NvidiaCatalog.VrrMode && exe is null && option is not null)
                await ApplyGSyncAsync(option.Value);
            else
                await ApplyNvidiaAsync(setting, option, exe);
            combo.IsEnabled = true;
        };

        if (setting.Id == NvidiaCatalog.VrrMode && setting.Custom is null)
        {
            // G-Sync: Apply runs the whole switch-on routine again for the value chosen in the list (no admin rights needed).
            // Repair is the older way: it turns on the driver's per-monitor switch as admin (see EnsureGSyncActiveAsync).
            var apply = new Button { Content = "Apply", VerticalAlignment = VerticalAlignment.Center };
            apply.Click += async (_, _) =>
            {
                DriverOption? chosenOption = combo.SelectedItem is string chosen
                    ? setting.Options.FirstOrDefault(o => o.Label == chosen)
                    : null;
                if (chosenOption is null)
                {
                    _status.Show(InfoBarSeverity.Informational, "Choose G-Sync first",
                        "Pick Off, Fullscreen only or Fullscreen and windowed in the list, then press Apply.");
                    return;
                }
                apply.IsEnabled = false;
                await ApplyGSyncAsync(chosenOption.Value);
                apply.IsEnabled = true;
            };
            var repair = new Button { Content = "Repair (admin)", VerticalAlignment = VerticalAlignment.Center };
            ToolTipService.SetToolTip(repair, "Only if G-Sync stays off after Apply: turns on the driver's per-monitor switch as admin. Screens go black for a second.");
            repair.Click += async (_, _) =>
            {
                repair.IsEnabled = false;
                await EnsureGSyncActiveAsync(manual: true);
                repair.IsEnabled = true;
            };
            var gsyncPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            gsyncPanel.Children.Add(combo);
            gsyncPanel.Children.Add(apply);
            gsyncPanel.Children.Add(repair);
            return FrameRow(setting.Title, setting.Help, gsyncPanel);
        }

        if (setting.Custom is not { } range)
            return FrameRow(setting.Title, setting.Help, combo);

        // Free input: type any number in the allowed range
        var box = new NumberBox
        {
            Minimum = range.Min,
            Maximum = range.Max,
            SmallChange = 1,
            LargeChange = 10,
            Width = 130,
            PlaceholderText = range.Unit,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden,
            VerticalAlignment = VerticalAlignment.Center,
            Value = current is { } c0 && c0 >= range.Min && c0 <= range.Max ? c0 : double.NaN,
        };
        var set = new Button { Content = "Set", VerticalAlignment = VerticalAlignment.Center };

        async Task ApplyTypedAsync()
        {
            if (double.IsNaN(box.Value))
            {
                _status.Show(InfoBarSeverity.Warning, "Type a number", $"Enter a value from {range.Min} to {range.Max} {range.Unit}.");
                return;
            }
            uint n = (uint)Math.Clamp(Math.Round(box.Value), range.Min, range.Max);
            set.IsEnabled = false;
            combo.IsEnabled = false;
            await ApplyNvidiaAsync(setting, new DriverOption($"{n} {range.Unit}", n), exe);
            await ReloadAfterAsync(exe); // show what the driver really has
        }

        set.Click += async (_, _) => await ApplyTypedAsync();
        box.KeyDown += async (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter) await ApplyTypedAsync();
        };

        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(combo);
        panel.Children.Add(box);
        panel.Children.Add(set);
        return FrameRow(setting.Title, setting.Help, panel);
    }

    private static UIElement FrameRow(string title, string detail, UIElement control)
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(Ui.Secondary(detail));
        grid.Children.Add(text);

        Grid.SetColumn((FrameworkElement)control, 1);
        grid.Children.Add(control);
        return Ui.CardFrame(grid, new Thickness(20, 14, 20, 14));
    }

    // ---------------------------------------------------------------- Actions

    private async Task ApplyWindowsAsync(string token, bool on, string title)
    {
        try
        {
            await Task.Run(() => WindowsGraphics.Set(token, on, $"{title}: {(on ? "on" : "off")}"));
            _status.Show(InfoBarSeverity.Success, "Saved", $"{title} is now {(on ? "on" : "off")}. You can undo it on the System check page.");
        }
        catch (Exception ex)
        {
            _status.Show(InfoBarSeverity.Error, "Could not change the Windows setting", ex.Message);
            await LoadAsync();
        }
    }

    private async Task ApplyNvidiaAsync(DriverSetting setting, DriverOption? option, string? exe = null)
    {
        try
        {
            var writes = NvidiaCatalog.WritesFor(setting, option);
            string what = option?.Label ?? (exe is null ? DefaultLabel.ToLowerInvariant() : "same as global");
            string title = exe is null
                ? $"NVIDIA: {setting.Title} → {what}"
                : $"NVIDIA ({exe}): {setting.Title} → {what}";
            await Task.Run(() => NvidiaDrs.Apply(title, writes, exe));
            _status.Show(InfoBarSeverity.Success, "Saved to the NVIDIA driver",
                $"{(exe is null ? "" : exe + " · ")}{setting.Title}: {what}. It applies to the next time the game starts. You can undo it on the System check page.");
        }
        catch (Exception ex)
        {
            _status.Show(InfoBarSeverity.Error, "Could not change the NVIDIA setting", ex.Message);
            await ReloadAfterAsync(exe); // show what the driver really has
        }
    }

    /// <summary>
    /// Switches G-Sync the way the NVIDIA Control Panel does it: settings in the driver's order with the screens redrawn
    /// in between (see <see cref="NvidiaGSync"/>). The screens blink once or twice.
    /// </summary>
    private async Task ApplyGSyncAsync(uint mode)
    {
        try
        {
            _status.Show(InfoBarSeverity.Informational, "Switching G-Sync...",
                "The screens blink once or twice. Please wait a few seconds.");
            await Task.Run(() => NvidiaGSync.Apply(mode));
            _status.Show(InfoBarSeverity.Success, mode == 0 ? "G-Sync is off" : "G-Sync is on",
                (mode == 0 ? "G-Sync is switched off in the driver." : "G-Sync is switched on in the driver.") +
                " It also needs G-Sync switched on in the monitor's own menu. You can undo it on the System check page.");
        }
        catch (Exception ex)
        {
            _status.Show(InfoBarSeverity.Error, "Could not switch G-Sync", ex.Message);
            await LoadAsync(); // show what the driver really has
        }
    }

    /// <summary>
    /// The driver also has an on/off switch per monitor, which is off after a fresh driver install. Without it
    /// G-Sync stays off whatever the settings say. Asks first, then turns it on and restarts the graphics driver.
    /// </summary>
    private async Task EnsureGSyncActiveAsync(bool manual = false)
    {
        try
        {
            var monitors = await Task.Run(NvidiaMonitorVrr.Read);
            // The registry key needs admin rights to read, so ZenTune often cannot see the switch. Then only the Apply
            // button goes on (the admin helper checks everything itself); choosing a value never asks by itself.
            if (monitors.Count == 0 && !manual) return;
            if (monitors.Count > 0 && !NvidiaMonitorVrr.NeedsActivation(monitors))
            {
                if (manual) _status.Show(InfoBarSeverity.Success, "Already on",
                    "The driver's G-Sync switch is already on for your monitors. Choose Fullscreen only or Fullscreen and windowed above if you have not.");
                return;
            }

            if (!await Ui.ConfirmAsync(XamlRoot, "Switch G-Sync on for your monitors?",
                    "The NVIDIA driver keeps its own on/off switch for each monitor, and it is off after a fresh driver install. " +
                    "ZenTune can turn it on, the same way the Apply button on the NVIDIA Control Panel's G-Sync page does.\n\n" +
                    "Windows will ask for admin rights once. Your screens go black for a second while the graphics driver restarts, so save anything open first.",
                    "Turn on"))
            {
                _status.Show(InfoBarSeverity.Warning, "G-Sync is saved, but not active yet",
                    "The driver's switch for your monitor is still off. Choose the setting again to turn it on, or press Apply on the NVIDIA Control Panel's G-Sync page.");
                return;
            }

            string? error = await NvidiaMonitorVrr.SetAsync(true);
            if (error is not null)
            {
                _status.Show(InfoBarSeverity.Warning, "G-Sync is saved, but not active yet", error);
                return;
            }
            await NvidiaMonitorVrr.RestartGraphicsDriver();
            _status.Show(InfoBarSeverity.Success, "G-Sync is on",
                "The driver's switch for your monitors is on and the graphics driver was restarted. It also needs G-Sync switched on in the monitor's own menu.");
        }
        catch (Exception ex)
        {
            _status.Show(InfoBarSeverity.Error, "Could not switch G-Sync on", ex.Message);
        }
    }

    private Task ReloadAfterAsync(string? exe) =>
        exe is null ? LoadAsync() : (_reloadGame?.Invoke() ?? Task.CompletedTask);

    private async Task ResetAllAsync()
    {
        if (!await Ui.ConfirmAsync(XamlRoot, "Reset NVIDIA settings?",
                "This removes every value shown on this page from the global NVIDIA profile, so the driver and each game decide again. " +
                "Settings you made in other tools that are not on this page stay as they are.",
                "Reset"))
            return;

        _resetAll.IsEnabled = false;
        try
        {
            var writes = NvidiaCatalog.AllIds().Distinct().Select(id => new DriverWrite(id, null)).ToList();
            await Task.Run(() => NvidiaDrs.Apply("NVIDIA: reset all driver settings from ZenTune", writes));
            _status.Show(InfoBarSeverity.Success, "NVIDIA settings reset", "The driver is back to its defaults for these settings.");
        }
        catch (Exception ex)
        {
            _status.Show(InfoBarSeverity.Error, "Could not reset the NVIDIA settings", ex.Message);
        }
        await LoadAsync();
    }
}
