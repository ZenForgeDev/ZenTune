using System.Globalization;
using System.Text;
using ZenTune.Core.Games;
using ZenTune.Core.Hardware;

namespace ZenTune.Core.Presets;

/// <summary>
/// A small INI editor that keeps the file exactly as it was (comments, order, blank lines)
/// and only changes the values it is asked to.
/// </summary>
internal sealed class IniFile
{
    private readonly List<string> _lines;
    private readonly string _newline;

    public string Path { get; }

    public IniFile(string path)
    {
        Path = path;
        var text = File.ReadAllText(path);
        _newline = text.Contains("\r\n") ? "\r\n" : "\n";
        _lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
    }

    /// <summary>Value of the first "key=value" line anywhere in the file (or in one section).</summary>
    public string? Get(string key, string? section = null)
    {
        var i = Find(key, section);
        return i < 0 ? null : _lines[i][(_lines[i].IndexOf('=') + 1)..].Trim();
    }

    /// <summary>Changes an existing key. Returns false when the key is not in the file.</summary>
    public bool SetExisting(string key, string value, string? section = null)
    {
        bool any = false;
        foreach (var i in FindAll(key, section))
        {
            _lines[i] = _lines[i][..(_lines[i].IndexOf('=') + 1)] + value;
            any = true;
        }
        return any;
    }

    /// <summary>Changes a key in a section, adding the key (and the section) when missing.</summary>
    public void Set(string section, string key, string value)
    {
        if (SetExisting(key, value, section)) return;

        int header = _lines.FindIndex(l => l.Trim().Equals($"[{section}]", StringComparison.OrdinalIgnoreCase));
        if (header < 0)
        {
            if (_lines.Count > 0 && _lines[^1].Trim().Length > 0) _lines.Add("");
            _lines.Add($"[{section}]");
            _lines.Add($"{key}={value}");
            return;
        }

        // Insert after the last non-empty line of that section
        int insert = header + 1;
        for (int i = header + 1; i < _lines.Count && !_lines[i].TrimStart().StartsWith('['); i++)
            if (_lines[i].Trim().Length > 0) insert = i + 1;
        _lines.Insert(insert, $"{key}={value}");
    }

    public void Save()
    {
        var info = new FileInfo(Path);
        bool readOnly = info.IsReadOnly;
        if (readOnly) info.IsReadOnly = false;
        File.WriteAllText(Path, string.Join(_newline, _lines), new UTF8Encoding(false));
        if (readOnly) new FileInfo(Path).IsReadOnly = true;
    }

    private int Find(string key, string? section) => FindAll(key, section).DefaultIfEmpty(-1).First();

    private IEnumerable<int> FindAll(string key, string? section)
    {
        string? current = null;
        for (int i = 0; i < _lines.Count; i++)
        {
            var t = _lines[i].Trim();
            if (t.StartsWith('[') && t.EndsWith(']')) { current = t[1..^1]; continue; }
            if (t.StartsWith(';') || t.StartsWith('#')) continue;
            int eq = t.IndexOf('=');
            if (eq <= 0) continue;
            if (!t[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            if (section is not null && !string.Equals(current, section, StringComparison.OrdinalIgnoreCase)) continue;
            yield return i;
        }
    }
}

/// <summary>
/// Presets for Unreal Engine games. They all read the same "scalability groups" from GameUserSettings.ini
/// (0 = Low, 1 = Medium, 2 = High, 3 = Epic, 4 = Cinematic), so one tuner covers most of them.
/// Game-specific keys (DLSS, Reflex, motion blur) are only changed when the game already has them.
/// </summary>
public sealed class UnrealTuner : IGameTuner
{
    private const string Scalability = "ScalabilityGroups";

    private static readonly HashSet<string> Games =
        ["arcraiders", "ff7rebirth", "tow", "tow2", "hogwarts", "avowed", "marvelrivals", "pubg",
         "thefinals", "dbd", "wukong", "stalker2", "satisfactory", "wardogs", "fortnite", "tinytina"];

    private static string? SettingsFile(DetectedGame game) =>
        game.ConfigFiles.FirstOrDefault(f => System.IO.Path.GetFileName(f).Equals("GameUserSettings.ini", StringComparison.OrdinalIgnoreCase));

    public bool Supports(DetectedGame game) => Games.Contains(game.Id) && SettingsFile(game) is not null;

    // Order matters only for how the changes are listed
    private static readonly (string Key, string Label)[] Groups =
    [
        ("sg.ViewDistanceQuality", "View distance"),
        ("sg.TextureQuality", "Textures"),
        ("sg.ShadowQuality", "Shadows"),
        ("sg.GlobalIlluminationQuality", "Global illumination"),
        ("sg.ReflectionQuality", "Reflections"),
        ("sg.AntiAliasingQuality", "Anti-aliasing"),
        ("sg.PostProcessQuality", "Post-processing"),
        ("sg.EffectsQuality", "Effects"),
        ("sg.FoliageQuality", "Foliage"),
        ("sg.ShadingQuality", "Shading"),
        ("sg.LandscapeQuality", "Landscape"),
    ];

    public PresetPlan Plan(DetectedGame game, PresetKind kind, HardwareProfile hw)
    {
        var file = SettingsFile(game) ?? throw new InvalidOperationException("Start the game once so it creates its settings file.");
        var ini = new IniFile(file);
        var plan = new PresetPlan { Game = game, Kind = kind, Summary = PresetInfo.Description(kind) };

        var levels = Levels(kind, hw);
        foreach (var (key, label) in Groups)
        {
            // Landscape only exists in some games; don't add it where it isn't used
            if (key == "sg.LandscapeQuality" && ini.Get(key, Scalability) is null) continue;
            int level = levels[key];
            plan.Changes.Add(new PlannedChange(file, key, label, ini.Get(key, Scalability), level.ToString(), WhyLevel(key, kind, hw)));
        }

        // Render resolution: 100% everywhere except handhelds, where upscaling should carry the load.
        // Some games (WARDOGS, Fortnite) let their own DLSS/FSR/TSR setting drive this value; leave it alone there,
        // or full resolution would quietly switch the upscaler off and cost a lot of FPS.
        bool gameUpscalerDrivesResolution = ini.Get("UpscalingMethod") is not null || ini.Get("FortAntiAliasingMethod") is not null;
        if (!gameUpscalerDrivesResolution)
            plan.Changes.Add(new PlannedChange(file, "sg.ResolutionQuality", "Render resolution", ini.Get("sg.ResolutionQuality", Scalability),
                kind == PresetKind.Handheld ? "67" : "100",
                kind == PresetKind.Handheld ? "Renders at two thirds of the screen resolution to save power." : "Full resolution; upscaling (if any) is set separately."));

        // Frame rate cap and V-Sync (only keys the game already uses)
        string cap = FrameCap(kind, hw).ToString("0.000000", CultureInfo.InvariantCulture);
        if (ini.Get("FrameRateLimit") is { } oldCap)
            plan.Changes.Add(new PlannedChange(file, "FrameRateLimit", "FPS limit", oldCap, cap, CapWhy(kind, hw)));
        if (ini.Get("bUseVSync") is { } oldVsync)
            plan.Changes.Add(new PlannedChange(file, "bUseVSync", "V-Sync", oldVsync, "False",
                "V-Sync adds input lag. G-Sync/FreeSync and the FPS limit keep the picture tear-free instead."));

        AddVendorExtras(ini, file, plan, kind, hw);
        return plan;
    }

    /// <summary>ARC Raiders (Embark) and other games with their own DLSS/Reflex keys.</summary>
    private static void AddVendorExtras(IniFile ini, string file, PresetPlan plan, PresetKind kind, HardwareProfile hw)
    {
        void IfPresent(string key, string label, string value, string why)
        {
            if (ini.Get(key) is { } old) plan.Changes.Add(new PlannedChange(file, key, label, old, value, why));
        }

        IfPresent("MotionBlurMode", "Motion blur", "Off", "Motion blur smears the picture when you turn. Almost everyone plays better without it.");
        // WARDOGS and Fortnite use plain true/false lines for these
        IfPresent("bUseMotionBlur", "Motion blur", "False", "Motion blur smears the picture when you turn. Almost everyone plays better without it.");
        IfPresent("bMotionBlur", "Motion blur", "False", "Motion blur smears the picture when you turn. Almost everyone plays better without it.");
        if (kind is PresetKind.Competitive or PresetKind.Handheld)
            IfPresent("bUseDepthOfField", "Depth of field", "False", "No blur around your sights, so the whole screen stays sharp.");
        if (ini.Get("bMotionBlurModeSelectedByUser") is not null)
            IfPresent("bMotionBlurModeSelectedByUser", "Motion blur (keep my choice)", "True", "Stops the game from turning motion blur back on.");

        if (hw.Vendor == GpuVendor.Nvidia && kind != PresetKind.Handheld)
            IfPresent("NvReflexMode", "NVIDIA Reflex", "Boost", "Reflex removes the queue of waiting frames, so your clicks reach the screen sooner.");
        if (hw.Vendor == GpuVendor.Amd)
            IfPresent("bAntiLag2Enabled", "AMD Anti-Lag 2", "True", "Lowers input lag on AMD cards.");

        if (hw.Upscaler == Upscaler.Dlss && kind != PresetKind.Handheld)
        {
            IfPresent("ResolutionScalingMethod", "Upscaler", "DLSS", "DLSS gives more FPS than other upscalers at the same image quality on RTX cards.");
            IfPresent("bResolutionScalingMethodSelectedByUser", "Upscaler (keep my choice)", "True", "Stops the game from switching upscaler on its own.");
            string mode = kind switch
            {
                PresetKind.Competitive => hw.Is4K ? "Balanced" : "Quality",
                _ => "Quality",
            };
            IfPresent("DLSSMode", "DLSS mode", mode, hw.Is4K && kind == PresetKind.Competitive
                ? "At 4K, DLSS Balanced still looks sharp and gives a big FPS boost."
                : "DLSS Quality looks as good as native resolution or better, with more FPS.");
        }
        else if (hw.Upscaler == Upscaler.Fsr || kind == PresetKind.Handheld)
        {
            IfPresent("ResolutionScalingMethod", "Upscaler", "FSR", "FSR works on every graphics card.");
            IfPresent("FSRMode", "FSR mode", kind is PresetKind.Handheld or PresetKind.Competitive ? "Balanced" : "Quality", "More FPS with little loss in sharpness.");
        }

        if (kind != PresetKind.Ultra)
            IfPresent("DLSSFrameGenerationMode", "Frame generation", "Off", "Frame generation adds input lag, so it is only used in the Ultra preset.");
    }

    // ---------------------------------------------------------------- Levels

    private static Dictionary<string, int> Levels(PresetKind kind, HardwareProfile hw)
    {
        int tex = hw.VramGb >= 15 && kind == PresetKind.Ultra ? 4
            : hw.VramGb >= 8 ? 3
            : hw.VramGb >= 6 ? 2
            : 1;

        var d = kind switch
        {
            PresetKind.Competitive => new Dictionary<string, int>
            {
                ["sg.ViewDistanceQuality"] = 3, ["sg.TextureQuality"] = tex, ["sg.ShadowQuality"] = 1,
                ["sg.GlobalIlluminationQuality"] = 1, ["sg.ReflectionQuality"] = 0, ["sg.AntiAliasingQuality"] = 2,
                ["sg.PostProcessQuality"] = 0, ["sg.EffectsQuality"] = 0, ["sg.FoliageQuality"] = 1,
                ["sg.ShadingQuality"] = 1, ["sg.LandscapeQuality"] = 2,
            },
            PresetKind.Balanced => Uniform(2, tex),
            PresetKind.Quality => Uniform(3, tex),
            PresetKind.Ultra => Uniform(4, tex),
            _ => new Dictionary<string, int>
            {
                ["sg.ViewDistanceQuality"] = 1, ["sg.TextureQuality"] = Math.Min(tex, 1), ["sg.ShadowQuality"] = 0,
                ["sg.GlobalIlluminationQuality"] = 0, ["sg.ReflectionQuality"] = 0, ["sg.AntiAliasingQuality"] = 1,
                ["sg.PostProcessQuality"] = 0, ["sg.EffectsQuality"] = 0, ["sg.FoliageQuality"] = 0,
                ["sg.ShadingQuality"] = 1, ["sg.LandscapeQuality"] = 1,
            },
        };

        // Weaker cards, and any card below the top tier at 4K, step the heavy settings down
        if (kind is PresetKind.Balanced or PresetKind.Quality or PresetKind.Ultra)
        {
            int penalty = hw.Tier switch { GpuTier.Low => 2, GpuTier.Mid => 1, _ => 0 };
            if (hw.Is4K && hw.Tier != GpuTier.Enthusiast) penalty++;
            foreach (var key in d.Keys.ToList())
                if (key != "sg.TextureQuality") d[key] = Math.Clamp(d[key] - penalty, 0, 4);
        }
        return d;
    }

    private static Dictionary<string, int> Uniform(int level, int tex) =>
        // Textures cost video memory, not speed, so they may sit one step above the rest
        Groups.ToDictionary(g => g.Key, g => g.Key == "sg.TextureQuality" ? Math.Min(tex, level + 1) : level);

    private static string WhyLevel(string key, PresetKind kind, HardwareProfile hw) => key switch
    {
        "sg.TextureQuality" => $"Textures cost video memory, not FPS. Your card has {hw.VramGb:0} GB, so they can stay this sharp.",
        "sg.ShadowQuality" when kind == PresetKind.Competitive => "Shadows are one of the most expensive settings. Low keeps them, without the cost.",
        "sg.EffectsQuality" when kind == PresetKind.Competitive => "Less smoke and particles make it easier to see enemies.",
        "sg.PostProcessQuality" when kind == PresetKind.Competitive => "Removes bloom and depth of field for a clearer picture.",
        "sg.FoliageQuality" when kind == PresetKind.Competitive => "Less grass means less to hide in, and more FPS.",
        "sg.ViewDistanceQuality" when kind == PresetKind.Competitive => "Kept high so you can spot players far away.",
        "sg.ReflectionQuality" when kind == PresetKind.Competitive => "Reflections cost a lot and don't help you play.",
        _ when kind == PresetKind.Handheld => "Lower settings save battery and keep the FPS steady on a small screen.",
        _ when hw.Is4K && hw.Tier != GpuTier.Enthusiast && kind != PresetKind.Competitive => "Lowered one step because 4K is heavy for your card.",
        _ => $"{PresetInfo.Title(kind)} level.",
    };

    private static int FrameCap(PresetKind kind, HardwareProfile hw) => kind switch
    {
        PresetKind.Competitive => 0,
        PresetKind.Handheld => PresetInfo.HandheldCap(hw),
        _ => Math.Max(30, hw.RefreshHz - 3),
    };

    private static string CapWhy(PresetKind kind, HardwareProfile hw) => kind switch
    {
        PresetKind.Competitive => "No limit: more FPS means less input lag.",
        PresetKind.Handheld => "A steady low FPS feels smoother and saves a lot of battery.",
        _ => $"{hw.RefreshHz - 3} FPS stays just under your {hw.RefreshHz} Hz screen, so G-Sync/FreeSync stays active and frame times stay even.",
    };

    // ---------------------------------------------------------------- Write

    public void Write(PresetPlan plan)
    {
        foreach (var group in plan.Effective.GroupBy(c => c.File))
        {
            var ini = new IniFile(group.Key);
            foreach (var c in group)
            {
                if (c.Setting.StartsWith("sg.", StringComparison.Ordinal))
                    ini.Set(Scalability, c.Setting, c.NewValue);
                else
                    ini.SetExisting(c.Setting, c.NewValue);
            }
            ini.Save();
        }
    }
}
