using System.Runtime.Versioning;
using System.Text.Json;
using ZenTune.Core.Cloud;

namespace ZenTune.Core.Saves;

/// <summary>
/// Backs up the saves of all games every day or every week. OFF by default; the user turns it on on the Saves page.
/// It only runs while ZenTune is running (turn on "Start ZenTune with Windows" to keep it in the tray). Games whose saves have not
/// changed since their last backup are skipped. Backups made this way count as automatic: the 15 newest per game are kept,
/// and backups the user made by hand are never deleted.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ScheduledBackup
{
    private sealed class Settings
    {
        public bool Enabled { get; set; }
        public int EveryDays { get; set; } = 1;
        public DateTime LastRun { get; set; }
    }

    private static readonly object Lock = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static CancellationTokenSource? _cts;

    private static string SettingsFile => Path.Combine(AppPaths.Root, "scheduled-backup.json");
    private static string LastFile => Path.Combine(AppPaths.Root, "scheduled-backup-last.txt");

    private static Settings Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsFile), Json) ?? new Settings();
        }
        catch { /* start fresh */ }
        return new Settings();
    }

    private static void Save(Settings s) => File.WriteAllText(SettingsFile, JsonSerializer.Serialize(s, Json));

    public static bool Enabled => Load().Enabled;
    public static int EveryDays => Load().EveryDays is 7 ? 7 : 1;

    /// <summary>Turns the schedule on or off. Turning it on starts the clock now, so the first backup comes after one full interval.</summary>
    public static void Configure(bool enabled, int everyDays)
    {
        var s = Load();
        bool wasOn = s.Enabled;
        s.Enabled = enabled;
        s.EveryDays = everyDays == 7 ? 7 : 1;
        if (enabled && !wasOn) s.LastRun = DateTime.Now;
        Save(s);

        if (enabled) Start();
        else Stop();
    }

    public static void StartIfEnabled()
    {
        if (Enabled) Start();
    }

    private static void Start()
    {
        lock (Lock)
        {
            if (_cts is not null) return;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _ = Task.Run(() => LoopAsync(token));
        }
    }

    public static void Stop()
    {
        lock (Lock)
        {
            _cts?.Cancel();
            _cts = null;
        }
    }

    /// <summary>"Next scheduled backup: 8 Nov 18:40" or an empty text when the schedule is off.</summary>
    public static string Status()
    {
        var s = Load();
        if (!s.Enabled) return "";
        var next = s.LastRun + TimeSpan.FromDays(s.EveryDays is 7 ? 7 : 1);
        return next <= DateTime.Now
            ? "A scheduled backup is due and runs within a few minutes."
            : $"Next scheduled backup: {next:d MMM HH:mm} (only while ZenTune is running).";
    }

    public static string LastResult()
    {
        try { return File.Exists(LastFile) ? File.ReadAllText(LastFile).Trim() : ""; }
        catch { return ""; }
    }

    private static void SaveLast(string text)
    {
        try { File.WriteAllText(LastFile, $"{DateTime.Now:d MMM HH:mm} · {text}"); }
        catch { /* only a note */ }
    }

    private static async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var s = Load();
                if (!s.Enabled) break;

                if (DateTime.Now - s.LastRun >= TimeSpan.FromDays(s.EveryDays is 7 ? 7 : 1))
                {
                    // Mark it first, so a failure does not make it start again every 10 minutes
                    s.LastRun = DateTime.Now;
                    Save(s);
                    if (await Gate.WaitAsync(0, ct))
                    {
                        try { await RunAsync(ct, notify: true); }
                        finally { Gate.Release(); }
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { SaveLast("error: " + ex.Message); }

            try { await Task.Delay(TimeSpan.FromMinutes(10), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Runs the same backup as the schedule right now (also when the schedule is off). Games whose saves have not changed since
    /// their last backup are skipped. If the schedule is on, its clock starts over from now.
    /// </summary>
    public static async Task<string> RunNowAsync(CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var s = Load();
            if (s.Enabled)
            {
                s.LastRun = DateTime.Now;
                Save(s);
            }
            return await RunAsync(ct, notify: false);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<string> RunAsync(CancellationToken ct, bool notify)
    {
        var db = SaveManifest.LoadCached();
        if (db is null)
        {
            const string skipped = "skipped: the game list is not downloaded yet (open the Saves page once)";
            SaveLast(skipped);
            return skipped;
        }

        var saves = await Task.Run(() => SaveScanner.Scan(db, CustomSaves.Load(), ct), ct);

        int done = 0, unchanged = 0, failed = 0;
        await Task.Run(() =>
        {
            foreach (var save in saves)
            {
                ct.ThrowIfCancellationRequested();
                if (save.TooLarge || save.Files.Count == 0) continue;

                var latest = SaveBackups.List(save.Name).FirstOrDefault();
                if (latest is not null && save.Newest <= latest.Created) { unchanged++; continue; }

                try
                {
                    SaveBackups.Create(save.Name, save.Files, "Scheduled backup", automatic: true);
                    done++;
                }
                catch { failed++; }
            }
        }, ct);

        var text = $"{done} games backed up, {unchanged} unchanged" + (failed > 0 ? $", {failed} failed" : "");
        SaveLast(text);
        if (done > 0) CloudSync.AutoUploadInBackground();
        if (notify && (done > 0 || failed > 0)) BackupNotifier.Send("Scheduled backup finished", text);
        return text;
    }
}
