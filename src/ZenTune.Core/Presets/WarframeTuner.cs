using ZenTune.Core.Games;
using ZenTune.Core.Hardware;

namespace ZenTune.Core.Presets;

/// <summary>
/// Presets for Warframe. The graphics settings are "Graphics.*" lines in EE.cfg. Only values that Warframe's own
/// settings documentation lists are written, and only for settings the file already has.
/// Close the game first: Warframe writes EE.cfg again when it quits.
/// </summary>
public sealed class WarframeTuner : IGameTuner
{
    // The frame rates the in-game Max Framerate list offers
    private static readonly int[] FrameRates = [30, 40, 50, 60, 72, 75, 120, 144, 165, 200, 240, 360];

    private static string? Cfg(DetectedGame game) =>
        game.ConfigFiles.FirstOrDefault(f => Path.GetFileName(f).Equals("EE.cfg", StringComparison.OrdinalIgnoreCase));

    public bool Supports(DetectedGame game) =>
        game.Id == "warframe" && Cfg(game) is { } f && File.ReadAllText(f).Contains("Graphics.GeometryDetail", StringComparison.Ordinal);

    // Columns: Competitive, Balanced, Quality, Ultra, Handheld
    private static readonly string[] Geometry = ["GD_LOW", "GD_MEDIUM", "GD_HIGH", "GD_HIGH", "GD_LOW"];
    private static readonly string[] Shadows = ["SQ_LOW", "SQ_MEDIUM", "SQ_HIGH", "SQ_HIGH", "SQ_LOW"];
    private static readonly string[] Particles = ["PQ_LOW", "PQ_MEDIUM", "PQ_HIGH", "PQ_HIGH", "PQ_LOW"];

    public PresetPlan Plan(DetectedGame game, PresetKind kind, HardwareProfile hw)
    {
        var file = Cfg(game) ?? throw new InvalidOperationException("Start the game once so it creates its settings file.");
        var ini = new IniFile(file);
        var plan = new PresetPlan { Game = game, Kind = kind, Summary = PresetInfo.Description(kind) };
        int c = PresetMath.Column(kind, hw);
        bool lean = kind is PresetKind.Competitive or PresetKind.Handheld;

        void Add(string key, string label, string value, string why)
        {
            var old = ini.Get(key);
            if (old is null) return; // the file does not use this setting
            plan.Changes.Add(new PlannedChange(file, key, label, old, value, why));
        }

        Add("Graphics.ShadowQuality", "Shadows", Shadows[c], "Shadows are one of the most expensive settings.");
        Add("Graphics.GeometryDetail", "Geometry", Geometry[c], "How detailed models and terrain are.");
        Add("Graphics.ParticleSysQuality", "Particles", Particles[c], "Abilities and explosions fill the screen in Warframe, so this setting matters a lot for FPS.");

        string texture = kind == PresetKind.Handheld || hw.VramGb < 6 ? "TQ_MEDIUM" : "TQ_HIGH";
        Add("Graphics.TextureQuality", "Textures", texture, $"Textures cost video memory, not FPS. Your card has {hw.VramGb:0} GB.");

        if (lean)
        {
            Add("Graphics.SSAOMode", "Ambient occlusion", "SSAOM_DISABLED", "Soft corner shadows cost FPS and add little in a fast game.");
            Add("Graphics.EnableContactShadows", "Contact shadows", "0", "Small extra shadows. Off saves FPS.");
        }
        else
        {
            Add("Graphics.EnableContactShadows", "Contact shadows", "1", "Small extra shadows that make the picture look grounded.");
        }
        Add("Graphics.EnableMotionBlur", "Motion blur", "0", "Motion blur smears the picture when you turn.");

        if (hw.Upscaler == Upscaler.Dlss)
        {
            Add("Graphics.UpscaleMethod", "Upscaler", "UM_DLSS", "DLSS gives the best picture per FPS on RTX cards.");
            Add("Graphics.UpscaleQuality", "Upscale mode", "UQ_QUALITY", "Quality looks as good as native resolution or better, with more FPS.");
        }

        Add("Graphics.VSyncMode", "V-Sync", "VSM_NEVER_SYNC", "V-Sync adds input lag. G-Sync/FreeSync and the FPS limit keep the picture tear-free instead.");

        int cap = kind == PresetKind.Competitive ? FrameRates.Max() : PresetMath.LargestAtMost(FrameRates, PresetMath.FpsCap(kind, hw));
        string capWhy = kind == PresetKind.Competitive
            ? "The highest limit the game offers: more FPS means less input lag."
            : PresetMath.FpsWhy(kind, hw);
        Add("Graphics.MaxFrameRate", "FPS limit", cap.ToString(), capWhy);

        return plan;
    }

    public void Write(PresetPlan plan)
    {
        foreach (var group in plan.Effective.GroupBy(x => x.File))
        {
            var ini = new IniFile(group.Key);
            foreach (var change in group)
                ini.SetExisting(change.Setting, change.NewValue);
            ini.Save();
        }
    }
}
