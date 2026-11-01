using System.Globalization;
using System.Text;

namespace ZenTune.Core.Hardware;

public enum GpuVendor { Nvidia, Amd, Intel, Other }

public sealed record GpuInfo(
    string Name,
    GpuVendor Vendor,
    ulong VramBytes,
    string DriverVersion,
    bool IsIntegrated,
    string PnpId)
{
    /// <summary>"VEN_10DE&amp;DEV_2C02" — used to match a display to the GPU it is plugged into.</summary>
    public string VendorDeviceKey => HardwareIds.VendorDeviceKey(PnpId);
}

public sealed record CpuInfo(
    string Name,
    int Cores,
    int Threads,
    int BaseClockMhz);

public sealed record MemoryInfo(
    ulong TotalBytes,
    int SpeedMts,
    int RatedSpeedMts,
    string Type,
    int Modules);

/// <summary>Who made the PC. Prebuilt PCs from big brands often lock BIOS options like XMP.</summary>
public sealed record SystemInfo(
    string Manufacturer,
    string Model,
    bool IsLaptop)
{
    private static readonly string[] OemBrands =
        ["Lenovo", "HP", "Hewlett", "Dell", "Alienware", "Acer", "Medion", "Fujitsu", "Microsoft", "Samsung", "Apple"];

    /// <summary>True for prebuilt PCs from a big brand (not a self-built PC with a retail motherboard).</summary>
    public bool IsBrandPc => OemBrands.Any(b => Manufacturer.Contains(b, StringComparison.OrdinalIgnoreCase));

    /// <summary>"Lenovo", "HP" ... in normal casing.</summary>
    public string BrandName
    {
        get
        {
            if (Manufacturer.Length == 0) return "the manufacturer";
            var m = Manufacturer.Trim();
            return m.ToUpperInvariant() == m && m.Length > 3
                ? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(m.ToLowerInvariant())
                : m;
        }
    }
}

public sealed record DisplayInfo(
    string Name,
    string GdiName,
    bool IsPrimary,
    string Connection,
    int Width,
    int Height,
    double RefreshHz,
    int NativeWidth,
    int NativeHeight,
    int MaxRefreshHz,
    int MaxRefreshAtCurrentHz,
    bool HdrSupported,
    bool HdrEnabled,
    int BitsPerColor,
    string AdapterKey)
{
    public bool IsRunningNative => Width == NativeWidth && Height == NativeHeight;
    public bool IsBuiltIn => Connection == "Built-in";
}

public sealed record HardwareSnapshot(
    IReadOnlyList<GpuInfo> Gpus,
    CpuInfo Cpu,
    MemoryInfo Memory,
    SystemInfo Pc,
    IReadOnlyList<DisplayInfo> Displays,
    IReadOnlyList<string> Warnings)
{
    /// <summary>The GPU games will run on: the dedicated card with the most VRAM.</summary>
    public GpuInfo? PrimaryGpu =>
        Gpus.Where(g => !g.IsIntegrated).OrderByDescending(g => g.VramBytes).FirstOrDefault()
        ?? Gpus.FirstOrDefault();

    public DisplayInfo? PrimaryDisplay =>
        Displays.FirstOrDefault(d => d.IsPrimary) ?? Displays.FirstOrDefault();

    /// <summary>The GPU a display is plugged into, or null if unknown.</summary>
    public GpuInfo? GpuFor(DisplayInfo d) =>
        d.AdapterKey.Length == 0 ? null : Gpus.FirstOrDefault(g => g.VendorDeviceKey == d.AdapterKey);

    public string ToReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine("ZenTune hardware report");
        sb.AppendLine("=======================");

        if (Pc.Manufacturer.Length > 0)
            sb.AppendLine($"PC: {Pc.Manufacturer} {Pc.Model}{(Pc.IsLaptop ? " (laptop)" : "")}");

        foreach (var g in Gpus)
        {
            var role = g == PrimaryGpu ? "primary" : g.IsIntegrated ? "integrated" : "secondary";
            sb.AppendLine($"GPU ({role}): {g.Name} | {g.Vendor} | {Format.Bytes(g.VramBytes)} VRAM | driver {g.DriverVersion}");
        }

        sb.AppendLine($"CPU: {Cpu.Name} | {Cpu.Cores} cores / {Cpu.Threads} threads | base {Cpu.BaseClockMhz} MHz");
        sb.AppendLine($"RAM: {Format.Bytes(Memory.TotalBytes)} {Memory.Type} @ {Memory.SpeedMts} MT/s (rated {Memory.RatedSpeedMts}) | {Memory.Modules} module(s)");

        foreach (var d in Displays)
        {
            var gpu = GpuFor(d);
            sb.AppendLine(
                $"Display{(d.IsPrimary ? " (primary)" : "")}: {d.Name} | {d.Connection} on {gpu?.Name ?? "unknown GPU"} | " +
                $"{d.Width}x{d.Height} @ {d.RefreshHz:0.##} Hz | native {d.NativeWidth}x{d.NativeHeight} @ max {d.MaxRefreshHz} Hz | " +
                $"HDR {(d.HdrSupported ? (d.HdrEnabled ? "on" : "off") : "not supported")} | {d.BitsPerColor}-bit");
        }

        foreach (var w in Warnings)
            sb.AppendLine($"Warning: {w}");

        return sb.ToString();
    }
}

public static class HardwareIds
{
    /// <summary>Pulls "VEN_xxxx&amp;DEV_xxxx" out of a PnP id or a device path (upper case), or "" if missing.</summary>
    public static string VendorDeviceKey(string? id)
    {
        var m = System.Text.RegularExpressions.Regex.Match(
            id ?? "", @"VEN_([0-9A-F]{4})&DEV_([0-9A-F]{4})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? $"VEN_{m.Groups[1].Value.ToUpperInvariant()}&DEV_{m.Groups[2].Value.ToUpperInvariant()}" : "";
    }
}

public static class Format
{
    public static string Bytes(ulong bytes)
    {
        if (bytes == 0) return "Unknown";
        double gb = bytes / 1024d / 1024d / 1024d;
        return gb >= 1 ? $"{Math.Round(gb):0} GB" : $"{bytes / 1024d / 1024d:0} MB";
    }

    /// <summary>Removes (R), (TM) and repeated spaces from hardware names.</summary>
    public static string CleanName(string name) =>
        System.Text.RegularExpressions.Regex.Replace(
            name.Replace("(R)", "", StringComparison.OrdinalIgnoreCase)
                .Replace("(TM)", "", StringComparison.OrdinalIgnoreCase)
                .Replace("CPU @", "@"),
            @"\s+", " ").Trim();
}
