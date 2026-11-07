using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Text.Json;
using ZenTune.Core.Games;

namespace ZenTune.Core.Saves;

/// <summary>A game ZenTune found save files for on this PC.</summary>
public sealed class DetectedSave
{
    public required string Name { get; init; }
    public required IReadOnlyList<string> Files { get; init; }
    public long Bytes { get; init; }
    public DateTime Newest { get; init; }

    /// <summary>Added by the user with "Add a game" (not from the database).</summary>
    public bool Custom { get; init; }

    /// <summary>The save data is too big to back up here (more than 8 GB or 20,000 files).</summary>
    public bool TooLarge { get; init; }

    /// <summary>Where the game is installed (Steam or Epic), when known. Used to see when the game is running.</summary>
    public string? InstallDir { get; init; }
}

/// <summary>A game the user added by hand: a folder (or file) with its saves.</summary>
public sealed class CustomSaveGame
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
}

/// <summary>The user's own list of games, for games the database does not know.</summary>
public static class CustomSaves
{
    private static string FilePath => System.IO.Path.Combine(AppPaths.Root, "custom-saves.json");

    public static List<CustomSaveGame> Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<List<CustomSaveGame>>(File.ReadAllText(FilePath)) ?? []
                : [];
        }
        catch
        {
            return [];
        }
    }

    public static void Add(string name, string path)
    {
        var list = Load();
        list.RemoveAll(g => g.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        list.Add(new CustomSaveGame { Name = name, Path = path });
        Save(list);
    }

    public static void Remove(string name)
    {
        var list = Load();
        list.RemoveAll(g => g.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        Save(list);
    }

    private static void Save(List<CustomSaveGame> list) =>
        File.WriteAllText(FilePath, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
}

/// <summary>
/// Looks for save files of every game in the save database. It only checks if folders and files exist and reads their size;
/// nothing is changed or opened inside a game. Safe with anti-cheat.
/// </summary>
[SupportedOSPlatform("windows")]
public static class SaveScanner
{
    private const int MaxFiles = 20000;
    private const long MaxBytes = 8L * 1024 * 1024 * 1024;

    private sealed record Ctx(
        string Home, string AppData, string LocalAppData, string LocalLow, string Documents,
        string Public, string ProgramData, string WinDir, string UserName, string? SteamDir,
        HashSet<string> Forbidden);

    public static List<DetectedSave> Scan(SaveDatabase db, IReadOnlyList<CustomSaveGame> custom, CancellationToken ct = default)
    {
        var steam = new SteamLibrary();
        var epic = new EpicLibrary();
        var ctx = BuildContext(steam);

        var steamById = steam.Apps
            .GroupBy(a => a.Id)
            .ToDictionary(g => g.Key, g => g.First());
        var byFolder = steam.Apps.Concat(epic.Apps)
            .GroupBy(a => System.IO.Path.GetFileName(a.InstallDir.TrimEnd('\\')), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Key.Length > 0)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var epicByName = epic.Apps
            .GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var found = new ConcurrentBag<DetectedSave>();

        Parallel.ForEach(db.Games, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, game =>
        {
            var app = FindApp(game, steamById, byFolder, epicByName);

            var matches = new List<string>();
            foreach (var template in game.Paths)
                foreach (var path in Resolve(template, game, app, ctx))
                    if (!IsTooBroad(path, ctx)) matches.Add(path);

            if (matches.Count == 0) return;
            var save = Collect(game.Name, matches, custom: false, app?.InstallDir);
            if (save is not null) found.Add(save);
        });

        // Games added by hand
        foreach (var c in custom)
        {
            if (string.IsNullOrWhiteSpace(c.Name) || string.IsNullOrWhiteSpace(c.Path)) continue;
            if (IsTooBroad(c.Path, ctx)) continue;
            var save = Collect(c.Name, [c.Path], custom: true);
            if (save is not null)
            {
                // A custom entry wins over the database entry with the same name
                var rest = found.Where(f => !f.Name.Equals(c.Name, StringComparison.OrdinalIgnoreCase)).ToList();
                found.Clear();
                foreach (var f in rest) found.Add(f);
                found.Add(save);
            }
        }

        return found.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static string Size(long bytes)
    {
        if (bytes <= 0) return "0 MB";
        double mb = bytes / 1024d / 1024d;
        return mb >= 1024 ? $"{mb / 1024:0.0} GB" : mb >= 10 ? $"{mb:0} MB" : $"{mb:0.0} MB";
    }

    // ---------------------------------------------------------------- Helpers

    private static InstalledApp? FindApp(
        SaveGameEntry game,
        Dictionary<string, InstalledApp> steamById,
        Dictionary<string, InstalledApp> byFolder,
        Dictionary<string, InstalledApp> epicByName)
    {
        if (game.SteamId > 0 && steamById.TryGetValue(game.SteamId.ToString(), out var bySteam)) return bySteam;

        foreach (var dir in game.InstallDirs)
            if (byFolder.TryGetValue(dir, out var byDir)) return byDir;

        return epicByName.TryGetValue(game.Name, out var byName) ? byName : null;
    }

    private static Ctx BuildContext(SteamLibrary steam)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = AppPaths.LocalAppData;
        var documents = AppPaths.Documents;
        var publicDir = Environment.GetEnvironmentVariable("PUBLIC") ?? @"C:\Users\Public";
        var programData = AppPaths.ProgramData;
        var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        // Folders that are never a game's save folder on their own. A badly written database entry that points at
        // one of these would otherwise back up half the user's profile.
        var forbidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Norm(home), Norm(appData), Norm(local), Norm(Path.Combine(Path.GetDirectoryName(local) ?? local, "LocalLow")),
            Norm(documents), Norm(publicDir), Norm(programData), Norm(winDir),
            Norm(Path.Combine(home, "Saved Games")), Norm(Path.Combine(home, "Desktop")), Norm(Path.Combine(home, "Downloads")),
            Norm(Path.Combine(home, "Pictures")), Norm(Path.Combine(home, "Videos")), Norm(Path.Combine(home, "Music")),
            Norm(Path.Combine(documents, "My Games")),
            Norm(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)),
            Norm(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)),
        };
        if (steam.SteamDir is not null) forbidden.Add(Norm(steam.SteamDir));

        return new Ctx(home, appData, local, Path.Combine(home, "AppData", "LocalLow"), documents,
            publicDir, programData, winDir, Environment.UserName, steam.SteamDir, forbidden);
    }

    private static string Norm(string path) => path.Replace('/', '\\').TrimEnd('\\');

    private static bool IsTooBroad(string path, Ctx ctx)
    {
        var n = Norm(path);
        if (n.Length <= 2) return true; // a drive root such as C:
        return ctx.Forbidden.Contains(n);
    }

    /// <summary>Turns a database path with placeholders into the real paths that exist on this PC.</summary>
    private static IEnumerable<string> Resolve(string template, SaveGameEntry game, InstalledApp? app, Ctx ctx)
    {
        var p = template.Replace('/', '\\');

        // Paths inside the game's install folder need to know where the game is installed
        if (p.Contains("<base>") || p.Contains("<root>") || p.Contains("<game>"))
        {
            if (app is null) yield break;

            var installDir = app.InstallDir.TrimEnd('\\');
            string root;
            if (p.StartsWith(@"<root>\userdata", StringComparison.OrdinalIgnoreCase) && ctx.SteamDir is not null)
                root = ctx.SteamDir;
            else if (app.Store == GameStore.Steam && app.LibraryDir.Length > 0)
                root = app.LibraryDir;
            else
                root = Path.GetDirectoryName(installDir) ?? "";
            if (root.Length == 0) yield break;

            p = p.Replace("<base>", installDir)
                 .Replace("<root>", root)
                 .Replace("<game>", Path.GetFileName(installDir));
        }

        var storeGameId = app?.Id ?? (game.SteamId > 0 ? game.SteamId.ToString() : "*");

        p = p.Replace("<home>", ctx.Home)
             .Replace("<winAppData>", ctx.AppData)
             .Replace("<winLocalAppData>", ctx.LocalAppData)
             .Replace("<winLocalAppDataLow>", ctx.LocalLow)
             .Replace("<winDocuments>", ctx.Documents)
             .Replace("<winPublic>", ctx.Public)
             .Replace("<winProgramData>", ctx.ProgramData)
             .Replace("<winDir>", ctx.WinDir)
             .Replace("<osUserName>", ctx.UserName)
             .Replace("<storeUserId>", "*")
             .Replace("<storeGameId>", storeGameId);

        // Anything left (for example Linux-only placeholders) can't be resolved here
        if (p.Contains('<')) yield break;

        foreach (var match in SaveGlob.Expand(p))
            yield return match;
    }

    /// <summary>Lists the files behind the matches: a file counts as itself, a folder as everything inside it.</summary>
    private static DetectedSave? Collect(string name, List<string> matches, bool custom, string? installDir = null)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint, // never follow links out of the save folder
        };

        // The whole ZenTune-backup folder (every PC) and the folder older versions used
        var ownBackups = new[] { BackupLocation.BackupFolder, BackupLocation.LegacySaves }
            .Select(p => p.TrimEnd('\\', '/') + "\\")
            .ToArray();
        bool tooMany = false;

        foreach (var match in matches)
        {
            try
            {
                if (File.Exists(match))
                {
                    files.Add(match);
                }
                else if (Directory.Exists(match))
                {
                    foreach (var f in Directory.EnumerateFiles(match, "*", options))
                    {
                        files.Add(f);
                        if (files.Count > MaxFiles) { tooMany = true; break; }
                    }
                }
            }
            catch { /* unreadable folder, skip it */ }

            if (tooMany) break;
        }

        // ZenTune's own backups are never part of a game's saves
        files.RemoveWhere(f => ownBackups.Any(b => f.StartsWith(b, StringComparison.OrdinalIgnoreCase)));
        if (files.Count == 0) return null;

        long bytes = 0;
        var newest = DateTime.MinValue;
        foreach (var f in files)
        {
            try
            {
                var info = new FileInfo(f);
                bytes += info.Length;
                if (info.LastWriteTime > newest) newest = info.LastWriteTime;
            }
            catch { /* file vanished */ }
        }

        return new DetectedSave
        {
            Name = name,
            Files = files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList(),
            Bytes = bytes,
            Newest = newest == DateTime.MinValue ? DateTime.Now : newest,
            Custom = custom,
            TooLarge = tooMany || bytes > MaxBytes,
            InstallDir = installDir,
        };
    }
}
