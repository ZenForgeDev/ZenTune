using System.Diagnostics;
using System.Runtime.Versioning;
using ZenTune.Core.Changes;
using ZenTune.Core.Hardware;

namespace ZenTune.Core.Checks;

public enum CheckStatus { Good, Info, Warning }

/// <summary>One line on the System check page.</summary>
public sealed class CheckResult
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Detail { get; init; }
    public required CheckStatus Status { get; init; }

    /// <summary>Button text, for example "Set to 165 Hz". Null when there is nothing to click.</summary>
    public string? ActionLabel { get; init; }

    /// <summary>Runs the fix. Must be safe to call from a background thread.</summary>
    public Action? Action { get; init; }

    /// <summary>True when the action changes Windows (and lands in the undo list), false when it only opens a page.</summary>
    public bool ActionChangesSystem { get; init; } = true;

    /// <summary>True for the checks shown together under "Ready to play" (set by SystemChecks.Run).</summary>
    public bool Preflight { get; set; }

    /// <summary>When set, the page asks this question (with a Cancel button) before the action runs.</summary>
    public string? ConfirmText { get; init; }

    /// <summary>Shown after an action that does not change the undo list (ActionChangesSystem = false) has finished; the page then checks again.</summary>
    public string? ActionDoneMessage { get; init; }
}

/// <summary>Looks for Windows and hardware settings that cost FPS or smoothness.</summary>
[SupportedOSPlatform("windows")]
public static class SystemChecks
{
    private const string GameBarKey = @"Software\Microsoft\GameBar";
    private const string GameDvrKey = @"Software\Microsoft\Windows\CurrentVersion\GameDVR";
    private const string DirectXKey = @"Software\Microsoft\DirectX\UserGpuPreferences";
    private const string GraphicsDriversKey = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";

    /// <summary>The checks that answer "can I start a game now?".</summary>
    private static readonly HashSet<string> PreflightIds = ["tpm", "secureboot", "driver-age", "power", "gamemode", "ram-speed", "gameinput"];

    public static List<CheckResult> Run(HardwareSnapshot hw)
    {
        var results = new List<CheckResult>();

        void Try(Func<IEnumerable<CheckResult>> check)
        {
            try { results.AddRange(check()); }
            catch (Exception ex)
            {
                results.Add(new CheckResult
                {
                    Id = "error-" + results.Count,
                    Title = "A check could not run",
                    Detail = ex.Message,
                    Status = CheckStatus.Info,
                });
            }
        }

        Try(() => RefreshRates(hw));
        Try(() => Resolution(hw));
        Try(() => DisplayCables(hw));
        Try(() => Hdr(hw));
        Try(() => MemoryChannels(hw));
        Try(() => HealthChecks.MemorySpeed(hw));
        Try(() => HealthChecks.MemoryIntegrity());
        Try(() => HealthChecks.ResizableBar(hw));
        Try(() => HealthChecks.IntelApo(hw));
        Try(() => HealthChecks.DriverAge(hw));
        Try(() => HealthChecks.SecureBoot());
        Try(() => HealthChecks.Tpm());
        Try(() => HealthChecks.DiskSpace());
        Try(() => GameInputCheck.Run());
        Try(() => [GameMode()]);
        Try(() => [Hags(hw)]);
        Try(() => [WindowedOptimizations()]);
        Try(() => [BackgroundRecording()]);
        Try(() => [PowerPlan()]);
        Try(() => [MouseAcceleration()]);

        foreach (var r in results)
            if (PreflightIds.Contains(r.Id) || r.Id.StartsWith("disk-", StringComparison.Ordinal))
                r.Preflight = true;

        // Problems first, then tips, then what is already good
        return results.OrderByDescending(r => r.Status).ToList();
    }

    // ---------------------------------------------------------------- Displays

    private static IEnumerable<CheckResult> RefreshRates(HardwareSnapshot hw)
    {
        foreach (var d in hw.Displays)
        {
            int current = (int)Math.Round(d.RefreshHz);
            int best = d.MaxRefreshAtCurrentHz;
            string id = "refresh-" + d.GdiName;

            if (best > 0 && current + 1 < best)
            {
                var display = d;
                yield return new CheckResult
                {
                    Id = id,
                    Title = $"{d.Name} runs at {current} Hz instead of {best} Hz",
                    Detail = $"The screen can show {best} frames per second, but Windows only sends {current}. " +
                             "Games will feel smoother and react faster at the higher rate.",
                    Status = CheckStatus.Warning,
                    ActionLabel = $"Set to {best} Hz",
                    Action = () => Tweaks.SetRefreshRate(display, best, $"{display.Name}: {current} Hz → {best} Hz"),
                };
            }
            else if (best > 0)
            {
                yield return new CheckResult
                {
                    Id = id,
                    Title = $"{d.Name} runs at its full {current} Hz",
                    Detail = "Windows uses the highest refresh rate the screen supports at this resolution.",
                    Status = CheckStatus.Good,
                };
            }
        }
    }

    private static IEnumerable<CheckResult> Resolution(HardwareSnapshot hw)
    {
        foreach (var d in hw.Displays.Where(d => !d.IsRunningNative && d.NativeWidth > 0))
        {
            yield return new CheckResult
            {
                Id = "native-" + d.GdiName,
                Title = $"{d.Name} is below its native resolution",
                Detail = $"Windows uses {d.Width} × {d.Height}, but the screen is {d.NativeWidth} × {d.NativeHeight}. " +
                         "The desktop and games in borderless mode will look soft.",
                Status = CheckStatus.Warning,
                ActionLabel = "Open display settings",
                Action = () => OpenUri("ms-settings:display"),
                ActionChangesSystem = false,
            };
        }
    }

    /// <summary>A screen in the motherboard's port runs games through the CPU's built-in graphics.</summary>
    private static IEnumerable<CheckResult> DisplayCables(HardwareSnapshot hw)
    {
        var dedicated = hw.Gpus.FirstOrDefault(g => !g.IsIntegrated);
        if (dedicated is null || hw.Pc.IsLaptop) yield break; // laptops are wired this way on purpose

        foreach (var d in hw.Displays.Where(d => !d.IsBuiltIn))
        {
            var gpu = hw.GpuFor(d);
            if (gpu is null) continue;

            if (gpu.IsIntegrated)
            {
                yield return new CheckResult
                {
                    Id = "cable-" + d.GdiName,
                    Title = $"{d.Name} is plugged into the motherboard",
                    Detail = d.IsPrimary
                        ? $"This is your main screen, but it is connected to {gpu.Name} instead of your {dedicated.Name}. " +
                          "Games shown here lose FPS and add input lag. Move the cable to a port on the graphics card (the lower ports on the back of the PC)."
                        : $"It is connected to {gpu.Name}. That is fine for a second screen with chat or videos, " +
                          $"but don't play games on it. If you want, move the cable to a free port on your {dedicated.Name}.",
                    Status = d.IsPrimary ? CheckStatus.Warning : CheckStatus.Info,
                };
            }
            else
            {
                yield return new CheckResult
                {
                    Id = "cable-" + d.GdiName,
                    Title = $"{d.Name} is plugged into the graphics card",
                    Detail = $"Connected to {gpu.Name}.",
                    Status = CheckStatus.Good,
                };
            }
        }
    }

    private static IEnumerable<CheckResult> Hdr(HardwareSnapshot hw)
    {
        var d = hw.PrimaryDisplay;
        if (d is null || !d.HdrSupported || d.HdrEnabled) yield break;

        yield return new CheckResult
        {
            Id = "hdr",
            Title = $"HDR is off on {d.Name}",
            Detail = "Your main screen supports HDR. Many new games look much better with it. " +
                     "Turn it on in Windows before starting a game that supports HDR, and off again if the desktop looks washed out.",
            Status = CheckStatus.Info,
            ActionLabel = "Open HDR settings",
            Action = () => OpenUri("ms-settings:display-hdr"),
            ActionChangesSystem = false,
        };
    }

    // ---------------------------------------------------------------- Memory

    private static IEnumerable<CheckResult> MemoryChannels(HardwareSnapshot hw)
    {
        var m = hw.Memory;
        if (m.Modules == 1)
        {
            yield return new CheckResult
            {
                Id = "ram-channels",
                Title = "Only one memory stick",
                Detail = "With one stick the memory runs in single channel, which can cost 10–20% FPS in CPU-heavy games like CS2 and Warzone. " +
                         "Two matching sticks run in dual channel.",
                Status = hw.Pc.IsLaptop ? CheckStatus.Info : CheckStatus.Warning,
            };
        }
        else if (m.Modules >= 2)
        {
            yield return new CheckResult
            {
                Id = "ram-channels",
                Title = $"{m.Modules} memory sticks",
                Detail = "Enough sticks for dual channel, which is what games want.",
                Status = CheckStatus.Good,
            };
        }
    }

    // ---------------------------------------------------------------- Windows gaming settings

    private static CheckResult GameMode()
    {
        // Missing value means on (the Windows default)
        bool on = Tweaks.ReadUserDword(GameBarKey, "AutoGameModeEnabled") != 0;
        return on
            ? new CheckResult
            {
                Id = "gamemode",
                Title = "Game Mode is on",
                Detail = "Windows gives games priority and holds back updates while you play.",
                Status = CheckStatus.Good,
            }
            : new CheckResult
            {
                Id = "gamemode",
                Title = "Game Mode is off",
                Detail = "Game Mode gives the game priority over background apps and stops Windows Update from installing drivers mid-game.",
                Status = CheckStatus.Warning,
                ActionLabel = "Turn on",
                Action = () => Tweaks.SetUserDword(GameBarKey, "AutoGameModeEnabled", 1, "Game Mode turned on"),
            };
    }

    /// <summary>Hardware-accelerated GPU scheduling. Needed for DLSS Frame Generation on RTX 40/50.</summary>
    private static CheckResult Hags(HardwareSnapshot hw)
    {
        var state = GpuScheduling.Query();
        var gpu = hw.PrimaryGpu;
        bool needsIt = gpu is { Vendor: GpuVendor.Nvidia } &&
                       System.Text.RegularExpressions.Regex.IsMatch(gpu.Name, @"RTX\s*[45]\d{3}");

        // Fall back to the registry only if the graphics kernel could not be asked
        if (state == HagsState.Unknown)
            state = Tweaks.ReadMachineDword(GraphicsDriversKey, "HwSchMode") == 2 ? HagsState.On : HagsState.Unknown;

        switch (state)
        {
            case HagsState.On:
                return new CheckResult
                {
                    Id = "hags",
                    Title = "Hardware-accelerated GPU scheduling is on",
                    Detail = needsIt ? "Required for DLSS Frame Generation on your card." : "Lets the graphics card manage its own memory.",
                    Status = CheckStatus.Good,
                };

            case HagsState.NotSupported:
                return new CheckResult
                {
                    Id = "hags",
                    Title = "GPU scheduling is not supported",
                    Detail = "Your graphics driver does not offer hardware-accelerated GPU scheduling. Updating the driver may add it.",
                    Status = CheckStatus.Info,
                };

            case HagsState.Off when ChangeJournal.Active().Any(c => c.Kind == "machine-registry" && c.Data.GetValueOrDefault("Name") == "HwSchMode"):
                return new CheckResult
                {
                    Id = "hags",
                    Title = "Restart to finish turning on GPU scheduling",
                    Detail = "ZenTune turned it on. Windows starts using it after the next restart.",
                    Status = CheckStatus.Info,
                };

            case HagsState.Off:
                return new CheckResult
                {
                    Id = "hags",
                    Title = "Hardware-accelerated GPU scheduling is off",
                    Detail = (needsIt
                        ? $"DLSS Frame Generation does not work without it on your {gpu!.Name}. "
                        : "It can lower input lag a little in some games. ") +
                        "Windows asks for admin rights, and the change takes effect after a restart.",
                    Status = needsIt ? CheckStatus.Warning : CheckStatus.Info,
                    ActionLabel = "Turn on",
                    Action = () => Tweaks.SetMachineDwordElevated(GraphicsDriversKey, "HwSchMode", 2,
                        "GPU scheduling turned on (restart needed)"),
                };

            default:
                return new CheckResult
                {
                    Id = "hags",
                    Title = "GPU scheduling could not be checked",
                    Detail = "Windows did not report whether hardware-accelerated GPU scheduling is on.",
                    Status = CheckStatus.Info,
                };
        }
    }

    /// <summary>"Optimizations for windowed games": borderless games get the same low latency as fullscreen.</summary>
    private static CheckResult WindowedOptimizations()
    {
        var settings = Tweaks.ReadUserString(DirectXKey, "DirectXUserGlobalSettings") ?? "";
        bool explicitOn = settings.Contains("SwapEffectUpgradeEnable=1", StringComparison.OrdinalIgnoreCase);

        if (explicitOn)
        {
            return new CheckResult
            {
                Id = "windowed",
                Title = "Optimizations for windowed games are on",
                Detail = "Borderless and windowed games get the same low latency as exclusive fullscreen.",
                Status = CheckStatus.Good,
            };
        }

        bool explicitOff = settings.Contains("SwapEffectUpgradeEnable=0", StringComparison.OrdinalIgnoreCase);

        // Windows 11 24H2 (build 26100) and newer turn it on by default when the switch was never touched
        if (!explicitOff && Environment.OSVersion.Version.Build >= 26100)
        {
            return new CheckResult
            {
                Id = "windowed",
                Title = "Optimizations for windowed games are on (Windows default)",
                Detail = "Your Windows version turns this on by default, so borderless games get the same low latency as fullscreen.",
                Status = CheckStatus.Good,
            };
        }

        return new CheckResult
        {
            Id = "windowed",
            Title = explicitOff ? "Optimizations for windowed games are off" : "Optimizations for windowed games use the Windows default",
            Detail = "Most games today run in borderless mode. With this on, they get lower input lag and G-Sync/FreeSync support like fullscreen games." +
                     (explicitOff ? "" : " The switch has never been touched, so Windows decides. On most PCs that means off. Turning it on sets it for sure."),
            Status = explicitOff ? CheckStatus.Warning : CheckStatus.Info,
            ActionLabel = "Turn on",
            Action = () => Tweaks.SetUserString(DirectXKey, "DirectXUserGlobalSettings",
                SetFlag(settings, "SwapEffectUpgradeEnable", "1"), "Optimizations for windowed games turned on"),
        };
    }

    /// <summary>Xbox Game Bar "Record what happened": records the last minutes all the time.</summary>
    private static CheckResult BackgroundRecording()
    {
        bool on = Tweaks.ReadUserDword(GameDvrKey, "HistoricalCaptureEnabled") == 1;
        return on
            ? new CheckResult
            {
                Id = "gamedvr",
                Title = "Background recording is on",
                Detail = "Xbox Game Bar keeps recording the last minutes of every game. That costs some FPS all the time. " +
                         "NVIDIA's own recording (ShadowPlay) is lighter if you want clips.",
                Status = CheckStatus.Warning,
                ActionLabel = "Turn off",
                Action = () => Tweaks.SetUserDword(GameDvrKey, "HistoricalCaptureEnabled", 0, "Background recording turned off"),
            }
            : new CheckResult
            {
                Id = "gamedvr",
                Title = "Background recording is off",
                Detail = "Xbox Game Bar is not recording in the background.",
                Status = CheckStatus.Good,
            };
    }

    private static CheckResult PowerPlan()
    {
        var plan = Tweaks.ActivePowerPlan();
        if (plan == Tweaks.PowerSaver)
        {
            return new CheckResult
            {
                Id = "power",
                Title = "Power saver is on",
                Detail = "The processor and graphics card are held back to save power. Games will run noticeably slower.",
                Status = CheckStatus.Warning,
                ActionLabel = "Switch to Balanced",
                Action = () => Tweaks.SetPowerPlan(Tweaks.Balanced, "Power plan: Power saver → Balanced"),
            };
        }

        return new CheckResult
        {
            Id = "power",
            Title = "Power plan is fine for games",
            Detail = plan == Tweaks.HighPerformance
                ? "High performance is active."
                : "Modern processors boost fully on Balanced, so there is no need for a special plan.",
            Status = CheckStatus.Good,
        };
    }

    private static CheckResult MouseAcceleration()
    {
        bool on = Tweaks.MouseAccelerationOn();
        return on
            ? new CheckResult
            {
                Id = "mouse",
                Title = "Mouse acceleration is on",
                Detail = "\"Enhance pointer precision\" makes the cursor move further when you move the mouse fast. " +
                         "CS2 and Warzone ignore it in-game, but turning it off keeps your aim the same in menus and older games.",
                Status = CheckStatus.Info,
                ActionLabel = "Turn off",
                Action = () => Tweaks.SetMouseAcceleration(false, "Mouse acceleration turned off"),
            }
            : new CheckResult
            {
                Id = "mouse",
                Title = "Mouse acceleration is off",
                Detail = "Your mouse moves the same distance no matter how fast you move it.",
                Status = CheckStatus.Good,
            };
    }

    // ---------------------------------------------------------------- Helpers

    /// <summary>Sets "Name=Value;" inside a "A=1;B=0;" style string and keeps the other entries.</summary>
    internal static string SetFlag(string settings, string name, string value)
    {
        var parts = settings.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => !p.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
            .ToList();
        parts.Add($"{name}={value}");
        return string.Join(";", parts) + ";";
    }

    public static void OpenUri(string uri) =>
        Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
}
