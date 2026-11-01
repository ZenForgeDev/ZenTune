using ZenTune.Core.Games;
using ZenTune.Core.Hardware;

namespace ZenTune.Core.Presets;

/// <summary>
/// Delta Force: a guide, not a preset. Delta Force's developer (Team Jade) warns that editing its .ini files
/// outside the game can lead to a 7-day account ban, so ZenTune never writes to this game. It only READS
/// UserSystemSettingHD.ini to show what to change in the game's own settings menu.
/// The value scales (0 = Low ... 5 = Ultimate) come from the community Delta-Force-Tweaker project and were
/// checked on one PC, so the names shown here follow that table.
/// </summary>
public sealed class DeltaForceGuide : IGameTuner
{
    private const string Section = "/Script/GPGlobalDefines.ClientVideoSettingHD";

    private static string? Hd(DetectedGame game) =>
        game.ConfigFiles.FirstOrDefault(f => Path.GetFileName(f).Equals("UserSystemSettingHD.ini", StringComparison.OrdinalIgnoreCase));

    public bool Supports(DetectedGame game) => game.Id == "deltaforce" && Hd(game) is not null;

    private static readonly string[] Levels = ["Low", "Medium", "High", "Ultra", "Extreme", "Ultimate"];
    private static readonly string[] DlssModes = ["Auto", "Ultra Performance", "Performance", "Balanced", "Quality"];
    private static readonly string[] ReflexModes = ["Off", "Low Latency", "Enhanced"];
    private static readonly int[] FpsValues = [0, 30, 60, 75, 100, 120, 144];
    private static readonly string[] FpsNames = ["Unlimited", "30", "60", "75", "100", "120", "144"];

    // Menu name, file key, level per column (Competitive, Balanced, Quality, Ultra, Handheld), why
    private static readonly (string Label, string Key, int[] Level, string Why)[] Rows =
    [
        ("Scene Details", "SceneDetailLevel", [2, 3, 4, 5, 1], "How much clutter is drawn in the world."),
        ("Scene View Distance", "ViewDistanceLevel", [4, 3, 4, 5, 1], "Kept high in Competitive so you can spot players far away."),
        ("Shaders", "ShadingQuality", [2, 3, 4, 5, 1], "Surface lighting detail."),
        ("Shadows", "ShadowQuality", [1, 2, 3, 5, 0], "Shadows are one of the most expensive settings."),
        ("Shadow Map", "ShadowMapResolution", [1, 2, 3, 5, 0], "How sharp shadow edges are."),
        ("Lumen (GI)", "GIQuality", [0, 1, 3, 5, 0], "Global illumination. Very expensive."),
        ("Reflections", "ReflectionQuality", [0, 1, 3, 5, 0], "Reflections cost a lot and rarely help you play."),
        ("Particles", "ParticleQuality", [1, 2, 3, 5, 0], "Smoke and explosions."),
        ("Distortion", "DistortionQuality", [0, 2, 3, 5, 0], "Heat haze and screen warping."),
        ("Post-Processing", "PostProcessQuality", [0, 2, 3, 5, 0], "Bloom and similar screen effects."),
        ("Volumetric Fog", "VolumetricFogQuality", [0, 1, 3, 5, 0], "Fog and light shafts."),
        ("Animation", "AnimationQuality", [2, 3, 4, 5, 1], "Animation detail for far-away characters."),
        ("Texture Filtering", "TextureFilteringQuality", [4, 4, 5, 5, 2], "Keeps the ground sharp at an angle, for almost no cost."),
        ("Streaming", "StreamingQuality", [3, 3, 4, 5, 1], "How much of the map is kept ready."),
    ];

    private static string Name(string? raw, string[] names)
    {
        if (raw is null) return "(not set)";
        return int.TryParse(raw, out int i) && i >= 0 && i < names.Length ? names[i] : raw;
    }

    public PresetPlan Plan(DetectedGame game, PresetKind kind, HardwareProfile hw)
    {
        var file = Hd(game) ?? throw new InvalidOperationException("Start the game once so it creates its settings file.");
        var ini = new IniFile(file);
        var plan = new PresetPlan
        {
            Game = game,
            Kind = kind,
            GuideOnly = true,
            Summary = PresetInfo.Description(kind) +
                      " ZenTune does not edit Delta Force's files, because its developer says that can lead to a 7-day account ban. " +
                      "Set these yourself in the game's graphics menu.",
        };
        int c = PresetMath.Column(kind, hw);

        void Add(string key, string label, string[] names, int newIndex, string why)
        {
            var old = ini.Get(key, Section);
            plan.Changes.Add(new PlannedChange(file, key, label, Name(old, names), names[newIndex], why));
        }

        // Textures cost video memory, not FPS
        int tex = kind == PresetKind.Handheld ? 2 : hw.VramGb >= 12 ? 5 : hw.VramGb >= 8 ? 4 : hw.VramGb >= 6 ? 3 : 2;
        Add("TextureQuality", "Textures", Levels, tex, $"Textures cost video memory, not FPS. Your card has {hw.VramGb:0} GB.");

        foreach (var (label, key, level, why) in Rows)
            Add(key, label, Levels, level[c], why);

        if (kind is PresetKind.Competitive or PresetKind.Handheld)
            plan.Changes.Add(new PlannedChange(file, "AmbientOcclusionQuality", "Ambient Occlusion",
                Name(ini.Get("AmbientOcclusionQuality", Section), ["Off", "Low", "Medium", "High", "Ultra", "Extreme"]), "Off",
                "Soft corner shadows cost FPS and add little in a fast game."));

        // DLSS only when the game is already using it (value 4 = NVIDIA DLSS in this game's file)
        if (hw.Upscaler == Upscaler.Dlss && ini.Get("SuperResolutionMethod", Section) == "4")
        {
            bool lean = kind == PresetKind.Handheld || (kind == PresetKind.Competitive && hw.Is4K);
            int mode = lean ? 3 : 4;
            Add("SuperResolutionQualityDLSS", "Super Resolution (DLSS) quality", DlssModes, mode,
                mode == 3 ? "Balanced still looks sharp and gives a big FPS boost." : "Quality looks as good as native resolution or better, with more FPS.");
        }

        if (hw.Vendor == GpuVendor.Nvidia && kind != PresetKind.Handheld)
            Add("NVIDIAReflexMode", "NVIDIA Reflex", ReflexModes, 2, "Reflex removes the queue of waiting frames, so your shots reach the screen sooner.");

        int capIndex = kind == PresetKind.Competitive ? 0 : IndexOfCap(PresetMath.FpsCap(kind, hw));
        plan.Changes.Add(new PlannedChange(file, "MaxFPS", "In-match frame rate cap",
            Name(ini.Get("MaxFPS", Section), FpsNames), FpsNames[capIndex],
            kind == PresetKind.Competitive ? "No limit: more FPS means less input lag." : PresetMath.FpsWhy(kind, hw)));

        return plan;
    }

    /// <summary>Position in the game's cap list of the biggest cap that is not above the target.</summary>
    private static int IndexOfCap(int target)
    {
        int best = 1; // 30 FPS
        for (int i = 1; i < FpsValues.Length; i++)
            if (FpsValues[i] <= target) best = i;
        return best;
    }

    public void Write(PresetPlan plan) =>
        throw new InvalidOperationException("ZenTune does not write to Delta Force's files. Set these in the game's own settings menu.");
}
