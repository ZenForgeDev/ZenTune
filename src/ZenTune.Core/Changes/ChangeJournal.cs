using System.Text.Json;

namespace ZenTune.Core.Changes;

/// <summary>One change ZenTune made to Windows, with what is needed to put it back.</summary>
public sealed class ChangeRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime When { get; set; } = DateTime.Now;
    public string Title { get; set; } = "";
    public string Kind { get; set; } = "";
    public Dictionary<string, string> Data { get; set; } = new();
    public bool Undone { get; set; }
}

/// <summary>
/// Every change ZenTune makes is written here first, so it can always be undone,
/// even after the app has been closed. Stored in %LOCALAPPDATA%\ZenTune\changes.json.
/// </summary>
public static class ChangeJournal
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static IReadOnlyList<ChangeRecord> All()
    {
        lock (Gate) return Load();
    }

    /// <summary>Changes that are still in effect, newest first.</summary>
    public static IReadOnlyList<ChangeRecord> Active() =>
        All().Where(c => !c.Undone).OrderByDescending(c => c.When).ToList();

    public static void Add(ChangeRecord record)
    {
        lock (Gate)
        {
            var list = Load();
            list.Add(record);
            // Keep the file small: drop old undone entries beyond 200
            if (list.Count > 200)
                list = list.Where(c => !c.Undone).Concat(list.Where(c => c.Undone).OrderByDescending(c => c.When).Take(50)).ToList();
            Save(list);
        }
    }

    public static void MarkUndone(string id)
    {
        lock (Gate)
        {
            var list = Load();
            var item = list.FirstOrDefault(c => c.Id == id);
            if (item is null) return;
            item.Undone = true;
            Save(list);
        }
    }

    /// <summary>Changes the saved data of an existing record (for example new startup arguments).</summary>
    public static void Update(string id, Action<ChangeRecord> change)
    {
        lock (Gate)
        {
            var list = Load();
            var item = list.FirstOrDefault(c => c.Id == id);
            if (item is null) return;
            change(item);
            Save(list);
        }
    }

    private static List<ChangeRecord> Load()
    {
        try
        {
            if (!File.Exists(AppPaths.ChangesFile)) return [];
            return JsonSerializer.Deserialize<List<ChangeRecord>>(File.ReadAllText(AppPaths.ChangesFile), Json) ?? [];
        }
        catch
        {
            // A broken file must never stop the app. Keep a copy and start fresh.
            try { File.Copy(AppPaths.ChangesFile, AppPaths.ChangesFile + ".broken", true); } catch { }
            return [];
        }
    }

    private static void Save(List<ChangeRecord> list)
    {
        var tmp = AppPaths.ChangesFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(list, Json));
        File.Move(tmp, AppPaths.ChangesFile, overwrite: true);
    }
}
