using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using ZenTune.Core.Changes;
using ZenTune.Core.Hardware;

namespace ZenTune.Core.Checks;

/// <summary>
/// Read-only health checks: memory speed, Memory integrity, Resizable BAR, Intel APO, driver age, Secure Boot,
/// TPM and free disk space. Nothing here changes Windows. Each check says in its text how it knows what it knows.
/// </summary>
[SupportedOSPlatform("windows")]
public static class HealthChecks
{
    // ---------------------------------------------------------------- Memory speed (XMP / EXPO)

    public static IEnumerable<CheckResult> MemorySpeed(HardwareSnapshot hw)
    {
        var m = hw.Memory;
        var pc = hw.Pc;
        if (m.SpeedMts <= 0) yield break;

        int running = m.SpeedMts;
        int rated = m.RatedSpeedMts;
        bool below = rated > 0 && running < rated * 0.98;
        bool looksDefault = (m.Type == "DDR5" && running <= 4800) || (m.Type == "DDR4" && running <= 2666);

        if (below)
        {
            yield return new CheckResult
            {
                Id = "ram-speed",
                Title = $"Memory runs at {running} MT/s, but is rated for {rated} MT/s",
                Detail = $"Your {m.Type} memory can run faster than it does now. " +
                         "Turn on XMP (Intel) or EXPO (AMD) in the BIOS to get the full speed. ZenTune can't change BIOS settings. " +
                         (pc.IsBrandPc || pc.IsLaptop
                             ? $"On PCs from {pc.BrandName} the BIOS may hide this option, so look for it before you spend time on it."
                             : ""),
                Status = pc.IsBrandPc || pc.IsLaptop ? CheckStatus.Info : CheckStatus.Warning,
            };
        }
        else if (looksDefault)
        {
            yield return new CheckResult
            {
                Id = "ram-speed",
                Title = $"Memory runs at {running} MT/s (the standard {m.Type} speed)",
                Detail = "That is the safe default. If the box or the product page of your memory says a higher speed, " +
                         "XMP (Intel) or EXPO (AMD) is probably off in the BIOS. Windows can't see a profile that was never turned on, " +
                         "so ZenTune can't tell for sure.",
                Status = CheckStatus.Info,
            };
        }
        else
        {
            yield return new CheckResult
            {
                Id = "ram-speed",
                Title = $"Memory runs at its rated speed ({running} MT/s)",
                Detail = rated > 0
                    ? $"The modules report {rated} MT/s as their speed, and that is what they run at."
                    : "That is a good speed for games.",
                Status = CheckStatus.Good,
            };
        }
    }

    // ---------------------------------------------------------------- Memory integrity (HVCI)

    private const string HvciKey = @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity";

    private static bool? HvciRunning()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\DeviceGuard", "SELECT SecurityServicesRunning FROM Win32_DeviceGuard");
            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    if (mo["SecurityServicesRunning"] is Array services)
                    {
                        foreach (var s in services)
                            if (Convert.ToInt32(s) == 2) return true; // 2 = HVCI (Memory integrity)
                        return false;
                    }
                }
            }
        }
        catch
        {
            // namespace missing or not readable
        }
        return null;
    }

    public static IEnumerable<CheckResult> MemoryIntegrity()
    {
        bool? running = HvciRunning();
        bool? configured = Tweaks.ReadMachineDword(HvciKey, "Enabled") is { } v ? v == 1 : null;

        if (running == true)
        {
            yield return new CheckResult
            {
                Id = "memintegrity",
                Title = "Memory integrity is on",
                Detail = "Windows is running its virtualization-based protection. It is a security feature, and it can cost some FPS in some games. " +
                         "You can turn it off on the Advanced page if you accept the lower protection.",
                Status = CheckStatus.Info,
            };
        }
        else if (configured == true)
        {
            yield return new CheckResult
            {
                Id = "memintegrity",
                Title = "Memory integrity is set to on, but not running yet",
                Detail = "It starts after the next restart, or Windows could not start it (for example because of an incompatible driver).",
                Status = CheckStatus.Info,
            };
        }
        else if (running == false || configured == false)
        {
            yield return new CheckResult
            {
                Id = "memintegrity",
                Title = "Memory integrity is off",
                Detail = "Windows is not spending any performance on virtualization-based protection.",
                Status = CheckStatus.Good,
            };
        }
        else
        {
            yield return new CheckResult
            {
                Id = "memintegrity",
                Title = "Memory integrity could not be checked",
                Detail = "Windows did not report whether Memory integrity is on.",
                Status = CheckStatus.Info,
            };
        }
    }

    // ---------------------------------------------------------------- Resizable BAR (NVIDIA)

    private static string? FindNvidiaSmi()
    {
        string[] candidates =
        [
            Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe"),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    private static double SectionTotalMiB(string text, string header)
    {
        var m = Regex.Match(text, Regex.Escape(header) + @"\s*Total\s*:\s*(\d+)\s*MiB", RegexOptions.IgnoreCase);
        return m.Success ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
    }

    private static (double BarMiB, double FbMiB) ReadNvidiaBar()
    {
        var exe = FindNvidiaSmi();
        if (exe is null) return (0, 0);

        try
        {
            var psi = new ProcessStartInfo(exe, "-q -d MEMORY")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return (0, 0);

            var output = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(8000))
            {
                try { p.Kill(); } catch { }
                return (0, 0);
            }

            string text = output.Result;
            return (SectionTotalMiB(text, "BAR1 Memory Usage"), SectionTotalMiB(text, "FB Memory Usage"));
        }
        catch
        {
            return (0, 0);
        }
    }

    public static IEnumerable<CheckResult> ResizableBar(HardwareSnapshot hw)
    {
        var gpu = hw.PrimaryGpu;
        if (gpu is null || gpu.IsIntegrated || gpu.Vendor != GpuVendor.Nvidia) yield break;

        var (bar, fb) = ReadNvidiaBar();
        const string how = " ZenTune reads this from NVIDIA's own nvidia-smi tool (the size of the memory window the CPU can use).";

        if (bar <= 0 || fb <= 0)
        {
            yield return new CheckResult
            {
                Id = "rebar",
                Title = "Resizable BAR could not be checked",
                Detail = "ZenTune could not read the memory window from nvidia-smi. You can see the state in the NVIDIA Control Panel under System Information.",
                Status = CheckStatus.Info,
            };
        }
        else if (bar >= fb * 0.9)
        {
            yield return new CheckResult
            {
                Id = "rebar",
                Title = "Resizable BAR is on",
                Detail = $"The CPU can reach all of the graphics card's memory ({bar / 1024:0.#} GB window).{how}",
                Status = CheckStatus.Good,
            };
        }
        else if (bar <= 1024)
        {
            yield return new CheckResult
            {
                Id = "rebar",
                Title = "Resizable BAR looks off",
                Detail = $"The CPU only sees a {bar:0} MB window of the card's {fb / 1024:0} GB. Turning on Resizable BAR (called \"Re-Size BAR Support\", " +
                         "and it needs \"Above 4G Decoding\") in the BIOS helps some games and does nothing in others. ZenTune can't change BIOS settings." + how,
                Status = CheckStatus.Info,
            };
        }
        else
        {
            yield return new CheckResult
            {
                Id = "rebar",
                Title = "Resizable BAR is only partly on",
                Detail = $"The CPU sees {bar / 1024:0.#} GB of the card's {fb / 1024:0} GB.{how}",
                Status = CheckStatus.Info,
            };
        }
    }

    // ---------------------------------------------------------------- Intel APO

    /// <summary>
    /// Looks for Intel's DTT/IPF driver (needed by APO, but also installed by many PC makers without APO) and for
    /// APO itself. Returns the names found, so the page can show them.
    /// </summary>
    private static (string? Dtt, string? Apo) FindApoParts()
    {
        string? dtt = null, apo = null;

        void Consider(string name)
        {
            if (name.Contains("Application Optimization", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Platform Performance Package", StringComparison.OrdinalIgnoreCase))
                apo ??= name;
            else if (name.Contains("Innovation Platform Framework", StringComparison.OrdinalIgnoreCase) ||
                     name.Contains("Dynamic Tuning", StringComparison.OrdinalIgnoreCase))
                dtt ??= name;
        }

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name FROM Win32_PnPEntity WHERE Name LIKE '%Innovation Platform Framework%' " +
                "OR Name LIKE '%Dynamic Tuning%' OR Name LIKE '%Application Optimization%'");
            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    if (mo["Name"] is string n && n.Length > 0) Consider(n);
                }
            }
        }
        catch
        {
            // fall through to the installed-programs list
        }

        string[] roots =
        [
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        ];
        foreach (var root in roots)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(root);
                if (key is null) continue;
                foreach (var sub in key.GetSubKeyNames())
                {
                    try
                    {
                        using var item = key.OpenSubKey(sub);
                        if (item?.GetValue("DisplayName") is string name) Consider(name);
                    }
                    catch
                    {
                        // unreadable entry
                    }
                }
            }
            catch
            {
                // unreadable list
            }
        }
        return (dtt, apo);
    }

    public static IEnumerable<CheckResult> IntelApo(HardwareSnapshot hw)
    {
        string cpu = hw.Cpu.Name;
        if (!cpu.Contains("Intel", StringComparison.OrdinalIgnoreCase)) yield break;

        // Processors on Intel's own "verified" list for Application Optimization (Intel support article, checked October 2026)
        bool verified =
            Regex.IsMatch(cpu, @"Ultra\s*[579]\s*(285K|265KF?|245KF?|270K\s*Plus|250KF?\s*Plus)", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(cpu, @"i[579]-14\d{2,3}KF?\b", RegexOptions.IgnoreCase);
        if (!verified) yield break;

        var (dtt, apo) = FindApoParts();
        const string limit = " ZenTune can't see whether a game is being optimized right now. Intel's optional Application Optimization app shows that; the button opens Intel's download page.";

        if (apo is not null)
        {
            yield return new CheckResult
            {
                Id = "apo",
                Title = "Intel Application Optimization looks installed",
                Detail = $"ZenTune found \"{apo}\". It works silently in the games Intel has profiles for, and gains differ a lot from game to game." + limit,
                Status = CheckStatus.Good,
                ActionLabel = "Get Intel's APO app",
                Action = () => SystemChecks.OpenUri("https://www.intel.com/content/www/us/en/download/870620"),
                ActionChangesSystem = false,
            };
        }
        else
        {
            yield return new CheckResult
            {
                Id = "apo",
                Title = dtt is null ? "Intel Application Optimization was not found" : "Intel's DTT driver is there, but Application Optimization was not found",
                Detail = $"Your {cpu} supports it. " +
                         (dtt is null ? "" : $"ZenTune found \"{dtt}\", which APO needs, but that driver is also installed by PC makers without APO. ") +
                         "APO needs Windows 11, the newest BIOS, the DTT/IPF driver (turned on in the BIOS on some boards) and Intel's Platform Performance Package. " +
                         "Gains are only in games Intel has profiles for. ZenTune looks for APO by its name in Device Manager and in installed programs, " +
                         "so a driver with another name can be missed.",
                Status = CheckStatus.Info,
                ActionLabel = "Get Intel's package",
                Action = () => SystemChecks.OpenUri("https://www.intel.com/content/www/us/en/download/869519"),
                ActionChangesSystem = false,
            };
        }
    }

    // ---------------------------------------------------------------- Graphics driver age

    public static IEnumerable<CheckResult> DriverAge(HardwareSnapshot hw)
    {
        var gpu = hw.PrimaryGpu;
        if (gpu is null) yield break;

        DateTime? date = null;
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT PNPDeviceID, DriverDate FROM Win32_VideoController");
            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    if (!string.Equals(mo["PNPDeviceID"] as string, gpu.PnpId, StringComparison.OrdinalIgnoreCase)) continue;
                    if (mo["DriverDate"] is string raw && raw.Length > 0)
                        date = ManagementDateTimeConverter.ToDateTime(raw);
                }
            }
        }
        catch
        {
            // date stays unknown
        }

        if (date is null) yield break;

        int days = (int)(DateTime.Today - date.Value.Date).TotalDays;
        string age = days < 60 ? $"{days} days old" : days < 700 ? $"about {days / 30} months old" : $"about {days / 365} years old";
        const string note = " The date comes from the driver package, so it can be a few days older than the release.";

        string? url = gpu.Vendor switch
        {
            GpuVendor.Nvidia => "https://www.nvidia.com/en-us/drivers/",
            GpuVendor.Amd => "https://www.amd.com/en/support/download/drivers.html",
            GpuVendor.Intel => "https://www.intel.com/content/www/us/en/download-center/home.html",
            _ => null,
        };

        var status = days <= 150 ? CheckStatus.Good : days <= 365 ? CheckStatus.Info : CheckStatus.Warning;

        yield return new CheckResult
        {
            Id = "driver-age",
            Title = status == CheckStatus.Good
                ? $"Graphics driver is recent ({age})"
                : $"Graphics driver is {age}",
            Detail = status == CheckStatus.Good
                ? $"{gpu.Name}, driver {gpu.DriverVersion}, from {date.Value:d MMM yyyy}." + note
                : $"{gpu.Name}, driver {gpu.DriverVersion}, from {date.Value:d MMM yyyy}. New games often need a newer driver to run well." + note,
            Status = status,
            ActionLabel = status != CheckStatus.Good && url is not null ? "Open the driver page" : null,
            Action = status != CheckStatus.Good && url is not null ? () => SystemChecks.OpenUri(url) : null,
            ActionChangesSystem = false,
        };
    }

    // ---------------------------------------------------------------- Secure Boot and TPM (needed by some games)

    public static IEnumerable<CheckResult> SecureBoot()
    {
        int? sb = Tweaks.ReadMachineDword(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled");

        if (sb == 1)
        {
            yield return new CheckResult
            {
                Id = "secureboot",
                Title = "Secure Boot is on",
                Detail = "Games with strict anti-cheat, like Call of Duty: Modern Warfare 4, need it.",
                Status = CheckStatus.Good,
            };
        }
        else if (sb == 0)
        {
            yield return new CheckResult
            {
                Id = "secureboot",
                Title = "Secure Boot is off",
                Detail = "Some games with strict anti-cheat (Call of Duty: Modern Warfare 4 lists it as a requirement) won't start without it. " +
                         "It is turned on in the BIOS. ZenTune can't change BIOS settings.",
                Status = CheckStatus.Info,
            };
        }
        else
        {
            yield return new CheckResult
            {
                Id = "secureboot",
                Title = "Secure Boot could not be checked",
                Detail = "Windows did not report a Secure Boot state. This PC may start in the older BIOS mode (Legacy/CSM).",
                Status = CheckStatus.Info,
            };
        }
    }

    public static IEnumerable<CheckResult> Tpm()
    {
        string? name = null;
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_PnPEntity WHERE Name LIKE 'Trusted Platform Module%'");
            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    name = mo["Name"] as string;
                    if (!string.IsNullOrEmpty(name)) break;
                }
            }
        }
        catch
        {
            // unknown
        }

        if (name is not null && name.Contains("2.0"))
        {
            yield return new CheckResult
            {
                Id = "tpm",
                Title = "TPM 2.0 is present",
                Detail = "Needed by Windows 11 and by games with strict anti-cheat. ZenTune found the device in Device Manager.",
                Status = CheckStatus.Good,
            };
        }
        else
        {
            yield return new CheckResult
            {
                Id = "tpm",
                Title = name is null ? "No TPM was found" : $"TPM found: {name}",
                Detail = "Games with strict anti-cheat, like Call of Duty: Modern Warfare 4, list TPM 2.0 as a requirement. " +
                         "It is turned on in the BIOS (called fTPM, PTT or Security Device). ZenTune looks for it in Device Manager, " +
                         "so a TPM that is switched off in the BIOS shows up as missing.",
                Status = CheckStatus.Info,
            };
        }
    }

    // ---------------------------------------------------------------- Free disk space

    public static IEnumerable<CheckResult> DiskSpace()
    {
        foreach (var d in DriveInfo.GetDrives())
        {
            long free = 0, total = 0;
            string label = "";
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                free = d.AvailableFreeSpace;
                total = d.TotalSize;
                label = d.VolumeLabel;
            }
            catch
            {
                continue;
            }
            if (total <= 0) continue;

            double freeGb = free / 1024d / 1024d / 1024d;
            double totalGb = total / 1024d / 1024d / 1024d;
            string drive = d.Name.TrimEnd('\\');
            string name = string.IsNullOrWhiteSpace(label) ? drive : $"{drive} ({label})";
            var status = freeGb < 20 ? CheckStatus.Warning : freeGb < 50 ? CheckStatus.Info : CheckStatus.Good;

            bool isSystem = string.Equals(d.Name, Path.GetPathRoot(Environment.SystemDirectory), StringComparison.OrdinalIgnoreCase);

            yield return new CheckResult
            {
                Id = "disk-" + drive.TrimEnd(':'),
                Title = $"{name} has {freeGb:0} GB free of {totalGb:0} GB",
                Detail = status == CheckStatus.Good
                    ? "Plenty of room for game updates and shader caches."
                    : "Games need room for patches and shader caches on top of what they take when installed. " +
                      "As a rule of thumb, keep 20–30 GB free. Games with big updates (Call of Duty) can need much more at once.",
                Status = status,
                ActionLabel = status != CheckStatus.Good && isSystem ? "Open Storage settings" : null,
                Action = status != CheckStatus.Good && isSystem ? () => SystemChecks.OpenUri("ms-settings:storagesense") : null,
                ActionChangesSystem = false,
            };
        }
    }
}
