using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ZenTune.Core.Changes;
using ZenTune.Core.Hardware;

namespace ZenTune.Core.Handheld;

public sealed record PowerStatus(bool OnBattery, bool HasBattery, int Percent, TimeSpan? TimeLeft);

public enum PowerMode { Unknown, BestEfficiency, Balanced, BestPerformance }

/// <summary>Battery, power mode and graphics memory: the things that matter most on handheld PCs.</summary>
[SupportedOSPlatform("windows")]
public static class HandheldInfo
{
    // ---------------------------------------------------------------- Battery

    public static PowerStatus Power()
    {
        if (!GetSystemPowerStatus(out var s)) return new PowerStatus(false, false, 100, null);

        bool hasBattery = s.BatteryFlag != 128 && s.BatteryFlag != 255; // 128 = no battery, 255 = unknown
        bool onBattery = hasBattery && s.ACLineStatus == 0;
        int percent = s.BatteryLifePercent == 255 ? 100 : s.BatteryLifePercent;
        TimeSpan? left = onBattery && s.BatteryLifeTime != uint.MaxValue ? TimeSpan.FromSeconds(s.BatteryLifeTime) : null;
        return new PowerStatus(onBattery, hasBattery, percent, left);
    }

    // ---------------------------------------------------------------- Windows 11 power mode

    // The "Power mode" slider in Settings → System → Power. It sits on top of the power plan.
    private static readonly Guid BestEfficiencyGuid = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    private static readonly Guid BestPerformanceGuid = new("ded574b5-45a0-4f42-8737-46345c09c238");

    public static PowerMode CurrentPowerMode()
    {
        try
        {
            if (PowerGetEffectiveOverlayScheme(out var g) != 0) return PowerMode.Unknown;
            return g == BestEfficiencyGuid ? PowerMode.BestEfficiency
                : g == BestPerformanceGuid ? PowerMode.BestPerformance
                : PowerMode.Balanced;
        }
        catch
        {
            return PowerMode.Unknown; // Windows 10 or a build without power modes
        }
    }

    public static void SetPowerMode(PowerMode mode)
    {
        var old = CurrentPowerMode();
        Apply(mode);
        ChangeJournal.Add(new ChangeRecord
        {
            Title = $"Power mode: {Name(old)} → {Name(mode)}",
            Kind = "powermode",
            Data = new() { ["Old"] = old.ToString() },
        });
    }

    internal static void Undo(ChangeRecord change)
    {
        if (Enum.TryParse<PowerMode>(change.Data.GetValueOrDefault("Old"), out var old) && old != PowerMode.Unknown)
            Apply(old);
    }

    private static void Apply(PowerMode mode)
    {
        var g = mode switch
        {
            PowerMode.BestEfficiency => BestEfficiencyGuid,
            PowerMode.BestPerformance => BestPerformanceGuid,
            _ => Guid.Empty, // Balanced
        };
        uint err = PowerSetActiveOverlayScheme(g);
        if (err != 0) throw new InvalidOperationException($"Windows refused the power mode change (code {err}).");
    }

    public static string Name(PowerMode m) => m switch
    {
        PowerMode.BestEfficiency => "Best power efficiency",
        PowerMode.BestPerformance => "Best performance",
        PowerMode.Balanced => "Balanced",
        _ => "Unknown",
    };

    // ---------------------------------------------------------------- Graphics memory

    /// <summary>
    /// Handhelds share system memory with the graphics chip. The part reserved for graphics (the "UMA frame buffer")
    /// is set in Armoury Crate, Legion Space or the BIOS. Too little makes textures blurry and games stutter.
    /// </summary>
    public static (double ReservedGb, double TotalRamGb, string Advice, bool TooLow) GraphicsMemory(HardwareSnapshot hw)
    {
        var gpu = hw.PrimaryGpu;
        double reserved = gpu is null ? 0 : gpu.VramBytes / 1024d / 1024d / 1024d;
        double total = hw.Memory.TotalBytes / 1024d / 1024d / 1024d;

        // Rough targets used by the handheld community: ~1/3 of memory, at most 8 GB
        double target = total >= 24 ? 8 : total >= 16 ? 6 : 4;
        bool tooLow = gpu is { IsIntegrated: true } && reserved > 0 && reserved < target - 0.5;

        string where = Where(hw);
        string advice = tooLow
            ? $"Only {reserved:0} GB of your {total:0} GB memory is reserved for graphics. New games want more: set it to {target:0} GB in {where}."
            : $"{reserved:0} GB of your {total:0} GB memory is reserved for graphics. That is a good amount for games.";
        return (Math.Round(reserved, 1), Math.Round(total), advice, tooLow);
    }

    private static string Where(HardwareSnapshot hw)
    {
        var model = $"{hw.Pc.Manufacturer} {hw.Pc.Model}";
        if (model.Contains("Ally", StringComparison.OrdinalIgnoreCase) || model.Contains("RC7", StringComparison.OrdinalIgnoreCase))
            return "Armoury Crate → Settings → Performance → GPU Settings (\"Memory assigned to GPU\")";
        if (model.Contains("Legion", StringComparison.OrdinalIgnoreCase))
            return "Legion Space → Settings, or the BIOS (\"UMA Frame Buffer Size\")";
        if (model.Contains("Claw", StringComparison.OrdinalIgnoreCase))
            return "MSI Center M, or the BIOS";
        if (model.Contains("Jupiter", StringComparison.OrdinalIgnoreCase) || model.Contains("Galileo", StringComparison.OrdinalIgnoreCase))
            return "the Steam Deck BIOS (hold Volume Up + Power, Setup Utility → UMA Frame Buffer Size)";
        return "your PC maker's control app or the BIOS (\"UMA Frame Buffer\")";
    }

    // ---------------------------------------------------------------- ROG Xbox Ally / battery health

    /// <summary>ROG Xbox Ally and ROG Xbox Ally X (model names RC73YA and RC73XA).</summary>
    public static bool IsXboxAlly(HardwareSnapshot hw)
    {
        var model = $"{hw.Pc.Manufacturer} {hw.Pc.Model}";
        return model.Contains("Xbox Ally", StringComparison.OrdinalIgnoreCase) || model.Contains("RC73", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Battery capacity when new versus when full now, in mWh, from Windows' battery data. Null when Windows does not
    /// share it (some PCs only report it to administrators).
    /// </summary>
    public static (int DesignMWh, int FullMWh)? BatteryHealth()
    {
        try
        {
            int design = 0, full = 0;

            using (var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT DesignedCapacity FROM BatteryStaticData"))
            {
                foreach (ManagementObject mo in searcher.Get())
                {
                    using (mo)
                    {
                        design = Convert.ToInt32(mo["DesignedCapacity"]);
                        break;
                    }
                }
            }

            using (var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity"))
            {
                foreach (ManagementObject mo in searcher.Get())
                {
                    using (mo)
                    {
                        full = Convert.ToInt32(mo["FullChargedCapacity"]);
                        break;
                    }
                }
            }

            return design > 0 && full > 0 ? (design, full) : null;
        }
        catch
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- Win32

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    [DllImport("powrprof.dll", EntryPoint = "PowerGetEffectiveOverlayScheme")]
    private static extern uint PowerGetEffectiveOverlayScheme(out Guid overlay);

    [DllImport("powrprof.dll", EntryPoint = "PowerSetActiveOverlayScheme")]
    private static extern uint PowerSetActiveOverlayScheme(Guid overlay);
}
