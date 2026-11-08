using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ZenTune.Core.Saves;

namespace ZenTune.Core.Driver;

/// <summary>A third-party driver package in Windows' driver store (what "pnputil /enum-drivers" lists).</summary>
public sealed record InstalledDriver(string Published, string Original, string Provider, string ClassName, string Version);

/// <summary>One driver package inside a backup.</summary>
public sealed class DriverBackupEntry
{
    public string Published { get; set; } = "";
    public string Original { get; set; } = "";
    public string Provider { get; set; } = "";
    public string ClassName { get; set; } = "";
    public string Version { get; set; } = "";

    /// <summary>The sub folder of the backup that holds this package.</summary>
    public string Folder { get; set; } = "";
}

public sealed class DriverBackupInfo
{
    public DateTime Created { get; set; }
    public string Label { get; set; } = "";
    public string Windows { get; set; } = "";
    public long TotalBytes { get; set; }
    public List<DriverBackupEntry> Drivers { get; set; } = [];

    [JsonIgnore] public string Folder { get; set; } = "";
    [JsonIgnore] public string Pc { get; set; } = "";

    [JsonIgnore]
    public bool FromOtherPc => Pc.Length > 0 && !string.Equals(Pc, BackupLocation.PcName, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Backs up the third-party drivers in Windows' driver store with PnPUtil and puts them back. Backups go to
/// &lt;backup folder&gt;\ZenTune-backup\&lt;PC name&gt;\Drivers\&lt;time&gt;, next to the Saves and Settings of the same PC, so the
/// cloud upload and the per-PC folders work exactly as they do for saves.
/// Exporting and installing drivers needs admin rights, so both show one Windows prompt.
/// </summary>
public static class DriverBackup
{
    private const string ManifestName = "backup.json";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>This PC's driver backups: &lt;backup folder&gt;\ZenTune-backup\&lt;PC name&gt;\Drivers</summary>
    public static string Root => Path.Combine(BackupLocation.BackupFolder, BackupLocation.PcName, "Drivers");

    // ---------------------------------------------------------------- Installed drivers

    /// <summary>
    /// Lists the third-party driver packages. The text of pnputil is translated into the language of Windows, so it is read by
    /// position, not by label: published name, original name, provider, class (the order does not change between languages).
    /// </summary>
    public static List<InstalledDriver> ListInstalled()
    {
        var psi = new ProcessStartInfo("pnputil.exe", "/enum-drivers")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start pnputil.");
        var error = p.StandardError.ReadToEndAsync();
        string text = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        error.Wait();

        var result = new List<InstalledDriver>();
        var values = new List<string>();

        void Flush()
        {
            if (values.Count >= 4 && Regex.IsMatch(values[0], @"^oem\d+\.inf$", RegexOptions.IgnoreCase))
            {
                string version = values.FirstOrDefault(v => Regex.IsMatch(v, @"^\d{1,4}[./-]\d{1,2}[./-]\d{1,4}\s+\S+")) ?? "";
                result.Add(new InstalledDriver(values[0], values[1], values[2], values[3], version));
            }
            values.Clear();
        }

        foreach (var raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0) { Flush(); continue; }

            int colon = line.IndexOf(':');
            if (colon > 0) values.Add(line[(colon + 1)..].Trim());
        }
        Flush();

        if (result.Count == 0)
            throw new InvalidOperationException("pnputil did not list any drivers. Is this a normal Windows 10 or 11 PC?");

        return result.OrderBy(d => d.ClassName, StringComparer.OrdinalIgnoreCase).ThenBy(d => d.Provider, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ---------------------------------------------------------------- Backup

    /// <summary>Backs up the chosen drivers. One admin prompt. Returns the backup and how many drivers could not be exported.</summary>
    public static async Task<(DriverBackupInfo Backup, int Failed)> BackupAsync(
        IReadOnlyList<InstalledDriver> selected, string label, CancellationToken ct = default)
    {
        if (selected.Count == 0) throw new InvalidOperationException("Choose at least one driver.");

        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        string finalDir = Path.Combine(Root, stamp);
        string tempDir = finalDir + ".copying";
        Directory.CreateDirectory(tempDir);

        try
        {
            // One sub folder per driver, so it is always clear which files belong to which package
            var jobs = new List<(InstalledDriver Driver, string Folder)>();
            int n = 0;
            foreach (var d in selected)
            {
                string folder = $"{++n:000}_{SafeName(Path.GetFileNameWithoutExtension(d.Original))}";
                Directory.CreateDirectory(Path.Combine(tempDir, folder));
                jobs.Add((d, folder));
            }

            var script = new StringBuilder();
            foreach (var (d, folder) in jobs)
            {
                script.AppendLine($"pnputil /export-driver {d.Published} \"{Path.Combine(tempDir, folder)}\" >> \"%LOG%\" 2>&1");
                script.AppendLine($"echo %ERRORLEVEL%^|{d.Published}>> \"%RES%\"");
            }

            var results = await RunElevatedScriptAsync("export", script.ToString(), tempDir, ct);

            var info = new DriverBackupInfo
            {
                Created = DateTime.Now,
                Label = label,
                Windows = Environment.OSVersion.VersionString,
                Folder = finalDir,
                Pc = BackupLocation.PcName,
            };

            int failed = 0;
            foreach (var (d, folder) in jobs)
            {
                string dir = Path.Combine(tempDir, folder);
                bool exported = results.TryGetValue(d.Published, out var code) && code == 0
                                && Directory.EnumerateFiles(dir, "*.inf", SearchOption.AllDirectories).Any();
                if (!exported)
                {
                    failed++;
                    try { Directory.Delete(dir, recursive: true); } catch { /* an empty folder is harmless */ }
                    continue;
                }

                info.Drivers.Add(new DriverBackupEntry
                {
                    Published = d.Published, Original = d.Original, Provider = d.Provider,
                    ClassName = d.ClassName, Version = d.Version, Folder = folder,
                });
            }

            if (info.Drivers.Count == 0)
                throw new InvalidOperationException("Windows did not export any of the drivers. " + LogTail());

            info.TotalBytes = DirSize(tempDir);
            File.WriteAllText(Path.Combine(tempDir, ManifestName), JsonSerializer.Serialize(info, Json));

            Directory.Move(tempDir, finalDir); // only now does it count as a backup
            return (info, failed);
        }
        catch
        {
            try { if (Directory.Exists(tempDir)) DeleteDirectory(tempDir); } catch { /* ignored by the list */ }
            throw;
        }
    }

    // ---------------------------------------------------------------- List, restore, delete

    /// <summary>Driver backups from every PC in the backup folder (also the ones downloaded from the cloud), newest first.</summary>
    public static List<DriverBackupInfo> ListBackups()
    {
        var list = new List<DriverBackupInfo>();
        string root = BackupLocation.BackupFolder;
        if (!Directory.Exists(root)) return list;

        foreach (var pcDir in SafeDirs(root))
        {
            string pc = Path.GetFileName(pcDir);
            foreach (var dir in SafeDirs(Path.Combine(pcDir, "Drivers")))
            {
                if (dir.EndsWith(".copying", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var info = JsonSerializer.Deserialize<DriverBackupInfo>(File.ReadAllText(Path.Combine(dir, ManifestName)), Json);
                    if (info is null) continue;
                    info.Folder = dir;
                    info.Pc = pc;
                    list.Add(info);
                }
                catch { /* not a finished backup */ }
            }
        }
        return list.OrderByDescending(b => b.Created).ToList();
    }

    /// <summary>
    /// Puts the chosen drivers back into Windows (one admin prompt). Windows only uses a restored driver on a device when it is
    /// the best match for it, so an older driver does not replace a newer one that is already in use.
    /// </summary>
    public static async Task<(int Ok, int Failed, bool Restart)> RestoreAsync(
        DriverBackupInfo backup, IReadOnlyList<DriverBackupEntry> selected, CancellationToken ct = default)
    {
        if (selected.Count == 0) throw new InvalidOperationException("Choose at least one driver.");

        var script = new StringBuilder();
        foreach (var d in selected)
        {
            string dir = Path.Combine(backup.Folder, d.Folder);
            if (!Directory.Exists(dir)) continue;
            script.AppendLine($"pnputil /add-driver \"{Path.Combine(dir, "*.inf")}\" /subdirs /install >> \"%LOG%\" 2>&1");
            script.AppendLine($"echo %ERRORLEVEL%^|{d.Folder}>> \"%RES%\"");
        }
        if (script.Length == 0) throw new InvalidOperationException("The files of this backup are missing.");

        var results = await RunElevatedScriptAsync("restore", script.ToString(), backup.Folder, ct);

        int ok = 0, failed = 0;
        bool restart = false;
        foreach (var d in selected)
        {
            if (!results.TryGetValue(d.Folder, out var code)) { failed++; continue; }
            if (code == 3010) { ok++; restart = true; }   // 3010: done, restart needed
            else if (code == 0) ok++;
            else failed++;
        }
        return (ok, failed, restart);
    }

    public static void Delete(DriverBackupInfo backup)
    {
        if (Directory.Exists(backup.Folder)) DeleteDirectory(backup.Folder);
    }

    // ---------------------------------------------------------------- Helpers

    private static string LogPath => Path.Combine(AppPaths.Root, "driver-job-log.txt");

    private static string LogTail()
    {
        try
        {
            if (!File.Exists(LogPath)) return "";
            var lines = File.ReadAllLines(LogPath).Where(l => l.Trim().Length > 0).TakeLast(3);
            return "pnputil said: " + string.Join(" ", lines);
        }
        catch { return ""; }
    }

    /// <summary>
    /// Runs a small command script as administrator (one prompt) and returns the exit code of each step, keyed by the text after "|".
    /// Output of the commands goes to driver-job-log.txt in ZenTune's data folder.
    /// </summary>
    private static async Task<Dictionary<string, int>> RunElevatedScriptAsync(string name, string commands, string pathToCheck, CancellationToken ct)
    {
        if (pathToCheck.Contains('%') || pathToCheck.Contains('"'))
            throw new InvalidOperationException("The backup folder path contains a % or a quote, which cannot be used here. Choose another backup folder.");

        string scriptPath = Path.Combine(AppPaths.Root, $"driver-{name}.cmd");
        string resPath = Path.Combine(AppPaths.Root, $"driver-{name}-result.txt");
        File.Delete(resPath);
        File.WriteAllText(LogPath, "");

        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("chcp 65001 >nul");
        sb.AppendLine($"set \"LOG={LogPath}\"");
        sb.AppendLine($"set \"RES={resPath}\"");
        sb.Append(commands);
        File.WriteAllText(scriptPath, sb.ToString(), new UTF8Encoding(false));

        var psi = new ProcessStartInfo("cmd.exe", $"/c \"\"{scriptPath}\"\"")
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        try
        {
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start cmd.exe.");
            await p.WaitForExitAsync(ct);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException("The admin prompt was cancelled, so nothing was done.");
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { /* it is deleted next time */ }
        }

        var results = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(resPath))
        {
            foreach (var line in File.ReadAllLines(resPath))
            {
                int bar = line.IndexOf('|');
                if (bar <= 0) continue;
                if (int.TryParse(line[..bar].Trim(), out int code))
                    results[line[(bar + 1)..].Trim()] = code;
            }
        }
        return results;
    }

    private static string SafeName(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(s.Select(c => invalid.Contains(c) || c == '%' ? '_' : c).ToArray()).Trim();
        return clean.Length == 0 ? "driver" : clean;
    }

    private static string[] SafeDirs(string dir)
    {
        try { return Directory.Exists(dir) ? Directory.GetDirectories(dir) : []; }
        catch { return []; }
    }

    private static long DirSize(string dir)
    {
        long total = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(f).Length; } catch { /* skip */ }
            }
        }
        catch { /* skip */ }
        return total;
    }

    /// <summary>Deletes a folder, also when the exported files are marked read-only.</summary>
    private static void DeleteDirectory(string dir)
    {
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            try { File.SetAttributes(f, FileAttributes.Normal); } catch { /* try the delete anyway */ }
        }
        Directory.Delete(dir, recursive: true);
    }
}
