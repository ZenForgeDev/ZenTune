using System.Text.Json;
using System.Text.Json.Serialization;
using ZenTune.Core.Presets;

namespace ZenTune.Core.Ai;

/// <summary>One setting inside a saved profile: the exact value, and Gemini's reason if it changed that setting.</summary>
public sealed record SavedAiSetting(string Key, string Value, string Why);

/// <summary>
/// AI settings the user gave a thumbs up. They hold the exact values of every setting the AI may touch, so using
/// them later gives the same result without asking Gemini again.
/// </summary>
public sealed record SavedAiProfile(
    string Id,
    string GameId,
    string GameName,
    AiMode Mode,
    string Name,
    string Gpu,
    string Screen,
    string Model,
    string Summary,
    DateTime Saved,
    List<SavedAiSetting> Settings,
    bool Manual = false)
{
    // Manual = the user saved their own current settings, no AI involved
    /// <summary>True if this profile was made on the same graphics card and screen resolution as this PC.</summary>
    public bool FitsThisPc(HardwareProfile hw) =>
        string.Equals(Gpu, hw.GpuName, StringComparison.OrdinalIgnoreCase) &&
        Screen.StartsWith($"{hw.ScreenWidth}x{hw.ScreenHeight}", StringComparison.Ordinal);
}

/// <summary>The list of saved AI settings, kept in %LOCALAPPDATA%\ZenTune\ai-saved.json.</summary>
public static class AiSavedProfiles
{
    private static readonly object Gate = new();

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static string FilePath => Path.Combine(AppPaths.Root, "ai-saved.json");

    public static List<SavedAiProfile> LoadAll()
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(FilePath)) return [];
                return JsonSerializer.Deserialize<List<SavedAiProfile>>(File.ReadAllText(FilePath), Json) ?? [];
            }
            catch
            {
                return [];
            }
        }
    }

    /// <summary>The saved profiles for one game, newest first.</summary>
    public static List<SavedAiProfile> ForGame(string gameId) =>
        LoadAll().Where(p => p.GameId == gameId).OrderByDescending(p => p.Saved).ToList();

    public static void Add(SavedAiProfile profile)
    {
        lock (Gate)
        {
            var all = LoadAll();
            all.Add(profile);
            Write(all);
        }
    }

    public static void Delete(string id)
    {
        lock (Gate)
        {
            var all = LoadAll();
            if (all.RemoveAll(p => p.Id == id) > 0) Write(all);
        }
    }

    private static void Write(List<SavedAiProfile> all)
    {
        // Write to a temp file first so a crash never leaves a half-written list
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(all, Json));
        File.Move(tmp, FilePath, overwrite: true);
    }
}
