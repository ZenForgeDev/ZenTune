using System.Text.RegularExpressions;
using ZenTune.Core.Games;
using ZenTune.Core.Hardware;

namespace ZenTune.Core.Presets;

/// <summary>
/// Presets for Counter-Strike 2. Video settings live in cs2_video.txt (Steam userdata, cfg folder) as lines like
/// "setting.mat_vsync"  "0". A setting is only changed when the file already has it AND its current value is one of
/// the values ZenTune knows for that setting, so an unfamiliar value is never overwritten.
/// The big quality levels (shadows, textures, ambient occlusion, particles) are left alone on purpose: what their
/// numbers mean is not documented anywhere ZenTune could check, so they stay in the game's own video menu.
/// Close the game first: CS2 writes the file again when it quits.
/// </summary>
public sealed class Cs2Tuner : IGameTuner
{
    private static string? Cfg(DetectedGame game) =>
        game.ConfigFiles.FirstOrDefault(f => Path.GetFileName(f).Equals("cs2_video.txt", StringComparison.OrdinalIgnoreCase));

    public bool Supports(DetectedGame game) =>
        game.Id == "cs2" && Cfg(game) is { } f && File.ReadAllText(f).Contains("\"setting.", StringComparison.Ordinal);

    private static readonly string[] OffOn = ["0", "1"];
    private static readonly string[] Reflex = ["0", "1", "2"];          // off, on, on + boost
    private static readonly string[] Msaa = ["0", "2", "4", "8"];       // samples
    private static readonly string[] Filtering = ["0", "1", "2", "3", "4", "5"]; // bilinear, trilinear, anisotropic 2x..16x

    // Columns: Competitive, Balanced, Quality, Ultra, Handheld
    private static readonly string[] MsaaValues = ["2", "4", "4", "8", "0"];
    private static readonly string[] FilteringValues = ["5", "5", "5", "5", "3"];
    private static readonly string[] ShaderValues = ["0", "1", "1", "1", "0"];

    public PresetPlan Plan(DetectedGame game, PresetKind kind, HardwareProfile hw)
    {
        var file = Cfg(game) ?? throw new InvalidOperationException("Start the game once so it creates its settings file.");
        var text = File.ReadAllText(file);
        var plan = new PresetPlan { Game = game, Kind = kind, Summary = PresetInfo.Description(kind) };
        int c = PresetMath.Column(kind, hw);

        void Add(string key, string label, string value, string why, string[] known)
        {
            if (Read(text, key) is not { } cur) return;                                            // the file does not use this setting
            if (!known.Contains(cur)) return;                                                      // unfamiliar value: leave it alone
            plan.Changes.Add(new PlannedChange(file, key, label, cur, value, why));
        }

        Add("setting.mat_vsync", "V-Sync", "0",
            "V-Sync adds input lag. G-Sync/FreeSync keep the picture tear-free instead.", OffOn);

        if (hw.Vendor == GpuVendor.Nvidia && kind != PresetKind.Handheld)
            Add("setting.r_low_latency", "NVIDIA Reflex", "2",
                "Reflex removes the queue of waiting frames, so your clicks reach the screen sooner.", Reflex);

        string msaa = MsaaValues[c];
        Add("setting.msaa_samples", "Anti-aliasing (MSAA)", msaa,
            msaa == "0" ? "MSAA is the most expensive setting in CS2's video menu, so it is off to save power."
            : msaa == "2" ? "MSAA is the most expensive setting in CS2's video menu. 2x keeps edges clean at a lower cost."
            : msaa == "4" ? "4x MSAA is the usual balance between clean edges and FPS."
            : "8x MSAA gives the cleanest edges and costs a lot of FPS.", Msaa);

        Add("setting.r_texturefilteringquality", "Texture filtering", FilteringValues[c],
            "Keeps floors and walls sharp at an angle, for almost no cost.", Filtering);

        Add("setting.shaderquality", "Shader detail", ShaderValues[c],
            ShaderValues[c] == "0" ? "Lower shader detail gives more FPS." : "High shader detail looks better and costs little on a modern card.", OffOn);

        return plan;
    }

    public void Write(PresetPlan plan)
    {
        foreach (var group in plan.Effective.GroupBy(x => x.File))
        {
            bool bom = PresetMath.HasBom(group.Key);
            var text = File.ReadAllText(group.Key);
            foreach (var change in group)
                text = Set(text, change.Setting, change.NewValue);
            PresetMath.SaveText(group.Key, text, bom);
        }
    }

    // ---------------------------------------------------------------- cs2_video.txt
    // "setting.name"<tabs>"value"

    private static Regex Line(string key) =>
        new($@"^(?<head>[ \t]*""{Regex.Escape(key)}""[ \t]+"")(?<value>[^""\r\n]*)(?<tail>"")", RegexOptions.Multiline);

    private static string? Read(string text, string key)
    {
        var m = Line(key).Match(text);
        return m.Success ? m.Groups["value"].Value.Trim() : null;
    }

    private static string Set(string text, string key, string value)
    {
        var m = Line(key).Match(text);
        if (!m.Success) return text;
        var g = m.Groups["value"];
        return text[..g.Index] + value + text[(g.Index + g.Length)..];
    }
}
