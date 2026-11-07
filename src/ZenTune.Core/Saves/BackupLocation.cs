namespace ZenTune.Core.Saves;

/// <summary>
/// Where ZenTune keeps backups: &lt;base&gt;\ZenTune-backup\&lt;PC name&gt;\Saves\&lt;game&gt;\&lt;time&gt;.
/// The base folder is Documents unless the user picks another one (for example a OneDrive folder).
/// Every PC gets its own folder, so backups from several PCs can live in the same cloud folder without mixing.
/// </summary>
public static class BackupLocation
{
    private static string SettingFile => Path.Combine(AppPaths.Root, "backup-root.txt");
    private static string LegacyFlag => Path.Combine(AppPaths.Root, "legacy-saves-handled.txt");

    /// <summary>The folder that holds ZenTune-backup when the user has not chosen one.</summary>
    public static string DefaultBase => AppPaths.Documents;

    /// <summary>The folder the user chose (or Documents).</summary>
    public static string Base
    {
        get
        {
            try
            {
                if (File.Exists(SettingFile))
                {
                    var s = File.ReadAllText(SettingFile).Trim();
                    if (s.Length > 0) return s;
                }
            }
            catch { /* use the default */ }
            return DefaultBase;
        }
    }

    public static bool IsDefault => !File.Exists(SettingFile) || string.Equals(Full(Base), Full(DefaultBase), StringComparison.OrdinalIgnoreCase);

    /// <summary>This PC's name, cleaned so it works as a folder name.</summary>
    public static string PcName
    {
        get
        {
            var invalid = Path.GetInvalidFileNameChars();
            var clean = new string(Environment.MachineName.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
            return clean.Length == 0 ? "PC" : clean;
        }
    }

    /// <summary>&lt;base&gt;\ZenTune-backup: holds one folder per PC.</summary>
    public static string BackupFolder => Path.Combine(Base, "ZenTune-backup");

    /// <summary>&lt;base&gt;\ZenTune-backup\&lt;this PC&gt;\Saves</summary>
    public static string SavesRoot => Path.Combine(BackupFolder, PcName, "Saves");

    /// <summary>Where older ZenTune versions kept save backups: Documents\ZenTune\Saves.</summary>
    public static string LegacySaves => Path.Combine(AppPaths.Documents, "ZenTune", "Saves");

    /// <summary>Checks a folder the user typed. Returns an error message, or null when the folder can be used (it is created if needed).</summary>
    public static string? Validate(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "Type or paste a folder path.";

        string full;
        try { full = Path.GetFullPath(path); }
        catch { return "That is not a valid folder path."; }

        if (!Path.IsPathFullyQualified(path)) return @"Use the full path, for example D:\Backups or C:\Users\You\OneDrive\Backups.";

        var root = Path.GetPathRoot(full) ?? "";
        if (string.Equals(full.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            return "Choose a folder, not a whole drive.";

        try
        {
            Directory.CreateDirectory(full);
            var test = Path.Combine(full, ".zentune-write-test");
            File.WriteAllText(test, "test");
            File.Delete(test);
        }
        catch (Exception ex)
        {
            return "ZenTune cannot write to that folder. " + ex.Message;
        }
        return null;
    }

    /// <summary>Saves the base folder. Null or empty goes back to Documents.</summary>
    public static void SetBase(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                if (File.Exists(SettingFile)) File.Delete(SettingFile);
            }
            else
            {
                File.WriteAllText(SettingFile, Path.GetFullPath(path.Trim()));
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Could not save the backup folder setting. " + ex.Message);
        }
    }

    /// <summary>
    /// Copies every backup (game\time folders) from one Saves folder to another. Nothing is deleted or overwritten:
    /// a backup that already exists in the target is skipped. A backup is copied to a temporary name first, so a stop halfway never leaves half a backup.
    /// </summary>
    public static (int Copied, int Skipped, int Failed) CopyBackups(string fromSaves, string toSaves)
    {
        var from = Full(fromSaves);
        var to = Full(toSaves);
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(from)) return (0, 0, 0);

        var sep = Path.DirectorySeparatorChar.ToString();
        if (to.StartsWith(from + sep, StringComparison.OrdinalIgnoreCase) || from.StartsWith(to + sep, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The new folder is inside the old one (or the other way round), so nothing was copied.");

        int copied = 0, skipped = 0, failed = 0;
        foreach (var gameDir in Dirs(from))
        {
            foreach (var backupDir in Dirs(gameDir))
            {
                var target = Path.Combine(to, Path.GetFileName(gameDir), Path.GetFileName(backupDir));
                if (Directory.Exists(target)) { skipped++; continue; }

                var temp = target + ".copying";
                try
                {
                    if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
                    CopyDir(backupDir, temp);
                    Directory.Move(temp, target);
                    copied++;
                }
                catch
                {
                    failed++;
                    try { if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true); } catch { /* leftover is ignored by the list */ }
                }
            }
        }
        return (copied, skipped, failed);
    }

    /// <summary>True when old Documents\ZenTune\Saves backups exist and the user has not answered the offer to copy them yet.</summary>
    public static bool HasLegacyToOffer()
    {
        try
        {
            if (File.Exists(LegacyFlag)) return false;
            var legacy = Full(LegacySaves);
            if (!Directory.Exists(legacy)) return false;
            if (string.Equals(legacy, Full(SavesRoot), StringComparison.OrdinalIgnoreCase)) return false;
            return Directory.EnumerateDirectories(legacy).Any();
        }
        catch { return false; }
    }

    public static void MarkLegacyHandled()
    {
        try { File.WriteAllText(LegacyFlag, "handled"); } catch { /* the offer just shows again */ }
    }

    private static string Full(string p) => Path.GetFullPath(p).TrimEnd('\\', '/');

    private static string[] Dirs(string dir)
    {
        try { return Directory.Exists(dir) ? Directory.GetDirectories(dir) : []; }
        catch { return []; }
    }

    private static void CopyDir(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: false);
        foreach (var sub in Directory.GetDirectories(source))
            CopyDir(sub, Path.Combine(target, Path.GetFileName(sub)));
    }
}
