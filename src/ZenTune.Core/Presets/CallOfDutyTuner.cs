using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ZenTune.Core.Games;
using ZenTune.Core.Hardware;

namespace ZenTune.Core.Presets;

/// <summary>
/// Presets for Call of Duty (Modern Warfare 4, Black Ops 7 / Warzone). Its settings files list the allowed values for every line
/// ("// one of Off, Low, High" or "// 0 to 3"), so ZenTune only ever writes values the game itself offers.
/// </summary>
public sealed partial class CallOfDutyTuner : IGameTuner
{
    // Key@hash;hash;hash = value // comment
    [GeneratedRegex(@"^(?<head>(?<key>[A-Za-z0-9_]+)@[^=\r\n]*=[ \t]*)(?<value>[^/\r\n]*?)(?<tail>[ \t]*(//[ \t]*(?<comment>[^\r\n]*))?)(?=\r?$)", RegexOptions.Multiline)]
    private static partial Regex LineRule();

    private static IEnumerable<string> Files(DetectedGame game) =>
        game.ConfigFiles
            .Where(f => Regex.IsMatch(Path.GetFileName(f), @"^s\..*\.txt\d?$", RegexOptions.IgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc);

    public bool Supports(DetectedGame game) =>
        (game.Id is "cod" or "mw4") && Files(game).Any(f => File.ReadAllText(f).Contains("GraphicsQuality@", StringComparison.Ordinal));

    // Columns: Competitive, Balanced, Quality, Ultra, Handheld
    private static readonly (string Key, string Label, string[] Values, string Why)[] Table =
    [
        ("ShadowQuality", "Shadows", ["Low", "High", "Very_High", "Very_High", "Very_Low"], "Shadows are one of the most expensive settings."),
        ("ScreenSpaceShadowQuality", "Screen-space shadows", ["Off", "Low", "High", "High", "Off"], "Small contact shadows that cost FPS but rarely help you play."),
        ("ShadowFilteringQuality", "Shadow filtering", ["Low", "Medium", "High", "High", "Low"], "Softer shadow edges."),
        ("ShadingQuality", "Shading", ["Low", "High", "High", "High", "Low"], "Lighting detail on surfaces."),
        ("SSRQuality", "Screen-space reflections", ["Off", "Low", "High", "High", "Off"], "Reflections in water and glass. Expensive and can distract."),
        ("AmbientLightingQuality", "Ambient lighting", ["Low", "High", "Ultra", "Ultra", "Low"], "Soft shadows in corners. Lower makes dark areas easier to see into."),
        ("VolumetricQuality", "Volumetric lighting", ["QUALITY_LOW", "QUALITY_MEDIUM", "QUALITY_HIGH", "QUALITY_VERYHIGH", "QUALITY_LOW"], "Light shafts and fog. Low means less haze between you and enemies."),
        ("ParticleQuality", "Particles", ["low", "high", "high", "high", "very low"], "Smoke, sparks and explosions."),
        ("DeferredPhysics", "Physics effects", ["Low Quality", "Medium Quality", "High Quality", "High Quality", "Low Quality"], "Debris and cloth simulation, which uses the processor."),
        ("WaterCausticsMode", "Water caustics", ["Off", "Low Quality", "High Quality", "High Quality", "Off"], "Light patterns under water. Pure eye candy."),
        ("WeatherGridVolumesQuality", "Weather effects", ["Off", "Medium", "High", "Ultra", "Off"], "Rain and dust volumes."),
        ("TerrainQuality", "Terrain", ["Medium", "High", "Very High", "Very High", "Low"], "Ground detail."),
        ("Tessellation", "Tessellation", ["0_Off", "1_Near", "1_Near", "2_All", "0_Off"], "Extra geometry on nearby surfaces."),
        ("ModelQuality", "Model quality", ["High Quality", "High Quality", "High Quality", "High Quality", "Medium Quality"], "Kept high so players don't pop in at a distance."),
        ("DepthOfFieldQuality", "Depth of field quality", ["Low", "High", "High", "High", "Low"], "Blur behind your sights."),
        ("WorldStreamingQuality", "World streaming", ["High", "High", "High", "High", "Low"], "How much of the map is kept ready. Lower helps on slow disks and small memory."),
    ];

    public PresetPlan Plan(DetectedGame game, PresetKind kind, HardwareProfile hw)
    {
        var files = Files(game).ToList();
        if (files.Count == 0) throw new InvalidOperationException("Start the game once so it creates its settings file.");
        var file = files[0];

        // Each Call of Duty version has its own file with slightly different setting names; read them all (newest first)
        var lines = new Dictionary<string, Line>(StringComparer.Ordinal);
        foreach (var f in files)
            foreach (var (key, line) in Parse(File.ReadAllText(f)))
                lines.TryAdd(key, line);
        var plan = new PresetPlan { Game = game, Kind = kind, Summary = PresetInfo.Description(kind) };

        void Add(string key, string label, string value, string why)
        {
            if (!lines.TryGetValue(key, out var line)) return;     // not in this game version
            var allowed = Allowed(line.Comment, value, line.Value);
            if (allowed is null) return;                            // value not offered by the game
            plan.Changes.Add(new PlannedChange(file, key, label, line.Value, allowed, why));
        }

        int column = Column(kind, hw);
        foreach (var (key, label, values, why) in Table)
            Add(key, label, values[column], why);

        // Textures cost video memory, not FPS (0 = highest in Call of Duty)
        int textures = kind == PresetKind.Handheld ? 3 : hw.VramGb >= 12 ? 0 : hw.VramGb >= 8 ? 1 : hw.VramGb >= 6 ? 2 : 3;
        Add("TextureQuality", "Texture resolution", textures.ToString(), $"Your card has {hw.VramGb:0} GB of video memory, so textures can be this sharp without costing FPS.");
        Add("TextureFilter", "Texture filtering", kind == PresetKind.Handheld ? "aniso 4x" : "aniso 16x", "Keeps the ground sharp at an angle, for almost no cost.");

        // Picture clarity
        Add("MotionBlur", "Motion blur", "Off", "Motion blur smears the picture when you turn.");
        Add("WeaponMotionBlur", "Weapon motion blur", "false", "Same, for your weapon.");
        if (kind is PresetKind.Competitive or PresetKind.Handheld)
        {
            // Different Call of Duty versions name this differently; set every one the file has
            const string dofWhy = "No blur around your sights, so the whole screen stays sharp.";
            Add("DepthOfField", "Depth of field", "Off", dofWhy);
            Add("DofEnable", "Depth of field", "Off", dofWhy);
            Add("DofWeaponDisable", "Weapon depth of field off", "true", dofWhy);
            Add("DofWorldDisable", "World depth of field off", "true", dofWhy);
        }

        // Latency
        Add("VSync", "V-Sync", "disabled", "V-Sync adds input lag. G-Sync/FreeSync and the FPS limit prevent tearing instead.");
        switch (hw.Vendor)
        {
            case GpuVendor.Nvidia:
                Add("NvidiaReflex", "NVIDIA Reflex", kind == PresetKind.Competitive ? "Enabled + boost" : "Enabled",
                    "Reflex removes the queue of waiting frames, so your shots reach the screen sooner.");
                break;
            case GpuVendor.Amd:
                Add("AmdAntilag2", "AMD Anti-Lag 2", "true", "Lowers input lag on AMD cards.");
                break;
            case GpuVendor.Intel:
                Add("IntelXeLL", "Intel XeLL", "true", "Lowers input lag on Intel cards.");
                break;
        }

        // Upscaling
        string quality = kind switch
        {
            PresetKind.Competitive => hw.Is4K ? "Balanced" : "Maximum Quality",
            PresetKind.Ultra => hw.Tier == GpuTier.Enthusiast ? "Native Resolution" : "Maximum Quality",
            PresetKind.Handheld => "Balanced",
            _ => hw.Is4K && hw.Tier != GpuTier.Enthusiast ? "Balanced" : "Maximum Quality",
        };
        string upscaleWhy = quality == "Native Resolution"
            ? "Your card is fast enough for full resolution, with DLSS only cleaning up the edges (DLAA)."
            : "Renders at a lower resolution and scales up, for a big FPS boost with little visible loss.";

        var upscaler = kind == PresetKind.Handheld && hw.Upscaler == Upscaler.Dlss ? Upscaler.Dlss : hw.Upscaler;
        switch (upscaler)
        {
            case Upscaler.Dlss:
                Add("AATechniquePreferredMP", "Upscaler", "DLSS", "DLSS gives the best picture per FPS on RTX cards.");
                Add("DLSSPerfModeMP", "DLSS mode", quality, upscaleWhy);
                break;
            case Upscaler.Fsr:
                Add("AATechniquePreferredMP", "Upscaler", "FSR AA", "FSR works on every graphics card.");
                Add("AMDSuperResolution2Quality", "FSR mode", quality, upscaleWhy);
                break;
            case Upscaler.Xess:
                Add("AATechniquePreferredMP", "Upscaler", "XeSS", "XeSS is made for Intel Arc cards.");
                Add("XeSSQuality", "XeSS mode", quality, upscaleWhy);
                break;
        }

        bool frameGen = kind == PresetKind.Ultra && hw.FrameGeneration && hw.Upscaler == Upscaler.Dlss;
        Add("DLSSFrameGeneration", "DLSS Frame Generation", frameGen ? "true" : "false",
            frameGen ? "Doubles the FPS you see. Adds a little input lag, so it is only used in Ultra."
                     : "Frame generation adds input lag, so it is only used in the Ultra preset.");

        // Shader pre-loading in the menus prevents stutter in the first matches after an update
        Add("EnableOfflineGameUpdater", "Shader pre-loading", "true",
            "Builds shaders in the menus, so you don't get stutter in your first matches after an update or driver change.");

        // FPS limit
        if (kind == PresetKind.Competitive)
        {
            Add("CapFps", "FPS limit", "false", "No limit: more FPS means less input lag. Reflex keeps it smooth.");
        }
        else
        {
            int cap = kind == PresetKind.Handheld ? PresetInfo.HandheldCap(hw) : Math.Max(30, hw.RefreshHz - 3);
            Add("CapFps", "FPS limit", "true", "A limit just under your screen's refresh rate keeps G-Sync/FreeSync active and frame times even.");
            Add("MaxFpsInGame", "FPS limit (in game)", cap.ToString(), $"{cap} FPS for your {hw.RefreshHz} Hz screen.");
        }

        return plan;
    }

    /// <summary>Which column of the table to use. Weaker cards and 4K step down one level.</summary>
    private static int Column(PresetKind kind, HardwareProfile hw)
    {
        if (kind is PresetKind.Competitive or PresetKind.Handheld) return (int)kind;
        int col = (int)kind; // Balanced 1, Quality 2, Ultra 3
        int penalty = hw.Tier switch { GpuTier.Low => 2, GpuTier.Mid => 1, _ => 0 };
        if (hw.Is4K && hw.Tier != GpuTier.Enthusiast) penalty++;
        return Math.Max(0, col - penalty);
    }

    // ---------------------------------------------------------------- File format

    private sealed record Line(string Value, string Comment);

    private static Dictionary<string, Line> Parse(string text)
    {
        var d = new Dictionary<string, Line>(StringComparer.Ordinal);
        foreach (Match m in LineRule().Matches(text))
        {
            var key = m.Groups["key"].Value;
            d.TryAdd(key, new Line(m.Groups["value"].Value.Trim(), m.Groups["comment"].Value.Trim()));
        }
        return d;
    }

    /// <summary>
    /// The value as the game spells it, if the line allows it. Some settings exist twice: once as a
    /// true/false line and once as an "Off, On" list, so On/Off are translated for the true/false line.
    /// </summary>
    private static string? Allowed(string comment, string value, string current)
    {
        if (comment.StartsWith("one of ", StringComparison.Ordinal))
        {
            var options = comment["one of ".Length..].Split(',').Select(o => o.Trim()).ToList();
            var match = options.FirstOrDefault(o => o.Equals(value, StringComparison.OrdinalIgnoreCase));
            // A true/false value planned from the boolean twin of this setting
            match ??= value switch
            {
                "true" => options.FirstOrDefault(o => o.Equals("On", StringComparison.OrdinalIgnoreCase)),
                "false" => options.FirstOrDefault(o => o.Equals("Off", StringComparison.OrdinalIgnoreCase)),
                _ => null,
            };
            return match;
        }

        var range = Regex.Match(comment, @"^(-?[\d.]+) to (-?[\d.]+)$");
        if (range.Success)
        {
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return null;
            double min = double.Parse(range.Groups[1].Value, CultureInfo.InvariantCulture);
            double max = double.Parse(range.Groups[2].Value, CultureInfo.InvariantCulture);
            return v >= min && v <= max ? value : null;
        }

        // true/false lines have no list; only booleans may go there
        if (current is "true" or "false")
        {
            return value.ToLowerInvariant() switch
            {
                "true" or "on" or "1" => "true",
                "false" or "off" or "0" => "false",
                _ => null,
            };
        }
        if (value is "true" or "false") return null;
        return comment.Length == 0 ? value : null;
    }

    public void Write(PresetPlan plan)
    {
        var changes = plan.Effective.ToDictionary(c => c.Setting, c => c.NewValue, StringComparer.Ordinal);
        if (changes.Count == 0) return;

        // Call of Duty keeps several copies (txt0 / txt1 and one per game version); update them all
        foreach (var file in Files(plan.Game))
        {
            var text = File.ReadAllText(file);
            var updated = LineRule().Replace(text, m =>
            {
                var key = m.Groups["key"].Value;
                if (!changes.TryGetValue(key, out var value)) return m.Value;
                var allowed = Allowed(m.Groups["comment"].Value.Trim(), value, m.Groups["value"].Value.Trim());
                return allowed is null ? m.Value : m.Groups["head"].Value + allowed + m.Groups["tail"].Value;
            });

            if (updated == text) continue;

            var info = new FileInfo(file);
            bool readOnly = info.IsReadOnly;
            if (readOnly) info.IsReadOnly = false;
            File.WriteAllText(file, updated, new UTF8Encoding(false));
            if (readOnly) new FileInfo(file).IsReadOnly = true;
        }
    }
}
