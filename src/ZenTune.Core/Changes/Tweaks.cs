using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using ZenTune.Core.Advanced;
using ZenTune.Core.Driver;
using ZenTune.Core.Hardware;

namespace ZenTune.Core.Changes;

/// <summary>
/// The small Windows changes ZenTune can make. Each one records the old value in the
/// <see cref="ChangeJournal"/> before changing anything, and <see cref="Undo"/> puts it back.
/// Nothing here needs admin rights.
/// </summary>
[SupportedOSPlatform("windows")]
public static class Tweaks
{
    // ---------------------------------------------------------------- Registry (current user only)

    public static void SetUserDword(string key, string name, int value, string title)
    {
        using var k = Registry.CurrentUser.CreateSubKey(key, writable: true)
            ?? throw new InvalidOperationException($"Could not open HKCU\\{key}");

        RecordRegistry(k, key, name, title);
        k.SetValue(name, value, RegistryValueKind.DWord);
    }

    public static void SetUserString(string key, string name, string value, string title)
    {
        using var k = Registry.CurrentUser.CreateSubKey(key, writable: true)
            ?? throw new InvalidOperationException($"Could not open HKCU\\{key}");

        RecordRegistry(k, key, name, title);
        k.SetValue(name, value, RegistryValueKind.String);
    }

    public static int? ReadUserDword(string key, string name)
    {
        using var k = Registry.CurrentUser.OpenSubKey(key);
        return k?.GetValue(name) is int i ? i : null;
    }

    public static string? ReadUserString(string key, string name)
    {
        using var k = Registry.CurrentUser.OpenSubKey(key);
        return k?.GetValue(name) as string;
    }

    public static int? ReadMachineDword(string key, string name)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(key);
            return k?.GetValue(name) is int i ? i : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Writes a DWORD under HKEY_LOCAL_MACHINE. That needs admin rights, so Windows shows a UAC prompt
    /// for reg.exe. ZenTune itself keeps running as a normal user.
    /// </summary>
    public static void SetMachineDwordElevated(string key, string name, int value, string title)
    {
        int? old = ReadMachineDword(key, name);
        RunRegElevated($"add \"HKLM\\{key}\" /v {name} /t REG_DWORD /d {value} /f");

        var data = new Dictionary<string, string> { ["Key"] = key, ["Name"] = name, ["Existed"] = old is null ? "0" : "1" };
        if (old is not null) data["OldValue"] = old.Value.ToString();
        ChangeJournal.Add(new ChangeRecord { Title = title, Kind = "machine-registry", Data = data });
    }

    private static void RunRegElevated(string arguments)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("reg.exe", arguments)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
        };

        try
        {
            using var p = System.Diagnostics.Process.Start(psi)
                ?? throw new InvalidOperationException("Could not start reg.exe.");
            p.WaitForExit();
            if (p.ExitCode != 0) throw new InvalidOperationException($"Windows refused the change (reg.exe exit code {p.ExitCode}).");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException("The admin prompt was cancelled, so nothing was changed.");
        }
    }

    private static void RecordRegistry(RegistryKey k, string key, string name, string title)
    {
        var old = k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        var data = new Dictionary<string, string>
        {
            ["Key"] = key,
            ["Name"] = name,
            ["Existed"] = old is null ? "0" : "1",
        };
        if (old is not null)
        {
            data["OldKind"] = k.GetValueKind(name).ToString();
            data["OldValue"] = old switch
            {
                int i => i.ToString(),
                long l => l.ToString(),
                string s => s,
                _ => Convert.ToString(old) ?? ""
            };
        }

        ChangeJournal.Add(new ChangeRecord { Title = title, Kind = "registry", Data = data });
    }

    // ---------------------------------------------------------------- Refresh rate

    /// <summary>Changes a monitor's refresh rate and keeps the resolution.</summary>
    public static void SetRefreshRate(DisplayInfo display, int hz, string title)
    {
        int oldHz = (int)Math.Round(display.RefreshHz);
        ApplyRefreshRate(display.GdiName, display.Width, display.Height, hz);

        ChangeJournal.Add(new ChangeRecord
        {
            Title = title,
            Kind = "refresh",
            Data = new()
            {
                ["Device"] = display.GdiName,
                ["Width"] = display.Width.ToString(),
                ["Height"] = display.Height.ToString(),
                ["Hz"] = oldHz.ToString(),
            },
        });
    }

    private static void ApplyRefreshRate(string device, int width, int height, int hz)
    {
        // Find the exact Windows mode, so we never ask for something the monitor can't do
        var mode = DisplayDetector.NewDevMode();
        var best = (DisplayDetector.DEVMODE?)null;
        for (int n = 0; DisplayDetector.EnumDisplaySettings(device, n, ref mode); n++)
        {
            if (mode.dmPelsWidth == width && mode.dmPelsHeight == height && mode.dmDisplayFrequency == hz)
            {
                if (best is null || mode.dmBitsPerPel > best.Value.dmBitsPerPel) best = mode;
            }
            mode = DisplayDetector.NewDevMode();
        }

        if (best is null)
            throw new InvalidOperationException($"The screen does not offer {width} × {height} at {hz} Hz.");

        var target = best.Value;
        target.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY | DM_BITSPERPEL;

        int result = ChangeDisplaySettingsEx(device, ref target, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero);
        if (result != 0)
            throw new InvalidOperationException($"Windows refused the change (code {result}).");
    }

    // ---------------------------------------------------------------- Power plan

    public static readonly Guid PowerSaver = new("a1841308-3541-4fab-bc81-f71556f20b4a");
    public static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    public static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");

    public static Guid? ActivePowerPlan()
    {
        try
        {
            if (PowerGetActiveScheme(IntPtr.Zero, out var ptr) != 0) return null;
            try { return Marshal.PtrToStructure<Guid>(ptr); }
            finally { LocalFree(ptr); }
        }
        catch
        {
            return null;
        }
    }

    public static void SetPowerPlan(Guid plan, string title)
    {
        var old = ActivePowerPlan() ?? Balanced;
        var g = plan;
        uint err = PowerSetActiveScheme(IntPtr.Zero, ref g);
        if (err != 0) throw new InvalidOperationException($"Windows refused the power plan change (code {err}).");

        ChangeJournal.Add(new ChangeRecord
        {
            Title = title,
            Kind = "powerplan",
            Data = new() { ["Guid"] = old.ToString() },
        });
    }

    // ---------------------------------------------------------------- Mouse acceleration

    /// <summary>"Enhance pointer precision" in Windows mouse settings.</summary>
    public static bool MouseAccelerationOn()
    {
        var values = new int[3];
        return SystemParametersInfo(SPI_GETMOUSE, 0, values, 0) && values[2] != 0;
    }

    public static void SetMouseAcceleration(bool on, string title)
    {
        var old = new int[3];
        SystemParametersInfo(SPI_GETMOUSE, 0, old, 0);

        ApplyMouse(on ? new[] { 6, 10, 1 } : new[] { 0, 0, 0 });

        ChangeJournal.Add(new ChangeRecord
        {
            Title = title,
            Kind = "mouse",
            Data = new() { ["Values"] = string.Join(",", old) },
        });
    }

    private static void ApplyMouse(int[] values)
    {
        if (!SystemParametersInfo(SPI_SETMOUSE, 0, values, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE))
            throw new InvalidOperationException("Windows refused the mouse setting change.");
    }

    // ---------------------------------------------------------------- Undo

    public static void Undo(ChangeRecord change)
    {
        var d = change.Data;
        switch (change.Kind)
        {
            case "registry":
            {
                using var k = Registry.CurrentUser.CreateSubKey(d["Key"], writable: true)
                    ?? throw new InvalidOperationException("Could not open the registry key.");
                if (d["Existed"] == "0")
                {
                    k.DeleteValue(d["Name"], throwOnMissingValue: false);
                }
                else
                {
                    var kind = Enum.Parse<RegistryValueKind>(d["OldKind"]);
                    object value = kind switch
                    {
                        RegistryValueKind.DWord => int.Parse(d["OldValue"]),
                        RegistryValueKind.QWord => long.Parse(d["OldValue"]),
                        _ => d["OldValue"],
                    };
                    k.SetValue(d["Name"], value, kind);
                }
                break;
            }

            case "powermode":
                Handheld.HandheldInfo.Undo(change);
                break;

            case "tweak":
                AdvancedTweaks.Undo(change);
                break;

            case "ultimate":
                AdvancedTweaks.UndoUltimate(change);
                break;

            case "faststart":
                StartupApps.Undo(change);
                break;

            case "machine-registry":
                RunRegElevated(d["Existed"] == "0"
                    ? $"delete \"HKLM\\{d["Key"]}\" /v {d["Name"]} /f"
                    : $"add \"HKLM\\{d["Key"]}\" /v {d["Name"]} /t REG_DWORD /d {d["OldValue"]} /f");
                break;

            case "refresh":
                ApplyRefreshRate(d["Device"], int.Parse(d["Width"]), int.Parse(d["Height"]), int.Parse(d["Hz"]));
                break;

            case "powerplan":
            {
                var g = Guid.Parse(d["Guid"]);
                uint err = PowerSetActiveScheme(IntPtr.Zero, ref g);
                if (err != 0) throw new InvalidOperationException($"Windows refused the power plan change (code {err}).");
                break;
            }

            case "mouse":
                ApplyMouse(d["Values"].Split(',').Select(int.Parse).ToArray());
                break;

            case "nvidia-drs":
                NvidiaDrs.Undo(change);
                break;

            case "nvidia-telemetry":
                NvidiaTelemetry.Undo(change);
                break;

            default:
                throw new InvalidOperationException($"Unknown change type '{change.Kind}'.");
        }

        ChangeJournal.MarkUndone(change.Id);
    }

    // ---------------------------------------------------------------- Win32

    private const uint DM_BITSPERPEL = 0x40000;
    private const uint DM_PELSWIDTH = 0x80000;
    private const uint DM_PELSHEIGHT = 0x100000;
    private const uint DM_DISPLAYFREQUENCY = 0x400000;
    private const uint CDS_UPDATEREGISTRY = 0x1;

    private const uint SPI_GETMOUSE = 0x0003;
    private const uint SPI_SETMOUSE = 0x0004;
    private const uint SPIF_UPDATEINIFILE = 0x01;
    private const uint SPIF_SENDCHANGE = 0x02;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "ChangeDisplaySettingsExW")]
    private static extern int ChangeDisplaySettingsEx(string deviceName, ref DisplayDetector.DEVMODE devMode, IntPtr hwnd, uint flags, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint param, int[] values, uint winIni);

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr rootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveScheme(IntPtr rootPowerKey, ref Guid schemeGuid);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);
}
