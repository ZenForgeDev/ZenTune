using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using ZenTune.Core.Games;

namespace ZenTune.Core.Advanced;

/// <summary>Something the PC cleaner can empty.</summary>
public sealed class CleanTarget
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public bool NeedsAdmin { get; init; }

    /// <summary>Folders whose contents are deleted (the folders themselves are kept).</summary>
    public string[] Folders { get; init; } = [];

    /// <summary>Single files that are deleted.</summary>
    public string[] Files { get; init; } = [];

    /// <summary>Only files matching this inside the folders (default: everything).</summary>
    public string Pattern { get; init; } = "*";

    /// <summary>Selected by default in the list.</summary>
    public bool Recommended { get; init; } = true;

    /// <summary>For targets without a size we can measure up front (component store cleanup).</summary>
    public bool SizeUnknown { get; init; }
}

public sealed record CleanScan(CleanTarget Target, long Bytes, bool Readable);

/// <summary>
/// The PC cleaner. Only touches caches and leftovers Windows and apps rebuild on their own:
/// never documents, downloads, saves or settings.
/// </summary>
[SupportedOSPlatform("windows")]
public static class Cleaner
{
    private static string Win => Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static string Local => AppPaths.LocalAppData;
    private static string ProgramData => AppPaths.ProgramData;

    public static IReadOnlyList<CleanTarget> Targets { get; } =
    [
        new CleanTarget
        {
            Id = "usertemp",
            Title = "Temporary files",
            Description = "Files apps leave in your temp folder. Files in use are skipped.",
            Folders = [Path.GetTempPath()],
        },
        new CleanTarget
        {
            Id = "wintemp",
            Title = "Windows temporary files",
            Description = "Leftovers from installers and Windows itself.",
            NeedsAdmin = true,
            Folders = [Path.Combine(Win, "Temp")],
        },
        new CleanTarget
        {
            Id = "wudownload",
            Title = "Windows Update downloads",
            Description = "Update files Windows already installed. It downloads them again if it needs them.",
            NeedsAdmin = true,
            Folders = [Path.Combine(Win, "SoftwareDistribution", "Download")],
        },
        new CleanTarget
        {
            Id = "docache",
            Title = "Delivery Optimization cache",
            Description = "Copies of updates Windows keeps to share with other PCs.",
            NeedsAdmin = true,
            Folders = [Path.Combine(Win, "ServiceProfiles", "NetworkService", "AppData", "Local", "Microsoft", "Windows", "DeliveryOptimization", "Cache")],
        },
        new CleanTarget
        {
            Id = "appdumps",
            Title = "App crash dumps",
            Description = "Memory dumps saved when apps or games crashed. Only useful for developers.",
            Folders = [Path.Combine(Local, "CrashDumps")],
        },
        new CleanTarget
        {
            Id = "sysdumps",
            Title = "Windows crash dumps",
            Description = "Memory dumps from blue screens. Keep them if you are still looking into a crash.",
            NeedsAdmin = true,
            Folders = [Path.Combine(Win, "Minidump"), Path.Combine(Win, "LiveKernelReports")],
            Files = [Path.Combine(Win, "MEMORY.DMP")],
        },
        new CleanTarget
        {
            Id = "wer",
            Title = "Error reports",
            Description = "Reports Windows collected about crashes and hangs.",
            NeedsAdmin = true,
            Folders =
            [
                Path.Combine(Local, "Microsoft", "Windows", "WER"),
                Path.Combine(ProgramData, "Microsoft", "Windows", "WER", "ReportArchive"),
                Path.Combine(ProgramData, "Microsoft", "Windows", "WER", "ReportQueue"),
            ],
        },
        new CleanTarget
        {
            Id = "thumbs",
            Title = "Thumbnail cache",
            Description = "Picture previews File Explorer saved. They are made again when you open a folder.",
            Folders = [Path.Combine(Local, "Microsoft", "Windows", "Explorer")],
            Pattern = "thumbcache_*.db",
            Recommended = false,
        },
        new CleanTarget
        {
            Id = "recycle",
            Title = "Recycle Bin",
            Description = "Empties the Recycle Bin on all drives.",
            Recommended = false,
        },
        new CleanTarget
        {
            Id = "dism",
            Title = "Old Windows components",
            Description = "Removes old versions of Windows files left by updates (DISM component cleanup). Can take 5–15 minutes.",
            NeedsAdmin = true,
            Recommended = false,
            SizeUnknown = true,
        },
    ];

    // ---------------------------------------------------------------- Scan

    public static List<CleanScan> Scan() => Targets.Select(ScanOne).ToList();

    private static CleanScan ScanOne(CleanTarget t)
    {
        if (t.Id == "recycle") return new CleanScan(t, RecycleBinSize(), true);
        if (t.SizeUnknown) return new CleanScan(t, 0, false);

        long bytes = 0;
        bool readable = true;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };

        foreach (var folder in t.Folders.Where(Directory.Exists))
        {
            try
            {
                bytes += Directory.EnumerateFiles(folder, t.Pattern, options).Sum(FileOps.SizeOf);
            }
            catch (UnauthorizedAccessException)
            {
                readable = false;
            }
        }

        foreach (var file in t.Files)
        {
            try { if (File.Exists(file)) bytes += new FileInfo(file).Length; }
            catch { readable = false; }
        }

        return new CleanScan(t, bytes, readable);
    }

    // ---------------------------------------------------------------- Clean

    /// <summary>Cleans the chosen targets. All admin targets share one UAC prompt. Returns bytes freed.</summary>
    public static long Clean(IReadOnlyList<CleanTarget> chosen)
    {
        long before = chosen.Select(ScanOne).Sum(s => s.Bytes);

        foreach (var t in chosen.Where(t => !t.NeedsAdmin))
        {
            if (t.Id == "recycle")
            {
                SHEmptyRecycleBin(IntPtr.Zero, null, 0x1 | 0x2 | 0x4); // no confirm, no progress, no sound
                continue;
            }

            foreach (var folder in t.Folders.Where(Directory.Exists))
            {
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
                FileOps.DeleteFiles(Directory.EnumerateFiles(folder, t.Pattern, options).ToList());
                if (t.Pattern == "*") FileOps.RemoveEmptyFolders(folder);
            }
            FileOps.DeleteFiles(t.Files);
        }

        var admin = chosen.Where(t => t.NeedsAdmin).ToList();
        if (admin.Count > 0)
        {
            var sb = new StringBuilder();
            foreach (var t in admin)
            {
                switch (t.Id)
                {
                    case "docache":
                        sb.AppendLine("Try-Run 'delivery optimization cache' { Delete-DeliveryOptimizationCache -Force -ErrorAction Stop }");
                        break;
                    case "dism":
                        sb.AppendLine("Try-Run 'component cleanup' { & Dism.exe /Online /Cleanup-Image /StartComponentCleanup /Quiet | Out-Null }");
                        break;
                    default:
                        foreach (var folder in t.Folders)
                        {
                            var q = ScriptRunner.Quote(folder);
                            sb.AppendLine($"if (Test-Path -LiteralPath {q}) {{ Get-ChildItem -LiteralPath {q} -Force -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue }}");
                        }
                        foreach (var file in t.Files)
                            sb.AppendLine($"Remove-Item -LiteralPath {ScriptRunner.Quote(file)} -Force -ErrorAction SilentlyContinue");
                        sb.AppendLine($"Log {ScriptRunner.Quote("cleaned " + t.Id)}");
                        break;
                }
            }

            var timeout = admin.Any(t => t.Id == "dism") ? TimeSpan.FromMinutes(45) : TimeSpan.FromMinutes(10);
            ScriptRunner.Run("clean", sb.ToString(), elevated: true, timeout);
        }

        long after = chosen.Select(ScanOne).Sum(s => s.Bytes);
        return Math.Max(0, before - after);
    }

    // ---------------------------------------------------------------- Recycle Bin

    private static long RecycleBinSize()
    {
        var info = new SHQUERYRBINFO { cbSize = (uint)Marshal.SizeOf<SHQUERYRBINFO>() };
        return SHQueryRecycleBin(null, ref info) == 0 ? info.i64Size : 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO
    {
        public uint cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? rootPath, ref SHQUERYRBINFO info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? rootPath, uint flags);
}

/// <summary>Outside tools ZenTune can fetch, like WinUtil does.</summary>
[SupportedOSPlatform("windows")]
public static class ExternalTools
{
    public const string ShutUp10Url = "https://dl5.oo-software.com/files/ooshutup10/OOSU10.exe";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(3) };

    /// <summary>Downloads the newest O&amp;O ShutUp10++ from O&amp;O's own server and starts it. It asks for admin itself.</summary>
    public static async Task RunShutUp10Async()
    {
        var dir = Path.Combine(AppPaths.Root, "Tools");
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, "OOSU10.exe");

        // Reuse a download from the last week, otherwise get the newest version
        if (!File.Exists(exe) || File.GetLastWriteTime(exe) < DateTime.Now.AddDays(-7))
        {
            var tmp = exe + ".download";
            using (var response = await Http.GetAsync(ShutUp10Url, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                await using var file = File.Create(tmp);
                await response.Content.CopyToAsync(file);
            }
            File.Move(tmp, exe, overwrite: true);
        }

        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = dir });
    }
}
