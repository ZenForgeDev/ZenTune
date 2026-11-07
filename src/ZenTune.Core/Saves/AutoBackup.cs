using System.Diagnostics;
using System.Runtime.Versioning;
using ZenTune.Core.Cloud;
using ZenTune.Core.Games;

namespace ZenTune.Core.Saves;

/// <summary>
/// Backs up a game's saves a few seconds after the game closes. OFF by default; the user turns it on on the Saves page.
/// It only reads the list of running programs (like Task Manager does) to see when a game stops, and never opens or touches the game,
/// so it is safe with anti-cheat. It works for games ZenTune can find in Steam or Epic, and only while ZenTune is running
/// (turn on "Start ZenTune with Windows" to keep it in the tray). Nothing is backed up if the saves have not changed since the last backup.
/// The 15 newest automatic backups per game are kept; backups the user made by hand are never deleted.
/// </summary>
[SupportedOSPlatform("windows")]
public static class AutoBackup
{
    private sealed record Watched(string Name, string[] Processes);

    private static readonly object Lock = new();
    private static CancellationTokenSource? _cts;
    private static volatile int _watchCount;
    private static volatile bool _scanning;
    private static volatile bool _scanned;
    private static volatile string _runningNow = "";

    private static string FlagFile => Path.Combine(AppPaths.Root, "auto-backup-on.txt");
    private static string LastFile => Path.Combine(AppPaths.Root, "auto-backup-last.txt");

    public static bool Enabled => File.Exists(FlagFile);

    public static void SetEnabled(bool on)
    {
        if (on)
        {
            File.WriteAllText(FlagFile, "on");
            Start();
        }
        else
        {
            try { File.Delete(FlagFile); } catch { /* the loop checks the flag too */ }
            Stop();
        }
    }

    /// <summary>Called when the app starts: runs the watcher only if the user turned it on.</summary>
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
            _scanned = false;
            _watchCount = 0;
            _runningNow = "";
        }
    }

    /// <summary>One line about what the watcher is doing right now, for the Saves page. Empty when it is off.</summary>
    public static string Status()
    {
        if (!Enabled) return "";
        if (_cts is null) return "Automatic backup is on but not running yet.";
        if (_scanning || !_scanned) return "Looking for your installed games…";
        if (_watchCount == 0)
            return "ZenTune found no installed games to watch. Scan once on this page first (it needs the game list), and note that only Steam and Epic games can be watched.";
        var s = $"Watching {_watchCount} installed games.";
        if (_runningNow.Length > 0) s += $" Running now: {_runningNow}.";
        return s;
    }

    /// <summary>"7 Nov 18:40 · Baldur's Gate 3 backed up (12 files)" or similar. Empty when nothing has run yet.</summary>
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

    // ---------------------------------------------------------------- Loop

    private static async Task LoopAsync(CancellationToken ct)
    {
        var watched = new List<Watched>();
        var lastScan = DateTime.MinValue;
        var wasRunning = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var notRunningPolls = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Find the installed games that have saves: at the start, then every few hours (or sooner while the list is empty)
                var age = DateTime.Now - lastScan;
                if (age > TimeSpan.FromHours(6) || (watched.Count == 0 && age > TimeSpan.FromMinutes(10)))
                {
                    _scanning = true;
                    try { watched = await Task.Run(() => BuildWatchList(ct), ct); }
                    finally { _scanning = false; }
                    _scanned = true;
                    _watchCount = watched.Count;
                    lastScan = DateTime.Now;
                }

                var running = RunningNames();
                foreach (var w in watched)
                {
                    if (w.Processes.Any(running.Contains))
                    {
                        wasRunning.Add(w.Name);
                        notRunningPolls.Remove(w.Name);
                    }
                    else if (wasRunning.Contains(w.Name))
                    {
                        // Two polls in a row without the game, so a game that restarts itself is not counted as closed
                        var n = notRunningPolls.GetValueOrDefault(w.Name) + 1;
                        notRunningPolls[w.Name] = n;
                        if (n >= 2)
                        {
                            wasRunning.Remove(w.Name);
                            notRunningPolls.Remove(w.Name);
                            await BackUpAfterCloseAsync(w.Name, ct);
                        }
                    }
                }
                _runningNow = string.Join(", ", watched.Where(w => wasRunning.Contains(w.Name)).Select(w => w.Name));
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { SaveLast("error while watching: " + ex.Message); }

            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
            catch (OperationCanceledException) { break; }

            if (!Enabled) break;
        }
    }

    private static HashSet<string> RunningNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            try { names.Add(p.ProcessName); }
            catch { /* a process that just ended */ }
            finally { p.Dispose(); }
        }
        return names;
    }

    private static List<Watched> BuildWatchList(CancellationToken ct)
    {
        var db = SaveManifest.LoadCached();
        if (db is null) return [];

        var result = new List<Watched>();
        foreach (var save in SaveScanner.Scan(db, CustomSaves.Load(), ct))
        {
            if (save.TooLarge || string.IsNullOrEmpty(save.InstallDir)) continue;
            var procs = GameScanner.FindExecutables(save.InstallDir);
            if (procs.Count > 0) result.Add(new Watched(save.Name, procs.ToArray()));
        }
        return result;
    }

    private static async Task BackUpAfterCloseAsync(string name, CancellationToken ct)
    {
        try
        {
            // Give the game a moment to finish writing its save files
            await Task.Delay(TimeSpan.FromSeconds(8), ct);

            var db = SaveManifest.LoadCached();
            if (db is null) return;

            var save = (await Task.Run(() => SaveScanner.Scan(db, CustomSaves.Load(), ct), ct))
                .FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (save is null || save.TooLarge || save.Files.Count == 0)
            {
                SaveLast($"{name} closed, but no save files were found to back up");
                return;
            }

            // Nothing new since the last backup of this game on this PC
            var latest = SaveBackups.List(name).FirstOrDefault();
            if (latest is not null && save.Newest <= latest.Created)
            {
                SaveLast($"{name} closed, the saves have not changed since the last backup, so nothing was copied");
                return;
            }

            var backup = await Task.Run(() => SaveBackups.Create(name, save.Files, "After the game closed", automatic: true), ct);
            SaveLast($"{name} backed up ({backup.Files.Count} files)");
            BackupNotifier.Send("Saves backed up", $"{name}: {backup.Files.Count} files were saved after the game closed.");
            CloudSync.AutoUploadInBackground();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            SaveLast($"{name} could not be backed up: {ex.Message}");
        }
    }
}
