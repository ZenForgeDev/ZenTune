using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using ZenTune.Core.Pulse;

namespace ZenTune.Core.Measure;

/// <summary>The numbers worked out from one recording.</summary>
public sealed class CaptureStats
{
    public int Frames { get; set; }
    public double Seconds { get; set; }
    public double AvgFps { get; set; }
    public double Low1Fps { get; set; }
    public double Low01Fps { get; set; }
    public double MedianMs { get; set; }
    public double P99Ms { get; set; }
    public double MaxMs { get; set; }
    public int Stutters { get; set; }

    public double StuttersPerMinute => Seconds > 0 ? Stutters / (Seconds / 60.0) : 0;
}

/// <summary>One saved recording (the numbers live here, the full frame list in the CSV file next to it).</summary>
public sealed class CaptureRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime When { get; set; } = DateTime.Now;
    public string Label { get; set; } = "";
    public string Process { get; set; } = "";
    public string CsvPath { get; set; } = "";
    public CaptureStats Stats { get; set; } = new();

    /// <summary>How busy the PC was during the recording (from Pulse). Null for older recordings or when it was switched off.</summary>
    public PulseSummary? Pulse { get; set; }
}

/// <summary>Turns a list of frame times (milliseconds) into FPS numbers.</summary>
public static class FrameStats
{
    /// <summary>
    /// Average FPS = frames / time. "1% low" = the average FPS of the slowest 1% of frames (0.1% low: the slowest 0.1%).
    /// 99th percentile = 99% of frames were faster than this. A stutter is a frame that took more than twice the median.
    /// </summary>
    public static CaptureStats Compute(IReadOnlyList<double> frameMs)
    {
        int n = frameMs.Count;
        if (n == 0) return new CaptureStats();

        double total = frameMs.Sum();
        var sorted = frameMs.OrderBy(x => x).ToArray(); // fastest first

        double WorstAverage(double share)
        {
            int count = Math.Max(1, (int)Math.Ceiling(n * share));
            return sorted.Skip(n - count).Average();
        }

        double median = sorted[n / 2];
        int p99Index = Math.Clamp((int)Math.Ceiling(0.99 * n) - 1, 0, n - 1);

        return new CaptureStats
        {
            Frames = n,
            Seconds = total / 1000.0,
            AvgFps = total > 0 ? n * 1000.0 / total : 0,
            Low1Fps = 1000.0 / WorstAverage(0.01),
            Low01Fps = 1000.0 / WorstAverage(0.001),
            MedianMs = median,
            P99Ms = sorted[p99Index],
            MaxMs = sorted[n - 1],
            Stutters = frameMs.Count(x => x > median * 2),
        };
    }

    /// <summary>Reads the time between frames from a PresentMon CSV. Works with the 1.x and 2.x column names.</summary>
    public static List<double> ReadFrameTimes(string csvPath)
    {
        var times = new List<double>();
        using var reader = new StreamReader(csvPath);
        var header = reader.ReadLine();
        if (header is null) return times;

        var cols = header.Split(',');
        int idx = Array.FindIndex(cols, c => c.Trim().Equals("MsBetweenPresents", StringComparison.OrdinalIgnoreCase));
        if (idx < 0) idx = Array.FindIndex(cols, c => c.Trim().Equals("FrameTime", StringComparison.OrdinalIgnoreCase));
        if (idx < 0) throw new InvalidOperationException("The recording has no frame time column. A different PresentMon version may name it differently.");

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var parts = line.Split(',');
            if (parts.Length <= idx) continue;
            if (double.TryParse(parts[idx], NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) && ms > 0 && ms < 5000)
                times.Add(ms);
        }
        return times;
    }
}

/// <summary>
/// PresentMon (Intel, MIT license): reads Windows' own graphics events (ETW) and writes a CSV of frame times.
/// It does not inject anything into the game and draws nothing on screen. ZenTune downloads one pinned version
/// and checks its SHA-256 before it will run it.
/// </summary>
[SupportedOSPlatform("windows")]
public static class PresentMonTool
{
    public const string Version = "2.6.0";
    public const string Sha256 = "b2a706bc6ad475749e3b7e3409263aa1e6906d45bdcf993f6dbc0f660188f1af";
    public const string Url = "https://github.com/GameTechDev/PresentMon/releases/download/v2.6.0/PresentMon-2.6.0-x64.exe";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(3) };

    public static string Folder => Path.Combine(AppPaths.Root, "Tools", "PresentMon");

    public static string ExePath => Path.Combine(Folder, $"PresentMon-{Version}-x64.exe");

    private static string HashOf(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    public static bool IsInstalled
    {
        get
        {
            try { return File.Exists(ExePath) && HashOf(ExePath) == Sha256; }
            catch { return false; }
        }
    }

    public static async Task EnsureInstalledAsync()
    {
        if (IsInstalled) return;

        Directory.CreateDirectory(Folder);
        var tmp = ExePath + ".download";

        using (var response = await Http.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            await using var file = File.Create(tmp);
            await response.Content.CopyToAsync(file);
        }

        if (HashOf(tmp) != Sha256)
        {
            try { File.Delete(tmp); } catch { }
            throw new InvalidOperationException("The downloaded file did not match the expected checksum, so ZenTune deleted it.");
        }

        File.Move(tmp, ExePath, overwrite: true);
    }
}

/// <summary>Records and stores before/after measurements.</summary>
[SupportedOSPlatform("windows")]
public static class Captures
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    // Games whose anti-cheat is strict: ZenTune does not record these (same rule as for their settings files)
    private static readonly string[] Blocked = ["valorant", "deltaforce"];

    private static readonly HashSet<string> NotGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "ZenTune", "ApplicationFrameHost", "SystemSettings", "TextInputHost", "ShellExperienceHost",
        "SearchHost", "StartMenuExperienceHost", "LockApp", "dwm", "taskmgr", "WidgetBoard", "Widgets",
    };

    public static string Folder
    {
        get
        {
            var dir = Path.Combine(AppPaths.Root, "Captures");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static string IndexFile => Path.Combine(Folder, "captures.json");

    public static bool IsBlocked(string exe) =>
        Blocked.Any(b => exe.Contains(b, StringComparison.OrdinalIgnoreCase));

    /// <summary>Programs that have a window right now, as (exe name, window title).</summary>
    public static List<(string Exe, string Title)> RunningWindows()
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.MainWindowHandle == IntPtr.Zero) continue;
                    if (NotGames.Contains(p.ProcessName)) continue;
                    string title = p.MainWindowTitle;
                    if (string.IsNullOrWhiteSpace(title)) continue;

                    string exe = p.ProcessName + ".exe";
                    if (!found.ContainsKey(exe)) found[exe] = title;
                }
                catch
                {
                    // process not readable
                }
            }
        }
        return found.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Select(kv => (kv.Key, kv.Value)).ToList();
    }

    // ---------------------------------------------------------------- Stored recordings

    public static List<CaptureRecord> List()
    {
        lock (Gate)
        {
            return Load().OrderByDescending(r => r.When).ToList();
        }
    }

    private static List<CaptureRecord> Load()
    {
        try
        {
            if (!File.Exists(IndexFile)) return [];
            return JsonSerializer.Deserialize<List<CaptureRecord>>(File.ReadAllText(IndexFile), Json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static void Save(List<CaptureRecord> list)
    {
        var tmp = IndexFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(list, Json));
        File.Move(tmp, IndexFile, overwrite: true);
    }

    public static void Delete(CaptureRecord record)
    {
        lock (Gate)
        {
            var list = Load();
            list.RemoveAll(r => r.Id == record.Id);
            Save(list);
        }
        try { if (File.Exists(record.CsvPath)) File.Delete(record.CsvPath); } catch { }
    }

    // ---------------------------------------------------------------- Recording

    /// <summary>
    /// Waits <paramref name="delaySeconds"/> (time to switch to the game), records <paramref name="seconds"/> of frames
    /// and saves the result. Tries without admin rights first and asks for them only when Windows refuses.
    /// </summary>
    public static CaptureRecord Record(string exe, string label, int delaySeconds, int seconds, bool withPulse = false)
    {
        string exeName = Path.GetFileName(exe.Trim());
        if (exeName.Length == 0 || exeName.Any(c => c == '"' || char.IsControl(c)))
            throw new InvalidOperationException("That program name can't be used.");
        if (IsBlocked(exeName))
            throw new InvalidOperationException("ZenTune doesn't record this game because of its anti-cheat.");
        if (!PresentMonTool.IsInstalled)
            throw new InvalidOperationException("PresentMon is not downloaded yet.");

        string safe = new(Path.GetFileNameWithoutExtension(exeName).Where(char.IsLetterOrDigit).ToArray());
        string csv = Path.Combine(Folder, $"{DateTime.Now:yyyyMMdd-HHmmss}-{safe}.csv");
        string args = $"--process_name \"{exeName}\" --output_file \"{csv}\" --no_console_stats " +
                      $"--delay {delaySeconds} --timed {seconds} --terminate_after_timed " +
                      "--session_name ZenTuneCapture --stop_existing_session";
        var limit = TimeSpan.FromSeconds(delaySeconds + seconds + 45);

        // Pulse measures in the background while PresentMon records, so the numbers can say what limited the FPS
        PulseCollector? pulse = null;
        if (withPulse)
        {
            try
            {
                pulse = new PulseCollector(PulseMonitor.Shared);
                pulse.Start(DateTime.Now.AddSeconds(delaySeconds));
            }
            catch
            {
                pulse?.Dispose();
                pulse = null;
            }
        }

        try
        {
        bool ok = RunTool(args, elevated: false, limit) && HasData(csv);
        if (!ok)
        {
            try { if (File.Exists(csv)) File.Delete(csv); } catch { }
            pulse?.ResetFrom(DateTime.Now.AddSeconds(delaySeconds));
            RunTool(args, elevated: true, limit);
        }

        if (!HasData(csv))
            throw new InvalidOperationException(
                "PresentMon did not record any frames. Make sure the game is running and showing frames while it records " +
                "(a game on the loading screen or minimized records nothing).");

        var times = FrameStats.ReadFrameTimes(csv);
        if (times.Count < 30)
            throw new InvalidOperationException($"Only {times.Count} frames were recorded. Keep the game on screen while it records.");

        var record = new CaptureRecord
        {
            Label = label,
            Process = exeName,
            CsvPath = csv,
            Stats = FrameStats.Compute(times),
        };

        lock (Gate)
        {
            var list = Load();
            list.Add(record);
            Save(list);
        }
        record.Pulse = pulse?.Stop();
        return record;
        }
        finally
        {
            pulse?.Dispose();
        }
    }

    private static bool HasData(string csv)
    {
        try { return File.Exists(csv) && new FileInfo(csv).Length > 1500; }
        catch { return false; }
    }

    private static bool RunTool(string args, bool elevated, TimeSpan limit)
    {
        var psi = new ProcessStartInfo(PresentMonTool.ExePath, args) { WorkingDirectory = Folder };
        if (elevated)
        {
            psi.UseShellExecute = true;
            psi.Verb = "runas";
            psi.WindowStyle = ProcessWindowStyle.Hidden;
        }
        else
        {
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
        }

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new OperationCanceledException("The admin prompt was cancelled, so nothing was recorded.");
        }

        if (process is null) return false;

        using (process)
        {
            if (!elevated)
            {
                _ = process.StandardOutput.ReadToEndAsync();
                _ = process.StandardError.ReadToEndAsync();
            }

            if (!process.WaitForExit(limit))
            {
                try { process.Kill(true); } catch { }
                return false;
            }
            return process.ExitCode == 0;
        }
    }
}
