using System.Management;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace ZenTune.Core.Hardware;

/// <summary>Detects GPU, CPU and RAM using WMI and the registry. No admin rights needed.</summary>
[SupportedOSPlatform("windows")]
internal static class SystemDetector
{
    private const string DisplayClassKey =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    // ---------------------------------------------------------------- GPU

    public static List<GpuInfo> DetectGpus(List<string> warnings)
    {
        var vramByName = ReadVramFromRegistry();
        var result = new List<GpuInfo>();

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, PNPDeviceID, DriverVersion, AdapterRAM FROM Win32_VideoController");

            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    var name = mo["Name"] as string ?? "";
                    var pnp = mo["PNPDeviceID"] as string ?? "";

                    // Only real PCI(e) graphics hardware. Skips Basic Display, Parsec, virtual displays etc.
                    if (!pnp.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var vendor = VendorFromPnp(pnp);
                    var rawDriver = mo["DriverVersion"] as string ?? "";

                    ulong vram = vramByName.TryGetValue(name, out var v) ? v : 0;
                    if (vram == 0 && mo["AdapterRAM"] is not null)
                        vram = Convert.ToUInt64(mo["AdapterRAM"]); // capped at 4 GB by Windows, last resort only

                    var clean = Format.CleanName(name);
                    result.Add(new GpuInfo(
                        clean,
                        vendor,
                        vram,
                        FriendlyDriverVersion(rawDriver, vendor),
                        IsIntegrated(clean, vendor, vram),
                        pnp));
                }
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"Could not read graphics cards: {ex.Message}");
        }

        if (result.Count == 0)
            warnings.Add("No graphics card was found.");

        return result;
    }

    /// <summary>Real VRAM size per adapter name. WMI's AdapterRAM stops at 4 GB, the driver's registry key does not.</summary>
    private static Dictionary<string, ulong> ReadVramFromRegistry()
    {
        var map = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(DisplayClassKey);
            if (root is null) return map;

            foreach (var sub in root.GetSubKeyNames())
            {
                if (!int.TryParse(sub, out _)) continue; // only 0000, 0001 ...

                try
                {
                    using var key = root.OpenSubKey(sub);
                    if (key?.GetValue("DriverDesc") is not string desc) continue;

                    ulong size = ReadSize(key.GetValue("HardwareInformation.qwMemorySize"))
                              ?? ReadSize(key.GetValue("HardwareInformation.MemorySize"))
                              ?? 0;

                    if (size > 0 && (!map.TryGetValue(desc, out var existing) || size > existing))
                        map[desc] = size;
                }
                catch
                {
                    // Some subkeys are locked for normal users. Skip them.
                }
            }
        }
        catch
        {
            // Registry unavailable. WMI fallback is used.
        }
        return map;
    }

    private static ulong? ReadSize(object? value) => value switch
    {
        long l when l > 0 => (ulong)l,
        int i when i != 0 => (uint)i,
        byte[] { Length: >= 8 } b => BitConverter.ToUInt64(b, 0),
        byte[] { Length: >= 4 } b => BitConverter.ToUInt32(b, 0),
        _ => null
    };

    private static GpuVendor VendorFromPnp(string pnp)
    {
        if (pnp.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase)) return GpuVendor.Nvidia;
        if (pnp.Contains("VEN_1002", StringComparison.OrdinalIgnoreCase) ||
            pnp.Contains("VEN_1022", StringComparison.OrdinalIgnoreCase)) return GpuVendor.Amd;
        if (pnp.Contains("VEN_8086", StringComparison.OrdinalIgnoreCase)) return GpuVendor.Intel;
        return GpuVendor.Other;
    }

    private static bool IsIntegrated(string name, GpuVendor vendor, ulong vram)
    {
        if (vendor == GpuVendor.Nvidia) return false;

        if (vendor == GpuVendor.Intel)
        {
            // Intel Arc A/B-series desktop cards are dedicated; everything else from Intel is built into the CPU.
            bool dedicatedArc = System.Text.RegularExpressions.Regex.IsMatch(name, @"Arc\s+[AB]\d", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return !dedicatedArc;
        }

        if (vendor == GpuVendor.Amd)
        {
            // Dedicated Radeon cards have "RX" or "Pro" in the name; APU graphics usually do not.
            bool dedicated = name.Contains(" RX", StringComparison.OrdinalIgnoreCase)
                          || name.Contains("Radeon Pro", StringComparison.OrdinalIgnoreCase);
            if (!dedicated) return true;
        }

        return vram > 0 && vram < 1024UL * 1024 * 1024;
    }

    /// <summary>NVIDIA's Windows driver version 32.0.15.8157 is the driver 581.57 people know.</summary>
    private static string FriendlyDriverVersion(string raw, GpuVendor vendor)
    {
        if (vendor != GpuVendor.Nvidia || string.IsNullOrWhiteSpace(raw)) return raw;

        var parts = raw.Split('.');
        if (parts.Length < 4) return raw;

        var digits = parts[2] + parts[3];
        if (digits.Length < 5) return raw;

        var last5 = digits[^5..];
        return $"{last5[..3]}.{last5[3..]}";
    }

    // ---------------------------------------------------------------- CPU

    public static CpuInfo DetectCpu(List<string> warnings)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor");

            string name = "";
            int cores = 0, threads = 0, mhz = 0;

            // Multi-socket systems report one row per CPU. Sum cores, keep the first name.
            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    if (name.Length == 0) name = Format.CleanName(mo["Name"] as string ?? "");
                    cores += ToInt(mo["NumberOfCores"]);
                    threads += ToInt(mo["NumberOfLogicalProcessors"]);
                    mhz = Math.Max(mhz, ToInt(mo["MaxClockSpeed"]));
                }
            }

            if (name.Length > 0)
                return new CpuInfo(name, cores, threads, mhz);
        }
        catch (Exception ex)
        {
            warnings.Add($"Could not read the processor: {ex.Message}");
        }

        return new CpuInfo("Unknown processor", 0, Environment.ProcessorCount, 0);
    }

    // ---------------------------------------------------------------- RAM

    public static MemoryInfo DetectMemory(List<string> warnings)
    {
        ulong total = 0;
        int speed = 0, rated = 0, modules = 0;
        string type = "";

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Capacity, ConfiguredClockSpeed, Speed, SMBIOSMemoryType FROM Win32_PhysicalMemory");

            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    modules++;
                    if (mo["Capacity"] is not null) total += Convert.ToUInt64(mo["Capacity"]);

                    int r = ToInt(mo["Speed"]);
                    int s = ToInt(mo["ConfiguredClockSpeed"]);
                    if (s == 0) s = r;
                    speed = Math.Max(speed, s);
                    rated = Math.Max(rated, r);

                    if (type.Length == 0) type = MemoryTypeName(ToInt(mo["SMBIOSMemoryType"]));
                }
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"Could not read memory modules: {ex.Message}");
        }

        if (total == 0)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
                foreach (ManagementObject mo in searcher.Get())
                    using (mo)
                        total = Convert.ToUInt64(mo["TotalPhysicalMemory"]);
            }
            catch { /* leave as unknown */ }
        }

        return new MemoryInfo(total, speed, rated > 0 ? rated : speed, type.Length > 0 ? type : "RAM", modules);
    }

    // ---------------------------------------------------------------- PC maker

    public static SystemInfo DetectSystem(List<string> warnings)
    {
        string maker = "", model = "";
        bool laptop = false;

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Manufacturer, Model, PCSystemType FROM Win32_ComputerSystem");
            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    maker = (mo["Manufacturer"] as string ?? "").Trim();
                    model = (mo["Model"] as string ?? "").Trim();
                    laptop = ToInt(mo["PCSystemType"]) == 2; // 2 = mobile
                }
            }

            // Lenovo puts a part number like "90UW000ANMX" in Model; the real name is in the product version.
            if (maker.Contains("Lenovo", StringComparison.OrdinalIgnoreCase))
            {
                using var product = new ManagementObjectSearcher("SELECT Version FROM Win32_ComputerSystemProduct");
                foreach (ManagementObject mo in product.Get())
                {
                    using (mo)
                    {
                        var version = (mo["Version"] as string ?? "").Trim();
                        if (version.Length > 0 && !version.Equals("None", StringComparison.OrdinalIgnoreCase))
                            model = version;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"Could not read the PC model: {ex.Message}");
        }

        // Self-built PCs often report placeholder text from the motherboard maker
        if (maker.Contains("O.E.M", StringComparison.OrdinalIgnoreCase) || maker.Contains("System manufacturer", StringComparison.OrdinalIgnoreCase))
            maker = "";
        if (model.Contains("O.E.M", StringComparison.OrdinalIgnoreCase) || model.Contains("System Product Name", StringComparison.OrdinalIgnoreCase))
            model = "";

        return new SystemInfo(maker, model, laptop);
    }

    private static string MemoryTypeName(int smbios) => smbios switch
    {
        24 => "DDR3",
        26 => "DDR4",
        29 => "LPDDR3",
        30 => "LPDDR4",
        34 => "DDR5",
        35 => "LPDDR5",
        _ => ""
    };

    private static int ToInt(object? o)
    {
        try { return o is null ? 0 : Convert.ToInt32(o); }
        catch { return 0; }
    }
}
