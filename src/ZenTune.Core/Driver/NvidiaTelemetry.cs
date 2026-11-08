using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using ZenTune.Core.Changes;

namespace ZenTune.Core.Driver;

/// <summary>A scheduled task NVIDIA's driver installs for crash and usage reporting.</summary>
public sealed record TelemetryTask(string Name, bool Disabled);

/// <summary>
/// Turns NVIDIA's telemetry scheduled tasks (their names start with NvTm) off and on again. This does not remove
/// anything. Part of the reporting is built into NVIDIA's display container and cannot be switched off from outside.
/// </summary>
public static class NvidiaTelemetry
{
    /// <summary>Finds the telemetry tasks and whether each one is turned off. Reading needs no admin rights.</summary>
    public static List<TelemetryTask> FindTasks()
    {
        var result = new List<TelemetryTask>();
        string csv = Run("/Query /FO CSV /NH");

        foreach (var line in csv.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = Regex.Matches(line, "\"([^\"]*)\"");
            if (fields.Count == 0) continue;

            string name = fields[0].Groups[1].Value;
            string leaf = name[(name.LastIndexOf('\\') + 1)..];
            if (!leaf.StartsWith("NvTm", StringComparison.OrdinalIgnoreCase)) continue;
            if (result.Any(t => t.Name == name)) continue; // schtasks repeats a task that runs more than one way

            result.Add(new TelemetryTask(name, IsDisabled(name)));
        }
        return result;
    }

    /// <summary>Turns the tasks off (one admin prompt) and records the change so it can be undone.</summary>
    public static void Disable(IReadOnlyList<string> names)
    {
        var safe = names.Where(IsSafeName).ToList();
        if (safe.Count == 0) return;

        RunElevated(safe, "/DISABLE");

        var stillOn = safe.Where(n => !IsDisabled(n)).ToList();
        if (stillOn.Count > 0)
            throw new InvalidOperationException($"Windows did not turn off {stillOn.Count} of the tasks.");

        ChangeJournal.Add(new ChangeRecord
        {
            Title = $"NVIDIA telemetry: {safe.Count} scheduled task(s) turned off",
            Kind = "nvidia-telemetry",
            Data = new Dictionary<string, string> { ["Tasks"] = string.Join("|", safe) },
        });
    }

    /// <summary>Called from <c>Tweaks.Undo</c>: turns the same tasks on again.</summary>
    public static void Undo(ChangeRecord change)
    {
        if (!change.Data.TryGetValue("Tasks", out var tasks) || string.IsNullOrWhiteSpace(tasks))
            throw new InvalidOperationException("This change has no saved tasks.");

        var names = tasks.Split('|', StringSplitOptions.RemoveEmptyEntries).Where(IsSafeName).ToList();
        RunElevated(names, "/ENABLE");
    }

    // ---------------------------------------------------------------- Helpers

    // The names go on a command line, so only plain ones are accepted
    private static bool IsSafeName(string name) =>
        name.Length > 0 && Regex.IsMatch(name, @"^[\\A-Za-z0-9_{}\-\.]+$");

    private static bool IsDisabled(string name)
    {
        string xml = Run($"/Query /TN {name} /XML");
        return xml.Contains("<Enabled>false</Enabled>", StringComparison.OrdinalIgnoreCase);
    }

    private static string Run(string arguments)
    {
        var psi = new ProcessStartInfo("schtasks.exe", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start schtasks.exe.");
        var error = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        error.Wait();
        return output;
    }

    private static void RunElevated(IEnumerable<string> names, string verb)
    {
        string command = string.Join(" & ", names.Select(n => $"schtasks.exe /Change /TN {n} {verb}"));
        var psi = new ProcessStartInfo("cmd.exe", "/c " + command)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        try
        {
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start cmd.exe.");
            p.WaitForExit();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException("The admin prompt was cancelled, so nothing was changed.");
        }
    }
}
