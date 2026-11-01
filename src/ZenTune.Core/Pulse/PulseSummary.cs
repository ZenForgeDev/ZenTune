namespace ZenTune.Core.Pulse;

/// <summary>
/// How busy the PC was during something, for example a Measure recording: averages and highest values from Pulse.
/// Saved together with the recording. A value is null when it could not be read (for example no CPU temperature
/// when sensors are off).
/// </summary>
public sealed class PulseSummary
{
    public int Samples { get; set; }
    public double CpuAverage { get; set; }
    public double CpuHighest { get; set; }
    public double BusiestThreadAverage { get; set; }
    public double? GpuAverage { get; set; }
    public double? GpuHighest { get; set; }
    public double? GpuTemperatureHighest { get; set; }
    public double? CpuTemperatureHighest { get; set; }
    public double MemoryHighestPercent { get; set; }
    public double? VideoMemoryHighestPercent { get; set; }
}

/// <summary>
/// Collects Pulse readings while something runs and sums them up afterwards. Asks the monitor to measure while it exists,
/// so create it only when needed and dispose it when done.
/// </summary>
public sealed class PulseCollector : IDisposable
{
    private readonly PulseMonitor _monitor;
    private readonly object _gate = new();
    private readonly List<PulseSnapshot> _readings = [];
    private DateTime _from = DateTime.MinValue;
    private bool _running;

    public PulseCollector(PulseMonitor monitor) => _monitor = monitor;

    /// <summary>Starts collecting. Readings from before <paramref name="from"/> are ignored (for example the countdown before a recording).</summary>
    public void Start(DateTime from)
    {
        ResetFrom(from);
        if (_running) return;

        _running = true;
        _monitor.Updated += OnUpdated;
        _monitor.AddViewer();
    }

    /// <summary>Forgets what was collected and starts counting from <paramref name="from"/>.</summary>
    public void ResetFrom(DateTime from)
    {
        lock (_gate)
        {
            _from = from;
            _readings.Clear();
        }
    }

    private void OnUpdated(PulseSnapshot snapshot)
    {
        lock (_gate)
        {
            if (snapshot.When >= _from) _readings.Add(snapshot);
        }
    }

    /// <summary>Stops collecting and returns the summary, or null if too few readings were made.</summary>
    public PulseSummary? Stop()
    {
        Detach();

        List<PulseSnapshot> list;
        lock (_gate) list = [.. _readings];
        if (list.Count < 3) return null;

        static double? Highest(IEnumerable<double?> values)
        {
            var real = values.Where(v => v.HasValue).Select(v => v!.Value).ToList();
            return real.Count == 0 ? null : real.Max();
        }

        static double? Average(IEnumerable<double?> values)
        {
            var real = values.Where(v => v.HasValue).Select(v => v!.Value).ToList();
            return real.Count == 0 ? null : real.Average();
        }

        return new PulseSummary
        {
            Samples = list.Count,
            CpuAverage = list.Average(s => s.CpuPercent),
            CpuHighest = list.Max(s => s.CpuPercent),
            BusiestThreadAverage = list.Average(s => s.BusiestCorePercent),
            GpuAverage = Average(list.Select(s => s.Gpu?.UsagePercent)),
            GpuHighest = Highest(list.Select(s => s.Gpu?.UsagePercent)),
            GpuTemperatureHighest = Highest(list.Select(s => s.Gpu?.TemperatureC)),
            CpuTemperatureHighest = Highest(list.Select(s => s.Sensors?.CpuTemperatureC)),
            MemoryHighestPercent = list.Max(s => s.MemoryPercent),
            VideoMemoryHighestPercent = Highest(list.Select(s => s.Gpu?.VramPercent)),
        };
    }

    private void Detach()
    {
        if (!_running) return;

        _running = false;
        _monitor.Updated -= OnUpdated;
        _monitor.RemoveViewer();
    }

    public void Dispose() => Detach();
}
