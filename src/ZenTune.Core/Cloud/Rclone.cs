using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace ZenTune.Core.Cloud;

public sealed record RcloneResult(int ExitCode, string Output, string Error)
{
    public bool Ok => ExitCode == 0;

    /// <summary>The last few lines rclone complained about, for messages to the user.</summary>
    public string ErrorSummary
    {
        get
        {
            var text = string.IsNullOrWhiteSpace(Error) ? Output : Error;
            var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).TakeLast(6);
            var joined = string.Join("\n", lines);
            return joined.Length > 0 ? joined : $"rclone stopped with exit code {ExitCode}.";
        }
    }
}

/// <summary>
/// rclone (MIT, https://rclone.org) is what talks to OneDrive, Google Drive and Dropbox. ZenTune does not ship it:
/// it downloads one pinned version from rclone's own download site the first time the Cloud page is used, and checks its SHA-256
/// before it unpacks anything. The checksum below was compared with the signed SHA256SUMS list of the same release.
/// rclone uses its own config file (inside ZenTune's data folder), never the user's own rclone setup, and that file is never uploaded.
/// </summary>
public static class Rclone
{
    public const string Version = "v1.76.0";
    public const string Sha256 = "8b4fd2737ade4e70c057ef4a49deb7cffe94b0fb9d7587c189d149823f21a1dc";
    public const string Url = "https://downloads.rclone.org/v1.76.0/rclone-v1.76.0-windows-amd64.zip";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private static bool _verified;

    public static string Folder
    {
        get
        {
            var dir = Path.Combine(AppPaths.Root, "rclone");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string ExePath => Path.Combine(Folder, "rclone.exe");

    /// <summary>rclone's config (holds the login tokens). Never uploaded, never part of a backup.</summary>
    public static string ConfigPath => Path.Combine(Folder, "rclone.conf");

    private static string MarkerPath => Path.Combine(Folder, "installed.txt");

    private static string HashOf(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>True when the pinned rclone is downloaded and the file is still the one ZenTune checked.</summary>
    public static bool IsInstalled()
    {
        try
        {
            if (!File.Exists(ExePath) || !File.Exists(MarkerPath)) return false;
            var parts = File.ReadAllText(MarkerPath).Trim().Split(' ');
            if (parts.Length != 2 || parts[0] != Version) return false;
            if (_verified) return true;
            _verified = HashOf(ExePath) == parts[1];
            return _verified;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Downloads, checks and unpacks rclone. Progress is 0–1 (or -1 when the size is unknown).</summary>
    public static async Task DownloadAsync(Action<double>? progress = null, CancellationToken ct = default)
    {
        var tmpZip = Path.Combine(Folder, "rclone.zip.download");
        var tmpExe = Path.Combine(Folder, "rclone.exe.new");
        try
        {
            using (var response = await Http.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                long total = response.Content.Headers.ContentLength ?? -1;
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var target = File.Create(tmpZip);

                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    progress?.Invoke(total > 0 ? (double)done / total : -1);
                }
            }

            if (HashOf(tmpZip) != Sha256)
                throw new InvalidOperationException("The downloaded file did not match the expected checksum, so ZenTune deleted it and did not use it.");

            using (var zip = ZipFile.OpenRead(tmpZip))
            {
                var entry = zip.Entries.FirstOrDefault(e =>
                    e.FullName.EndsWith("/rclone.exe", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(e.FullName, "rclone.exe", StringComparison.OrdinalIgnoreCase));
                if (entry is null) throw new InvalidOperationException("rclone.exe was not found inside the download.");
                entry.ExtractToFile(tmpExe, overwrite: true);
            }

            var exeHash = HashOf(tmpExe);
            File.Move(tmpExe, ExePath, overwrite: true);
            File.WriteAllText(MarkerPath, $"{Version} {exeHash}");
            _verified = true;

            var check = await RunAsync(["version"], TimeSpan.FromSeconds(30), ct);
            if (!check.Ok || !check.Output.Contains("rclone", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("rclone was unpacked but did not start. " + check.ErrorSummary);
        }
        finally
        {
            try { if (File.Exists(tmpZip)) File.Delete(tmpZip); } catch { /* leftover is harmless */ }
            try { if (File.Exists(tmpExe)) File.Delete(tmpExe); } catch { /* leftover is harmless */ }
        }
    }

    /// <summary>Runs rclone with ZenTune's own config. <paramref name="onLine"/> gets every line rclone writes to its log (stderr).</summary>
    public static async Task<RcloneResult> RunAsync(IEnumerable<string> args, TimeSpan timeout, CancellationToken ct = default, Action<string>? onLine = null, string? stdin = null)
    {
        if (!File.Exists(ExePath)) throw new InvalidOperationException("rclone is not downloaded yet.");

        var psi = new ProcessStartInfo(ExePath)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            WorkingDirectory = Folder,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("--config");
        psi.ArgumentList.Add(ConfigPath);
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        var output = new StringBuilder();
        var error = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (error) error.AppendLine(e.Data);
            try { onLine?.Invoke(e.Data); } catch { /* a progress callback must never stop rclone */ }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (stdin is not null)
        {
            // Empty lines accept rclone's default answers, so a question during setup can never hang the app
            try { await process.StandardInput.WriteAsync(stdin); process.StandardInput.Close(); } catch { /* process already finished */ }
        }

        using var timer = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timer.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException("rclone took too long and was stopped.");
        }

        // Make sure the last output lines have arrived
        process.WaitForExit();
        return new RcloneResult(process.ExitCode, output.ToString(), error.ToString());
    }

    /// <summary>The ZenTune remotes (named zentune-…) that are set up, not counting the encryption layers.</summary>
    public static async Task<List<string>> ListRemotesAsync()
    {
        if (!IsInstalled() || !File.Exists(ConfigPath)) return [];
        var r = await RunAsync(["listremotes"], TimeSpan.FromSeconds(20));
        if (!r.Ok) return [];
        return r.Output.Split('\n')
            .Select(l => l.Trim().TrimEnd(':'))
            .Where(n => n.StartsWith("zentune-", StringComparison.Ordinal) && !n.EndsWith("-crypt", StringComparison.Ordinal))
            .ToList();
    }

    /// <summary>
    /// Sets up a cloud account. rclone opens the browser for the login and waits for it (up to 5 minutes).
    /// Afterwards the connection is tested by listing the top level.
    /// </summary>
    public static async Task ConnectAsync(string remoteName, string type, string[] extra, CancellationToken ct = default)
    {
        var args = new List<string> { "config", "create", remoteName, type };
        args.AddRange(extra);

        KillStrayProcesses();
        bool existed = (await ListRemotesAsync()).Contains(remoteName);
        try
        {
            var create = await RunAsync(args, TimeSpan.FromMinutes(5), ct, null, "\n\n\n\n\n\n");
            if (!create.Ok) throw new InvalidOperationException("The login did not finish.\n" + create.ErrorSummary);

            var test = await RunAsync(["lsd", remoteName + ":"], TimeSpan.FromSeconds(60), ct);
            if (!test.Ok) throw new InvalidOperationException("Logged in, but ZenTune could not read the account.\n" + test.ErrorSummary);
        }
        catch
        {
            // Do not leave a half-made account behind (it would show up in the list but never work)
            if (!existed)
            {
                try { await RunAsync(["config", "delete", remoteName], TimeSpan.FromSeconds(20)); } catch { /* nothing more to do */ }
            }
            throw;
        }
    }

    /// <summary>
    /// A login that was left waiting (window closed, app restarted) keeps rclone running and holds port 53682, so the next login fails.
    /// This ends rclone processes that were started from ZenTune's own rclone.exe, unless ZenTune itself is uploading right now.
    /// </summary>
    private static void KillStrayProcesses()
    {
        if (CloudSync.Busy) return;
        foreach (var p in Process.GetProcessesByName("rclone"))
        {
            try
            {
                var path = p.MainModule?.FileName;
                if (path is not null && string.Equals(Path.GetFullPath(path), Path.GetFullPath(ExePath), StringComparison.OrdinalIgnoreCase))
                {
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(3000);
                }
            }
            catch { /* not ours or already gone */ }
            finally { p.Dispose(); }
        }
    }

    public static async Task DeleteRemoteAsync(string remoteName)
    {
        foreach (var name in new[] { remoteName + "-crypt", remoteName })
        {
            var r = await RunAsync(["config", "delete", name], TimeSpan.FromSeconds(20));
            if (!r.Ok && name == remoteName) throw new InvalidOperationException(r.ErrorSummary);
        }
    }

    /// <summary>True when the encryption layer for this remote already exists.</summary>
    public static async Task<bool> CryptExistsAsync(string remoteName)
    {
        var r = await RunAsync(["listremotes"], TimeSpan.FromSeconds(20));
        return r.Ok && r.Output.Split('\n').Any(l => l.Trim() == remoteName + "-crypt:");
    }

    /// <summary>
    /// Adds an encryption layer (rclone crypt) on top of a remote: file names and contents are encrypted before they leave the PC.
    /// rclone hides the passwords in its config, but they can be read back with rclone, so the config must stay private.
    /// </summary>
    public static async Task CreateCryptAsync(string remoteName, string password, string salt)
    {
        var r = await RunAsync(
        [
            "config", "create", remoteName + "-crypt", "crypt",
            $"remote={remoteName}:ZenTune-backup-encrypted",
            $"password={password}",
            $"password2={salt}",
            "filename_encryption=standard",
            "directory_name_encryption=true",
            "--obscure",
        ], TimeSpan.FromSeconds(30));
        if (!r.Ok) throw new InvalidOperationException("Could not set up encryption.\n" + r.ErrorSummary);
    }

    public static string GeneratePassword() =>
        RandomNumberGenerator.GetString("ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789", 24);
}
