using System.Globalization;

namespace ZenTune.Core.Pulse;

/// <summary>How worried to look. Idle is calm, Fine is normal, High is worth a look, Critical needs attention.</summary>
public enum PulseLevel { Idle, Fine, High, Critical }

/// <summary>A short plain-words comment on a number, with a level that decides its colour.</summary>
public readonly record struct PulseNote(string Text, PulseLevel Level);

/// <summary>Turns the numbers into plain words. The same words are used on the Pulse page and in the log.</summary>
public static class PulseAdvice
{
    public const double HotGpuC = 83;

    public static PulseNote Load(double percent) => percent switch
    {
        < 10 => new PulseNote("Idle", PulseLevel.Idle),
        < 40 => new PulseNote("Light load", PulseLevel.Fine),
        < 75 => new PulseNote("Busy", PulseLevel.Fine),
        < 90 => new PulseNote("Heavy load", PulseLevel.High),
        _ => new PulseNote("Maxed out", PulseLevel.Critical),
    };

    /// <summary>In a game the graphics card should be the busy one, so working at its limit is shown as normal, not as a problem.</summary>
    public static PulseNote GpuLoad(double percent) => percent switch
    {
        < 10 => new PulseNote("Idle", PulseLevel.Idle),
        < 50 => new PulseNote("Light load", PulseLevel.Fine),
        < 95 => new PulseNote("Busy", PulseLevel.Fine),
        _ => new PulseNote("Working at its limit", PulseLevel.High),
    };

    public static PulseNote Memory(double percent) => percent switch
    {
        < 60 => new PulseNote("Plenty free", PulseLevel.Fine),
        < 80 => new PulseNote("Fine", PulseLevel.Fine),
        < 90 => new PulseNote("Getting full", PulseLevel.High),
        _ => new PulseNote("Nearly full: close programs you do not need", PulseLevel.Critical),
    };

    public static PulseNote VideoMemory(double percent) => percent switch
    {
        < 70 => new PulseNote("Plenty free", PulseLevel.Fine),
        < 90 => new PulseNote("Getting full", PulseLevel.High),
        _ => new PulseNote("Nearly full: lower the texture quality if the game stutters", PulseLevel.Critical),
    };

    public static PulseNote Temperature(double celsius) => celsius switch
    {
        < 45 => new PulseNote("Cool", PulseLevel.Idle),
        < 75 => new PulseNote("Normal", PulseLevel.Fine),
        < HotGpuC => new PulseNote("Warm, normal under heavy load", PulseLevel.Fine),
        < 90 => new PulseNote("Hot: check airflow and dust if this happens often", PulseLevel.High),
        _ => new PulseNote("Very hot", PulseLevel.Critical),
    };

    /// <summary>Modern processors are made to run hot under load, so the limits are higher than for the graphics card.</summary>
    public static PulseNote CpuTemperature(double celsius) => celsius switch
    {
        < 50 => new PulseNote("Cool", PulseLevel.Idle),
        < 85 => new PulseNote("Normal", PulseLevel.Fine),
        < 95 => new PulseNote("Hot: check the cooler and airflow if this happens often", PulseLevel.High),
        _ => new PulseNote("Very hot: the processor may slow itself down", PulseLevel.Critical),
    };

    public static PulseNote Disk(double percent) => percent switch
    {
        < 10 => new PulseNote("Idle", PulseLevel.Idle),
        < 70 => new PulseNote("Fine", PulseLevel.Fine),
        < 95 => new PulseNote("Busy", PulseLevel.High),
        _ => new PulseNote("Maxed out: loading or stutter may come from the disk", PulseLevel.Critical),
    };

    public static PulseNote Network(double downBytesPerSec, double upBytesPerSec) =>
        Math.Max(downBytesPerSec, upBytesPerSec) < 100 * 1024
            ? new PulseNote("Quiet", PulseLevel.Idle)
            : new PulseNote("Active", PulseLevel.Fine);

    /// <summary>One to three short sentences about what is holding the PC back right now. A guess from the numbers, not a fact.</summary>
    public static IReadOnlyList<string> Limits(PulseSnapshot s)
    {
        var lines = new List<string>();
        double? gpu = s.Gpu?.UsagePercent;
        double gpuNow = gpu ?? 0;

        if (gpu is >= 95)
        {
            lines.Add($"The graphics card is working at its limit ({gpu:0} %). In a game this is the normal and best case. " +
                      "If you want more FPS, lower the graphics settings.");
        }
        else if (s.BusiestCorePercent >= 90 && gpuNow < 90)
        {
            string gpuPart = gpu is null ? "" : $" while the graphics card still has room ({gpu:0} %)";
            lines.Add($"One processor core is almost maxed out ({s.BusiestCorePercent:0} %){gpuPart}. " +
                      "This looks like a CPU limit. Lowering CPU-heavy settings such as view distance, crowds or shadows may help.");
        }
        else if (gpuNow >= 60 || s.CpuPercent >= 60)
        {
            lines.Add("Busy, but nothing is maxed out. Nothing is clearly holding you back right now.");
        }
        else
        {
            lines.Add("Nothing is maxed out right now. If you are in a game, the FPS may be capped " +
                      "(V-Sync, a frame limit or your screen's refresh rate).");
        }

        if (s.MemoryPercent >= 92)
            lines.Add($"Memory is almost full ({s.MemoryPercent:0} %). Close programs you do not need.");

        if (s.Gpu?.VramPercent is double vram && vram >= 92)
            lines.Add($"Video memory is almost full ({vram:0} %). Lower the texture quality if the game stutters.");

        if (s.Gpu?.TemperatureC is double temp && temp >= HotGpuC)
            lines.Add($"The graphics card is hot ({temp:0} °C).");

        return lines;
    }

    // ---------------------------------------------------------------- Formatting

    public static string Rate(double bytesPerSec)
    {
        double mb = bytesPerSec / 1024d / 1024d;
        return mb >= 1
            ? mb.ToString("0.0", CultureInfo.InvariantCulture) + " MB/s"
            : (bytesPerSec / 1024d).ToString("0", CultureInfo.InvariantCulture) + " KB/s";
    }

    public static string Gigabytes(double bytes) =>
        (bytes / 1024d / 1024d / 1024d).ToString("0.0", CultureInfo.InvariantCulture) + " GB";
}
