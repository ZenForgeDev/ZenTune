using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;

namespace ZenTune.Core.Driver;

/// <summary>One monitor's "G-Sync enabled" switch in the NVIDIA driver.</summary>
public sealed record MonitorVrrState(string Key, bool Enabled);

/// <summary>
/// The NVIDIA driver keeps a separate on/off switch for variable refresh rate per monitor. The NVIDIA Control
/// Panel's "Apply" button on the G-Sync page sets it; the driver settings database (see <see cref="NvidiaDrs"/>)
/// has no way to. On a freshly installed driver the switch is off, and G-Sync stays off whatever the
/// settings say. This class reads the switch (no admin needed) and can turn it on or off.
/// Writing needs the SYSTEM account (Administrators only have read access to this registry key), so
/// <see cref="SetAsync"/> asks Windows for admin rights once, then runs a one-time task as SYSTEM.
/// The driver only reads the switch after the graphics driver restarts, which is the same as pressing
/// Win+Ctrl+Shift+B; <see cref="RestartGraphicsDriver"/> does that.
/// Found by comparing the registry before and after the Control Panel's Apply button. Untested on other
/// driver versions: every value is validated first and anything unexpected is left alone.
/// </summary>
[SupportedOSPlatform("windows")]
public static class NvidiaMonitorVrr
{
    private const string BasePath = @"SYSTEM\CurrentControlSet\Services\nvlddmkm\State\DisplayDatabase";
    private const string ValueName = "MonitorDataRegistryKey";

    /// <summary>Reads every monitor the driver knows that has the switch. Empty if there are none or the layout is not the expected one.</summary>
    public static List<MonitorVrrState> Read()
    {
        var result = new List<MonitorVrrState>();
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var db = hklm.OpenSubKey(BasePath);
            if (db is null) return result;
            foreach (var name in db.GetSubKeyNames())
            {
                if (name.StartsWith("ADAPTER", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("CONNECTOR", StringComparison.OrdinalIgnoreCase)) continue;
                using var sub = db.OpenSubKey(name);
                if (sub?.GetValue(ValueName) is not byte[] v || !IsValid(v)) continue;
                result.Add(new MonitorVrrState(name, v[12] == 1));
            }
        }
        catch
        {
            // unreadable or different layout: report nothing, ZenTune then does not touch anything
        }
        return result;
    }

    /// <summary>True when at least one monitor has the switch and it is off.</summary>
    public static bool NeedsActivation(List<MonitorVrrState> monitors) => monitors.Any(m => !m.Enabled);

    // 20 bytes: db 02 00 00 | 14 00 00 00 | 00 00 00 00 | flag (dword) | checksum (dword)
    private static bool IsValid(byte[] v)
    {
        if (v.Length != 20 || v[0] != 0xDB || v[1] != 2 || v[4] != 0x14 || v[12] > 1) return false;
        int sum = 0;
        for (int i = 0; i < 16; i++) sum += v[i];
        return (sum & 0xFF) == v[16];
    }

    /// <summary>
    /// Turns the switch on (or off) for every monitor. Shows the Windows admin prompt once.
    /// Returns null on success, or a short reason. Does not restart the graphics driver.
    /// </summary>
    public static async Task<string?> SetAsync(bool enable)
    {
        string dir = Path.Combine(AppPaths.Root, "Gsync");
        Directory.CreateDirectory(dir);

        // Safety copy of the driver's display database (readable without admin)
        try
        {
            string reg = Path.Combine(dir, $"backup-{DateTime.Now:yyyyMMdd-HHmmss}.reg");
            using var export = Process.Start(new ProcessStartInfo("reg.exe",
                $"export \"HKLM\\{BasePath}\" \"{reg}\" /y")
            { CreateNoWindow = true, UseShellExecute = false });
            if (export is not null) await export.WaitForExitAsync();
        }
        catch { /* best effort */ }

        // The admin helper is passed on the command line, not as a file: a file in the user's folder could be
        // swapped by another program before it runs with admin rights.
        string resultFile = Path.Combine(dir, "result.txt");
        File.Delete(resultFile);
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(LauncherScript(enable, resultFile)));
        try
        {
            using var p = Process.Start(new ProcessStartInfo("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand {encoded}")
            { Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
            if (p is null) return "Windows did not start the helper.";
            await p.WaitForExitAsync();
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return "You cancelled the Windows admin prompt.";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }

        // The helper reports what it did (the registry key can only be read with admin rights)
        try
        {
            string text = File.Exists(resultFile) ? File.ReadAllText(resultFile) : "";
            var m = System.Text.RegularExpressions.Regex.Match(text, @"valid=(\d+) ok=(\d+)");
            if (!m.Success) return "The admin helper did not report back.";
            int valid = int.Parse(m.Groups[1].Value), ok = int.Parse(m.Groups[2].Value);
            if (valid == 0) return "ZenTune could not find a monitor switch in the driver.";
            if (ok != valid) return "The change did not stick. Use the NVIDIA Control Panel's G-Sync page and press Apply instead.";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
        return null;
    }

    // Runs with admin rights: puts the worker in a folder only admins can write to, runs it once as SYSTEM
    // through a temporary scheduled task, then removes the task and the folder.
    private static string LauncherScript(bool enable, string resultFile) => $$"""
        $ErrorActionPreference = 'Stop'
        $d = Join-Path $env:ProgramData 'ZenTune-Gsync'
        $name = 'ZenTuneGsyncOnce'
        try {
          New-Item -ItemType Directory -Force -Path $d | Out-Null
          icacls $d /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
          $w = @'
        {{WorkerScript(enable)}}
        '@
          Set-Content -LiteralPath (Join-Path $d 'worker.ps1') -Value $w -Encoding ASCII
          Remove-Item -LiteralPath (Join-Path $d 'done.txt') -Force -ErrorAction SilentlyContinue
          schtasks /create /tn $name /tr "powershell.exe -NoProfile -ExecutionPolicy Bypass -File $d\worker.ps1" /sc once /st 00:00 /ru SYSTEM /rl HIGHEST /f | Out-Null
          schtasks /run /tn $name | Out-Null
          for ($i = 0; $i -lt 60; $i++) { if (Test-Path -LiteralPath (Join-Path $d 'done.txt')) { break }; Start-Sleep -Milliseconds 500 }
          Copy-Item -LiteralPath (Join-Path $d 'done.txt') -Destination '{{resultFile}}' -Force -ErrorAction SilentlyContinue
        } finally {
          schtasks /delete /tn $name /f 2>$null | Out-Null
          Remove-Item -LiteralPath $d -Recurse -Force -ErrorAction SilentlyContinue
        }
        """;

    private static string WorkerScript(bool enable) => $$"""
        $ErrorActionPreference = 'Stop'
        $flag = {{(enable ? 1 : 0)}}
        $valid = 0; $ok = 0
        $base = 'HKLM:\{{BasePath}}'
        try {
          foreach ($k in Get-ChildItem -LiteralPath $base) {
            $n = $k.PSChildName
            if ($n -like 'ADAPTER*' -or $n -like 'CONNECTOR*') { continue }
            $p = Get-ItemProperty -LiteralPath $k.PSPath -Name {{ValueName}} -ErrorAction SilentlyContinue
            if ($null -eq $p) { continue }
            $v = [byte[]]($p.{{ValueName}}).Clone()
            if ($v.Length -ne 20 -or $v[0] -ne 0xdb -or $v[1] -ne 2 -or $v[4] -ne 0x14) { continue }
            $sum = 0; for ($i = 0; $i -lt 16; $i++) { $sum += $v[$i] }
            if (($sum -band 255) -ne $v[16]) { continue }
            $v[12] = $flag
            $sum = 0; for ($i = 0; $i -lt 16; $i++) { $sum += $v[$i] }
            $v[16] = $sum -band 255
            Set-ItemProperty -LiteralPath $k.PSPath -Name {{ValueName}} -Value ([byte[]]$v) -Type Binary
            $valid++
            $chk = (Get-ItemProperty -LiteralPath $k.PSPath -Name {{ValueName}}).{{ValueName}}
            if ($chk[12] -eq $flag) { $ok++ }
          }
        } finally {
          Set-Content -LiteralPath (Join-Path $env:ProgramData 'ZenTune-Gsync\done.txt') -Value "valid=$valid ok=$ok" -Encoding ASCII
        }
        """;

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    /// <summary>
    /// Restarts the graphics driver by pressing Win+Ctrl+Shift+B, the way a person would. The screens
    /// go black for a second. Needed so the driver notices the changed switch.
    /// </summary>
    public static async Task RestartGraphicsDriver()
    {
        const byte Win = 0x5B, Ctrl = 0x11, Shift = 0x10, B = 0x42;
        const uint Up = 0x0002;
        foreach (var k in new[] { Win, Ctrl, Shift, B }) { keybd_event(k, 0, 0, UIntPtr.Zero); await Task.Delay(30); }
        foreach (var k in new[] { B, Shift, Ctrl, Win }) { keybd_event(k, 0, Up, UIntPtr.Zero); await Task.Delay(30); }
        await Task.Delay(2500); // let the screens come back
    }
}
