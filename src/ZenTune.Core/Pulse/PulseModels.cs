namespace ZenTune.Core.Pulse;

/// <summary>What the graphics card is doing. A value is null when it cannot be read on this PC.</summary>
public sealed record GpuReading(
    string Name,
    double? UsagePercent,
    double? VramUsedBytes,
    double? VramTotalBytes,
    double? TemperatureC,
    double? PowerWatts,
    double? ClockMhz,
    double? FanPercent)
{
    public double? VramPercent =>
        VramUsedBytes is double used && VramTotalBytes is double total && total > 0 ? used * 100.0 / total : null;
}

/// <summary>One reading of how busy the PC is.</summary>
public sealed record PulseSnapshot(
    DateTime When,
    double CpuPercent,
    double BusiestCorePercent,
    ulong MemoryUsedBytes,
    ulong MemoryTotalBytes,
    double DiskPercent,
    double DiskBytesPerSec,
    double NetDownBytesPerSec,
    double NetUpBytesPerSec,
    GpuReading? Gpu,
    IReadOnlyList<double>? CoreLoads = null,
    IReadOnlyList<ProgramUse>? TopByCpu = null,
    IReadOnlyList<ProgramUse>? TopByMemory = null,
    SensorReading? Sensors = null,
    IReadOnlyList<ProgramUse>? TopByGpu = null)
{
    public double MemoryPercent => MemoryTotalBytes == 0 ? 0 : MemoryUsedBytes * 100.0 / MemoryTotalBytes;
}

/// <summary>A program (all its processes added together) and what it uses. Approximate: shared memory can be counted more than once.</summary>
public sealed record ProgramUse(string Name, double CpuPercent, ulong MemoryBytes, double GpuPercent = 0);

/// <summary>A named number, for example a motherboard temperature.</summary>
public sealed record NamedValue(string Name, double Value);

/// <summary>
/// Processor and motherboard sensors. Only present when the user has turned sensors on. A value is null when it cannot be read;
/// Problem then says why, in words for the user.
/// </summary>
public sealed record SensorReading(
    double? CpuTemperatureC,
    double? CpuHottestCoreC,
    double? CpuPowerW,
    double? CpuClockMhz,
    IReadOnlyList<NamedValue> BoardTemperatures,
    string? Problem,
    double? CpuClockAverageMhz = null);
