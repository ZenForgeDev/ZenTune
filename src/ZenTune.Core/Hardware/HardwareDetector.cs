using System.Runtime.Versioning;

namespace ZenTune.Core.Hardware;

/// <summary>Entry point for hardware detection. Safe to call from a background thread.</summary>
[SupportedOSPlatform("windows")]
public static class HardwareDetector
{
    public static HardwareSnapshot Detect()
    {
        var warnings = new List<string>();

        var gpus = SystemDetector.DetectGpus(warnings);
        var cpu = SystemDetector.DetectCpu(warnings);
        var memory = SystemDetector.DetectMemory(warnings);
        var system = SystemDetector.DetectSystem(warnings);
        var displays = DisplayDetector.DetectDisplays(warnings);

        // Order displays: primary first
        displays = displays.OrderByDescending(d => d.IsPrimary).ToList();

        return new HardwareSnapshot(gpus, cpu, memory, system, displays, warnings);
    }
}
