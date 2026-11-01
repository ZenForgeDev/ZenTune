using System.Text.RegularExpressions;
using ZenTune.Core.Games;
using ZenTune.Core.Hardware;

namespace ZenTune.Core.Presets;

/// <summary>
/// Presets for Grand Theft Auto V Enhanced. Its settings.xml has no list of allowed values, so only the settings whose
/// scales are documented for GTA V's settings.xml are changed (shadows 0-3, reflections 0-3, textures 0-2, tessellation 0-3,
/// grass 0-3, anisotropic filtering 0/2/4/8/16), plus ray tracing, V-Sync and the FPS limit.
/// Close the game first: GTA V writes settings.xml again when it quits.
/// </summary>
public sealed class GtaVEnhancedTuner : IGameTuner
{
    private static string? Settings(DetectedGame game) =>
        game.ConfigFiles.FirstOrDefault(f => Path.GetFileName(f).Equals("settings.xml", StringComparison.OrdinalIgnoreCase));

    public bool Supports(DetectedGame game) =>
        game.Id == "gtavenhanced" && Settings(game) is { } f && File.ReadAllText(f).Contains("<graphics>", StringComparison.Ordinal);

    // Columns: Competitive, Balanced, Quality, Ultra, Handheld
    private static readonly string[] Shadows = ["1", "2", "3", "3", "1"];
    private static readonly string[] Reflections = ["0", "1", "2", "3", "0"];
    private static readonly string[] Tessellation = ["0", "1", "2", "3", "0"];
    private static readonly string[] Grass = ["0", "1", "2", "3", "0"];
    private static readonly string[] Aniso = ["8", "16", "16", "16", "4"];

    public PresetPlan Plan(DetectedGame game, PresetKind kind, HardwareProfile hw)
    {
        var file = Settings(game) ?? throw new InvalidOperationException("Start the game once so it creates its settings file.");
        var text = File.ReadAllText(file);
        var plan = new PresetPlan { Game = game, Kind = kind, Summary = PresetInfo.Description(kind) };
        int c = PresetMath.Column(kind, hw);

        void Add(string block, string key, string label, string value, string why)
        {
            if (Read(text, block, key) is not { } old) return; // the file does not use this setting
            plan.Changes.Add(new PlannedChange(file, $"{block}/{key}", label, old, value, why));
        }

        Add("graphics", "ShadowQuality", "Shadows", Shadows[c], "Shadows are one of the most expensive settings.");
        Add("graphics", "ReflectionQuality", "Reflections", Reflections[c], "Reflections cost a lot and rarely help you play.");
        Add("graphics", "Tessellation", "Tessellation", Tessellation[c], "Extra geometry on roads, buildings and cars.");
        Add("graphics", "GrassQuality", "Grass", Grass[c], "Dense grass is heavy on the GPU.");

        // Textures cost video memory, not FPS (0 = Normal, 1 = High, 2 = Very High)
        string texture = kind == PresetKind.Handheld ? "1" : hw.VramGb >= 8 ? "2" : hw.VramGb >= 4 ? "1" : "0";
        Add("graphics", "TextureQuality", "Textures", texture, $"Textures cost video memory, not FPS. Your card has {hw.VramGb:0} GB.");
        Add("graphics", "AnisotropicFiltering", "Anisotropic filtering", Aniso[c], "Keeps roads sharp at an angle, for almost no cost.");

        // Ray tracing is the biggest FPS cost in GTA V Enhanced
        bool rt = kind switch
        {
            PresetKind.Quality => hw.Tier == GpuTier.Enthusiast,
            PresetKind.Ultra => hw.Tier is GpuTier.Enthusiast or GpuTier.High,
            _ => false,
        };
        Add("graphics", "Raytracing_Enabled", "Ray tracing", rt ? "true" : "false",
            rt ? "Your card is fast enough for ray tracing in this preset." : "Ray tracing is the biggest FPS cost in GTA V Enhanced, so it is off in this preset.");

        Add("video", "VSync", "V-Sync", "0", "V-Sync adds input lag. G-Sync/FreeSync and the FPS limit keep the picture tear-free instead.");
        Add("video", "FrameLimit", "FPS limit", PresetMath.FpsCap(kind, hw).ToString(), PresetMath.FpsWhy(kind, hw));

        return plan;
    }

    public void Write(PresetPlan plan)
    {
        foreach (var group in plan.Effective.GroupBy(x => x.File))
        {
            bool bom = PresetMath.HasBom(group.Key);
            var text = File.ReadAllText(group.Key);
            foreach (var change in group)
            {
                int slash = change.Setting.IndexOf('/');
                text = Set(text, change.Setting[..slash], change.Setting[(slash + 1)..], change.NewValue);
            }
            PresetMath.SaveText(group.Key, text, bom);
        }
    }

    // ---------------------------------------------------------------- settings.xml
    // Lines look like <ShadowQuality value="3" />. Some names (ReflectionQuality) also appear in the <Presets> block,
    // so every lookup stays inside one block such as <graphics> or <video>.

    private static (int Start, int Length)? Block(string text, string name)
    {
        int start = text.IndexOf($"<{name}>", StringComparison.Ordinal);
        if (start < 0) return null;
        int end = text.IndexOf($"</{name}>", start, StringComparison.Ordinal);
        return end < 0 ? null : (start, end - start);
    }

    private static Regex Value(string key) => new($@"<{Regex.Escape(key)}\s+value=""(?<v>[^""]*)""");

    private static string? Read(string text, string block, string key)
    {
        if (Block(text, block) is not { } b) return null;
        var m = Value(key).Match(text, b.Start, b.Length);
        return m.Success ? m.Groups["v"].Value : null;
    }

    private static string Set(string text, string block, string key, string value)
    {
        if (Block(text, block) is not { } b) return text;
        var m = Value(key).Match(text, b.Start, b.Length);
        if (!m.Success) return text;
        var g = m.Groups["v"];
        return text[..g.Index] + value + text[(g.Index + g.Length)..];
    }
}
