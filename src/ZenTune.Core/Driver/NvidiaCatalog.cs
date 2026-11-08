using ZenTune.Core.Driver;

namespace ZenTune.Core.Driver;

/// <summary>One choice for a driver setting. <see cref="Also"/> are extra settings that must be written together with it.</summary>
public sealed record DriverOption(string Label, uint Value, DriverWrite[]? Also = null);

/// <summary>A number the user may type for a setting, within a range (for example a frame cap of 20 to 1000 FPS).</summary>
public sealed record NumberRange(uint Min, uint Max, string Unit);

/// <summary>
/// A driver setting ZenTune shows. "Driver default" is always offered on top of the options and means
/// "remove the value, so the driver and each game decide". <see cref="Companions"/> are removed together with it.
/// </summary>
public sealed record DriverSetting(
    uint Id,
    string Group,
    string Title,
    string Help,
    IReadOnlyList<DriverOption> Options,
    uint[]? Companions = null,
    NumberRange? Custom = null);

/// <summary>
/// The NVIDIA settings ZenTune offers. Ids and values come from NVIDIA's public NVAPI header
/// (NvApiDriverSettings.h). This is a curated list, not all 100+ settings the driver has.
/// </summary>
public static class NvidiaCatalog
{
    public const uint VrrMode = 0x1194F158;
    public const uint VSync = 0x00A879CF;
    public const uint PreRender = 0x007BA09E;
    public const uint Frl = 0x10835002;
    public const uint Pstate = 0x1057EB71;
    public const uint ShaderCache = 0x00198FFF;
    public const uint ShaderCacheSize = 0x00AC8497;
    public const uint AnisoSelector = 0x10D2BB16;
    public const uint AnisoLevel = 0x101E61A9;
    public const uint NegLod = 0x0019BB68;
    public const uint OglThread = 0x20C1221E;

    public const string GroupSync = "Display and sync";
    public const string GroupLatency = "Latency and performance";
    public const string GroupShader = "Shaders";
    public const string GroupQuality = "Image quality";

    private static DriverOption[] Fps(params uint[] values) =>
        values.Select(v => new DriverOption(v == 0 ? "Off (no limit)" : $"{v} FPS", v)).ToArray();

    public static readonly IReadOnlyList<DriverSetting> All =
    [
        new(VrrMode, GroupSync, "G-Sync / variable refresh rate",
            "Turns G-Sync on from the driver, without the NVIDIA Control Panel. Needs a G-Sync or G-Sync Compatible monitor with the feature switched on in the monitor's own menu. 'Fullscreen and windowed' also covers borderless games and apps.",
            [
                new("Off", 0),
                new("Fullscreen only", 1),
                new("Fullscreen and windowed", 2),
            ]),

        new(VSync, GroupSync, "V-Sync",
            "Sets V-Sync for every game, overriding what the game asks for. With G-Sync, 'Force on' plus an FPS limit a few frames under your refresh rate gives the smoothest result with the least delay.",
            [
                new("Use the game's setting", 0x60925292),
                new("Force off", 0x08416747),
                new("Force on", 0x47814940),
                new("Half refresh rate (1/2)", 0x32610244),
                new("Third of refresh rate (1/3)", 0x71271021),
                new("Quarter of refresh rate (1/4)", 0x13245256),
                new("Fast sync", 0x18888888),
            ]),

        new(Frl, GroupSync, "Max frame rate",
            "A frame cap set in the driver for every game. Handy when a game has no limiter of its own. Choose a few FPS below your refresh rate when using G-Sync, or type your own number.",
            Fps(0, 30, 40, 45, 60, 72, 90, 100, 120, 138, 141, 144, 162, 165, 180, 237, 240, 357, 360),
            Custom: new NumberRange(20, 1000, "FPS")),

        new(PreRender, GroupLatency, "Low latency (pre-rendered frames)",
            "How many frames the CPU may prepare ahead of the GPU. 1 gives the lowest delay between mouse and screen, higher can smooth out uneven frame times. This is the same as 'Low Latency Mode: On' in the Control Panel.",
            [
                new("Let the game decide", 0),
                new("1 frame (lowest delay)", 1),
                new("2 frames", 2),
                new("3 frames", 3),
                new("4 frames", 4),
            ]),

        new(Pstate, GroupLatency, "Power management mode",
            "'Prefer maximum performance' keeps the graphics card at full speed while a game runs, which can remove stutter from clock changes. It uses more power and runs warmer.",
            [
                new("Optimal power", 5),
                new("Adaptive", 0),
                new("Prefer consistent performance", 3),
                new("Prefer maximum performance", 1),
            ]),

        new(OglThread, GroupLatency, "Threaded optimization",
            "Lets the driver use several CPU cores for OpenGL games. The default (automatic) is right for most games; turn it off only if a specific game stutters.",
            [
                new("Off", 2),
                new("On", 1),
            ]),

        new(ShaderCache, GroupShader, "Shader cache",
            "Saves compiled shaders to disk so games stutter less the second time. Leave it on unless you are testing something.",
            [
                new("Off", 0),
                new("On", 1),
            ]),

        new(ShaderCacheSize, GroupShader, "Shader cache size",
            "How much disk space the shader cache may use. When it is full the driver deletes old shaders and games have to compile them again. The sizes are in MB (1 GB = 1024 MB); you can also type your own.",
            [
                new("128 MB", 128),
                new("256 MB", 256),
                new("512 MB", 512),
                new("1 GB", 1024),
                new("5 GB", 5120),
                new("10 GB", 10240),
                new("16 GB (the value the driver uses by default)", 16384),
                new("100 GB", 102400),
                new("Unlimited", 0xFFFFFFFF),
            ],
            Custom: new NumberRange(128, 1048576, "MB")),

        new(AnisoLevel, GroupQuality, "Anisotropic filtering",
            "Sharper textures at an angle, such as roads and floors. Forces the level for every game that lets the driver decide. The cost is small on modern cards.",
            [
                new("2x", 2, [new DriverWrite(AnisoSelector, 1)]),
                new("4x", 4, [new DriverWrite(AnisoSelector, 1)]),
                new("8x", 8, [new DriverWrite(AnisoSelector, 1)]),
                new("16x", 16, [new DriverWrite(AnisoSelector, 1)]),
            ],
            Companions: [AnisoSelector]),

        new(NegLod, GroupQuality, "Negative LOD bias",
            "'Clamp' stops games from making textures sharper than intended, which removes shimmering on some older games.",
            [
                new("Allow", 0),
                new("Clamp", 1),
            ]),
    ];

    /// <summary>Every id ZenTune may have written, including companion settings.</summary>
    public static IEnumerable<uint> AllIds() =>
        All.SelectMany(s => new[] { s.Id }.Concat(s.Companions ?? []));

    /// <summary>The writes needed to put one setting at an option, or back to the driver default when <paramref name="option"/> is null.</summary>
    public static List<DriverWrite> WritesFor(DriverSetting setting, DriverOption? option)
    {
        var list = new List<DriverWrite>();
        if (option is null)
        {
            list.Add(new DriverWrite(setting.Id, null));
            foreach (var c in setting.Companions ?? []) list.Add(new DriverWrite(c, null));
        }
        else
        {
            list.Add(new DriverWrite(setting.Id, option.Value));
            if (option.Also is not null) list.AddRange(option.Also);
        }
        return list;
    }
}
