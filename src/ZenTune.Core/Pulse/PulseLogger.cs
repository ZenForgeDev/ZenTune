using System.Globalization;
using System.Text;

namespace ZenTune.Core.Pulse;

public sealed record PulseLogResult(string Folder, TimeSpan Duration, int Readings);

/// <summary>
/// Saves Pulse readings as text files in Documents\Pulse\&lt;date_time&gt;\ (a new folder for every logging session):
///   log.txt   what PC it was, notable events (hot graphics card, full memory) and a summary at the end
///   perf.txt  CPU, GPU, memory, video memory, disk and network, one line per reading, separated by tabs
///   temp.txt  graphics card temperature, power, clock and fan, one line per reading, separated by tabs
///   threads.txt  the load of every processor thread, one line per reading, separated by tabs
///   programs.txt the programs using the most processor and memory, one line per reading, separated by tabs
/// The files are flushed after every line, so nothing is lost if the PC crashes, and can be opened while logging.
/// No user name, PC name or file names are written. programs.txt does contain the names of the programs that were running
/// (for example a game), so look at it before sharing a log.
/// </summary>
public sealed class PulseLogger : IDisposable
{
    private sealed class Stat
    {
        public int Count;
        public double Sum;
        public double Max = double.MinValue;

        public void Add(double? value)
        {
            if (value is not double v) return;
            Count++;
            Sum += v;
            if (v > Max) Max = v;
        }

        public string Highest(string unit, string format = "0") =>
            Count == 0 ? "no data" : Max.ToString(format, Inv) + unit;

        public string Summary(string unit) =>
            Count == 0 ? "no data" : $"average {(Sum / Count).ToString("0", Inv)}{unit}, highest {Max.ToString("0", Inv)}{unit}";
    }

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly StreamWriter _log;
    private readonly StreamWriter _perf;
    private readonly StreamWriter _temp;
    private readonly StreamWriter _threads;
    private readonly StreamWriter _programs;
    private bool _threadsHeader;
    private readonly int _every;
    private readonly DateTime _started = DateTime.Now;

    private readonly Stat _cpu = new(), _core = new(), _gpu = new(), _ram = new(), _vram = new(), _disk = new();
    private readonly Stat _gpuTemp = new(), _gpuPower = new(), _cpuTemp = new(), _cpuPower = new();

    private int _readings;
    private bool _stopped;
    private bool _noTempNoted;
    private bool _hot, _ramFull, _vramFull, _cpuHot;

    public string Folder { get; }

    public PulseLogger(int everySeconds, string hardwareReport)
    {
        _every = Math.Max(1, everySeconds);
        Folder = CreateFolder();

        _log = Open("log.txt");
        _perf = Open("perf.txt");
        _temp = Open("temp.txt");
        _threads = Open("threads.txt");
        _programs = Open("programs.txt");

        _log.WriteLine("ZenTune Pulse log");
        _log.WriteLine($"Started: {_started:yyyy-MM-dd HH:mm:ss}");
        _log.WriteLine($"A line is saved every {_every} second(s) in perf.txt, temp.txt, threads.txt and programs.txt.");
        _log.WriteLine();
        if (!string.IsNullOrWhiteSpace(hardwareReport))
        {
            _log.WriteLine(hardwareReport.TrimEnd());
            _log.WriteLine();
        }
        _log.WriteLine("Events:");

        _perf.WriteLine("Time\tCPU %\tBusiest core %\tGPU %\tRAM %\tRAM used GB\tVRAM %\tVRAM used GB\tDisk %\tDisk MB/s\tNet down MB/s\tNet up MB/s");
        _temp.WriteLine("Time\tGPU temp C\tGPU power W\tGPU clock MHz\tGPU fan %\tCPU temp C\tCPU hottest core C\tCPU power W\tCPU clock MHz");
        _programs.WriteLine("Time\tMost processor (name percent; ...)\tMost graphics card (name percent; ...)\tMost memory (name MB; ...)");
    }

    private static string CreateFolder()
    {
        string root = Path.Combine(AppPaths.Documents, "Pulse");
        string name = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", Inv);
        string path = Path.Combine(root, name);
        for (int i = 2; Directory.Exists(path); i++)
            path = Path.Combine(root, $"{name}_{i}");
        Directory.CreateDirectory(path);
        return path;
    }

    private StreamWriter Open(string fileName) =>
        new(new FileStream(Path.Combine(Folder, fileName), FileMode.CreateNew, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(false)) { AutoFlush = true };

    /// <summary>Adds one reading (about one per second). Lines are written every N seconds; the summary uses every reading.</summary>
    public void Add(PulseSnapshot s)
    {
        if (_stopped) return;

        _readings++;

        var gpu = s.Gpu;
        _cpu.Add(s.CpuPercent);
        _core.Add(s.BusiestCorePercent);
        _gpu.Add(gpu?.UsagePercent);
        _ram.Add(s.MemoryPercent);
        _vram.Add(gpu?.VramPercent);
        _disk.Add(s.DiskPercent);
        _gpuTemp.Add(gpu?.TemperatureC);
        _gpuPower.Add(gpu?.PowerWatts);
        _cpuTemp.Add(s.Sensors?.CpuTemperatureC);
        _cpuPower.Add(s.Sensors?.CpuPowerW);

        CheckEvents(s);

        if ((_readings - 1) % _every != 0) return;

        string time = s.When.ToString("yyyy-MM-dd HH:mm:ss", Inv);

        _perf.WriteLine(string.Join('\t',
            time,
            F(s.CpuPercent),
            F(s.BusiestCorePercent),
            F(gpu?.UsagePercent),
            F(s.MemoryPercent),
            F(s.MemoryUsedBytes / 1024d / 1024d / 1024d),
            F(gpu?.VramPercent),
            F(gpu?.VramUsedBytes / 1024d / 1024d / 1024d),
            F(s.DiskPercent),
            F(s.DiskBytesPerSec / 1024d / 1024d, "0.00"),
            F(s.NetDownBytesPerSec / 1024d / 1024d, "0.00"),
            F(s.NetUpBytesPerSec / 1024d / 1024d, "0.00")));

        if (s.CoreLoads is { Count: > 0 } loads)
        {
            if (!_threadsHeader)
            {
                _threadsHeader = true;
                _threads.WriteLine("Time\t" + string.Join('\t', Enumerable.Range(0, loads.Count).Select(i => "Thread " + i + " %")));
            }
            _threads.WriteLine(time + "\t" + string.Join('\t', loads.Select(l => F(l, "0"))));
        }

        _programs.WriteLine(string.Join('\t',
            time,
            string.Join("; ", (s.TopByCpu ?? []).Select(p => p.Name + " " + p.CpuPercent.ToString("0.0", Inv))),
            string.Join("; ", (s.TopByGpu ?? []).Select(p => p.Name + " " + p.GpuPercent.ToString("0.0", Inv))),
            string.Join("; ", (s.TopByMemory ?? []).Select(p => p.Name + " " + (p.MemoryBytes / 1048576d).ToString("0", Inv)))));

        var sensors = s.Sensors;
        bool anyGpu = gpu is not null && (gpu.TemperatureC is not null || gpu.PowerWatts is not null || gpu.ClockMhz is not null || gpu.FanPercent is not null);
        bool anyCpu = sensors is not null && (sensors.CpuTemperatureC is not null || sensors.CpuPowerW is not null || sensors.CpuClockMhz is not null);

        if (anyGpu || anyCpu)
        {
            _temp.WriteLine(string.Join('\t',
                time,
                F(gpu?.TemperatureC), F(gpu?.PowerWatts), F(gpu?.ClockMhz, "0"), F(gpu?.FanPercent, "0"),
                F(sensors?.CpuTemperatureC), F(sensors?.CpuHottestCoreC), F(sensors?.CpuPowerW), F(sensors?.CpuClockMhz, "0")));
        }
        else if (!_noTempNoted)
        {
            _noTempNoted = true;
            Event(s.When, "No temperature sensors can be read, so temp.txt stays empty. ZenTune reads graphics card temperatures " +
                          "from NVIDIA cards only, and processor temperatures need \"Turn on sensors\" on the Pulse page.");
        }
    }

    private void CheckEvents(PulseSnapshot s)
    {
        var when = s.When;
        double? temp = s.Gpu?.TemperatureC;
        double? vram = s.Gpu?.VramPercent;

        Edge(ref _hot, temp >= PulseAdvice.HotGpuC, temp < PulseAdvice.HotGpuC - 5,
            $"Graphics card is hot: {temp:0} °C", $"Graphics card cooled down: {temp:0} °C", when);
        double? cpuTemp = s.Sensors?.CpuTemperatureC;
        Edge(ref _cpuHot, cpuTemp >= 95, cpuTemp < 90,
            $"Processor is very hot: {cpuTemp:0} °C", $"Processor cooled down: {cpuTemp:0} °C", when);
        Edge(ref _ramFull, s.MemoryPercent >= 90, s.MemoryPercent < 85,
            $"Memory is nearly full: {s.MemoryPercent:0} %", $"Memory is back to {s.MemoryPercent:0} %", when);
        Edge(ref _vramFull, vram >= 92, vram < 88,
            $"Video memory is nearly full: {vram:0} %", $"Video memory is back to {vram:0} %", when);
    }

    /// <summary>Writes an event when a value crosses its limit, and again when it is back to normal (so a long spell is two lines, not hundreds).</summary>
    private void Edge(ref bool active, bool high, bool low, string onMessage, string offMessage, DateTime when)
    {
        if (!active && high)
        {
            active = true;
            Event(when, onMessage);
        }
        else if (active && low)
        {
            active = false;
            Event(when, offMessage);
        }
    }

    private void Event(DateTime when, string message) => _log.WriteLine($"[{when:HH:mm:ss}] {message}");

    private static string F(double? value, string format = "0.0") =>
        value is double v && double.IsFinite(v) ? v.ToString(format, Inv) : "-";

    /// <summary>Writes the summary and closes the files.</summary>
    public PulseLogResult Stop()
    {
        var duration = DateTime.Now - _started;
        if (_stopped) return new PulseLogResult(Folder, duration, _readings);
        _stopped = true;

        try
        {
            _log.WriteLine();
            _log.WriteLine($"Stopped: {DateTime.Now:yyyy-MM-dd HH:mm:ss} (length {(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}, {_readings} readings)");
            _log.WriteLine();
            _log.WriteLine("Summary:");
            _log.WriteLine($"  CPU: {_cpu.Summary(" %")}");
            _log.WriteLine($"  Busiest core: highest {_core.Highest(" %")}");
            _log.WriteLine($"  GPU: {_gpu.Summary(" %")}");
            _log.WriteLine($"  Memory: highest {_ram.Highest(" %")}");
            _log.WriteLine($"  Video memory: highest {_vram.Highest(" %")}");
            _log.WriteLine($"  Disk: highest {_disk.Highest(" %")}");
            _log.WriteLine($"  GPU temperature: highest {_gpuTemp.Highest(" °C")}");
            _log.WriteLine($"  GPU power: highest {_gpuPower.Highest(" W")}");
            _log.WriteLine($"  CPU temperature: highest {_cpuTemp.Highest(" °C")}");
            _log.WriteLine($"  CPU power: highest {_cpuPower.Highest(" W")}");
        }
        finally
        {
            _log.Dispose();
            _perf.Dispose();
            _temp.Dispose();
            _threads.Dispose();
            _programs.Dispose();
        }

        return new PulseLogResult(Folder, duration, _readings);
    }

    public void Dispose() => Stop();
}
