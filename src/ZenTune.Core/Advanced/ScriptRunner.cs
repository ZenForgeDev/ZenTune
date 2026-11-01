using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;

namespace ZenTune.Core.Advanced;

/// <summary>
/// Runs a generated PowerShell script, as admin when needed. Everything that needs admin rights in one
/// action goes into one script, so the user sees one UAC prompt instead of many.
/// Scripts and their logs are kept in %LOCALAPPDATA%\ZenTune\Scripts so every change can be inspected.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ScriptRunner
{
    public sealed record Result(int ExitCode, string Log, string ScriptPath)
    {
        public bool Ok => ExitCode == 0;
    }

    /// <summary>Helpers every script starts with. Errors are logged, never shown as red text to the user.</summary>
    private const string Prelude = """
        $ErrorActionPreference = 'Continue'
        $ProgressPreference = 'SilentlyContinue'
        $script:failures = 0
        function Log([string]$m) { Add-Content -LiteralPath $LogPath -Value $m -Encoding UTF8 }
        function Fail([string]$m) { $script:failures++; Log ("ERROR: " + $m) }
        function SetReg([string]$p, [string]$n, [string]$t, $v) {
            try {
                if (-not (Test-Path -LiteralPath $p)) { New-Item -Path $p -Force -ErrorAction Stop | Out-Null }
                New-ItemProperty -LiteralPath $p -Name $n -PropertyType $t -Value $v -Force -ErrorAction Stop | Out-Null
                Log ("set " + $p + "\" + $n)
            } catch { Fail ("set " + $p + "\" + $n + ": " + $_) }
        }
        function DelReg([string]$p, [string]$n) {
            try {
                if (Test-Path -LiteralPath $p) { Remove-ItemProperty -LiteralPath $p -Name $n -ErrorAction SilentlyContinue }
                Log ("removed " + $p + "\" + $n)
            } catch { Fail ("remove " + $p + "\" + $n + ": " + $_) }
        }
        function SetSvc([string]$n, [string]$t) {
            $map = @{ Automatic = 'auto'; Manual = 'demand'; Disabled = 'disabled'; AutomaticDelayedStart = 'delayed-auto' }
            $out = & sc.exe config $n start= $map[$t] 2>&1
            if ($LASTEXITCODE -ne 0) { Fail ("service " + $n + " -> " + $t + ": " + $out) } else { Log ("service " + $n + " -> " + $t) }
        }
        function Try-Run([string]$what, [scriptblock]$block) {
            try { & $block; Log ("ok: " + $what) } catch { Fail ($what + ": " + $_) }
        }

        """;

    private const string Epilogue = """

        if ($script:failures -gt 0) { exit 1 } else { exit 0 }
        """;

    public static Result Run(string name, string body, bool elevated, TimeSpan? timeout = null)
    {
        var dir = Path.Combine(AppPaths.Root, "Scripts");
        Directory.CreateDirectory(dir);

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        var script = Path.Combine(dir, $"{stamp}-{name}.ps1");
        var log = Path.Combine(dir, $"{stamp}-{name}.log");

        var text = new StringBuilder()
            .AppendLine($"$LogPath = {Quote(log)}")
            .Append(Prelude)
            .AppendLine(body)
            .Append(Epilogue)
            .ToString();

        // Windows PowerShell 5.1 needs a BOM to read non-English characters correctly
        File.WriteAllText(script, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var psi = new ProcessStartInfo("powershell.exe",
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\"")
        {
            UseShellExecute = elevated,
            CreateNoWindow = !elevated,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        if (elevated) psi.Verb = "runas";

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new OperationCanceledException("The admin prompt was cancelled, so nothing was changed.");
        }

        if (process is null) throw new InvalidOperationException("Could not start PowerShell.");

        using (process)
        {
            if (!process.WaitForExit(timeout ?? TimeSpan.FromMinutes(10)))
            {
                try { process.Kill(); } catch { }
                throw new TimeoutException("The change took too long and was stopped.");
            }

            string logText = File.Exists(log) ? File.ReadAllText(log) : "";
            return new Result(process.ExitCode, logText, script);
        }
    }

    /// <summary>A PowerShell single-quoted string literal.</summary>
    public static string Quote(string s) => "'" + s.Replace("'", "''") + "'";
}
