using ZenTune.Core.Presets;

namespace ZenTune.Core.Ai;

/// <summary>What the AI should aim for when it picks settings.</summary>
public enum AiMode { BestFps, UltraPerformance, Performance, Balanced, Quality }

public static class AiModeInfo
{
    /// <summary>The modes shown in the drop-down (Best FPS has its own button).</summary>
    public static readonly AiMode[] Picker = [AiMode.UltraPerformance, AiMode.Performance, AiMode.Balanced, AiMode.Quality];

    public static string Title(AiMode mode) => mode switch
    {
        AiMode.BestFps => "Best FPS",
        AiMode.UltraPerformance => "Ultra Performance",
        AiMode.Performance => "Performance",
        AiMode.Balanced => "Balanced",
        _ => "Quality",
    };

    /// <summary>What we tell Gemini to aim for.</summary>
    internal static string Goal(AiMode mode, int refreshHz) => mode switch
    {
        AiMode.BestFps =>
            "Get the highest possible sustained frame rate and the best 1% lows, with the lowest input lag. " +
            "Picture quality does not matter, except that enemies and objects must stay easy to see. " +
            "Turn off or lower everything expensive, use the strongest upscaling that still looks acceptable at this screen resolution, " +
            "do not cap the frame rate, and avoid frame generation because it adds input lag.",
        AiMode.UltraPerformance =>
            "Very high FPS with simple graphics: the lowest practical settings for shadows, reflections, volumetric effects, particles and physics. " +
            "Keep textures sharp if the video memory allows it. Prefer upscaling over a lower resolution.",
        AiMode.Performance =>
            $"High FPS, at or above the screen's {refreshHz} Hz, with a decent picture. " +
            "Lower the most expensive settings but keep textures and model quality high.",
        AiMode.Balanced =>
            $"Good-looking and smooth. Aim for an FPS close to the screen's {refreshHz} Hz refresh rate without making the picture look cheap.",
        _ =>
            "The best picture quality that still runs smoothly (at least 60 FPS, ideally close to the screen's refresh rate). " +
            "Use native resolution or quality upscaling. Frame generation is allowed if the card supports it and it helps smoothness.",
    };

    /// <summary>The closest built-in preset kind. Only used to fill in the plan; the AI's values are what get written.</summary>
    public static PresetKind ToPresetKind(this AiMode mode) => mode switch
    {
        AiMode.BestFps or AiMode.UltraPerformance or AiMode.Performance => PresetKind.Competitive,
        AiMode.Balanced => PresetKind.Balanced,
        _ => PresetKind.Quality,
    };
}
