namespace ZenTune.Core.Games;

/// <summary>
/// A folder plus the file patterns ZenTune cares about inside it. Folder can contain tokens:
/// {LocalAppData}, {Documents}, {Install} (game folder), {Library} (the Steam library the game is in),
/// {AppId} (Steam app id) and {SteamUserdata} (Steam\userdata\&lt;your account&gt;).
/// </summary>
public sealed record FileLocation(string Folder, string[] Patterns, bool Recursive = false);

/// <summary>
/// Changes one "Key@... = value" line in the game's settings files. Used for Call of Duty, which
/// rebuilds its shaders when it thinks the graphics driver changed.
/// </summary>
public sealed record ConfigLineEdit(string FilePattern, string Key, string Value);

/// <summary>A game ZenTune knows how to handle.</summary>
public sealed class GameDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary>One line shown under the name.</summary>
    public string Engine { get; init; } = "";

    public int[] SteamAppIds { get; init; } = [];

    /// <summary>Steam install folder names to match when the app id is new or unknown (for example "Call of Duty").</summary>
    public string[] SteamFolderHints { get; init; } = [];

    /// <summary>Returns true for the Epic Games Store display name of this game.</summary>
    public Func<string, bool> EpicMatch { get; init; } = _ => false;

    /// <summary>The game's settings files. Save games are never included.</summary>
    public FileLocation[] Config { get; init; } = [];

    /// <summary>Shader caches the game rebuilds on its own when they are deleted.</summary>
    public FileLocation[] Shaders { get; init; } = [];

    /// <summary>
    /// For games that keep their shaders in the graphics driver's cache instead of their own files.
    /// Reset shaders then clears the driver cache, and this text explains why.
    /// </summary>
    public string? UsesDriverCache { get; init; }

    /// <summary>For games with their own "rebuild shaders" button. Reset shaders then explains where it is.</summary>
    public string? InGameShaderReset { get; init; }

    /// <summary>Optional: a settings change that makes the game rebuild its shaders on the next start.</summary>
    public ConfigLineEdit? ShaderRebuildEdit { get; init; }

    /// <summary>Shown after resetting shaders.</summary>
    public string ShaderNote { get; init; } = "The next start takes longer while the game builds its shaders again.";

    /// <summary>
    /// For games that share an install with another entry (Call of Duty HQ holds several games):
    /// only show this one when its own settings files exist.
    /// </summary>
    public bool HideWithoutSettings { get; init; }
}

public static class GameCatalog
{
    private static readonly string[] UnrealConfig = ["GameUserSettings.ini", "Engine.ini", "Scalability.ini"];
    private static readonly string[] UnrealShaders = ["*.ushaderprecache", "*.upipelinecache"];

    private static FileLocation SteamShaderCache => new(@"{Library}\steamapps\shadercache\{AppId}", ["*"], Recursive: true);

    public static readonly IReadOnlyList<GameDefinition> All =
    [
        new GameDefinition
        {
            Id = "cs2",
            Name = "Counter-Strike 2",
            Engine = "Source 2",
            SteamAppIds = [730],
            Config =
            [
                new(@"{SteamUserdata}\730\local\cfg", ["cs2_*.txt", "cs2_*.vcfg"]),
            ],
            Shaders = [SteamShaderCache],
        },

        new GameDefinition
        {
            Id = "mw4",
            Name = "Call of Duty: Modern Warfare 4 / Warzone",
            Engine = "IW engine · runs inside Call of Duty HQ · Warzone shares these settings",
            // 1938090 = Call of Duty (HQ), which MW4 installs into; 4435490 = MW4's own store page;
            // 1962663 = Call of Duty: Warzone (moved here from the Black Ops 7 card, 7 Nov 2026).
            // NOT yet confirmed on disk that Warzone writes to cod26 files: check which file changes after a Warzone settings change.
            SteamAppIds = [1938090, 4435490, 1962663],
            SteamFolderHints = ["Call of Duty"],
            // MW4 is "cod26" in its file names. Graphics are in s.1.0.cod26.txt; gameplay settings, presets and
            // key binds are in the folder named after your account (and its SteamCloud folder on Steam).
            Config =
            [
                new(@"{LocalAppData}\Activision\Call of Duty\players", ["s.*.cod26.txt*"]),
                new(@"{LocalAppData}\Activision\Call of Duty\players\*", ["g.cod26.*"]),
                new(@"{LocalAppData}\Activision\Call of Duty\players\*\SteamCloud",
                    ["g.cod26.*", "keybinds.cod26.*", "settingspresets.cod26.*", "favoritesettings.cod26.*"]),
            ],
            Shaders = [SteamShaderCache],
            InGameShaderReset = "Call of Duty keeps track of its own shader pre-loading, so deleting files does not make it start over. " +
                                "Use the game's own button instead: look for Restart Shaders Pre-Loading in the game's Graphics settings. " +
                                "The game then rebuilds all shaders in the main menu. Let it reach 100% before you play.",
            ShaderNote = "Let \"Shader pre-loading\" reach 100% in the main menu before you play.",
        },

        new GameDefinition
        {
            Id = "cod",
            Name = "Call of Duty: Black Ops 7",
            Engine = "IW engine · Black Ops 7 (cod25)",
            // 1938090 = Call of Duty (HQ). Warzone (1962663) now lives on the MW4 card.
            // The id stays "cod" so saved AI profiles and share codes made earlier still match this card.
            SteamAppIds = [1938090],
            SteamFolderHints = ["Call of Duty"],
            // cod25 = Black Ops 7, cod24 = Black Ops 6. MW4 (cod26) has its own entry above and its own files.
            // Do NOT add cod26 here: the tuner writes to every file a game lists, so this card would then change MW4's settings too.
            // (Checked on a real PC: s.1.0.cod25.txt0/txt1 and s.1.0.cod26.txt exist side by side with different values.)
            Config =
            [
                new(@"{LocalAppData}\Activision\Call of Duty\Players", ["s.*.cod25.txt*", "s.*.cod24.txt*"]),
                new(@"{Documents}\Call of Duty\players", ["s.*.cod24.txt*", "options.*.txt*", "*.cst"]),
            ],
            HideWithoutSettings = true,
            Shaders = [SteamShaderCache],
            InGameShaderReset = "Call of Duty keeps track of its own shader pre-loading, so deleting files does not make it start over. " +
                                "Use the game's own button instead: Settings → Graphics → Display → Restart Shaders Pre-Loading. " +
                                "The game then rebuilds all shaders in the main menu. Let it reach 100% before you play.",
            // Tested: changing GPUDriverVersion in the settings does not make the game pre-load again,
            // it just writes the real version back. The in-game button is the only reliable way.
            ShaderNote = "Let \"Shader pre-loading\" reach 100% in the main menu before you play.",
        },

        new GameDefinition
        {
            Id = "tow",
            Name = "The Outer Worlds",
            Engine = "Unreal Engine 4",
            SteamAppIds = [578650, 1920490],
            EpicMatch = n => n.StartsWith("The Outer Worlds", StringComparison.OrdinalIgnoreCase) && !n.Contains("Outer Worlds 2", StringComparison.OrdinalIgnoreCase),
            Config =
            [
                new(@"{LocalAppData}\Indiana\Saved\Config\WindowsNoEditor", UnrealConfig),
                new(@"{LocalAppData}\Indiana\Saved\Config\Windows", UnrealConfig),
            ],
            Shaders =
            [
                new(@"{LocalAppData}\Indiana\Saved", UnrealShaders),
                SteamShaderCache,
            ],
        },

        new GameDefinition
        {
            Id = "tow2",
            Name = "The Outer Worlds 2",
            Engine = "Unreal Engine 5",
            SteamAppIds = [1449110],
            EpicMatch = n => n.Contains("Outer Worlds 2", StringComparison.OrdinalIgnoreCase),
            Config =
            [
                new(@"{LocalAppData}\Arkansas\Saved\Config\Windows", UnrealConfig),
            ],
            Shaders =
            [
                new(@"{LocalAppData}\Arkansas\Saved", UnrealShaders),
                new(@"{LocalAppData}\Arkansas\Saved\PipelineCaches", ["*"], Recursive: true),
                SteamShaderCache,
            ],
        },

        new GameDefinition
        {
            Id = "arcraiders",
            Name = "ARC Raiders",
            Engine = "Unreal Engine 5",
            SteamAppIds = [1808500],
            SteamFolderHints = ["Arc Raiders"],
            EpicMatch = n => n.Contains("ARC Raiders", StringComparison.OrdinalIgnoreCase),
            // Embark's internal project name is "PioneerGame"
            Config =
            [
                new(@"{LocalAppData}\PioneerGame\Saved\Config\WindowsClient", ["GameUserSettings.ini", "Engine.ini"]),
            ],
            Shaders =
            [
                new(@"{LocalAppData}\PioneerGame\Saved", UnrealShaders),
                new(@"{LocalAppData}\PioneerGame\Saved\PipelineCaches", ["*"], Recursive: true),
                SteamShaderCache,
            ],
        },

        new GameDefinition
        {
            Id = "bf6",
            Name = "Battlefield 6 / REDSEC",
            Engine = "Frostbite · Battlefield 6 and REDSEC share settings",
            SteamAppIds = [2807960],
            SteamFolderHints = ["Battlefield 6", "Battlefield REDSEC"],
            EpicMatch = n => n.Contains("Battlefield 6", StringComparison.OrdinalIgnoreCase) || n.Contains("REDSEC", StringComparison.OrdinalIgnoreCase),
            Config =
            [
                new(@"{Documents}\Battlefield 6\settings", ["PROFSAVE_profile*", "user.cfg"]),
            ],
        },

        new GameDefinition
        {
            Id = "fallout76",
            Name = "Fallout 76",
            Engine = "Creation Engine",
            SteamAppIds = [1151340],
            Config =
            [
                new(@"{Documents}\My Games\Fallout 76", ["Fallout76Prefs.ini", "Fallout76Custom.ini"]),
            ],
        },

        new GameDefinition
        {
            Id = "apex",
            Name = "Apex Legends",
            Engine = "Source (Respawn)",
            SteamAppIds = [1172470],
            EpicMatch = n => n.Contains("Apex Legends", StringComparison.OrdinalIgnoreCase),
            Config =
            [
                new(@"{SavedGames}\Respawn\Apex\local", ["videoconfig.txt", "settings.cfg"]),
            ],
        },

        new GameDefinition
        {
            Id = "r6siege",
            Name = "Rainbow Six Siege",
            Engine = "AnvilNext",
            SteamAppIds = [359550],
            EpicMatch = n => n.Contains("Rainbow Six Siege", StringComparison.OrdinalIgnoreCase),
            Config =
            [
                new(@"{Documents}\My Games\Rainbow Six - Siege\*", ["GameSettings.ini"]),
            ],
        },

        new GameDefinition
        {
            Id = "pubg",
            Name = "PUBG: Battlegrounds",
            Engine = "Unreal Engine 4",
            SteamAppIds = [578080],
            EpicMatch = n => n.Contains("PUBG", StringComparison.OrdinalIgnoreCase),
            Config =
            [
                new(@"{LocalAppData}\TslGame\Saved\Config\WindowsNoEditor", UnrealConfig),
            ],
            Shaders =
            [
                new(@"{LocalAppData}\TslGame\Saved", UnrealShaders),
                SteamShaderCache,
            ],
        },

        new GameDefinition
        {
            Id = "marvelrivals",
            Name = "Marvel Rivals",
            Engine = "Unreal Engine 5",
            SteamAppIds = [2767030],
            EpicMatch = n => n.Contains("Marvel Rivals", StringComparison.OrdinalIgnoreCase),
            Config =
            [
                new(@"{LocalAppData}\Marvel\Saved\Config\Windows", UnrealConfig),
            ],
            Shaders =
            [
                new(@"{LocalAppData}\Marvel\Saved", UnrealShaders),
                SteamShaderCache,
            ],
        },

        new GameDefinition
        {
            Id = "helldivers2",
            Name = "Helldivers 2",
            Engine = "Autodesk Stingray",
            SteamAppIds = [553850],
            Config =
            [
                new(@"{RoamingAppData}\Arrowhead\Helldivers2", ["user_settings.config"]),
            ],
            Shaders =
            [
                new(@"{RoamingAppData}\Arrowhead\Helldivers2\shader_cache", ["*"], Recursive: true),
                SteamShaderCache,
            ],
        },

        new GameDefinition
        {
            Id = "hogwarts",
            Name = "Hogwarts Legacy",
            Engine = "Unreal Engine 4",
            SteamAppIds = [990080],
            EpicMatch = n => n.Contains("Hogwarts Legacy", StringComparison.OrdinalIgnoreCase),
            Config =
            [
                new(@"{LocalAppData}\Hogwarts Legacy\Saved\Config\WindowsNoEditor", UnrealConfig),
                new(@"{LocalAppData}\Phoenix\Saved\Config\WindowsNoEditor", UnrealConfig),
            ],
            Shaders =
            [
                new(@"{LocalAppData}\Hogwarts Legacy\Saved", UnrealShaders),
                new(@"{LocalAppData}\Phoenix\Saved", UnrealShaders),
                SteamShaderCache,
            ],
        },

        new GameDefinition
        {
            Id = "avowed",
            Name = "Avowed",
            Engine = "Unreal Engine 5",
            SteamAppIds = [2457220],
            // Obsidian's internal project name is "Alabama"
            Config =
            [
                new(@"{LocalAppData}\Alabama\Saved\Config\Windows", UnrealConfig),
            ],
            Shaders =
            [
                new(@"{LocalAppData}\Alabama\Saved", UnrealShaders),
                new(@"{LocalAppData}\Alabama\Saved\PipelineCaches", ["*"], Recursive: true),
                SteamShaderCache,
            ],
        },

        new GameDefinition
        {
            Id = "cyberpunk",
            Name = "Cyberpunk 2077",
            Engine = "REDengine 4",
            SteamAppIds = [1091500],
            EpicMatch = n => n.Contains("Cyberpunk 2077", StringComparison.OrdinalIgnoreCase),
            Config =
            [
                new(@"{LocalAppData}\CD Projekt Red\Cyberpunk 2077", ["UserSettings.json"]),
            ],
            Shaders = [SteamShaderCache],
        },

        new GameDefinition
        {
            Id = "eldenring",
            Name = "Elden Ring",
            Engine = "FromSoftware engine",
            SteamAppIds = [1245620],
            Config =
            [
                new(@"{RoamingAppData}\EldenRing", ["GraphicsConfig.xml"]),
            ],
            Shaders = [SteamShaderCache],
        },

        new GameDefinition
        {
            Id = "ff7rebirth",
            Name = "Final Fantasy VII Rebirth",
            Engine = "Unreal Engine 4",
            SteamAppIds = [2909400],
            EpicMatch = n => n.Contains("FINAL FANTASY VII REBIRTH", StringComparison.OrdinalIgnoreCase),
            Config =
            [
                new(@"{Documents}\My Games\FINAL FANTASY VII REBIRTH\Saved\Config\WindowsNoEditor", UnrealConfig),
            ],
            Shaders =
            [
                new(@"{Documents}\My Games\FINAL FANTASY VII REBIRTH\Saved", UnrealShaders),
                SteamShaderCache,
            ],
        },

        new GameDefinition
        {
            Id = "thefinals",
            Name = "The Finals",
            Engine = "Unreal Engine 5",
            SteamAppIds = [2073850],
            // Embark's internal project name is "Discovery"
            Config =
            [
                new(@"{LocalAppData}\Discovery\Saved\Config\WindowsClient", UnrealConfig),
            ],
            Shaders =
            [
                new(@"{LocalAppData}\Discovery\Saved", UnrealShaders),
                SteamShaderCache,
            ],
        },

        new GameDefinition
        {
            Id = "dbd",
            Name = "Dead by Daylight",
            Engine = "Unreal Engine 4",
            SteamAppIds = [381210],
            EpicMatch = n => n.Contains("Dead by Daylight", StringComparison.OrdinalIgnoreCase),
            // Steam uses WindowsClient, the Epic version EGS
            Config =
            [
                new(@"{LocalAppData}\DeadByDaylight\Saved\Config\WindowsClient", UnrealConfig),
                new(@"{LocalAppData}\DeadByDaylight\Saved\Config\EGS", UnrealConfig),
            ],
            Shaders =
            [
                new(@"{LocalAppData}\DeadByDaylight\Saved", UnrealShaders),
                SteamShaderCache,
            ],
        },

        new GameDefinition
        {
            Id = "wukong",
            Name = "Black Myth: Wukong",
            Engine = "Unreal Engine 5",
            SteamAppIds = [2358720],
            Config =
            [
                new(@"{LocalAppData}\b1\Saved\Config\Windows", UnrealConfig),
            ],
            Shaders =
            [
                new(@"{LocalAppData}\b1\Saved", UnrealShaders),
                SteamShaderCache,
            ],
        },

        new GameDefinition
        {
            Id = "stalker2",
            Name = "S.T.A.L.K.E.R. 2: Heart of Chornobyl",
            Engine = "Unreal Engine 5",
            SteamAppIds = [1643320],
            Config =
            [
                new(@"{LocalAppData}\Stalker2\Saved\Config\Windows", UnrealConfig),
            ],
            Shaders =
            [
                new(@"{LocalAppData}\Stalker2\Saved", UnrealShaders),
                SteamShaderCache,
            ],
        },

        new GameDefinition
        {
            Id = "satisfactory",
            Name = "Satisfactory",
            Engine = "Unreal Engine 5",
            SteamAppIds = [526870],
            EpicMatch = n => n.Contains("Satisfactory", StringComparison.OrdinalIgnoreCase),
            // Coffee Stain's project name is "FactoryGame". Older versions (Unreal Engine 4) used WindowsNoEditor.
            Config =
            [
                new(@"{LocalAppData}\FactoryGame\Saved\Config\Windows", UnrealConfig),
                new(@"{LocalAppData}\FactoryGame\Saved\Config\WindowsNoEditor", UnrealConfig),
            ],
            Shaders =
            [
                new(@"{LocalAppData}\FactoryGame\Saved", UnrealShaders),
                SteamShaderCache,
            ],
        },

        new GameDefinition
        {
            Id = "wardogs",
            Name = "WARDOGS",
            Engine = "Unreal Engine 5",
            SteamAppIds = [1867240],
            Config =
            [
                new(@"{LocalAppData}\Wardogs\Saved\Config\WindowsClient", UnrealConfig),
            ],
            Shaders =
            [
                new(@"{LocalAppData}\Wardogs\Saved", UnrealShaders),
                SteamShaderCache,
            ],
        },

        new GameDefinition
        {
            Id = "fortnite",
            Name = "Fortnite",
            Engine = "Unreal Engine 5",
            EpicMatch = n => n.Equals("Fortnite", StringComparison.OrdinalIgnoreCase),
            Config =
            [
                new(@"{LocalAppData}\FortniteGame\Saved\Config\WindowsClient", UnrealConfig),
            ],
        },

        new GameDefinition
        {
            Id = "dawnwalker",
            Name = "The Blood of Dawnwalker",
            Engine = "Unreal Engine 5 · graphics settings are in RebelSettings.sav",
            SteamAppIds = [3751260],
            // The graphics settings live in SaveGames\RebelSettings.sav. It is the only .sav file ZenTune
            // backs up, and only because it is named exactly here; real save games are never touched.
            Config =
            [
                new(@"{LocalAppData}\Dawnwalker\Saved\Config\Windows", ["GameUserSettings.ini", "GameUserFramegen.ini", "Engine.ini"]),
                new(@"{LocalAppData}\Dawnwalker\Saved\SaveGames", ["RebelSettings.sav"]),
            ],
            Shaders =
            [
                new(@"{LocalAppData}\Dawnwalker\Saved", UnrealShaders),
                SteamShaderCache,
            ],
        },

        new GameDefinition
        {
            Id = "tinytina",
            Name = "Tiny Tina's Wonderlands",
            Engine = "Unreal Engine 4 (Gearbox)",
            SteamAppIds = [1286680],
            EpicMatch = n => n.Contains("Tiny Tina's Wonderlands", StringComparison.OrdinalIgnoreCase),
            Config =
            [
                new(@"{Documents}\My Games\Tiny Tina's Wonderlands\Saved\Config\WindowsNoEditor", UnrealConfig),
            ],
            Shaders =
            [
                new(@"{LocalAppData}\Tiny Tina's Wonderlands\Saved", UnrealShaders),
                SteamShaderCache,
            ],
        },

        new GameDefinition
        {
            Id = "doomtda",
            Name = "DOOM: The Dark Ages",
            Engine = "id Tech 8",
            SteamAppIds = [3017860],
            Config =
            [
                new(@"{SavedGames}\id Software\DOOMTheDarkAges\base", ["DOOMTheDarkAgesConfig.cfg", "DOOMTheDarkAgesConfig.local"]),
            ],
            Shaders = [SteamShaderCache],
        },

        new GameDefinition
        {
            Id = "avatarfop",
            Name = "Avatar: Frontiers of Pandora",
            Engine = "Snowdrop",
            SteamAppIds = [2840770],
            Config =
            [
                new(@"{Documents}\My Games\AFOP", ["graphic settings.cfg", "persistent_settings.cfg", "state.cfg", "settings.cfg", "player_input.cfg", "user.bindings"]),
            ],
            Shaders =
            [
                new(@"{Documents}\My Games\AFOP\ShaderCache", ["*"], Recursive: true),
                new(@"{Documents}\My Games\AFOP\ShaderByteCode", ["*"], Recursive: true),
                SteamShaderCache,
            ],
        },

        new GameDefinition
        {
            Id = "control",
            Name = "Control",
            Engine = "Northlight",
            SteamAppIds = [870780],
            EpicMatch = n => n.StartsWith("Control", StringComparison.OrdinalIgnoreCase),
            // The Steam version keeps renderer.ini in the game folder, other versions in Local AppData
            Config =
            [
                new(@"{Install}", ["renderer.ini"]),
                new(@"{LocalAppData}\Remedy\Control", ["renderer.ini"]),
            ],
            Shaders = [SteamShaderCache],
        },

        new GameDefinition
        {
            Id = "witcher3",
            Name = "The Witcher 3: Wild Hunt",
            Engine = "REDengine 3",
            SteamAppIds = [292030],
            EpicMatch = n => n.Contains("Witcher 3", StringComparison.OrdinalIgnoreCase),
            Config =
            [
                new(@"{Documents}\The Witcher 3", ["user.settings", "dx11user.settings", "dx12user.settings", "input.settings"]),
            ],
            Shaders = [SteamShaderCache],
        },

        new GameDefinition
        {
            Id = "dota2",
            Name = "Dota 2",
            Engine = "Source 2",
            SteamAppIds = [570],
            // Graphics are in video.txt; the .vcfg files hold the other settings and key binds
            Config =
            [
                new(@"{SteamUserdata}\570\local\cfg", ["video.txt", "machine_convars.vcfg", "user_convars_*.vcfg", "user_keys_*.vcfg"]),
            ],
            Shaders = [SteamShaderCache],
        },

        new GameDefinition
        {
            Id = "deadlock",
            Name = "Deadlock",
            Engine = "Source 2",
            SteamAppIds = [1422450],
            Config =
            [
                new(@"{SteamUserdata}\1422450\local\cfg", ["video.txt", "machine_convars.vcfg", "user_convars_*.vcfg", "user_keys_*.vcfg"]),
            ],
            Shaders = [SteamShaderCache],
        },

        new GameDefinition
        {
            Id = "warthunder",
            Name = "War Thunder",
            Engine = "Dagor Engine",
            SteamAppIds = [236390],
            // config.blk sits in the game folder. The game rewrites it when it closes.
            Config =
            [
                new(@"{Install}", ["config.blk"]),
            ],
            // The launcher makes this folder again. Only compiledShaders is touched, not the rest of the game folder.
            Shaders =
            [
                new(@"{Install}\compiledShaders", ["*"], Recursive: true),
                SteamShaderCache,
            ],
        },

        new GameDefinition
        {
            Id = "warframe",
            Name = "Warframe",
            Engine = "Evolution Engine",
            SteamAppIds = [230410],
            // EE.cfg holds the graphics settings and key binds. Close the game before restoring it.
            Config =
            [
                new(@"{LocalAppData}\Warframe", ["EE.cfg"]),
            ],
            Shaders = [SteamShaderCache],
        },

        new GameDefinition
        {
            Id = "gtavenhanced",
            Name = "Grand Theft Auto V Enhanced",
            Engine = "RAGE",
            SteamAppIds = [3240220],
            EpicMatch = n => n.Contains("Grand Theft Auto V Enhanced", StringComparison.OrdinalIgnoreCase),
            // Only settings.xml. Profiles and save games in the same folder are never included.
            Config =
            [
                new(@"{Documents}\Rockstar Games\GTAV Enhanced", ["settings.xml"]),
            ],
            Shaders =
            [
                new(@"{Documents}\Rockstar Games\GTAV Enhanced\Settings", ["*PipelineCache*", "*WarmupCache*", "*.pipelineMarkerFile"]),
                SteamShaderCache,
            ],
        },

        new GameDefinition
        {
            Id = "deltaforce",
            Name = "Delta Force",
            Engine = "Unreal Engine 5",
            SteamAppIds = [2507950],
            // GPSystemSetting.ini is left out on purpose: it holds a login token.
            Config =
            [
                new(@"{Install}\Game\DeltaForce\Saved\Config\WindowsClient",
                    ["GameUserSettings.ini", "UserSystemSettingHD.ini", "UserSystemSetting.ini"]),
            ],
            Shaders =
            [
                new(@"{Install}\Game\DeltaForce\Saved\PSOCache", ["*"], Recursive: true),
                SteamShaderCache,
            ],
        },

        new GameDefinition
        {
            Id = "destiny2",
            Name = "Destiny 2",
            Engine = "Tiger Engine",
            SteamAppIds = [1085660],
            // The game makes cvars.xml the first time it starts.
            Config =
            [
                new(@"{RoamingAppData}\Bungie\DestinyPC\prefs", ["cvars.xml"]),
            ],
            Shaders = [SteamShaderCache],
        },
    ];
}
