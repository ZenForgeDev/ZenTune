using System.Text.Json;

namespace ZenTune.Core.Games;

public sealed class BackupFile
{
    public string Original { get; set; } = "";
    public string Stored { get; set; } = "";
}

public sealed class BackupInfo
{
    public string GameId { get; set; } = "";
    public string GameName { get; set; } = "";
    public DateTime Created { get; set; }
    public string Label { get; set; } = "";
    public bool Automatic { get; set; }
    public List<BackupFile> Files { get; set; } = [];

    /// <summary>The backup's own folder. Not saved in the manifest.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Folder { get; set; } = "";
}

/// <summary>
/// Copies a game's settings files to %LOCALAPPDATA%\ZenTune\Backups\&lt;game&gt;\&lt;time&gt; and puts them back.
/// Every restore first backs up the current files, so a restore can be undone too.
/// </summary>
public static class Backups
{
    private const string ManifestName = "backup.json";
    private const int KeepAutomatic = 15;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static BackupInfo Create(DetectedGame game, string label, bool automatic = false)
    {
        if (game.ConfigFiles.Count == 0)
            throw new InvalidOperationException("No settings files found yet. Start the game once so it creates them.");

        var folder = Path.Combine(AppPaths.Backups, game.Id, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(folder);

        var info = new BackupInfo
        {
            GameId = game.Id,
            GameName = game.Name,
            Created = DateTime.Now,
            Label = label,
            Automatic = automatic,
            Folder = folder,
        };

        int n = 0;
        foreach (var file in game.ConfigFiles)
        {
            var stored = $"{n++:00}_{Path.GetFileName(file)}";
            File.Copy(file, Path.Combine(folder, stored), overwrite: true);
            info.Files.Add(new BackupFile { Original = file, Stored = stored });
        }

        File.WriteAllText(Path.Combine(folder, ManifestName), JsonSerializer.Serialize(info, Json));
        Prune(game.Id);
        return info;
    }

    public static List<BackupInfo> List(string gameId)
    {
        var root = Path.Combine(AppPaths.Backups, gameId);
        if (!Directory.Exists(root)) return [];

        var list = new List<BackupInfo>();
        foreach (var dir in Directory.GetDirectories(root))
        {
            try
            {
                var info = JsonSerializer.Deserialize<BackupInfo>(File.ReadAllText(Path.Combine(dir, ManifestName)), Json);
                if (info is null) continue;
                info.Folder = dir;
                list.Add(info);
            }
            catch { /* not a backup folder */ }
        }
        return list.OrderByDescending(b => b.Created).ToList();
    }

    /// <summary>Puts the files from a backup back in place. The game must be closed.</summary>
    public static void Restore(DetectedGame game, BackupInfo backup)
    {
        if (GameScanner.IsRunning(game))
            throw new InvalidOperationException($"Close {game.Name} first. Games overwrite their settings when they quit.");

        // Safety copy of what is there now
        if (game.ConfigFiles.Count > 0)
            Create(game, $"Before restoring {backup.Created:d MMM HH:mm}", automatic: true);

        foreach (var f in backup.Files)
            FileOps.CopyOver(Path.Combine(backup.Folder, f.Stored), f.Original);
    }

    public static void Delete(BackupInfo backup)
    {
        if (Directory.Exists(backup.Folder))
            Directory.Delete(backup.Folder, recursive: true);
    }

    /// <summary>Keeps every backup the user made, and the newest automatic ones.</summary>
    private static void Prune(string gameId)
    {
        foreach (var old in List(gameId).Where(b => b.Automatic).Skip(KeepAutomatic))
        {
            try { Delete(old); } catch { /* try again next time */ }
        }
    }
}

/// <summary>Deletes shader caches so games build fresh ones on the next start.</summary>
public static class ShaderCache
{
    public static (long Freed, int Skipped) Reset(DetectedGame game)
    {
        if (GameScanner.IsRunning(game))
            throw new InvalidOperationException($"Close {game.Name} first. The shader cache is in use while the game runs.");

        return FileOps.DeleteFiles(game.ShaderFiles);
    }

    /// <summary>
    /// Makes a game rebuild its shaders by editing one line in its settings files (see <see cref="ConfigLineEdit"/>).
    /// Backs up the settings first. Returns how many files were changed.
    /// </summary>
    public static int ForceRebuild(DetectedGame game)
    {
        var edit = game.Definition.ShaderRebuildEdit
            ?? throw new InvalidOperationException($"ZenTune can't do this for {game.Name}.");

        if (GameScanner.IsRunning(game))
            throw new InvalidOperationException($"Close {game.Name} first. It writes its settings back when it quits.");

        var nameRule = new System.Text.RegularExpressions.Regex(edit.FilePattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var lineRule = new System.Text.RegularExpressions.Regex(
            $@"^([ \t]*{System.Text.RegularExpressions.Regex.Escape(edit.Key)}@[^=\r\n]*=[ \t]*)([^/\r\n]*?)([ \t]*(//[^\r\n]*)?)(?=\r?$)",
            System.Text.RegularExpressions.RegexOptions.Multiline);

        var targets = game.ConfigFiles.Where(f => nameRule.IsMatch(Path.GetFileName(f))).ToList();
        if (targets.Count == 0)
            throw new InvalidOperationException("No settings files found yet. Start the game once so it creates them.");

        Backups.Create(game, "Before shader rebuild", automatic: true);

        int changed = 0;
        foreach (var file in targets)
        {
            var text = File.ReadAllText(file);
            if (!lineRule.IsMatch(text)) continue;

            var updated = lineRule.Replace(text, m => m.Groups[1].Value + edit.Value + m.Groups[3].Value);
            if (updated == text) continue;

            var info = new FileInfo(file);
            bool readOnly = info.IsReadOnly;
            if (readOnly) info.IsReadOnly = false;
            File.WriteAllText(file, updated, new System.Text.UTF8Encoding(false));
            if (readOnly) new FileInfo(file).IsReadOnly = true;
            changed++;
        }

        if (changed == 0)
            throw new InvalidOperationException($"The {edit.Key} line was not found, so nothing was changed. A game update may have renamed it.");
        return changed;
    }

    /// <summary>Removes a Steam shader cache folder for a game that is not installed any more.</summary>
    public static long DeleteOrphan(OrphanCache cache)
    {
        long size = FileOps.FolderSize(cache.Folder);
        try
        {
            Directory.Delete(cache.Folder, recursive: true);
            return size;
        }
        catch
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            return FileOps.DeleteFiles(Directory.EnumerateFiles(cache.Folder, "*", options)).Freed;
        }
    }

    /// <summary>The graphics driver's own caches, shared by every game.</summary>
    public static IReadOnlyList<string> DriverCacheFolders()
    {
        var local = AppPaths.LocalAppData;
        var localLow = Path.Combine(Path.GetDirectoryName(local) ?? local, "LocalLow");
        string[] candidates =
        [
            Path.Combine(localLow, "NVIDIA", "PerDriverVersion", "DXCache"),
            Path.Combine(localLow, "NVIDIA", "PerDriverVersion", "GLCache"),
            Path.Combine(local, "NVIDIA", "DXCache"),
            Path.Combine(local, "NVIDIA", "GLCache"),
            Path.Combine(local, "NVIDIA Corporation", "NV_Cache"),
            Path.Combine(local, "AMD", "DxCache"),
            Path.Combine(local, "AMD", "DxcCache"),
            Path.Combine(local, "AMD", "VkCache"),
            Path.Combine(local, "Intel", "ShaderCache"),
            Path.Combine(local, "D3DSCache"),
        ];
        return candidates.Where(Directory.Exists).ToList();
    }

    public static long DriverCacheSize() => DriverCacheFolders().Sum(FileOps.FolderSize);

    /// <summary>Clears the driver caches. Files in use (by a running game or app) are skipped.</summary>
    public static (long Freed, int Skipped) ClearDriverCache()
    {
        long freed = 0;
        int skipped = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (var folder in DriverCacheFolders())
        {
            var r = FileOps.DeleteFiles(Directory.EnumerateFiles(folder, "*", options).ToList());
            freed += r.Freed;
            skipped += r.Skipped;
            FileOps.RemoveEmptyFolders(folder);
        }
        return (freed, skipped);
    }

    public static string Size(long bytes) => FileOps.Size(bytes);
}
