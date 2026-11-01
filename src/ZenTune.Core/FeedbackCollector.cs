using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ZenTune.Core.Ai;

namespace ZenTune.Core;

public sealed record FeedbackOptions(
    string Note,
    bool IncludeGameCrashes,
    bool IncludeSavedAi,
    bool AskGeminiForSummary);

public sealed record FeedbackResult(string ZipPath, string Report, IReadOnlyList<string> Files);

/// <summary>
/// Collects what a developer needs to understand a problem into one zip file on the desktop
/// (ZenTuneFeedbackZip01.zip, 02, ...; an earlier zip is never overwritten).
/// Never included: the Gemini API key, game memory dumps, dxdiag files, game accounts.
/// Names of the Windows user and the PC are replaced before anything is written.
/// </summary>
public static class FeedbackCollector
{
    private const int MaxLogBytes = 200_000;
    private const int MaxCrashReports = 3;

    private static readonly string[] CrashInfoKeys =
    [
        "build_number", "build_exe_datetime", "changelist", "crash_signature", "crash_time", "error_type",
        "exception_code", "exception_type", "crashed_module_file", "crashed_module_version",
        "executable_name", "engine_runtime_total_fg_ms", "crashes_since_boot",
    ];

    public static async Task<FeedbackResult> CollectAsync(FeedbackOptions options, CancellationToken ct = default)
    {
        var files = new List<(string Name, string Content)>();
        var report = new StringBuilder();

        // ---- This PC
        report.AppendLine("ZenTune feedback report");
        report.AppendLine($"Created: {DateTime.Now:yyyy-MM-dd HH:mm}");
        report.AppendLine($"ZenTune: {AppVersion()}");
        report.AppendLine($"Windows: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        report.AppendLine($"Logical processors: {Environment.ProcessorCount}");
        report.AppendLine($"GameInput: {GameInputVersion()}");
        report.AppendLine();

        if (!string.IsNullOrWhiteSpace(options.Note))
        {
            report.AppendLine("What the user wrote:");
            report.AppendLine(options.Note.Trim());
            report.AppendLine();
        }

        // ---- Game crashes
        if (options.IncludeGameCrashes)
        {
            var crashes = ReadGameCrashes(out var extraFiles);
            report.AppendLine("Call of Duty crash reports (newest first):");
            report.AppendLine(crashes.Length == 0 ? "(none found)" : crashes);
            report.AppendLine();
            files.AddRange(extraFiles);
        }

        // ---- ZenTune's own crash log
        var crashLog = Path.Combine(AppPaths.Root, "crash.log");
        var ownCrash = ReadTail(crashLog, MaxLogBytes);
        if (ownCrash is not null)
        {
            files.Add(("zentune-crash.log", ownCrash));
            report.AppendLine("ZenTune crash log: included (zentune-crash.log)");
        }
        else
        {
            report.AppendLine("ZenTune crash log: none (ZenTune itself has not crashed)");
        }

        // ---- Saved AI settings (no key in this file)
        if (options.IncludeSavedAi)
        {
            var saved = ReadTail(Path.Combine(AppPaths.Root, "ai-saved.json"), 1_000_000);
            if (saved is not null)
            {
                files.Add(("ai-saved.json", saved));
                report.AppendLine("Saved AI settings: included (ai-saved.json)");
            }
        }
        report.AppendLine();

        // ---- Optional short English summary from Gemini, made from the redacted text only
        var plain = Redact(report.ToString());
        string? summary = null;
        if (options.AskGeminiForSummary)
            summary = await SummariseAsync(plain, ct);

        var finalReport = new StringBuilder();
        if (summary is not null)
        {
            finalReport.AppendLine("Summary written by Gemini (may be wrong, check the details below):");
            finalReport.AppendLine(summary);
            finalReport.AppendLine();
        }
        finalReport.Append(plain);
        var reportText = finalReport.ToString();

        // ---- Write the zip
        var entries = new List<(string Name, string Content)> { ("report.txt", reportText) };
        foreach (var (name, content) in files)
            entries.Add((name, Redact(content)));

        var zipPath = NextZipPath();
        await Task.Run(() =>
        {
            using var stream = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
            foreach (var (name, content) in entries)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        }, ct);

        return new FeedbackResult(zipPath, reportText, entries.Select(e => e.Name).ToList());
    }

    // ---------------------------------------------------------------- Call of Duty

    private static string ReadGameCrashes(out List<(string Name, string Content)> extra)
    {
        extra = [];
        var sb = new StringBuilder();
        var dir = Path.Combine(AppPaths.LocalAppData, "Activision", "Call of Duty", "crash_reports");
        if (!Directory.Exists(dir)) return "";

        var zips = new DirectoryInfo(dir).GetFiles("*.zip")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Take(MaxCrashReports)
            .ToList();

        bool gameInput4 = (GameInputVersion() ?? "").StartsWith("4.", StringComparison.Ordinal);
        bool watchdog = false;

        int n = 0;
        foreach (var file in zips)
        {
            n++;
            sb.AppendLine($"- {file.Name}");
            try
            {
                using var fs = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var zip = new ZipArchive(fs, ZipArchiveMode.Read);

                var info = zip.GetEntry("info.json");
                if (info is not null)
                {
                    using var r = new StreamReader(info.Open());
                    using var doc = JsonDocument.Parse(r.ReadToEnd());
                    foreach (var key in CrashInfoKeys)
                    {
                        if (!doc.RootElement.TryGetProperty(key, out var v)) continue;
                        var text = v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();
                        sb.AppendLine($"    {key}: {text}");
                        if (key == "exception_code" && text == "0x00001337") watchdog = true;
                    }
                }

                var stack = zip.GetEntry("stacktrace_crash.csv");
                if (stack is not null)
                {
                    using var r = new StreamReader(stack.Open());
                    var csv = r.ReadToEnd();
                    extra.Add(($"cod-crash{n}-stacktrace.csv", csv));
                    foreach (var line in csv.Split('\n'))
                    {
                        var parts = line.Trim().Split(',');
                        if (parts.Length >= 6 && parts[0] == "1")
                            sb.AppendLine($"    crash frame: {parts[5]}");
                    }
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"    (could not read: {ex.GetType().Name})");
            }
        }

        if (watchdog && gameInput4)
            sb.AppendLine("Note: exception 0x00001337 (watchdog) with GameInput 4.x is a known problem, see microsoftconnect/GameInput issue 53. GameInput 3.5.283 works.");
        return sb.ToString().TrimEnd();
    }

    // ---------------------------------------------------------------- Gemini

    private static async Task<string?> SummariseAsync(string redactedReport, CancellationToken ct)
    {
        var ai = AiConfig.Load();
        if (string.IsNullOrWhiteSpace(ai.ApiKey)) return null;

        var prompt =
            "Below is a diagnostics report from ZenTune, a game settings optimizer for Windows. " +
            "Write a short plain-English summary for the developer: what the user seems to have a problem with, " +
            "the most likely cause given the details, and anything missing. Maximum 8 lines. Do not invent facts. " +
            "Reply as JSON: {\"summary\": \"...\"}\n\n" + redactedReport;
        try
        {
            var json = await GeminiClient.GenerateJsonAsync(ai.ApiKey!, ai.Model, prompt, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.String)
                return Redact(s.GetString() ?? "").Trim();
        }
        catch (OperationCanceledException) { throw; }
        catch { /* the report is useful without a summary */ }
        return null;
    }

    // ---------------------------------------------------------------- Helpers

    private static string NextZipPath()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        for (int i = 1; ; i++)
        {
            var path = Path.Combine(desktop, $"ZenTuneFeedbackZip{i:00}.zip");
            if (!File.Exists(path)) return path;
        }
    }

    private static string? ReadTail(string path, int maxBytes)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length > maxBytes) fs.Seek(-maxBytes, SeekOrigin.End);
            using var r = new StreamReader(fs, Encoding.UTF8);
            return r.ReadToEnd();
        }
        catch { return null; }
    }

    private static string AppVersion()
    {
        var v = Assembly.GetEntryAssembly()?.GetName().Version;
        return v is null ? "unknown" : v.ToString();
    }

    private static string? GameInputVersion()
    {
        try
        {
            var path = Path.Combine(Environment.SystemDirectory, "GameInputRedist.dll");
            return File.Exists(path) ? FileVersionInfo.GetVersionInfo(path).FileVersion : "not installed";
        }
        catch { return null; }
    }

    /// <summary>Replaces the Windows user name, the PC name, e-mail addresses and anything that looks like a Gemini key.</summary>
    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        text = Regex.Replace(text, @"(?i)([A-Z]:\\Users\\)[^\\/:*?""<>|\r\n]+", "$1<user>");
        text = Regex.Replace(text, @"AIza[0-9A-Za-z_\-]{30,}", "<key>");
        text = Regex.Replace(text, @"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", "<email>");

        foreach (var (name, replacement) in new[] { (Environment.UserName, "<user>"), (Environment.MachineName, "<pc>") })
        {
            if (name is { Length: >= 3 })
                text = Regex.Replace(text, Regex.Escape(name), replacement, RegexOptions.IgnoreCase);
        }
        return text;
    }
}
