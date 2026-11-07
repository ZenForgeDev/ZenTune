using System.Diagnostics;
using System.Runtime.Versioning;

namespace ZenTune.Core.Games;

/// <summary>A supported game that is installed, with the files ZenTune found for it.</summary>
public sealed class DetectedGame
{
    public required GameDefinition Definition { get; init; }
    public required InstalledApp App { get; init; }

    /// <summary>Settings files that exist right now.</summary>
    public IReadOnlyList<string> ConfigFiles { get; init; } = [];

    /// <summary>Where the settings live (first existing folder), or the expected folder if the game never ran.</summary>
    public string? ConfigFolder { get; init; }

    public IReadOnlyList<string> ShaderFiles { get; init; } = [];
    public long ShaderBytes { get; init; }

    /// <summary>Process names (without .exe) that mean the game is running.</summary>
    public IReadOnlyList<string> ProcessNames { get; init; } = [];

    public string Id => Definition.Id;
    public string Name => Definition.Name;
}

public sealed class GameScanResult
{
    public required IReadOnlyList<DetectedGame> Supported { get; init; }
    public required IReadOnlyList<GameDefinition> NotInstalled { get; init; }
    public required IReadOnlyList<InstalledApp> OtherGames { get; init; }
    public required IReadOnlyList<OrphanCache> OldShaderCaches { get; init; }
    public bool SteamFound { get; init; }
    public bool EpicFound { get; init; }
}

public enum LeftoverKind { ShaderCache, GameFolder }

/// <summary>Something a Steam game left behind after it was uninstalled: a shader cache or its game folder.</summary>
public sealed class OrphanCache(string appId, string folder, long bytes, LeftoverKind kind = LeftoverKind.ShaderCache)
{
    public LeftoverKind Kind { get; } = kind;

    /// <summary>Steam app id for shader caches, the folder name for game folders.</summary>
    public string AppId { get; } = appId;
    public string Folder { get; } = folder;
    public long Bytes { get; } = bytes;
    public string? Name { get; set; }
}

[SupportedOSPlatform("windows")]
public static class GameScanner
{
    public static GameScanResult Scan()
    {
        var steam = new SteamLibrary();
        var epic = new EpicLibrary();

        var supported = new List<DetectedGame>();
        var used = new HashSet<InstalledApp>();

        foreach (var def in GameCatalog.All)
        {
            var app = FindApp(def, steam, epic);
            if (app is null) continue;
            var detected = Inspect(def, app, steam);
            if (def.HideWithoutSettings && detected.ConfigFiles.Count == 0) continue;
            used.Add(app);
            supported.Add(detected);
        }

        var notInstalled = GameCatalog.All.Where(d => supported.All(s => s.Definition != d)).ToList();

        var others = steam.Apps.Concat(epic.Apps)
            .Where(a => !used.Contains(a) && !GameCatalog.All.Any(d => Matches(d, a)))
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var orphans = steam.OrphanShaderCaches()
            .Select(o => new OrphanCache(o.AppId, o.Folder, FileOps.FolderSize(o.Folder)))
            .Concat(steam.LeftoverGameFolders()
                .Select(f => new OrphanCache(f.Name, f.Folder, FileOps.FolderSize(f.Folder), LeftoverKind.GameFolder) { Name = f.Name }))
            .Where(o => o.Bytes > 0)
            .OrderByDescending(o => o.Bytes)
            .ToList();

        return new GameScanResult
        {
            Supported = supported,
            NotInstalled = notInstalled,
            OtherGames = others,
            OldShaderCaches = orphans,
            SteamFound = steam.Found,
            EpicFound = epic.Found,
        };
    }

    /// <summary>Re-reads files for one game (after a backup, restore or shader reset).</summary>
    public static DetectedGame Refresh(DetectedGame game) => Inspect(game.Definition, game.App, new SteamLibrary());

    private static InstalledApp? FindApp(GameDefinition def, SteamLibrary steam, EpicLibrary epic)
    {
        // Prefer a fully installed copy, then Steam over Epic
        return steam.Apps.Concat(epic.Apps)
            .Where(a => Matches(def, a))
            .OrderByDescending(a => a.FullyInstalled)
            .ThenBy(a => a.Store)
            .FirstOrDefault();
    }

    private static bool Matches(GameDefinition def, InstalledApp app) => app.Store switch
    {
        GameStore.Steam => def.SteamAppIds.Contains(int.TryParse(app.Id, out var id) ? id : -1) ||
                           def.SteamFolderHints.Any(h => Path.GetFileName(app.InstallDir).StartsWith(h, StringComparison.OrdinalIgnoreCase)),
        GameStore.Epic => def.EpicMatch(app.Name),
        _ => false,
    };

    private static DetectedGame Inspect(GameDefinition def, InstalledApp app, SteamLibrary steam)
    {
        var ctx = new PathContext(app, steam);

        // Use the first folder that really has settings files. For Steam userdata that is the most recent account.
        var configFiles = new List<string>();
        string? configFolder = null;
        foreach (var loc in def.Config)
        {
            foreach (var folder in ctx.Expand(loc.Folder))
            {
                var found = FileOps.Find(folder, loc.Patterns, loc.Recursive).ToList();
                if (found.Count == 0) continue;
                configFolder ??= folder;
                configFiles.AddRange(found);
                break;
            }
        }
        configFolder ??= def.Config.SelectMany(l => ctx.Expand(l.Folder)).FirstOrDefault(Directory.Exists);

        var shaderFiles = def.Shaders
            .SelectMany(loc => ctx.Expand(loc.Folder).SelectMany(f => FileOps.Find(f, loc.Patterns, loc.Recursive)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new DetectedGame
        {
            Definition = def,
            App = app,
            ConfigFiles = configFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            ConfigFolder = configFolder,
            ShaderFiles = shaderFiles,
            ShaderBytes = shaderFiles.Sum(FileOps.SizeOf),
            ProcessNames = FindExecutables(app.InstallDir),
        };
    }

    /// <summary>Names of the .exe files in the game folder. Used only to see if the game is running.</summary>
    internal static List<string> FindExecutables(string installDir)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(installDir)) return [];

        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true };
            foreach (var exe in Directory.EnumerateFiles(installDir, "*.exe", options).Take(300))
            {
                var name = Path.GetFileNameWithoutExtension(exe);
                if (IsHelper(name)) continue;
                names.Add(name);
            }
        }
        catch { /* best effort */ }

        return names.ToList();
    }

    private static bool IsHelper(string name)
    {
        string[] helpers = ["CrashReport", "UnityCrashHandler", "vc_redist", "vcredist", "dxsetup", "DXSETUP", "UE4PrereqSetup", "UEPrereqSetup",
                            "EasyAntiCheat", "start_protected_game", "Uninstall", "unins", "crashpad", "QtWebEngineProcess", "dotNetFx", "oalinst"];
        return helpers.Any(h => name.StartsWith(h, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// True when one of the game's programs is running. Only reads the process list
    /// (like Task Manager does) and never opens the game's process, so it is safe with anti-cheat.
    /// </summary>
    public static bool IsRunning(DetectedGame game)
    {
        try
        {
            var running = new HashSet<string>(Process.GetProcesses().Select(p => { try { return p.ProcessName; } catch { return ""; } }),
                StringComparer.OrdinalIgnoreCase);
            return game.ProcessNames.Any(running.Contains);
        }
        catch
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- Path tokens

    private sealed class PathContext(InstalledApp app, SteamLibrary steam)
    {
        public IEnumerable<string> Expand(string template)
        {
            foreach (var path in ExpandTokens(template))
                foreach (var expanded in ExpandWildcards(path))
                    yield return expanded;
        }

        /// <summary>
        /// A folder name with * (for example "Rainbow Six - Siege\*") matches every subfolder,
        /// for games that keep settings in a folder named after your account id.
        /// </summary>
        private static IEnumerable<string> ExpandWildcards(string path)
        {
            int star = path.IndexOf('*');
            if (star < 0)
            {
                yield return path;
                yield break;
            }

            int start = path.LastIndexOf('\\', star);
            int end = path.IndexOf('\\', star);
            if (start < 0) yield break;

            var parent = path[..start];
            var pattern = end < 0 ? path[(start + 1)..] : path[(start + 1)..end];
            var rest = end < 0 ? "" : path[end..];

            string[] dirs;
            try { dirs = Directory.Exists(parent) ? Directory.GetDirectories(parent, pattern) : []; }
            catch { yield break; }

            // Newest first, so the account used last wins
            foreach (var dir in dirs.OrderByDescending(Directory.GetLastWriteTimeUtc))
                foreach (var expanded in ExpandWildcards(dir + rest))
                    yield return expanded;
        }

        private IEnumerable<string> ExpandTokens(string template)
        {
            var path = template
                .Replace("{LocalAppData}", AppPaths.LocalAppData)
                .Replace("{RoamingAppData}", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData))
                .Replace("{SavedGames}", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Saved Games"))
                .Replace("{Documents}", AppPaths.Documents)
                .Replace("{Install}", app.InstallDir)
                .Replace("{AppId}", app.Id);

            if (path.Contains("{Library}"))
            {
                if (app.Store != GameStore.Steam || app.LibraryDir.Length == 0) yield break;
                path = path.Replace("{Library}", app.LibraryDir);
            }

            if (path.Contains("{SteamUserdata}"))
            {
                foreach (var user in steam.UserDataDirs)
                    yield return path.Replace("{SteamUserdata}", user);
                yield break;
            }

            yield return path;
        }
    }
}
