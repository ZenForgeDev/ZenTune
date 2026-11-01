using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32;

namespace ZenTune.Core.Games;

public enum GameStore { Steam, Epic }

/// <summary>A game a store says is installed.</summary>
public sealed record InstalledApp(
    GameStore Store,
    string Id,
    string Name,
    string InstallDir,
    string LibraryDir,
    bool FullyInstalled,
    long SizeBytes,
    string? CoverImage);

/// <summary>Reads Steam's own library files. Nothing is changed.</summary>
[SupportedOSPlatform("windows")]
public sealed class SteamLibrary
{
    private const long SteamId64Base = 76561197960265728;

    public string? SteamDir { get; }
    public IReadOnlyList<string> Libraries { get; } = [];
    public IReadOnlyList<InstalledApp> Apps { get; } = [];

    /// <summary>Steam\userdata\&lt;id&gt; folders, the most recently signed-in account first.</summary>
    public IReadOnlyList<string> UserDataDirs { get; } = [];

    public bool Found => SteamDir is not null;

    public SteamLibrary()
    {
        SteamDir = FindSteam();
        if (SteamDir is null) return;

        Libraries = FindLibraries(SteamDir);
        Apps = Libraries.SelectMany(ReadManifests).ToList();
        UserDataDirs = FindUserData(SteamDir);
    }

    private static string? FindSteam()
    {
        string? path = null;
        try
        {
            using var hkcu = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            path = hkcu?.GetValue("SteamPath") as string;

            if (string.IsNullOrWhiteSpace(path))
            {
                using var hklm = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
                path = hklm?.GetValue("InstallPath") as string;
            }
        }
        catch { /* fall through to the default folder */ }

        if (string.IsNullOrWhiteSpace(path))
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");

        path = Path.GetFullPath(path.Replace('/', '\\'));
        return Directory.Exists(Path.Combine(path, "steamapps")) ? path : null;
    }

    private static List<string> FindLibraries(string steamDir)
    {
        var result = new List<string> { steamDir };

        var vdf = VdfNode.Load(Path.Combine(steamDir, "steamapps", "libraryfolders.vdf"))
               ?? VdfNode.Load(Path.Combine(steamDir, "config", "libraryfolders.vdf"));

        var folders = vdf?["libraryfolders"];
        if (folders is not null)
        {
            foreach (var entry in folders.Children.Values)
            {
                // New format: "0" { "path" "D:\\SteamLibrary" }. Old format: "1" "D:\\SteamLibrary"
                var p = entry.Value ?? entry.Get("path");
                if (p.Length == 0) continue;

                try
                {
                    var full = Path.GetFullPath(p);
                    if (Directory.Exists(Path.Combine(full, "steamapps")) &&
                        !result.Any(r => r.Equals(full, StringComparison.OrdinalIgnoreCase)))
                        result.Add(full);
                }
                catch { /* bad path in the file */ }
            }
        }

        return result;
    }

    private IEnumerable<InstalledApp> ReadManifests(string library)
    {
        var steamapps = Path.Combine(library, "steamapps");
        string[] files;
        try { files = Directory.GetFiles(steamapps, "appmanifest_*.acf"); }
        catch { yield break; }

        foreach (var file in files)
        {
            var state = VdfNode.Load(file)?["AppState"];
            if (state is null) continue;

            var appId = state.Get("appid");
            var name = state.Get("name");
            var dir = state.Get("installdir");
            if (appId.Length == 0 || dir.Length == 0) continue;
            if (IsTool(appId, name)) continue;

            _ = int.TryParse(state.Get("StateFlags"), out int flags);
            _ = long.TryParse(state.Get("SizeOnDisk"), out long size);

            // StateFlags bit 4 = fully installed (a pending update keeps it). Without it the game is still downloading.
            bool complete = (flags & 4) != 0;

            yield return new InstalledApp(
                GameStore.Steam,
                appId,
                name.Length > 0 ? name : $"Steam app {appId}",
                Path.Combine(steamapps, "common", dir),
                library,
                complete,
                size,
                FindCover(appId));
        }
    }

    /// <summary>Proton, runtimes and redistributables are not games.</summary>
    private static bool IsTool(string appId, string name) =>
        appId is "228980" or "1070560" or "1391110" or "1628350" or "250820" ||
        name.StartsWith("Proton", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Steam Linux Runtime", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Redistributable", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("SteamVR", StringComparison.OrdinalIgnoreCase);

    /// <summary>The tall cover art Steam already downloaded for its library view.</summary>
    private string? FindCover(string appId)
    {
        if (SteamDir is null) return null;
        var cache = Path.Combine(SteamDir, "appcache", "librarycache");

        try
        {
            // Old layout: librarycache\730_library_600x900.jpg
            var flat = Path.Combine(cache, $"{appId}_library_600x900.jpg");
            if (File.Exists(flat)) return flat;

            // New layout (2024+): librarycache\730\library_600x900.jpg, sometimes inside a hashed subfolder
            var folder = Path.Combine(cache, appId);
            if (Directory.Exists(folder))
            {
                var cover = Directory.EnumerateFiles(folder, "library_600x900*.jpg", SearchOption.AllDirectories).FirstOrDefault()
                         ?? Directory.EnumerateFiles(folder, "library_capsule*.jpg", SearchOption.AllDirectories).FirstOrDefault();
                if (cover is not null) return cover;
            }
        }
        catch { /* no cover is fine */ }

        return null;
    }

    private static List<string> FindUserData(string steamDir)
    {
        var userdata = Path.Combine(steamDir, "userdata");
        if (!Directory.Exists(userdata)) return [];

        var dirs = Directory.GetDirectories(userdata)
            .Where(d => long.TryParse(Path.GetFileName(d), out var id) && id > 0)
            .ToList();

        // Put the account that signed in last first
        string? recent = null;
        var users = VdfNode.Load(Path.Combine(steamDir, "config", "loginusers.vdf"))?["users"];
        if (users is not null)
        {
            var best = users.Children
                .OrderByDescending(u => u.Value.Get("MostRecent") == "1")
                .ThenByDescending(u => long.TryParse(u.Value.Get("Timestamp"), out var t) ? t : 0)
                .FirstOrDefault();

            if (best.Key is not null && long.TryParse(best.Key, out var id64) && id64 > SteamId64Base)
                recent = (id64 - SteamId64Base).ToString();
        }

        return dirs
            .OrderByDescending(d => Path.GetFileName(d) == recent)
            .ThenByDescending(d => Directory.GetLastWriteTimeUtc(d))
            .ToList();
    }

    /// <summary>All steamapps\shadercache\&lt;appid&gt; folders whose game is no longer installed.</summary>
    public IEnumerable<(string AppId, string Folder)> OrphanShaderCaches()
    {
        var installed = new HashSet<string>(Apps.Select(a => a.Id));
        // Tools are filtered out of Apps, but their caches are not orphans either
        foreach (var lib in Libraries)
            foreach (var f in SafeFiles(Path.Combine(lib, "steamapps"), "appmanifest_*.acf"))
                installed.Add(Path.GetFileNameWithoutExtension(f).Replace("appmanifest_", ""));

        foreach (var lib in Libraries)
        {
            var root = Path.Combine(lib, "steamapps", "shadercache");
            foreach (var dir in SafeDirs(root))
            {
                var id = Path.GetFileName(dir);
                if (id.All(char.IsDigit) && !installed.Contains(id))
                    yield return (id, dir);
            }
        }
    }

    /// <summary>
    /// Folders in steamapps\common that no installed Steam app uses any more. They are left behind
    /// when a game is uninstalled with mods, saves or downloads inside, or after a Windows reinstall.
    /// </summary>
    public IEnumerable<(string Name, string Folder)> LeftoverGameFolders()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Steamworks Shared", "Steam Controller Configs", "SteamVR" };
        foreach (var lib in Libraries)
        {
            foreach (var f in SafeFiles(Path.Combine(lib, "steamapps"), "appmanifest_*.acf"))
            {
                var dir = VdfNode.Load(f)?["AppState"]?.Get("installdir");
                if (!string.IsNullOrEmpty(dir)) used.Add(dir);
            }
        }

        foreach (var lib in Libraries)
        {
            foreach (var dir in SafeDirs(Path.Combine(lib, "steamapps", "common")))
            {
                var name = Path.GetFileName(dir);
                if (!used.Contains(name))
                    yield return (name, dir);
            }
        }
    }

    private static string[] SafeFiles(string dir, string pattern)
    {
        try { return Directory.Exists(dir) ? Directory.GetFiles(dir, pattern) : []; }
        catch { return []; }
    }

    private static string[] SafeDirs(string dir)
    {
        try { return Directory.Exists(dir) ? Directory.GetDirectories(dir) : []; }
        catch { return []; }
    }
}

/// <summary>Reads the Epic Games Launcher's install manifests. Nothing is changed.</summary>
public sealed class EpicLibrary
{
    public IReadOnlyList<InstalledApp> Apps { get; } = [];
    public bool Found { get; }

    public EpicLibrary()
    {
        var dir = Path.Combine(AppPaths.ProgramData, "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(dir)) return;
        Found = true;

        var apps = new List<InstalledApp>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.item"))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var root = doc.RootElement;

                string Str(string key) => root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                bool Bool(string key) => root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;
                long Long(string key) => root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : 0;

                var name = Str("DisplayName");
                var install = Str("InstallLocation");
                if (name.Length == 0 || install.Length == 0) continue;

                apps.Add(new InstalledApp(
                    GameStore.Epic,
                    Str("AppName"),
                    name,
                    install.Replace('/', '\\'),
                    "",
                    !Bool("bIsIncompleteInstall") && Directory.Exists(install),
                    Long("InstallSize"),
                    null));
            }
            catch { /* skip broken manifests */ }
        }
        Apps = apps;
    }
}
