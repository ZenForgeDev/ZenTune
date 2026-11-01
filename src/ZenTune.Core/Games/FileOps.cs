namespace ZenTune.Core.Games;

internal static class FileOps
{
    public static IEnumerable<string> Find(string folder, string[] patterns, bool recursive)
    {
        if (!Directory.Exists(folder)) return [];

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = recursive,
            IgnoreInaccessible = true,
            MatchCasing = MatchCasing.CaseInsensitive,
        };

        try
        {
            // Never touch save games (.sav). The only exception is a .sav file named exactly, without
            // wildcards, for games that keep their settings in one (Dawnwalker's RebelSettings.sav).
            return patterns
                .SelectMany(p => Directory.EnumerateFiles(folder, p, options)
                    .Where(f => !f.EndsWith(".sav", StringComparison.OrdinalIgnoreCase) ||
                                (!p.Contains('*') && !p.Contains('?') && Path.GetFileName(f).Equals(p, StringComparison.OrdinalIgnoreCase))))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    public static long SizeOf(string file)
    {
        try { return new FileInfo(file).Length; }
        catch { return 0; }
    }

    public static long FolderSize(string folder)
    {
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            return Directory.EnumerateFiles(folder, "*", options).Sum(SizeOf);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Deletes files, skipping the ones that are locked. Returns bytes freed and how many were skipped.</summary>
    public static (long Freed, int Skipped) DeleteFiles(IEnumerable<string> files)
    {
        long freed = 0;
        int skipped = 0;
        foreach (var f in files)
        {
            try
            {
                var info = new FileInfo(f);
                if (!info.Exists) continue;
                long size = info.Length;
                if (info.IsReadOnly) info.IsReadOnly = false;
                info.Delete();
                freed += size;
            }
            catch
            {
                skipped++;
            }
        }
        return (freed, skipped);
    }

    /// <summary>Removes folders that became empty after deleting files (deepest first). The root itself is kept.</summary>
    public static void RemoveEmptyFolders(string root)
    {
        if (!Directory.Exists(root)) return;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(dir).Any())
                        Directory.Delete(dir);
                }
                catch { /* in use */ }
            }
        }
        catch { /* best effort */ }
    }

    /// <summary>Copies a file over another and keeps the destination's read-only flag the way it was.</summary>
    public static void CopyOver(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        bool wasReadOnly = false;
        var dest = new FileInfo(destination);
        if (dest.Exists && dest.IsReadOnly)
        {
            wasReadOnly = true;
            dest.IsReadOnly = false;
        }

        File.Copy(source, destination, overwrite: true);

        if (wasReadOnly)
            new FileInfo(destination).IsReadOnly = true;
    }

    public static string Size(long bytes)
    {
        if (bytes <= 0) return "0 MB";
        double mb = bytes / 1024d / 1024d;
        return mb >= 1024 ? $"{mb / 1024:0.0} GB" : mb >= 10 ? $"{mb:0} MB" : $"{mb:0.0} MB";
    }
}
