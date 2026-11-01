using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ZenTune.Core.Ai;

/// <summary>
/// Share saved AI settings with a friend as a text code or a small file, and import one. Nothing here is trusted:
/// a friend's code is checked here, and every value is checked again against the game's own rules when it is used.
/// </summary>
public static class AiShare
{
    private const string Prefix = "ZENTUNE1:";
    private const int MaxSettings = 1500;
    private const int MaxJsonBytes = 2_000_000;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private sealed record ShareData(
        int V,
        string GameId,
        string GameName,
        AiMode Mode,
        string Name,
        string Gpu,
        string Screen,
        string Model,
        string Summary,
        bool Manual,
        List<SavedAiSetting> Settings);

    /// <summary>One line of text that holds the whole profile. It is safe to paste into a chat.</summary>
    public static string Export(SavedAiProfile p)
    {
        var data = new ShareData(1, p.GameId, p.GameName, p.Mode, p.Name, p.Gpu, p.Screen, p.Model, p.Summary, p.Manual, p.Settings);
        var raw = JsonSerializer.SerializeToUtf8Bytes(data, Json);

        using var output = new MemoryStream();
        using (var gz = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            gz.Write(raw, 0, raw.Length);

        return Prefix + Convert.ToBase64String(output.ToArray());
    }

    /// <summary>Writes the code to a small file on the desktop and returns its path.</summary>
    public static string SaveToFile(SavedAiProfile p)
    {
        var safe = Regex.Replace($"ZenTune {p.GameName} {p.Name}", @"[^\w\- ]+", "").Trim();
        if (safe.Length == 0) safe = "ZenTune settings";
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var path = Path.Combine(desktop, safe + ".zentune");
        for (int i = 2; File.Exists(path); i++)
            path = Path.Combine(desktop, $"{safe} ({i}).zentune");
        File.WriteAllText(path, Export(p));
        return path;
    }

    /// <summary>
    /// Reads what the user pasted: a code (with or without text around it), or the path to a .zentune file.
    /// Throws a message the user can read if it is not a valid code for this game.
    /// </summary>
    public static SavedAiProfile Import(string input, string gameId, string gameName)
    {
        var text = (input ?? "").Trim().Trim('"');
        if (text.Length == 0)
            throw new InvalidOperationException("Paste the code first.");

        if (text.Length < 400 && File.Exists(text))
        {
            if (new FileInfo(text).Length > 1_000_000)
                throw new InvalidOperationException("That file is too big to be a ZenTune code.");
            text = File.ReadAllText(text);
        }

        int at = text.IndexOf(Prefix, StringComparison.Ordinal);
        if (at < 0)
            throw new InvalidOperationException("That does not look like a ZenTune code. It should start with ZENTUNE1:");

        // A chat app may break a long code over several lines, and a friend may write text after it.
        // Join the pieces that look like code and drop trailing pieces until the code reads correctly.
        var pieces = text[(at + Prefix.Length)..]
            .Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .TakeWhile(IsCodeText)
            .ToList();

        ShareData? data = null;
        for (int count = pieces.Count; count >= 1 && data is null; count--)
            data = TryRead(string.Concat(pieces.Take(count)));

        if (data is null)
            throw new InvalidOperationException("The code is damaged or incomplete. Ask your friend to share it again, and copy all of it.");

        if (data.V != 1)
            throw new InvalidOperationException("This code comes from a newer ZenTune. Update ZenTune and try again.");
        if (!string.Equals(data.GameId, gameId, StringComparison.Ordinal))
            throw new InvalidOperationException($"This code is for {Clean(data.GameName, 60)}, not {gameName}. Import it on that game's card.");
        if (data.Settings is null || data.Settings.Count == 0 || data.Settings.Count > MaxSettings)
            throw new InvalidOperationException("The code has no usable settings.");

        var settings = new List<SavedAiSetting>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in data.Settings)
        {
            var key = Clean(s.Key, 120);
            var value = Clean(s.Value, 200);
            if (key.Length == 0 || value.Length == 0 || AiGuard.IsForbidden(key) || !seen.Add(key)) continue;
            settings.Add(new SavedAiSetting(key, value, Clean(s.Why, 300)));
        }
        if (settings.Count == 0)
            throw new InvalidOperationException("The code has no usable settings.");

        var name = Clean(data.Name, 70);
        return new SavedAiProfile(
            Id: Guid.NewGuid().ToString("N"),
            GameId: gameId,
            GameName: gameName,
            Mode: Enum.IsDefined(data.Mode) ? data.Mode : AiMode.Balanced,
            Name: (name.Length == 0 ? "Shared settings" : name) + " (imported)",
            Gpu: Clean(data.Gpu, 80),
            Screen: Clean(data.Screen, 40),
            Model: Clean(data.Model, 60),
            Summary: Clean(data.Summary, 400),
            Saved: DateTime.Now,
            Settings: settings,
            Manual: data.Manual);
    }

    private static bool IsCodeText(string piece)
    {
        foreach (var ch in piece)
            if (!(char.IsAsciiLetterOrDigit(ch) || ch is '+' or '/' or '=')) return false;
        return true;
    }

    private static ShareData? TryRead(string token)
    {
        try
        {
            var packed = Convert.FromBase64String(token);
            using var input = new MemoryStream(packed);
            using var gz = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int n, total = 0;
            while ((n = gz.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += n;
                if (total > MaxJsonBytes) return null;
                output.Write(buffer, 0, n);
            }
            return JsonSerializer.Deserialize<ShareData>(output.ToArray(), Json);
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or JsonException or EndOfStreamException)
        {
            return null;
        }
    }

    // Text from a friend is only ever shown or compared, but keep it short and free of control characters anyway
    private static string Clean(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(Math.Min(s.Length, max));
        foreach (var ch in s)
        {
            if (sb.Length >= max) break;
            if (!char.IsControl(ch)) sb.Append(ch);
        }
        return sb.ToString().Trim();
    }
}
