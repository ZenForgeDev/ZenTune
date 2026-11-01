using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ZenTune.Core.Changes;
using ZenTune.Core.Games;
using ZenTune.Core.Handheld;
using ZenTune.Core.Hardware;
using ZenTune.Core.Presets;

namespace ZenTune.App.Pages;

/// <summary>"Handheld": battery, power mode, graphics memory and one-tap presets for handheld gaming PCs.</summary>
internal sealed partial class HandheldPage : UserControl
{
    private readonly StackPanel _results = new() { Spacing = 8 };
    private readonly InfoBar _status;
    private readonly Button _refresh;
    private bool _loading;

    public HandheldPage()
    {
        _refresh = Ui.IconButton(Ui.IconRefresh, "Refresh", accent: true);
        _refresh.Click += async (_, _) => await LoadAsync();

        var (root, body, status) = Ui.Page("Handheld",
            "Battery, power and graphics settings for handheld gaming PCs like the ROG Ally, Steam Deck and Legion Go.", _refresh);
        _status = status;
        body.Children.Add(_results);
        Content = root;

        _ = LoadAsync();
    }

    public void Reload()
    {
        if (!_loading) _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        _loading = true;
        _refresh.IsEnabled = false;
        if (_results.Children.Count == 0) _results.Children.Add(Ui.Busy());

        try
        {
            var hw = await AppState.HardwareAsync();
            var data = await Task.Run(() => (
                Profile: HardwareProfile.From(hw),
                Power: HandheldInfo.Power(),
                Mode: HandheldInfo.CurrentPowerMode(),
                Memory: HandheldInfo.GraphicsMemory(hw),
                Battery: HandheldInfo.BatteryHealth()));
            Render(hw, data.Profile, data.Power, data.Mode, data.Memory, data.Battery);
        }
        catch (Exception ex)
        {
            _results.Children.Clear();
            _status.Show(InfoBarSeverity.Error, "Could not read the handheld status", ex.Message);
        }
        finally
        {
            _refresh.IsEnabled = true;
            _loading = false;
        }
    }

    private void Render(HardwareSnapshot hw, HardwareProfile profile, PowerStatus power, PowerMode mode,
        (double ReservedGb, double TotalRamGb, string Advice, bool TooLow) memory, (int DesignMWh, int FullMWh)? health)
    {
        _results.Children.Clear();

        if (!profile.IsHandheld)
        {
            _results.Children.Add(new InfoBar
            {
                Severity = InfoBarSeverity.Warning,
                Title = "This PC is not a handheld",
                Message = "These settings are meant for handheld PCs like the ROG Ally, Steam Deck and Legion Go. " +
                          "On this PC they may make games look worse. Everything still works, so you can try it out.",
                IsOpen = true,
                IsClosable = false,
            });
        }

        // Status
        string device = $"{hw.Pc.BrandName} {hw.Pc.Model}".Trim();
        string battery = !power.HasBattery ? "No battery"
            : power.OnBattery
                ? $"{power.Percent}% · on battery" + (power.TimeLeft is { } t ? $" · about {(int)t.TotalHours} h {t.Minutes:00} min left" : "")
                : $"{power.Percent}% · on the charger";
        var display = hw.PrimaryDisplay;

        _results.Children.Add(Ui.Card(Ui.IconLaptop, "Status", device.Length > 0 ? device : "This PC",
        [
            ("Battery", battery),
            ("Screen", display is null ? "Unknown" : $"{display.Width} × {display.Height} · {display.RefreshHz:0} Hz"),
            ("Graphics", profile.GpuName),
            ("Graphics memory", memory.ReservedGb > 0 ? $"{memory.ReservedGb:0.#} GB reserved of {memory.TotalRamGb:0} GB" : "Unknown"),
            ("Windows power mode", HandheldInfo.Name(mode)),
            ("Battery health", health is { } bat
                ? $"{Math.Min(100, bat.FullMWh * 100.0 / bat.DesignMWh):0}% of the original capacity ({bat.FullMWh / 1000.0:0.#} of {bat.DesignMWh / 1000.0:0.#} Wh)"
                : power.HasBattery ? "Windows does not share it" : "No battery"),
        ]));

        // Graphics memory
        _results.Children.Add(Ui.SectionHeader("Graphics memory"));
        _results.Children.Add(Row(memory.TooLow ? Ui.IconWarning : Ui.IconCheck,
            memory.TooLow ? "SystemFillColorCautionBrush" : "SystemFillColorSuccessBrush",
            memory.TooLow ? "Graphics memory is low" : "Graphics memory looks good",
            memory.Advice + (profile.IsHandheld ? "" : " (On PCs with a graphics card this is the card's own memory.)"),
            null));

        // Power mode
        _results.Children.Add(Ui.SectionHeader("Power mode"));
        var suggested = power.OnBattery ? PowerMode.BestEfficiency : PowerMode.BestPerformance;
        _results.Children.Add(Ui.Secondary(power.OnBattery
            ? "On battery, Best power efficiency gives the longest play time; games run a bit slower."
            : power.HasBattery
                ? "On the charger, Best performance lets the chip run at full speed."
                : "This PC has no battery, so Best performance is the natural choice."));

        var modes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var m in new[] { PowerMode.BestEfficiency, PowerMode.Balanced, PowerMode.BestPerformance })
        {
            var b = new Button { Content = HandheldInfo.Name(m) + (m == mode ? " ✓" : "") + (m == suggested ? " ★" : "") };
            if (m == mode) b.Style = Ui.StyleRes("AccentButtonStyle");
            b.IsEnabled = mode != PowerMode.Unknown && m != mode;
            var target = m;
            b.Click += async (_, _) =>
            {
                b.IsEnabled = false;
                try
                {
                    await Task.Run(() => HandheldInfo.SetPowerMode(target));
                    _status.Show(InfoBarSeverity.Success, "Power mode changed", $"Windows now uses {HandheldInfo.Name(target)}. You can undo it on the System check page.");
                }
                catch (Exception ex)
                {
                    _status.Show(InfoBarSeverity.Error, "Could not change the power mode", ex.Message);
                }
                await LoadAsync();
            };
            modes.Children.Add(b);
        }
        _results.Children.Add(modes);
        if (mode == PowerMode.Unknown)
            _results.Children.Add(Ui.Secondary("This version of Windows does not report its power mode."));

        // Battery saving: refresh rate
        if (power.OnBattery && hw.PrimaryDisplay is { } screen && screen.RefreshHz >= 100)
        {
            var lower = new Button { Content = "Set to 60 Hz", VerticalAlignment = VerticalAlignment.Center };
            lower.Click += async (_, _) =>
            {
                lower.IsEnabled = false;
                try
                {
                    await Task.Run(() => Tweaks.SetRefreshRate(screen, 60, $"{screen.Name}: {screen.RefreshHz:0} Hz → 60 Hz (battery)"));
                    _status.Show(InfoBarSeverity.Success, "Refresh rate lowered", "60 Hz is easier on the battery. You can undo it on the System check page.");
                }
                catch (Exception ex)
                {
                    _status.Show(InfoBarSeverity.Error, "Could not change the refresh rate", ex.Message);
                }
                await LoadAsync();
            };
            _results.Children.Add(Ui.SectionHeader("Battery saving"));
            _results.Children.Add(Row(Ui.IconInfo, "AccentTextFillColorPrimaryBrush",
                $"The screen runs at {screen.RefreshHz:0} Hz on battery",
                "A lower refresh rate can save battery when you play slower games or cap them at 40–60 FPS. Switch back to the full rate on the charger.",
                lower));
        }

        // ROG Xbox Ally
        if (HandheldInfo.IsXboxAlly(hw))
        {
            _results.Children.Add(Ui.SectionHeader("ROG Xbox Ally"));
            _results.Children.Add(Row(Ui.IconInfo, "AccentTextFillColorPrimaryBrush", "Operating modes (from ASUS)",
                "ASUS lists Silent at 13 W, Performance at 17 W, and Turbo at a 35 W default on the Xbox Ally X. " +
                (power.OnBattery
                    ? "On battery, Silent or Performance gives the longest play time. "
                    : "On the charger, Turbo gives the most performance. ") +
                "The mode is changed in Armoury Crate or the Xbox Game Bar; ZenTune can't change it for you.",
                null));
            _results.Children.Add(Row(Ui.IconInfo, "AccentTextFillColorPrimaryBrush", "Xbox full screen experience",
                "ASUS says this mode improves performance and battery life by closing background processes you don't need while you play. " +
                "Use it for games, and switch to the desktop when you need other apps.",
                null));
            _results.Children.Add(Row(Ui.IconInfo, "AccentTextFillColorPrimaryBrush", "Memory assigned to the GPU",
                "Set in Armoury Crate (Settings → Operating Mode → GPU Settings) and needs a restart. 4 GB is the default; one source lists 6 and 8 GB as choices. " +
                "More graphics memory helps texture-heavy games and leaves less for Windows, so change it only if a game stutters or shows blurry textures.",
                null));
        }

        // Presets for all games
        _results.Children.Add(Ui.SectionHeader("Handheld preset for all games"));
        int cap = PresetInfo.HandheldCap(profile);
        var applyAll = Ui.IconButton(Ui.IconGame, "Apply to all games", accent: profile.IsHandheld);
        applyAll.Click += async (_, _) => await ApplyAllAsync(applyAll, profile);
        _results.Children.Add(Row(Ui.IconGame, "AccentTextFillColorPrimaryBrush",
            "Give every supported game the Handheld preset",
            $"Lower settings, upscaling and an FPS limit of {cap} " +
            (profile.IsHandheld ? (power.OnBattery ? "(on battery; 60 on the charger)." : "(on the charger; 40 on battery).") : "(the lowest that is safe on this screen).") +
            " Every game's settings are backed up first, and running games are skipped.",
            applyAll));

        // AMD features
        if (profile.Vendor == GpuVendor.Amd)
        {
            _results.Children.Add(Ui.SectionHeader("AMD features worth knowing"));
            _results.Children.Add(Row(Ui.IconInfo, "AccentTextFillColorPrimaryBrush", "Radeon Chill",
                "Lowers the FPS when little moves on screen and raises it when you move. Saves a lot of battery in slower games. AMD Software → Gaming → Graphics.", null));
            _results.Children.Add(Row(Ui.IconInfo, "AccentTextFillColorPrimaryBrush", "AMD Fluid Motion Frames (AFMF)",
                "Frame generation for almost any game. Makes 40 FPS look like 80, with a little extra input lag. Best in single-player games.", null));
            _results.Children.Add(Row(Ui.IconInfo, "AccentTextFillColorPrimaryBrush", "Radeon Super Resolution (RSR)",
                "Upscaling for games without FSR: run the game at 720p in fullscreen and RSR sharpens it to the screen's resolution.", null));
        }
    }

    private async Task ApplyAllAsync(Button button, HardwareProfile profile)
    {
        button.IsEnabled = false;
        try
        {
            var scan = await Task.Run(GameScanner.Scan);
            var plans = await Task.Run(() => scan.Supported
                .Select(g => (Game: g, Tuner: PresetService.For(g)))
                .Where(x => x.Tuner is not null)
                .Select(x => x.Tuner!.Plan(x.Game, PresetKind.Handheld, profile))
                .Where(p => p.Effective.Any())
                .ToList());

            if (plans.Count == 0)
            {
                _status.Show(InfoBarSeverity.Informational, "Nothing to change",
                    "No supported game was found, or they all use the Handheld preset already.");
                return;
            }

            bool ok = await Ui.ConfirmAsync(XamlRoot, "Apply the Handheld preset to all games?",
                string.Join("\n", plans.Select(p => $"• {p.Game.Name}: {p.Effective.Count()} settings")) +
                "\n\nEach game's current settings are backed up first. Close the games before you continue.",
                "Apply");
            if (!ok) return;

            var done = new List<string>();
            var skipped = new List<string>();
            foreach (var plan in plans)
            {
                try
                {
                    await Task.Run(() => PresetService.Apply(plan));
                    done.Add(plan.Game.Name);
                }
                catch (Exception ex)
                {
                    skipped.Add($"{plan.Game.Name} ({ex.Message})");
                }
            }

            _status.Show(skipped.Count == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
                $"Handheld preset applied to {done.Count} {(done.Count == 1 ? "game" : "games")}",
                (done.Count > 0 ? string.Join(", ", done) + ". " : "") +
                (skipped.Count > 0 ? "Skipped: " + string.Join("; ", skipped) : "To go back, use Restore on each game's card."));
        }
        catch (Exception ex)
        {
            _status.Show(InfoBarSeverity.Error, "Could not apply the presets", ex.Message);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private static UIElement Row(string glyph, string brushKey, string title, string detail, Button? button)
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(new FontIcon { Glyph = glyph, FontSize = 18, Foreground = Ui.Res(brushKey), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) });

        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(Ui.Secondary(detail));
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        if (button is not null)
        {
            button.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(button, 2);
            grid.Children.Add(button);
        }

        return Ui.CardFrame(grid, new Thickness(20, 14, 20, 14));
    }
}
