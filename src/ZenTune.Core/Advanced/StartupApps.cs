using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using ZenTune.Core.Changes;

namespace ZenTune.Core.Advanced;

public enum StartupSource { RunKey, StartupFolder }

/// <summary>One app that Windows starts when you sign in.</summary>
public sealed class StartupEntry
{
    /// <summary>Stable id used to find this entry in the undo journal.</summary>
    public required string Key { get; init; }

    public required string Name { get; init; }
    public required string Command { get; init; }
    public required StartupSource Source { get; init; }

    /// <summary>True for entries installed for all users. Fast startup still only changes how it starts for you.</summary>
    public required bool Machine { get; init; }

    /// <summary>"Run", "Run32" or "StartupFolder": the StartupApproved key that switches this entry on or off.</summary>
    public required string ApprovalSubKey { get; init; }

    /// <summary>The value name inside that key: the Run value name, or the file name for Startup folder items.</summary>
    public required string ApprovalName { get; init; }

    /// <summary>False when the entry is switched off in Windows (Settings or Task Manager).</summary>
    public required bool Enabled { get; init; }

    public string? Exe { get; init; }
    public string Args { get; init; } = "";
    public string? WorkDir { get; init; }

    /// <summary>Why this entry cannot be made fast, or null when it can.</summary>
    public string? Problem { get; init; }

    /// <summary>Start arguments chosen in ZenTune (only while Fast startup is on). Null means the original arguments.</summary>
    public string? CustomArgs { get; set; }

    public string EffectiveArgs => CustomArgs ?? Args;

    public bool FastOn { get; set; }
    public bool Running { get; set; }
    public double? SecondsAfterSignIn { get; set; }

    public string Where =>
        Machine ? "Installed for all users"
        : Source == StartupSource.StartupFolder ? "Your Startup folder"
        : "Your startup list";
}

/// <summary>
/// "Fast startup" per app. Windows waits a while before it starts the apps in your Run list and Startup folder.
/// Turning Fast startup on makes a Task Scheduler task that starts the app right when you sign in and switches
/// the original entry off (the same switch Task Manager uses), so the app does not start twice.
/// Undo deletes the task and puts the original entry back exactly as it was.
/// </summary>
[SupportedOSPlatform("windows")]
public static class StartupApps
{
    private const string JournalKind = "faststart";
    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunPath32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedRoot = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";
    private const string ProtectedProblem = "Windows parts and security software already start early, so ZenTune leaves them alone.";

    /// <summary>Windows' own entries and security software (like ESET and Windows Security) are never made "fast".</summary>
    private static string? ProtectedReason(string name, string? exe)
    {
        if (name.Equals("SecurityHealth", StringComparison.OrdinalIgnoreCase)) return ProtectedProblem;
        if (exe is null) return null;

        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrEmpty(windows) && exe.StartsWith(windows + "\\", StringComparison.OrdinalIgnoreCase)) return ProtectedProblem;

        string[] securityMarks = ["\\ESET\\", "\\Windows Defender\\", "\\Windows Security\\"];
        return securityMarks.Any(m => exe.Contains(m, StringComparison.OrdinalIgnoreCase)) ? ProtectedProblem : null;
    }

    // ---------------------------------------------------------------- Listing

    public static List<StartupEntry> List()
    {
        var fastKeys = new HashSet<string>(
            ChangeJournal.Active().Where(c => c.Kind == JournalKind).Select(c => c.Data.GetValueOrDefault("Key") ?? ""));

        var list = new List<StartupEntry>();
        AddRunKeys(list, Registry.CurrentUser, RunPath, "Run", machine: false);
        AddFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.Startup), machine: false);
        AddRunKeys(list, Registry.LocalMachine, RunPath, "Run", machine: true);
        AddRunKeys(list, Registry.LocalMachine, RunPath32, "Run32", machine: true);
        AddFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), machine: true);

        foreach (var e in list)
        {
            e.FastOn = fastKeys.Contains(e.Key);
            if (e.FastOn && ActiveChange(e.Key) is { } change && change.Data.TryGetValue("CustomArgs", out var custom))
                e.CustomArgs = custom;
        }
        FillTimings(list);

        return list.OrderBy(e => e.Machine).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static ChangeRecord? ActiveChange(string key) =>
        ChangeJournal.Active().FirstOrDefault(c => c.Kind == JournalKind && c.Data.GetValueOrDefault("Key") == key);

    private static void AddRunKeys(List<StartupEntry> list, RegistryKey root, string path, string approvalSub, bool machine)
    {
        try
        {
            using var key = root.OpenSubKey(path);
            if (key is null) return;

            foreach (var name in key.GetValueNames())
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string raw || string.IsNullOrWhiteSpace(raw))
                    continue;

                var (exe, args) = ParseCommand(raw);
                list.Add(new StartupEntry
                {
                    Key = (machine ? "mrun|" + approvalSub + "|" : "run|") + name,
                    Name = name,
                    Command = Environment.ExpandEnvironmentVariables(raw),
                    Source = StartupSource.RunKey,
                    Machine = machine,
                    ApprovalSubKey = approvalSub,
                    ApprovalName = name,
                    Enabled = IsEnabled(approvalSub, name, machine),
                    Exe = exe,
                    Args = args,
                    WorkDir = exe is null ? null : Path.GetDirectoryName(exe),
                    Problem = exe is null ? "ZenTune could not find the program this entry starts." : ProtectedReason(name, exe),
                });
            }
        }
        catch
        {
            // unreadable key: skip it
        }
    }

    private static void AddFolder(List<StartupEntry> list, string folder, bool machine)
    {
        try
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;

            foreach (var file in Directory.EnumerateFiles(folder))
            {
                string fileName = Path.GetFileName(file);
                if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;

                string? exe = file;
                string args = "";
                string? workDir = Path.GetDirectoryName(file);
                string? problem = null;

                if (file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    var (target, linkArgs, linkDir) = ReadShortcut(file);
                    if (string.IsNullOrWhiteSpace(target) || !File.Exists(target))
                    {
                        exe = null;
                        problem = "This shortcut does not point to a normal program, so ZenTune cannot start it from Task Scheduler.";
                    }
                    else
                    {
                        exe = target;
                        args = linkArgs;
                        workDir = string.IsNullOrWhiteSpace(linkDir) ? Path.GetDirectoryName(target) : linkDir;
                    }
                }

                list.Add(new StartupEntry
                {
                    Key = (machine ? "mfolder|" : "folder|") + file.ToLowerInvariant(),
                    Name = Path.GetFileNameWithoutExtension(file),
                    Command = file,
                    Source = StartupSource.StartupFolder,
                    Machine = machine,
                    ApprovalSubKey = "StartupFolder",
                    ApprovalName = fileName,
                    Enabled = IsEnabled("StartupFolder", fileName, machine),
                    Exe = exe,
                    Args = args,
                    WorkDir = workDir,
                    Problem = problem ?? ProtectedReason(Path.GetFileNameWithoutExtension(file), exe),
                });
            }
        }
        catch
        {
            // unreadable folder: skip it
        }
    }

    // ---------------------------------------------------------------- Command and shortcut parsing

    /// <summary>Splits a Run-key command into the program and its arguments. Exe is null when no real file is found.</summary>
    internal static (string? Exe, string Args) ParseCommand(string raw)
    {
        string cmd = Environment.ExpandEnvironmentVariables(raw.Trim());
        if (cmd.Length == 0) return (null, "");

        if (cmd[0] == '"')
        {
            int end = cmd.IndexOf('"', 1);
            if (end > 1)
                return (Resolve(cmd.Substring(1, end - 1)), cmd.Substring(end + 1).Trim());
        }

        var whole = Resolve(cmd);
        if (whole is not null) return (whole, "");

        // unquoted path followed by arguments: take the shortest start that is a real file
        for (int i = cmd.IndexOf(' '); i > 0; i = cmd.IndexOf(' ', i + 1))
        {
            var head = Resolve(cmd.Substring(0, i));
            if (head is not null) return (head, cmd.Substring(i + 1).Trim());
        }

        return (null, "");
    }

    private static string? Resolve(string program)
    {
        try
        {
            program = program.Trim();
            if (program.Length == 0) return null;
            if (Path.IsPathRooted(program)) return File.Exists(program) ? program : null;

            var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Prepend(Environment.GetFolderPath(Environment.SpecialFolder.System));

            foreach (var dir in dirs)
            {
                foreach (var name in new[] { program, program + ".exe" })
                {
                    var candidate = Path.Combine(dir.Trim().Trim('"'), name);
                    if (File.Exists(candidate)) return candidate;
                }
            }
        }
        catch
        {
            // fall through
        }
        return null;
    }

    private static (string Target, string Args, string WorkDir) ReadShortcut(string path)
    {
        object? shell = null;
        object? link = null;
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null) return ("", "", "");

            shell = Activator.CreateInstance(type);
            link = type.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
            if (link is null) return ("", "", "");

            var comLink = link;
            var linkType = comLink.GetType();
            string Get(string prop) => linkType.InvokeMember(prop, BindingFlags.GetProperty, null, comLink, null) as string ?? "";

            return (Environment.ExpandEnvironmentVariables(Get("TargetPath")),
                    Get("Arguments"),
                    Environment.ExpandEnvironmentVariables(Get("WorkingDirectory")));
        }
        catch
        {
            return ("", "", "");
        }
        finally
        {
            if (link is not null && Marshal.IsComObject(link)) Marshal.ReleaseComObject(link);
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell);
        }
    }

    // ---------------------------------------------------------------- StartupApproved (the on/off switch Windows uses)

    /// <summary>First byte even (02, 06) = on, odd (03) = off. A missing value means on.</summary>
    private static bool ApprovedIn(RegistryKey root, string sub, string name)
    {
        try
        {
            using var key = root.OpenSubKey(ApprovedRoot + "\\" + sub);
            if (key?.GetValue(name) is byte[] { Length: > 0 } data) return (data[0] & 1) == 0;
            return true;
        }
        catch
        {
            return true;
        }
    }

    private static bool IsEnabled(string sub, string name, bool machine) =>
        ApprovedIn(Registry.CurrentUser, sub, name) && (!machine || ApprovedIn(Registry.LocalMachine, sub, name));

    private static byte[]? ReadApproval(string sub, string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ApprovedRoot + "\\" + sub);
            return key?.GetValue(name) as byte[];
        }
        catch
        {
            return null;
        }
    }

    private static void WriteApproval(string sub, string name, byte[]? data)
    {
        using var key = Registry.CurrentUser.CreateSubKey(ApprovedRoot + "\\" + sub, writable: true)
            ?? throw new InvalidOperationException("Could not open the startup settings in the registry.");

        if (data is null) key.DeleteValue(name, throwOnMissingValue: false);
        else key.SetValue(name, data, RegistryValueKind.Binary);
    }

    // ---------------------------------------------------------------- Timing (how long after sign-in did it start?)

    private static DateTime? SignInTime()
    {
        int session = Process.GetCurrentProcess().SessionId;
        DateTime? best = null;
        foreach (var name in new[] { "sihost", "explorer" })
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                using (p)
                {
                    try
                    {
                        if (p.SessionId != session) continue;
                        var t = p.StartTime;
                        if (best is null || t < best) best = t;
                    }
                    catch
                    {
                        // process not readable
                    }
                }
            }
        }
        return best;
    }

    private static void FillTimings(List<StartupEntry> list)
    {
        DateTime? signIn;
        int session;
        try
        {
            signIn = SignInTime();
            session = Process.GetCurrentProcess().SessionId;
        }
        catch
        {
            return;
        }

        foreach (var e in list)
        {
            if (e.Exe is null) continue;
            string processName = Path.GetFileNameWithoutExtension(e.Exe);
            if (string.IsNullOrEmpty(processName)) continue;

            DateTime? earliest = null;
            try
            {
                foreach (var p in Process.GetProcessesByName(processName))
                {
                    using (p)
                    {
                        try
                        {
                            if (p.SessionId != session) continue;
                            var t = p.StartTime;
                            if (earliest is null || t < earliest) earliest = t;
                        }
                        catch
                        {
                            // process not readable
                        }
                    }
                }
            }
            catch
            {
                continue;
            }

            if (earliest is null) continue;
            e.Running = true;

            if (signIn is { } s)
            {
                double secs = (earliest.Value - s).TotalSeconds;
                if (secs >= 0 && secs <= 900) e.SecondsAfterSignIn = secs;
            }
        }
    }

    // ---------------------------------------------------------------- Turn on

    private static string TaskNameFor(StartupEntry e)
    {
        var clean = new StringBuilder();
        foreach (char c in e.Name)
            clean.Append(char.IsLetterOrDigit(c) || c == ' ' || c == '-' || c == '_' ? c : '_');

        string readable = clean.ToString().Trim();
        if (readable.Length > 40) readable = readable.Substring(0, 40).Trim();

        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(e.Key))).Substring(0, 6);
        return $"FastStart-{readable}-{hash}";
    }

    private static string Errors(ScriptRunner.Result r)
    {
        var lines = r.Log.Split('\n').Where(l => l.StartsWith("ERROR")).Select(l => l.Trim()).Take(5).ToList();
        return lines.Count > 0 ? string.Join("\n", lines) : $"PowerShell exited with code {r.ExitCode}. See {r.ScriptPath}";
    }

    /// <summary>
    /// Makes the app start from Task Scheduler at sign-in and switches the original entry off.
    /// Shows one admin prompt (Windows only lets administrators create scheduled tasks).
    /// </summary>
    public static void MakeFast(StartupEntry e, string? argsOverride = null)
    {
        if (e.Problem is not null) throw new InvalidOperationException(e.Problem);
        if (e.Exe is null) throw new InvalidOperationException("ZenTune could not find the program this entry starts.");
        if (!e.Enabled) throw new InvalidOperationException("This app is switched off in Windows. Turn it on there first.");
        if (ActiveChange(e.Key) is not null) return;

        string args = argsOverride is null ? e.Args : CleanArgs(argsOverride);
        string taskName = TaskNameFor(e);
        string user = WindowsIdentity.GetCurrent().Name;
        string workDir = string.IsNullOrWhiteSpace(e.WorkDir) ? (Path.GetDirectoryName(e.Exe) ?? Environment.SystemDirectory) : e.WorkDir;
        var oldApproval = ReadApproval(e.ApprovalSubKey, e.ApprovalName);

        var sb = new StringBuilder();
        sb.AppendLine("Try-Run 'create the startup task' {");
        sb.AppendLine("    $exe = " + ScriptRunner.Quote(e.Exe));
        sb.AppendLine("    $argLine = " + ScriptRunner.Quote(args));
        sb.AppendLine("    $workDir = " + ScriptRunner.Quote(workDir));
        sb.AppendLine("    $user = " + ScriptRunner.Quote(user));
        sb.AppendLine("    $name = " + ScriptRunner.Quote(taskName));
        sb.AppendLine("    if ($argLine) { $action = New-ScheduledTaskAction -Execute $exe -Argument $argLine -WorkingDirectory $workDir } else { $action = New-ScheduledTaskAction -Execute $exe -WorkingDirectory $workDir }");
        sb.AppendLine("    $trigger = New-ScheduledTaskTrigger -AtLogOn -User $user");
        sb.AppendLine("    $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited");
        sb.AppendLine("    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero)");
        sb.AppendLine("    Register-ScheduledTask -TaskPath '\\ZenTune\\' -TaskName $name -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Description 'Started by ZenTune Fast startup' -Force -ErrorAction Stop | Out-Null");
        sb.AppendLine("    if (-not (Get-ScheduledTask -TaskPath '\\ZenTune\\' -TaskName $name -ErrorAction SilentlyContinue)) { throw 'The task was not created.' }");
        sb.AppendLine("}");

        var result = ScriptRunner.Run("faststart", sb.ToString(), elevated: true);
        if (!result.Ok)
            throw new InvalidOperationException("Windows could not create the startup task:\n" + Errors(result));

        // record first, so Undo works even if the next step fails
        var data = new Dictionary<string, string>
        {
            ["Key"] = e.Key,
            ["Task"] = taskName,
            ["Sub"] = e.ApprovalSubKey,
            ["Value"] = e.ApprovalName,
            ["OldExisted"] = oldApproval is null ? "0" : "1",
            ["OldBytes"] = oldApproval is null ? "" : Convert.ToBase64String(oldApproval),
        };
        if (argsOverride is not null) data["CustomArgs"] = args;

        ChangeJournal.Add(new ChangeRecord
        {
            Title = "Fast startup: " + e.Name,
            Kind = JournalKind,
            Data = data,
        });

        // switch the original entry off, the same way Task Manager does (03 + time)
        var off = new byte[12];
        off[0] = 3;
        BitConverter.GetBytes(DateTime.Now.ToFileTime()).CopyTo(off, 4);
        try
        {
            WriteApproval(e.ApprovalSubKey, e.ApprovalName, off);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "The task was created, but the original entry could not be switched off, so the app may start twice. " +
                "Undo removes the task. " + ex.Message);
        }
    }

    // ---------------------------------------------------------------- Start arguments

    private static string CleanArgs(string args)
    {
        string a = (args ?? "").Trim();
        if (a.Length > 1000) throw new InvalidOperationException("The arguments are too long (1000 characters at most).");
        if (a.Any(c => char.IsControl(c))) throw new InvalidOperationException("The arguments can't contain line breaks or control characters.");
        return a;
    }

    /// <summary>
    /// Changes the arguments an app is started with. Only possible for apps with Fast startup on, because the task is what
    /// starts the app. Windows asks for admin rights once. Undo still puts everything back as it was.
    /// </summary>
    public static void SetArguments(StartupEntry e, string newArgs)
    {
        var change = ActiveChange(e.Key) ?? throw new InvalidOperationException("Turn on Fast startup for this app first.");
        if (e.Exe is null) throw new InvalidOperationException("ZenTune could not find the program this entry starts.");

        string args = CleanArgs(newArgs);
        string workDir = string.IsNullOrWhiteSpace(e.WorkDir) ? (Path.GetDirectoryName(e.Exe) ?? Environment.SystemDirectory) : e.WorkDir;

        var sb = new StringBuilder();
        sb.AppendLine("Try-Run 'change the startup arguments' {");
        sb.AppendLine("    $exe = " + ScriptRunner.Quote(e.Exe));
        sb.AppendLine("    $argLine = " + ScriptRunner.Quote(args));
        sb.AppendLine("    $workDir = " + ScriptRunner.Quote(workDir));
        sb.AppendLine("    $name = " + ScriptRunner.Quote(change.Data["Task"]));
        sb.AppendLine("    if (-not (Get-ScheduledTask -TaskPath '\\ZenTune\\' -TaskName $name -ErrorAction SilentlyContinue)) { throw 'The startup task was not found.' }");
        sb.AppendLine("    if ($argLine) { $action = New-ScheduledTaskAction -Execute $exe -Argument $argLine -WorkingDirectory $workDir } else { $action = New-ScheduledTaskAction -Execute $exe -WorkingDirectory $workDir }");
        sb.AppendLine("    Set-ScheduledTask -TaskPath '\\ZenTune\\' -TaskName $name -Action $action -ErrorAction Stop | Out-Null");
        sb.AppendLine("}");

        var result = ScriptRunner.Run("startup-args", sb.ToString(), elevated: true);
        if (!result.Ok)
            throw new InvalidOperationException("Windows could not change the startup task:\n" + Errors(result));

        ChangeJournal.Update(change.Id, c => c.Data["CustomArgs"] = args);
    }

    // ---------------------------------------------------------------- Undo

    /// <summary>Deletes the task and puts the original entry back. Called from <see cref="Tweaks.Undo"/>.</summary>
    internal static void Undo(ChangeRecord change)
    {
        var d = change.Data;

        var sb = new StringBuilder();
        sb.AppendLine("Try-Run 'remove the startup task' {");
        sb.AppendLine("    $name = " + ScriptRunner.Quote(d["Task"]));
        sb.AppendLine("    if (Get-ScheduledTask -TaskPath '\\ZenTune\\' -TaskName $name -ErrorAction SilentlyContinue) {");
        sb.AppendLine("        Unregister-ScheduledTask -TaskPath '\\ZenTune\\' -TaskName $name -Confirm:$false -ErrorAction Stop");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        var result = ScriptRunner.Run("undo-faststart", sb.ToString(), elevated: true);
        if (!result.Ok)
            throw new InvalidOperationException("Windows could not remove the startup task:\n" + Errors(result));

        byte[]? old = d.GetValueOrDefault("OldExisted") == "1" && !string.IsNullOrEmpty(d.GetValueOrDefault("OldBytes"))
            ? Convert.FromBase64String(d["OldBytes"])
            : null;
        WriteApproval(d["Sub"], d["Value"], old);
    }
}
