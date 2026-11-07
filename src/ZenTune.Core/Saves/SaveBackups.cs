using System.Text.Json;
using System.Text.Json.Serialization;
using ZenTune.Core.Games;

namespace ZenTune.Core.Saves;

public sealed class SaveBackupFile
{
    /// <summary>Where the file came from. Restore puts it back here.</summary>
    public string Original { get; set; } = "";

    /// <summary>The file's name inside the backup folder.</summary>
    public string Stored { get; set; } = "";

    public long Bytes { get; set; }
}

public sealed class SaveBackup
{
    public string GameName { get; set; } = "";
    public DateTime Created { get; set; }
    public string Label { get; set; } = "";
    public bool Automatic { get; set; }
    public long TotalBytes { get; set; }

    /// <summary>Files that could not be copied (for example locked by a running game).</summary>
    public int Skipped { get; set; }

    public List<SaveBackupFile> Files { get; set; } = [];

    /// <summary>The backup's own folder. Not saved in the manifest.</summary>
    [JsonIgnore]
    public string Folder { get; set; } = "";

    /// <summary>Name of the PC this backup sits under (its folder name). Not saved in the manifest.</summary>
    [JsonIgnore]
    public string Pc { get; set; } = "";

    /// <summary>True for a backup made on another PC (or under an old PC name).</summary>
    [JsonIgnore]
    public bool FromOtherPc => Pc.Length > 0 && !string.Equals(Pc, BackupLocation.PcName, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Copies a game's save files to &lt;backup folder&gt;\ZenTune-backup\&lt;PC name&gt;\Saves\&lt;game&gt;\&lt;time&gt; (see BackupLocation) and puts them back where they came from.
/// Every restore first backs up what is there now, so a restore can be undone too.
/// </summary>
public static class SaveBackups
{
    private const string ManifestName = "backup.json";
    private const int KeepAutomatic = 15;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>This PC's Saves folder: &lt;backup folder&gt;\ZenTune-backup\&lt;PC name&gt;\Saves</summary>
    public static string Root
    {
        get
        {
            var dir = BackupLocation.SavesRoot;
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string GameFolder(string gameName) => Path.Combine(Root, SafeName(gameName));

    public static string Size(long bytes) => FileOps.Size(bytes);

    /// <summary>Backs up the given files. Throws if not a single file could be copied.</summary>
    public static SaveBackup Create(string gameName, IReadOnlyList<string> files, string label, bool automatic = false)
    {
        if (files.Count == 0)
            throw new InvalidOperationException("No save files found for this game.");

        var folder = Path.Combine(GameFolder(gameName), DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(folder);

        var backup = new SaveBackup
        {
            GameName = gameName,
            Created = DateTime.Now,
            Label = label,
            Automatic = automatic,
            Folder = folder,
        };

        int n = 0;
        foreach (var file in files)
        {
            var stored = $"{n++:00000}_{SafeName(Path.GetFileName(file))}";
            try
            {
                // Share read and write so a save that a game still has open can be copied too
                using (var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var target = File.Create(Path.Combine(folder, stored)))
                    source.CopyTo(target);

                backup.Files.Add(new SaveBackupFile { Original = file, Stored = stored, Bytes = FileOps.SizeOf(file) });
                backup.TotalBytes += FileOps.SizeOf(file);
            }
            catch
            {
                backup.Skipped++;
                try { File.Delete(Path.Combine(folder, stored)); } catch { /* leftover is harmless */ }
            }
        }

        if (backup.Files.Count == 0)
        {
            try { Directory.Delete(folder, recursive: true); } catch { /* nothing to clean */ }
            throw new InvalidOperationException("None of the save files could be copied. Are they in use or protected?");
        }

        File.WriteAllText(Path.Combine(folder, ManifestName), JsonSerializer.Serialize(backup, Json));
        Prune(gameName);
        return backup;
    }

    /// <summary>All backups of one game, newest first.</summary>
    public static List<SaveBackup> List(string gameName) => ReadFolder(GameFolder(gameName), BackupLocation.PcName);

    /// <summary>All backups of all games from every PC in the backup folder, grouped by game name.</summary>
    public static Dictionary<string, List<SaveBackup>> ListAll()
    {
        var result = new Dictionary<string, List<SaveBackup>>(StringComparer.OrdinalIgnoreCase);

        foreach (var pcDir in SafeDirs(BackupLocation.BackupFolder))
        {
            var pc = Path.GetFileName(pcDir);
            foreach (var gameDir in SafeDirs(Path.Combine(pcDir, "Saves")))
            {
                foreach (var backup in ReadFolder(gameDir, pc))
                {
                    if (backup.GameName.Length == 0) continue;
                    if (!result.TryGetValue(backup.GameName, out var list))
                        result[backup.GameName] = list = [];
                    list.Add(backup);
                }
            }
        }

        foreach (var list in result.Values)
            list.Sort((a, b) => b.Created.CompareTo(a.Created));
        return result;
    }

    /// <summary>
    /// Puts the files from a backup back where they came from, over the current ones.
    /// First makes a safety backup of the current files; if that fails nothing is changed.
    /// </summary>
    public static (int Restored, int Failed) Restore(SaveBackup backup)
    {
        var current = backup.Files.Select(f => f.Original).Where(File.Exists).ToList();
        if (current.Count > 0)
        {
            try
            {
                Create(backup.GameName, current, $"Before restoring {backup.Created:d MMM HH:mm}", automatic: true);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Could not make a safety backup of the current save, so nothing was restored. {ex.Message}");
            }
        }

        int restored = 0, failed = 0;
        foreach (var f in backup.Files)
        {
            try
            {
                FileOps.CopyOver(Path.Combine(backup.Folder, f.Stored), f.Original);
                restored++;
            }
            catch
            {
                failed++;
            }
        }
        return (restored, failed);
    }

    public static void Delete(SaveBackup backup)
    {
        if (Directory.Exists(backup.Folder))
            Directory.Delete(backup.Folder, recursive: true);
    }

    // ---------------------------------------------------------------- Helpers

    private static List<SaveBackup> ReadFolder(string gameDir, string pc = "")
    {
        var list = new List<SaveBackup>();
        foreach (var dir in SafeDirs(gameDir))
        {
            // A copy that is still being made (or was stopped halfway) is not a backup yet
            if (dir.EndsWith(".copying", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var backup = JsonSerializer.Deserialize<SaveBackup>(File.ReadAllText(Path.Combine(dir, ManifestName)), Json);
                if (backup is null) continue;
                backup.Folder = dir;
                backup.Pc = pc;
                list.Add(backup);
            }
            catch { /* not a backup folder */ }
        }
        return list.OrderByDescending(b => b.Created).ToList();
    }

    /// <summary>Keeps every backup the user made, and the newest automatic ones.</summary>
    private static void Prune(string gameName)
    {
        foreach (var old in List(gameName).Where(b => b.Automatic).Skip(KeepAutomatic))
        {
            try { Delete(old); } catch { /* try again next time */ }
        }
    }

    private static string[] SafeDirs(string dir)
    {
        try { return Directory.Exists(dir) ? Directory.GetDirectories(dir) : []; }
        catch { return []; }
    }

    private static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (clean.Length == 0) clean = "game";
        return clean.Length > 80 ? clean[..80].TrimEnd() : clean;
    }
}
