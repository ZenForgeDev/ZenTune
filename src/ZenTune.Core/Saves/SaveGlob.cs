namespace ZenTune.Core.Saves;

/// <summary>
/// Finds the files and folders that match a path with * and ? in it, for example
/// C:\Users\Me\AppData\Local\Game\Saved\*\SaveGames. A "**" folder matches any depth (up to 6 levels).
/// Only looks, never changes anything.
/// </summary>
internal static class SaveGlob
{
    public static IEnumerable<string> Expand(string pattern)
    {
        pattern = pattern.Replace('/', '\\');

        string root;
        try { root = Path.GetPathRoot(pattern) ?? ""; }
        catch { yield break; }

        // Only normal drive paths such as C:\...
        if (root.Length < 3 || root.StartsWith(@"\\", StringComparison.Ordinal)) yield break;

        var segments = pattern[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        foreach (var match in Walk(root, segments, 0, 0))
            yield return match;
    }

    private static IEnumerable<string> Walk(string current, string[] segments, int index, int depth)
    {
        if (index == segments.Length)
        {
            if (File.Exists(current) || Directory.Exists(current)) yield return current;
            yield break;
        }

        var segment = segments[index];
        bool last = index == segments.Length - 1;

        if (segment == "**")
        {
            foreach (var match in Walk(current, segments, index + 1, depth)) yield return match;

            if (depth < 6)
            {
                foreach (var dir in Entries(current, "*", directoriesOnly: true))
                    foreach (var match in Walk(dir, segments, index, depth + 1))
                        yield return match;
            }
            yield break;
        }

        if (segment.IndexOfAny(['*', '?']) >= 0)
        {
            foreach (var entry in Entries(current, segment, directoriesOnly: !last))
                foreach (var match in Walk(entry, segments, index + 1, depth))
                    yield return match;
            yield break;
        }

        var next = Path.Combine(current, segment);
        bool exists = last ? File.Exists(next) || Directory.Exists(next) : Directory.Exists(next);
        if (!exists) yield break;

        foreach (var match in Walk(next, segments, index + 1, depth))
            yield return match;
    }

    private static string[] Entries(string folder, string pattern, bool directoriesOnly)
    {
        try
        {
            return directoriesOnly
                ? Directory.GetDirectories(folder, pattern)
                : Directory.GetFileSystemEntries(folder, pattern);
        }
        catch
        {
            return [];
        }
    }
}
