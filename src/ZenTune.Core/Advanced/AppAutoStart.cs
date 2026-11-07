using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;

namespace ZenTune.Core.Advanced;

/// <summary>
/// "Start with Windows": a Task Scheduler task that starts ZenTune a few seconds after you sign in, quietly in the tray,
/// plus the choice to keep ZenTune in the tray when the window is closed.
/// </summary>
[SupportedOSPlatform("windows")]
public static class AppAutoStart
{
    /// <summary>Start argument the autostart task passes: ZenTune then stays in the tray instead of opening its window.</summary>
    public const string TrayArg = "--tray";

    private const string TaskFolder = "\\ZenTune\\";
    private const string TaskName = "ZenTune autostart";

    private static string TrayFlagFile => Path.Combine(AppPaths.Root, "run-in-tray.txt");

    /// <summary>When on, the window's close button hides ZenTune in the tray instead of quitting.</summary>
    public static bool KeepInTray
    {
        get => File.Exists(TrayFlagFile);
        set
        {
            try
            {
                if (value) File.WriteAllText(TrayFlagFile, "on");
                else if (File.Exists(TrayFlagFile)) File.Delete(TrayFlagFile);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Could not save the tray setting. " + ex.Message);
            }
        }
    }

    /// <summary>True when the autostart task exists.</summary>
    public static bool IsEnabled()
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", $"/Query /TN \"{TaskFolder}{TaskName}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            if (!p.WaitForExit(8000))
            {
                try { p.Kill(); } catch { }
                return false;
            }
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The program file the autostart task starts, or null when there is no task.</summary>
    public static string? TaskTarget()
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", $"/Query /TN \"{TaskFolder}{TaskName}\" /XML")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            var xml = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            if (!p.WaitForExit(8000)) { try { p.Kill(); } catch { } return null; }
            if (p.ExitCode != 0) return null;

            var m = System.Text.RegularExpressions.Regex.Match(xml, "<Command>(.*?)</Command>", System.Text.RegularExpressions.RegexOptions.Singleline);
            return m.Success ? System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Trim().Trim('"') : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Creates (or replaces) the task for this copy of ZenTune.exe.</summary>
    public static void Enable()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) throw new InvalidOperationException("ZenTune could not find its own program file.");

        string user = WindowsIdentity.GetCurrent().Name;
        string workDir = Path.GetDirectoryName(exe) ?? Environment.SystemDirectory;

        var sb = new StringBuilder();
        sb.AppendLine("Try-Run 'create the autostart task' {");
        sb.AppendLine("    $exe = " + ScriptRunner.Quote(exe));
        sb.AppendLine("    $workDir = " + ScriptRunner.Quote(workDir));
        sb.AppendLine("    $user = " + ScriptRunner.Quote(user));
        sb.AppendLine("    $name = " + ScriptRunner.Quote(TaskName));
        sb.AppendLine("    $action = New-ScheduledTaskAction -Execute $exe -Argument " + ScriptRunner.Quote(TrayArg) + " -WorkingDirectory $workDir");
        sb.AppendLine("    $trigger = New-ScheduledTaskTrigger -AtLogOn -User $user");
        sb.AppendLine("    $trigger.Delay = 'PT15S'");
        sb.AppendLine("    $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited");
        sb.AppendLine("    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero)");
        sb.AppendLine("    Register-ScheduledTask -TaskPath '\\ZenTune\\' -TaskName $name -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Description 'Starts ZenTune when you sign in' -Force -ErrorAction Stop | Out-Null");
        sb.AppendLine("    if (-not (Get-ScheduledTask -TaskPath '\\ZenTune\\' -TaskName $name -ErrorAction SilentlyContinue)) { throw 'The task was not created.' }");
        sb.AppendLine("}");

        Apply("autostart-on", sb.ToString());
        if (!IsEnabled()) throw new InvalidOperationException("The autostart task was not created. Look in " + Path.Combine(AppPaths.Root, "Scripts") + " for the log.");
    }

    /// <summary>Removes the task.</summary>
    public static void Disable()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Try-Run 'remove the autostart task' {");
        sb.AppendLine("    $name = " + ScriptRunner.Quote(TaskName));
        sb.AppendLine("    if (Get-ScheduledTask -TaskPath '\\ZenTune\\' -TaskName $name -ErrorAction SilentlyContinue) {");
        sb.AppendLine("        Unregister-ScheduledTask -TaskPath '\\ZenTune\\' -TaskName $name -Confirm:$false -ErrorAction Stop");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        Apply("autostart-off", sb.ToString());
        if (IsEnabled()) throw new InvalidOperationException("The autostart task is still there. Look in " + Path.Combine(AppPaths.Root, "Scripts") + " for the log.");
    }

    // First without admin rights. If Windows refuses, once more with the admin prompt.
    private static void Apply(string name, string script)
    {
        var first = ScriptRunner.Run(name, script, elevated: false);
        if (first.Ok) return;

        var second = ScriptRunner.Run(name, script, elevated: true);
        if (!second.Ok)
            throw new InvalidOperationException("Windows could not change the startup task:\n" + Errors(second));
    }

    private static string Errors(ScriptRunner.Result r)
    {
        var lines = r.Log.Split('\n').Where(l => l.StartsWith("ERROR")).Select(l => l.Trim()).Take(5).ToList();
        return lines.Count > 0 ? string.Join("\n", lines) : $"PowerShell exited with code {r.ExitCode}. See {r.ScriptPath}";
    }
}
