using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace ZenTune.Core.Pulse;

/// <summary>
/// Takes one reading of CPU, memory, disk, network and graphics card each time Sample() is called.
/// Only reads; nothing is installed and no administrator rights are needed (not verified on every PC; if a counter
/// is missing its value is simply left out). The graphics card is read through NVML for NVIDIA and through Windows'
/// own GPU counters for other cards (no temperature there yet).
/// Call Sample() from one thread at a time.
/// </summary>
public sealed class PerformanceSampler : IDisposable
{
    private static readonly Regex AdapterPattern =
        new(@"luid_0x[0-9A-Fa-f]+_0x[0-9A-Fa-f]+_phys_\d+", RegexOptions.Compiled);

    private static readonly Regex PidPattern = new(@"pid_(\d+)", RegexOptions.Compiled);

    private static readonly Regex EnginePattern =
        new(@"_eng_(\d+)_engtype_(.+)$", RegexOptions.Compiled);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus buffer);

    private readonly PdhQuery _pdh = new();
    private readonly ProcessSampler _programs = new();

    // Sensors (processor temperature and so on) are only read when the user has turned them on
    private SensorReader? _sensors;
    private string? _sensorProblem;
    private bool _sensorsFailed;
    private volatile bool _sensorsEnabled;
    private volatile bool _sensorsRestart;
    private readonly NvmlReader? _nvml;
    private readonly string _gpuNameHint;
    private readonly ulong _vramHint;

    private readonly IntPtr _cpuUtility;
    private readonly IntPtr _cpuTime;
    private readonly IntPtr _cores;
    private readonly IntPtr _diskTime;
    private readonly IntPtr _diskBytes;
    private readonly IntPtr _netReceived;
    private readonly IntPtr _netSent;
    private readonly IntPtr _gpuEngines;
    private readonly IntPtr _gpuMemory;

    public PerformanceSampler(string? gpuName, ulong vramTotalBytes)
    {
        _gpuNameHint = string.IsNullOrWhiteSpace(gpuName) ? "Graphics card" : gpuName;
        _vramHint = vramTotalBytes;

        // Current Task Manager builds use time-based load (CPU time / elapsed time / logical processors), which is
        // "% Processor Time". "% Processor Utility" follows the clock speed and reads higher at low clocks.
        // The older counter set is the fallback.
        _cpuUtility = _pdh.Add(@"\Processor Information(_Total)\% Processor Time");
        _cpuTime = _pdh.Add(@"\Processor(_Total)\% Processor Time");
        _cores = _pdh.Add(@"\Processor Information(*)\% Processor Time");
        _diskTime = _pdh.Add(@"\PhysicalDisk(_Total)\% Disk Time");
        _diskBytes = _pdh.Add(@"\PhysicalDisk(_Total)\Disk Bytes/sec");
        _netReceived = _pdh.Add(@"\Network Interface(*)\Bytes Received/sec");
        _netSent = _pdh.Add(@"\Network Interface(*)\Bytes Sent/sec");

        _nvml = NvmlReader.TryCreate();

        // Windows' own GPU counters are what Task Manager shows (the busiest engine), so they are used for the usage
        // number on every card. NVML still gives the temperature, power, clock, fan and video memory for NVIDIA.
        _gpuEngines = _pdh.Add(@"\GPU Engine(*)\Utilization Percentage");
        _gpuMemory = _pdh.Add(@"\GPU Adapter Memory(*)\Dedicated Usage");

        // The first reading only gives the counters a starting point
        _pdh.Collect();
    }

    public PulseSnapshot Sample()
    {
        _pdh.Collect();

        double cpu = _pdh.Value(_cpuUtility, noCap: true) ?? _pdh.Value(_cpuTime) ?? 0;
        cpu = Math.Clamp(cpu, 0, 100);

        double busiest = cpu;
        var cores = _pdh.Values(_cores, noCap: true)
            .Where(c => !c.Name.Contains("_Total", StringComparison.Ordinal))
            .OrderBy(c => CoreOrder(c.Name))
            .ToList();
        if (cores.Count > 0) busiest = Math.Clamp(cores.Max(c => c.Value), 0, 100);
        var coreLoads = cores.Select(c => Math.Clamp(c.Value, 0, 100)).ToList();

        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        ulong total = 0, used = 0;
        if (GlobalMemoryStatusEx(ref memory))
        {
            total = memory.TotalPhys;
            used = memory.TotalPhys - memory.AvailPhys;
        }

        double disk = Math.Clamp(_pdh.Value(_diskTime) ?? 0, 0, 100);
        double diskBytes = Math.Max(0, _pdh.Value(_diskBytes) ?? 0);

        // The busiest network adapter, so traffic that passes through a VPN is not counted twice
        double down = _pdh.Values(_netReceived).Select(v => v.Value).DefaultIfEmpty(0).Max();
        double up = _pdh.Values(_netSent).Select(v => v.Value).DefaultIfEmpty(0).Max();

        GpuReading? gpu = null;
        if (_nvml is not null)
        {
            var n = _nvml.Read();
            // Same number as Task Manager if Windows' counters work; NVIDIA's own usage figure is the fallback
            double? nvidiaUsage = WindowsGpuUsage() ?? (n.UsagePercent is double u ? Math.Clamp(u, 0, 100) : (double?)null);
            gpu = new GpuReading(n.Name,
                nvidiaUsage,
                n.VramUsedBytes,
                n.VramTotalBytes ?? VramHint(),
                n.TemperatureC, n.PowerWatts, n.ClockMhz, n.FanPercent);
        }
        else
        {
            gpu = ReadWindowsGpu();
        }

        var (byCpu, byMemory) = _programs.Sample();

        return new PulseSnapshot(DateTime.Now, cpu, busiest, used, total, disk, diskBytes, Math.Max(0, down), Math.Max(0, up), gpu,
            coreLoads, byCpu, byMemory, ReadSensors(), ReadGpuPrograms());
    }

    private readonly Dictionary<int, string?> _processNames = [];
    private int _gpuProgramReads;

    /// <summary>
    /// The programs that use the graphics card the most, from the same Windows counters Task Manager uses: the counter name
    /// holds the process number ("pid_1234_..."), and a program's number is its busiest engine. Programs with several
    /// processes are added together. Empty if Windows gives nothing.
    /// </summary>
    private IReadOnlyList<ProgramUse> ReadGpuPrograms()
    {
        try
        {
            var engines = _pdh.Values(_gpuEngines);
            if (engines.Count == 0) return [];

            // Process numbers can be reused by Windows, so forget the names now and then
            if (++_gpuProgramReads % 60 == 0) _processNames.Clear();

            var perProcess = new Dictionary<int, double>();
            foreach (var engine in engines)
            {
                var match = PidPattern.Match(engine.Name);
                if (!match.Success || !int.TryParse(match.Groups[1].Value, out int pid)) continue;
                perProcess[pid] = Math.Max(perProcess.GetValueOrDefault(pid), engine.Value);
            }

            var perProgram = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var (pid, value) in perProcess)
            {
                if (value < 0.1) continue;

                string? name = NameOf(pid);
                if (name is null) continue;
                perProgram[name] = Math.Min(100, perProgram.GetValueOrDefault(name) + value);
            }

            return perProgram
                .OrderByDescending(p => p.Value)
                .Take(5)
                .Select(p => new ProgramUse(p.Key, 0, 0, p.Value))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private string? NameOf(int pid)
    {
        if (_processNames.TryGetValue(pid, out var known)) return known;

        string? name = null;
        try
        {
            using var process = Process.GetProcessById(pid);
            name = process.ProcessName;
        }
        catch
        {
            // The program closed, or Windows does not let us look at it
        }

        _processNames[pid] = name;
        return name;
    }

    /// <summary>Turns the sensors on or off. Takes effect at the next reading.</summary>
    public bool SensorsEnabled
    {
        get => _sensorsEnabled;
        set => _sensorsEnabled = value;
    }

    /// <summary>Starts the sensors again at the next reading, for example after the sensor driver was installed.</summary>
    public void RestartSensors() => _sensorsRestart = true;

    private SensorReading? ReadSensors()
    {
        if (_sensorsRestart)
        {
            _sensorsRestart = false;
            DisposeSensors();
            _sensorsFailed = false;
        }

        if (!_sensorsEnabled)
        {
            DisposeSensors();
            _sensorsFailed = false;
            return null;
        }

        if (_sensors is null && !_sensorsFailed)
        {
            _sensors = SensorReader.TryCreate(out _sensorProblem);
            _sensorsFailed = _sensors is null;
        }

        if (_sensors is null) return new SensorReading(null, null, null, null, [], _sensorProblem);

        try
        {
            return _sensors.Read();
        }
        catch (Exception ex)
        {
            DisposeSensors();
            _sensorsFailed = true;
            _sensorProblem = "The sensors stopped working: " + ex.Message;
            return new SensorReading(null, null, null, null, [], _sensorProblem);
        }
    }

    private void DisposeSensors()
    {
        _sensors?.Dispose();
        _sensors = null;
    }

    /// <summary>Counter names look like "0,5" (group, core). Sorts them 0,0 0,1 ... 0,10 instead of as text.</summary>
    private static int CoreOrder(string instance)
    {
        var parts = instance.Split(',');
        if (parts.Length == 2 && int.TryParse(parts[0], out int group) && int.TryParse(parts[1], out int core))
            return group * 1000 + core;
        return int.MaxValue;
    }

    /// <summary>
    /// The busiest engine of the main card, like Task Manager (programs are added together per engine).
    /// Null if Windows' counters give nothing on this PC.
    /// </summary>
    private double? WindowsGpuUsage()
    {
        var engines = _pdh.Values(_gpuEngines);
        if (engines.Count == 0) return null;

        string? adapter = _pdh.Values(_gpuMemory)
            .Select(m => (Key: AdapterOf(m.Name), m.Value))
            .Where(m => m.Key is not null)
            .GroupBy(m => m.Key!)
            .Select(g => (Key: g.Key, Used: g.Sum(x => x.Value)))
            .OrderByDescending(g => g.Used)
            .Select(g => g.Key)
            .FirstOrDefault();

        double busiest = engines
            .Where(e => adapter is null || AdapterOf(e.Name) == adapter)
            .GroupBy(e => EngineOf(e.Name))
            .Select(g => g.Sum(x => x.Value))
            .DefaultIfEmpty(0)
            .Max();

        return Math.Clamp(busiest, 0, 100);
    }

    /// <summary>Graphics card usage from Windows' own counters (AMD, Intel, or NVIDIA without NVML). No temperature.</summary>
    private GpuReading? ReadWindowsGpu()
    {
        var memory = _pdh.Values(_gpuMemory);
        var engines = _pdh.Values(_gpuEngines);
        if (memory.Count == 0 && engines.Count == 0) return null;

        // The main card is the one with the most video memory in use (an integrated GPU uses very little of its own)
        string? adapter = memory
            .Select(m => (Key: AdapterOf(m.Name), m.Value))
            .Where(m => m.Key is not null)
            .GroupBy(m => m.Key!)
            .Select(g => (Key: g.Key, Used: g.Sum(x => x.Value)))
            .OrderByDescending(g => g.Used)
            .Select(g => g.Key)
            .FirstOrDefault();

        double? usage = null;
        if (engines.Count > 0)
        {
            // Add up all programs per engine (3D, copy, video ...) and take the busiest engine, like Task Manager
            usage = engines
                .Where(e => adapter is null || AdapterOf(e.Name) == adapter)
                .GroupBy(e => EngineOf(e.Name))
                .Select(g => g.Sum(x => x.Value))
                .DefaultIfEmpty(0)
                .Max();
            usage = Math.Clamp(usage.Value, 0, 100);
        }

        double? vramUsed = null;
        if (adapter is not null)
            vramUsed = memory.Where(m => AdapterOf(m.Name) == adapter).Sum(m => m.Value);

        return new GpuReading(_gpuNameHint, usage, vramUsed, VramHint(), null, null, null, null);
    }

    /// <summary>The video memory size found when ZenTune scanned the hardware, or null if unknown.</summary>
    private double? VramHint() => _vramHint > 0 ? (double?)_vramHint : null;

    private static string? AdapterOf(string instance)
    {
        var m = AdapterPattern.Match(instance);
        return m.Success ? m.Value : null;
    }

    private static string EngineOf(string instance)
    {
        var m = EnginePattern.Match(instance);
        return (AdapterOf(instance) ?? "") + (m.Success ? $"_eng{m.Groups[1].Value}_{m.Groups[2].Value}" : instance);
    }

    public void Dispose()
    {
        DisposeSensors();
        _nvml?.Dispose();
        _pdh.Dispose();
    }
}
