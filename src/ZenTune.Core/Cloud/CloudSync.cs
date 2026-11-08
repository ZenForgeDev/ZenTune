using System.Text.Json;
using ZenTune.Core.Saves;

namespace ZenTune.Core.Cloud;

public sealed class CloudTarget
{
    /// <summary>Upload to this account.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Upload through this account's encryption layer.</summary>
    public bool Encrypted { get; set; }
}

public sealed class CloudSettings
{
    /// <summary>The account shown on the Cloud page (and used for Download), for example zentune-onedrive. Empty when none.</summary>
    public string Remote { get; set; } = "";

    /// <summary>Per account: whether it is used for uploads and whether it is encrypted.</summary>
    public Dictionary<string, CloudTarget> Targets { get; set; } = new();

    public CloudTarget For(string remote)
    {
        if (!Targets.TryGetValue(remote, out var t)) Targets[remote] = t = new CloudTarget();
        return t;
    }

    /// <summary>Upload automatically after a save backup.</summary>
    public bool AutoUpload { get; set; }

    /// <summary>Also put ZenTune's own settings, profiles and game config backups in the backup folder, so they are uploaded too.</summary>
    public bool IncludeSettings { get; set; } = true;

    /// <summary>Also upload driver backups (Advanced → Driver backup). They can be several GB, so this can be switched off.</summary>
    public bool IncludeDrivers { get; set; } = true;
}

/// <summary>
/// Uploads the backup folder of this PC (ZenTune-backup\&lt;PC name&gt;) to the cloud with rclone "copy", which never deletes anything
/// in the cloud, and downloads backups from other PCs the same way. Only the backup folder is ever sent: not rclone's config, not caches,
/// not the Gemini key.
/// </summary>
public static class CloudSync
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static string SettingsFile => Path.Combine(AppPaths.Root, "cloud.json");
    private static string LastFile => Path.Combine(AppPaths.Root, "cloud-last.txt");

    public static bool Busy => Gate.CurrentCount == 0;

    public static CloudSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
                return JsonSerializer.Deserialize<CloudSettings>(File.ReadAllText(SettingsFile), Json) ?? new CloudSettings();
        }
        catch { /* start fresh */ }
        return new CloudSettings();
    }

    public static void Save(CloudSettings settings)
    {
        try { File.WriteAllText(SettingsFile, JsonSerializer.Serialize(settings, Json)); }
        catch (Exception ex) { throw new InvalidOperationException("Could not save the cloud settings. " + ex.Message); }
    }

    /// <summary>"7 Nov 17:30 · 12 files uploaded" or similar. Empty when nothing has run yet.</summary>
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

    /// <summary>Friendly name of an account, for example "OneDrive" for zentune-onedrive.</summary>
    public static string Label(string remote)
    {
        var slug = remote.StartsWith("zentune-", StringComparison.Ordinal) ? remote[8..] : remote;
        return slug switch
        {
            "onedrive" => "OneDrive",
            "gdrive" => "Google Drive",
            "dropbox" => "Dropbox",
            "elkjop" => "Elkjøp Cloud",
            _ => slug,
        };
    }

    // Plain: <remote>:ZenTune-backup/<PC>.  Encrypted: <remote>-crypt:<PC> (the crypt layer points at <remote>:ZenTune-backup-encrypted).
    private static string UploadTarget(string remote, bool encrypted) =>
        encrypted ? $"{remote}-crypt:{BackupLocation.PcName}" : $"{remote}:ZenTune-backup/{BackupLocation.PcName}";

    private static string DownloadSource(string remote, bool encrypted) =>
        encrypted ? $"{remote}-crypt:" : $"{remote}:ZenTune-backup";

    private static readonly string[] CommonArgs =
    [
        "--transfers", "4", "--checkers", "8",
        "--stats", "2s", "--stats-one-line", "--stats-log-level", "NOTICE",
        "--exclude", "*.copying/**",
        "--exclude", "rclone.conf",
    ];

    private static void Check()
    {
        if (!Rclone.IsInstalled()) throw new InvalidOperationException("rclone is not downloaded yet.");
    }

    /// <summary>Uploads this PC's backup folder. <paramref name="quietIfBusy"/> skips silently when another transfer is running.</summary>
    public static async Task<string?> UploadAsync(Action<string>? progress = null, bool quietIfBusy = false, CancellationToken ct = default)
    {
        var s = Load();
        if (quietIfBusy) { if (!await Gate.WaitAsync(0, ct)) return null; }
        else await Gate.WaitAsync(ct);

        try
        {
            Check();

            var remotes = (await Rclone.ListRemotesAsync()).Where(r => s.For(r).Enabled).ToList();
            if (remotes.Count == 0) throw new InvalidOperationException("No cloud account is switched on for uploads. Connect one and turn on \"Upload to this account\".");

            if (s.IncludeSettings) StageSettings();

            var source = Path.Combine(BackupLocation.BackupFolder, BackupLocation.PcName);
            if (!Directory.Exists(source)) throw new InvalidOperationException("There are no backups to upload yet.");

            var ok = new List<string>();
            var failed = new List<string>();
            foreach (var remote in remotes)
            {
                var label = Label(remote);
                var encrypted = s.For(remote).Encrypted;
                progress?.Invoke($"Uploading to {label}…");

                var args = new List<string> { "copy", source, UploadTarget(remote, encrypted) };
                args.AddRange(CommonArgs);
                if (!s.IncludeDrivers) args.AddRange(["--exclude", "Drivers/**"]);

                var r = await Rclone.RunAsync(args, TimeSpan.FromHours(6), ct, line => progress?.Invoke($"{label}: {line}"));
                if (r.Ok) ok.Add(label + (encrypted ? " (encrypted)" : ""));
                else failed.Add($"{label}: {r.ErrorSummary}");
            }

            if (failed.Count > 0)
                throw new InvalidOperationException(
                    (ok.Count > 0 ? $"Uploaded to {string.Join(", ", ok)}.\n" : "") + "Did not work:\n" + string.Join("\n", failed));

            var done = "Uploaded to " + string.Join(", ", ok);
            SaveLast(done);
            return done;
        }
        catch (OperationCanceledException)
        {
            SaveLast("upload cancelled");
            throw;
        }
        catch (Exception ex)
        {
            SaveLast("upload failed: " + ex.Message.Split('\n')[0]);
            throw;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Called after a save backup: uploads in the background when auto-upload is on. Never throws and never blocks the user.</summary>
    public static void AutoUploadInBackground()
    {
        var s = Load();
        if (!s.AutoUpload || !Rclone.IsInstalled()) return;
        _ = Task.Run(async () =>
        {
            try { await UploadAsync(quietIfBusy: true); }
            catch { /* the result is saved in cloud-last.txt and shown on the Cloud page */ }
        });
    }

    /// <summary>Copies the backups in the cloud (all PCs) to the local backup folder. Nothing local is overwritten with older files or deleted.</summary>
    public static async Task<string> DownloadAsync(Action<string>? progress = null, CancellationToken ct = default)
    {
        var s = Load();
        await Gate.WaitAsync(ct);
        try
        {
            Check();
            if (string.IsNullOrWhiteSpace(s.Remote)) throw new InvalidOperationException("Connect a cloud account first.");
            Directory.CreateDirectory(BackupLocation.BackupFolder);

            // --update: never replace a newer local file with an older one
            var args = new List<string> { "copy", DownloadSource(s.Remote, s.For(s.Remote).Encrypted), BackupLocation.BackupFolder, "--update" };
            args.AddRange(CommonArgs);

            var r = await Rclone.RunAsync(args, TimeSpan.FromHours(6), ct, line => progress?.Invoke(line));
            if (!r.Ok) throw new InvalidOperationException(r.ErrorSummary);

            var done = "Download finished";
            SaveLast(done);
            return done;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>The PC names that have backups in the chosen cloud account (the folders in ZenTune-backup).</summary>
    public static async Task<List<string>> ListPcsAsync(CancellationToken ct = default)
    {
        var s = Load();
        await Gate.WaitAsync(ct);
        try
        {
            Check();
            if (string.IsNullOrWhiteSpace(s.Remote)) throw new InvalidOperationException("Connect a cloud account first.");

            var args = new List<string> { "lsf", DownloadSource(s.Remote, s.For(s.Remote).Encrypted), "--dirs-only" };
            var r = await Rclone.RunAsync(args, TimeSpan.FromMinutes(2), ct);
            if (!r.Ok) throw new InvalidOperationException(r.ErrorSummary);

            return r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim().TrimEnd('/'))
                .Where(l => l.Length > 0)
                .OrderBy(l => l, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Copies only the driver backups of one PC from the cloud to the local backup folder (nothing local is replaced by older files).</summary>
    public static async Task<string> DownloadDriversAsync(string pc, Action<string>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(pc) || pc.IndexOfAny(['/', '\\', ':', '"', '*', '?', '<', '>', '|']) >= 0 || pc.Contains(".."))
            throw new InvalidOperationException("That is not a valid PC name.");

        var s = Load();
        await Gate.WaitAsync(ct);
        try
        {
            Check();
            if (string.IsNullOrWhiteSpace(s.Remote)) throw new InvalidOperationException("Connect a cloud account first.");

            bool encrypted = s.For(s.Remote).Encrypted;
            string from = encrypted ? $"{s.Remote}-crypt:{pc}/Drivers" : $"{s.Remote}:ZenTune-backup/{pc}/Drivers";
            string to = Path.Combine(BackupLocation.BackupFolder, pc, "Drivers");
            Directory.CreateDirectory(to);

            var args = new List<string> { "copy", from, to, "--update" };
            args.AddRange(CommonArgs);

            var r = await Rclone.RunAsync(args, TimeSpan.FromHours(6), ct, line => progress?.Invoke(line));
            if (!r.Ok) throw new InvalidOperationException(r.ErrorSummary);

            var done = $"Driver backups from {pc} downloaded";
            SaveLast(done);
            return done;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// Copies the things worth keeping into the backup folder: ZenTune's saved AI profiles, the games you added by hand, the list of changes
    /// ZenTune made (for Undo) and the backups of game config files. Never the Gemini key, rclone's config or caches.
    /// </summary>
    public static void StageSettings()
    {
        var pcRoot = Path.Combine(BackupLocation.BackupFolder, BackupLocation.PcName);
        var settingsDir = Path.Combine(pcRoot, "Settings");
        Directory.CreateDirectory(settingsDir);

        foreach (var name in new[] { "ai-saved.json", "custom-saves.json", "changes.json" })
            CopyIfNewer(Path.Combine(AppPaths.Root, name), Path.Combine(settingsDir, name));

        var configs = AppPaths.Backups;
        var target = Path.Combine(pcRoot, "GameConfigs");
        var full = Path.GetFullPath(configs).TrimEnd('\\') + "\\";
        if (!Path.GetFullPath(target).StartsWith(full, StringComparison.OrdinalIgnoreCase)) // never copy a folder into itself
            CopyTree(configs, target);
    }

    private static void CopyIfNewer(string from, string to)
    {
        try
        {
            if (!File.Exists(from)) return;
            if (File.Exists(to) && File.GetLastWriteTimeUtc(to) >= File.GetLastWriteTimeUtc(from) && new FileInfo(to).Length == new FileInfo(from).Length) return;
            File.Copy(from, to, overwrite: true);
        }
        catch { /* a file in use is skipped this time */ }
    }

    private static void CopyTree(string from, string to)
    {
        try
        {
            if (!Directory.Exists(from)) return;
            Directory.CreateDirectory(to);
            foreach (var file in Directory.GetFiles(from))
                CopyIfNewer(file, Path.Combine(to, Path.GetFileName(file)));
            foreach (var dir in Directory.GetDirectories(from))
                CopyTree(dir, Path.Combine(to, Path.GetFileName(dir)));
        }
        catch { /* skip what cannot be read */ }
    }
}
