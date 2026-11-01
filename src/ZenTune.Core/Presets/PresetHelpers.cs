using System.Text;

namespace ZenTune.Core.Presets;

/// <summary>Small helpers shared by the game-specific tuners.</summary>
internal static class PresetMath
{
    /// <summary>
    /// Which column of a Competitive / Balanced / Quality / Ultra / Handheld table to use.
    /// Weaker cards, and any card below the top tier at 4K, step down one or two columns.
    /// </summary>
    public static int Column(PresetKind kind, HardwareProfile hw)
    {
        if (kind is PresetKind.Competitive or PresetKind.Handheld) return (int)kind;
        int col = (int)kind; // Balanced 1, Quality 2, Ultra 3
        int penalty = hw.Tier switch { GpuTier.Low => 2, GpuTier.Mid => 1, _ => 0 };
        if (hw.Is4K && hw.Tier != GpuTier.Enthusiast) penalty++;
        return Math.Max(0, col - penalty);
    }

    /// <summary>FPS limit for a preset: the refresh rate for Competitive, just under it otherwise.</summary>
    public static int FpsCap(PresetKind kind, HardwareProfile hw) => kind switch
    {
        PresetKind.Competitive => hw.RefreshHz,
        PresetKind.Handheld => PresetInfo.HandheldCap(hw),
        _ => Math.Max(30, hw.RefreshHz - 3),
    };

    public static string FpsWhy(PresetKind kind, HardwareProfile hw) => kind switch
    {
        PresetKind.Competitive => $"Your screen shows {hw.RefreshHz} FPS at most, so more only adds heat. Keeps input lag low.",
        PresetKind.Handheld => "A steady low FPS feels smoother and saves a lot of battery.",
        _ => $"Just under your {hw.RefreshHz} Hz screen, so G-Sync/FreeSync stays active and frame times stay even.",
    };

    /// <summary>The biggest allowed value that is not above the target (or the smallest one when all are above it).</summary>
    public static int LargestAtMost(int[] allowed, int target)
    {
        int best = allowed.Min();
        foreach (var a in allowed)
            if (a <= target && a > best) best = a;
        return best;
    }

    /// <summary>Writes a settings file, keeping its read-only flag the way it was.</summary>
    public static void SaveText(string path, string text, bool bom)
    {
        var info = new FileInfo(path);
        bool readOnly = info.IsReadOnly;
        if (readOnly) info.IsReadOnly = false;
        File.WriteAllText(path, text, new UTF8Encoding(bom));
        if (readOnly) new FileInfo(path).IsReadOnly = true;
    }

    public static bool HasBom(string path)
    {
        using var s = File.OpenRead(path);
        var b = new byte[3];
        int n = s.Read(b, 0, 3);
        return n == 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF;
    }
}
