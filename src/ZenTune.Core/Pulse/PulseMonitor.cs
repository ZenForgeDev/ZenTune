namespace ZenTune.Core.Pulse;

/// <summary>
/// Measures the PC once per second, but only while someone needs it: a page or window that shows the numbers,
/// or logging. With nobody watching and no log running, nothing is measured at all.
/// Updated is raised on a background thread; a window must hand the reading over to its own thread.
/// </summary>
public sealed class PulseMonitor
{
    public static PulseMonitor Shared { get; } = new();

    /// <summary>A new reading, about once per second.</summary>
    public event Action<PulseSnapshot>? Updated;

    /// <summary>Something went wrong (measuring could not start, or logging stopped). The text is for the user.</summary>
    public event Action<string>? Failed;

    public PulseSnapshot? Latest { get; private set; }

    private readonly object _gate = new();
    private int _viewers;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private PerformanceSampler? _sampler;
    private PulseLogger? _logger;
    private string? _gpuName;
    private ulong _vramBytes;
    private string _hardwareReport = "";

    public bool IsLogging
    {
        get { lock (_gate) return _logger is not null; }
    }

    public string? LogFolder
    {
        get { lock (_gate) return _logger?.Folder; }
    }

    /// <summary>Tells the monitor which graphics card is the main one and what PC this is (written at the top of every log). Call before the first reading.</summary>
    public void Configure(string? gpuName, ulong vramBytes, string hardwareReport)
    {
        lock (_gate)
        {
            _gpuName = gpuName;
            _vramBytes = vramBytes;
            _hardwareReport = hardwareReport;
        }
    }

    /// <summary>A page or window that shows the numbers calls this when it appears, and RemoveViewer when it goes away.</summary>
    public void AddViewer()
    {
        lock (_gate)
        {
            _viewers++;
            EnsureRunning();
        }
    }

    public void RemoveViewer()
    {
        lock (_gate)
        {
            if (_viewers > 0) _viewers--;
            StopIfIdle();
        }
    }

    /// <summary>Starts a new log in Documents\Pulse and returns its folder. Logging goes on even when no page is open.</summary>
    public string StartLogging(int everySeconds)
    {
        lock (_gate)
        {
            if (_logger is not null) return _logger.Folder;

            _logger = new PulseLogger(Math.Clamp(everySeconds, 1, 60), _hardwareReport);
            EnsureRunning();
            return _logger.Folder;
        }
    }

    /// <summary>Writes the summary and closes the log. Returns null if no log was running.</summary>
    public PulseLogResult? StopLogging()
    {
        lock (_gate)
        {
            if (_logger is null) return null;

            var result = _logger.Stop();
            _logger = null;
            StopIfIdle();
            return result;
        }
    }

    /// <summary>Turns processor/motherboard sensors on or off while measuring. Save the choice in SensorSetting first.</summary>
    public void SetSensors(bool enabled)
    {
        var sampler = _sampler;
        if (sampler is not null) sampler.SensorsEnabled = enabled;
    }

    /// <summary>Starts the sensors again, for example after the sensor driver was installed.</summary>
    public void RestartSensors() => _sampler?.RestartSensors();

    // ---------------------------------------------------------------- Measuring loop (call with _gate held)

    private void EnsureRunning()
    {
        if (_cts is not null) return;

        var cts = new CancellationTokenSource();
        _cts = cts;

        var previous = _loop;
        string? gpuName = _gpuName;
        ulong vram = _vramBytes;

        // A loop that was just stopped may still be finishing; wait for it so two never measure at once
        _loop = Task.Run(async () =>
        {
            if (previous is not null)
            {
                try { await previous; } catch { /* it reported its own problem */ }
            }
            await RunAsync(gpuName, vram, cts);
        });
    }

    private void StopIfIdle()
    {
        if (_viewers > 0 || _logger is not null) return;

        _cts?.Cancel();
        _cts = null;
    }

    private async Task RunAsync(string? gpuName, ulong vram, CancellationTokenSource cts)
    {
        try
        {
            _sampler ??= new PerformanceSampler(gpuName, vram);
            _sampler.SensorsEnabled = SensorSetting.Enabled;
            _sampler.Sample(); // the first reading only gives the counters a starting point

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(cts.Token))
            {
                var snapshot = _sampler.Sample();
                Latest = snapshot;

                LogReading(snapshot);

                try { Updated?.Invoke(snapshot); }
                catch { /* a listener must never stop the measuring */ }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped on purpose
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_cts, cts)) _cts = null; // lets the next AddViewer try again
            }
            RaiseFailed("Pulse could not measure this PC: " + ex.Message);
        }
    }

    private void LogReading(PulseSnapshot snapshot)
    {
        string? problem = null;

        lock (_gate)
        {
            if (_logger is null) return;

            try
            {
                _logger.Add(snapshot);
            }
            catch (Exception ex)
            {
                problem = ex.Message;
                try { _logger.Stop(); } catch { /* already failing */ }
                _logger = null;
                StopIfIdle();
            }
        }

        if (problem is not null) RaiseFailed("Logging stopped: " + problem);
    }

    private void RaiseFailed(string message)
    {
        try { Failed?.Invoke(message); }
        catch { /* nothing to do */ }
    }
}
