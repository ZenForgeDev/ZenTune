using ZenTune.Core.Games;
using ZenTune.Core.Hardware;

namespace ZenTune.Core.Presets;

public enum PresetKind { Competitive, Balanced, Quality, Ultra, Handheld }

public enum GpuTier { Low, Mid, High, Enthusiast }

public enum Upscaler { None, Dlss, Fsr, Xess }

/// <summary>What the presets need to know about the PC, boiled down from the hardware scan.</summary>
public sealed record HardwareProfile(
    GpuVendor Vendor,
    string GpuName,
    GpuTier Tier,
    double VramGb,
    Upscaler Upscaler,
    bool FrameGeneration,
    int ScreenWidth,
    int ScreenHeight,
    int RefreshHz,
    bool IsHandheld,
    bool OnBattery)
{
    public bool Is4K => ScreenWidth >= 3800;
    public bool Is1440p => ScreenWidth >= 2500 && ScreenWidth < 3800;

    private static readonly string[] HandheldNames =
        ["ROG Ally", "Xbox Ally", "RC71L", "RC72L", "RC73", "Legion Go", "83E1", "83L3", "83N0", "Claw", "Jupiter", "Galileo", "Steam Deck", "AYANEO", "GPD WIN", "ONEXPLAYER", "OneXFly", "Zotac Zone"];

    public static HardwareProfile From(HardwareSnapshot hw)
    {
        var gpu = hw.PrimaryGpu;
        var display = hw.PrimaryDisplay;
        double vram = gpu is null ? 0 : gpu.VramBytes / 1024d / 1024d / 1024d;
        string name = gpu?.Name ?? "";
        var vendor = gpu?.Vendor ?? GpuVendor.Other;

        bool rtx = vendor == GpuVendor.Nvidia && name.Contains("RTX", StringComparison.OrdinalIgnoreCase);
        bool rtx40Plus = rtx && System.Text.RegularExpressions.Regex.IsMatch(name, @"RTX\s*[4-9]\d{3}");
        bool arc = vendor == GpuVendor.Intel && name.Contains("Arc", StringComparison.OrdinalIgnoreCase);

        var upscaler = rtx ? Upscaler.Dlss : arc ? Upscaler.Xess : gpu is null ? Upscaler.None : Upscaler.Fsr;

        string model = $"{hw.Pc.Manufacturer} {hw.Pc.Model}";
        bool handheld = HandheldNames.Any(n => model.Contains(n, StringComparison.OrdinalIgnoreCase));

        // Shared-memory GPUs (handhelds, laptops without a graphics card) count as low end
        var tier = gpu is null || gpu.IsIntegrated ? GpuTier.Low
            : vram >= 15 ? GpuTier.Enthusiast
            : vram >= 10 ? GpuTier.High
            : vram >= 6 ? GpuTier.Mid
            : GpuTier.Low;

        return new HardwareProfile(
            vendor,
            name,
            tier,
            Math.Round(vram, 1),
            upscaler,
            rtx40Plus || vendor == GpuVendor.Amd, // AMD cards get FSR frame generation
            display?.Width ?? 1920,
            display?.Height ?? 1080,
            (int)Math.Round(display?.RefreshHz ?? 60),
            handheld,
            Handheld.HandheldInfo.Power().OnBattery);
    }
}

/// <summary>One setting a preset changes.</summary>
public sealed record PlannedChange(string File, string Setting, string Label, string? OldValue, string NewValue, string Why);

public sealed class PresetPlan
{
    public required DetectedGame Game { get; init; }
    public required PresetKind Kind { get; init; }
    public required string Summary { get; init; }

    /// <summary>
    /// True for games ZenTune must not write to (anti-cheat risk). The plan then only lists what to set in the game's own menu.
    /// </summary>
    public bool GuideOnly { get; init; }
    public List<PlannedChange> Changes { get; } = [];

    /// <summary>Only the settings that actually change.</summary>
    public IEnumerable<PlannedChange> Effective => Changes.Where(c => !string.Equals(c.OldValue, c.NewValue, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Knows how to write presets for a family of games.</summary>
public interface IGameTuner
{
    bool Supports(DetectedGame game);
    PresetPlan Plan(DetectedGame game, PresetKind kind, HardwareProfile hw);

    /// <summary>Writes the plan's changes. The caller has already made a backup.</summary>
    void Write(PresetPlan plan);
}

public static class PresetInfo
{
    public static string Title(PresetKind k) => k switch
    {
        PresetKind.Competitive => "Competitive",
        PresetKind.Balanced => "Balanced",
        PresetKind.Quality => "Quality",
        PresetKind.Ultra => "Ultra",
        _ => "Handheld",
    };

    public static string Description(PresetKind k) => k switch
    {
        PresetKind.Competitive => "Highest FPS and lowest input lag. Fewer shadows and effects so enemies are easier to see; textures stay sharp.",
        PresetKind.Balanced => "Good-looking and smooth. Aims for FPS close to your screen's refresh rate.",
        PresetKind.Quality => "High settings with upscaling doing the heavy lifting.",
        PresetKind.Ultra => "Everything on maximum, with frame generation where the game and your card support it.",
        _ => "For handheld PCs like ROG Ally, Legion Go and Steam Deck: low power, an FPS cap and upscaling.",
    };

    /// <summary>
    /// FPS limit for the Handheld preset. On battery handhelds use 40 FPS (smooth on their 120 Hz screens, much
    /// less power); on the charger 60. On a desktop monitor anything below about 48 FPS falls out of the
    /// G-Sync/FreeSync range and can flicker, so 60 is the lowest ZenTune uses there.
    /// </summary>
    public static int HandheldCap(HardwareProfile hw) =>
        hw.IsHandheld && hw.OnBattery ? (hw.RefreshHz >= 120 ? 40 : 30) : 60;

    /// <summary>The preset ZenTune suggests for this PC.</summary>
    public static PresetKind Recommended(HardwareProfile hw) =>
        hw.IsHandheld ? PresetKind.Handheld
        : hw.Tier == GpuTier.Enthusiast ? PresetKind.Quality
        : PresetKind.Balanced;
}
