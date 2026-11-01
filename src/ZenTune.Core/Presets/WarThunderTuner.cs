using System.Text.RegularExpressions;
using ZenTune.Core.Games;
using ZenTune.Core.Hardware;

namespace ZenTune.Core.Presets;

/// <summary>
/// Presets for War Thunder. Settings live in config.blk in the game folder, as lines like shadowQuality:t="high"
/// (t = text, i = whole number, b = yes/no). A setting is only changed when the file already has it AND its current
/// value is one of the values ZenTune knows for that setting, so an unfamiliar value is never overwritten.
/// Close the game first: War Thunder writes config.blk again when it quits.
/// </summary>
public sealed class WarThunderTuner : IGameTuner
{
    private static string? Cfg(DetectedGame game) =>
        game.ConfigFiles.FirstOrDefault(f => Path.GetFileName(f).Equals("config.blk", StringComparison.OrdinalIgnoreCase));

    public bool Supports(DetectedGame game) =>
        game.Id == "warthunder" && Cfg(game) is { } f && Regex.IsMatch(File.ReadAllText(f), @"(?m)^[ \t]*graphics[ \t]*\{");

    private static readonly string[] Four = ["low", "medium", "high", "ultrahigh"];
    private static readonly string[] Three = ["low", "medium", "high"];
    private static readonly string[] Aniso = ["1", "2", "4", "8", "16"];
    private static readonly string[] YesNo = ["yes", "no"];

    // Columns: Competitive, Balanced, Quality, Ultra, Handheld
    private static readonly string[] Shadows = ["low", "medium", "high", "ultrahigh", "low"];
    private static readonly string[] Effects = ["low", "medium", "high", "ultrahigh", "low"];
    private static readonly string[] Water = ["low", "medium", "high", "ultrahigh", "low"];
    private static readonly string[] Gi = ["low", "medium", "high", "high", "low"];
    private static readonly string[] AnisoValues = ["8", "16", "16", "16", "4"];

    public PresetPlan Plan(DetectedGame game, PresetKind kind, HardwareProfile hw)
    {
        var file = Cfg(game) ?? throw new InvalidOperationException("Start the game once so it creates its settings file.");
        var text = File.ReadAllText(file);
        var plan = new PresetPlan { Game = game, Kind = kind, Summary = PresetInfo.Description(kind) };
        int c = PresetMath.Column(kind, hw);

        void Add(string block, string key, string label, string value, string why, string[]? known)
        {
            if (Read(text, block, key) is not { } cur) return;                  // the file does not use this setting
            if (known is not null && !known.Contains(cur.Value, StringComparer.OrdinalIgnoreCase)) return; // unfamiliar value: leave it alone
            plan.Changes.Add(new PlannedChange(file, $"{block}/{key}", label, cur.Value, value, why));
        }

        // Without this, the game can replace the values below with one of its built-in presets
        Add("", "graphicsQuality", "Quality preset", "custom", "Keeps the settings below from being replaced by one of the game's built-in presets.", null);

        Add("graphics", "shadowQuality", "Shadows", Shadows[c], "Shadows are one of the most expensive settings.", Four);
        Add("graphics", "fxQuality", "Effects", Effects[c], "Smoke, explosions and tracers. Lower makes it easier to see targets.", Four);
        Add("graphics", "waterQuality", "Water", Water[c], "Water detail costs a lot on naval maps.", Four);
        Add("graphics", "giQuality", "Global illumination", Gi[c], "Indirect lighting. Expensive and subtle.", Three);

        string texture = kind == PresetKind.Handheld ? "medium" : hw.VramGb >= 8 ? "high" : hw.VramGb >= 4 ? "medium" : "low";
        Add("graphics", "texquality", "Textures", texture, $"Textures cost video memory, not FPS. Your card has {hw.VramGb:0} GB.", Three);
        Add("graphics", "anisotropy", "Anisotropic filtering", AnisoValues[c], "Keeps the ground sharp at an angle, for almost no cost.", Aniso);

        Add("video", "vsync", "V-Sync", "no", "V-Sync adds input lag. G-Sync/FreeSync and the FPS limit keep the picture tear-free instead.", YesNo);
        Add("video", "fpsLimit", "FPS limit", PresetMath.FpsCap(kind, hw).ToString(), PresetMath.FpsWhy(kind, hw), null);

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

    // ---------------------------------------------------------------- config.blk
    // A block is "name{ ... }" with no blocks inside it. An empty block name means the top of the file.

    private static (int Start, int Length)? Block(string text, string name)
    {
        if (name.Length == 0) return (0, text.Length);
        var m = Regex.Match(text, $@"(?m)^[ \t]*{Regex.Escape(name)}[ \t]*\{{");
        if (!m.Success) return null;
        int end = text.IndexOf('}', m.Index + m.Length);
        return end < 0 ? null : (m.Index, end - m.Index);
    }

    private static Regex Line(string key) =>
        new($@"^(?<head>[ \t]*{Regex.Escape(key)}:(?<type>[tibr])[ \t]*=[ \t]*)(?<value>[^\r\n]*?)(?<tail>[ \t]*)(?=\r?$)", RegexOptions.Multiline);

    private static (string Type, string Value)? Read(string text, string block, string key)
    {
        if (Block(text, block) is not { } b) return null;
        var m = Line(key).Match(text, b.Start, b.Length);
        return m.Success ? (m.Groups["type"].Value, m.Groups["value"].Value.Trim().Trim('"')) : null;
    }

    private static string Set(string text, string block, string key, string value)
    {
        if (Block(text, block) is not { } b) return text;
        var m = Line(key).Match(text, b.Start, b.Length);
        if (!m.Success) return text;
        var g = m.Groups["value"];
        string written = m.Groups["type"].Value == "t" ? $"\"{value}\"" : value;
        return text[..g.Index] + written + text[(g.Index + g.Length)..];
    }
}
