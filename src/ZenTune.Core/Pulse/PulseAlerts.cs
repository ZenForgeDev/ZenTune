namespace ZenTune.Core.Pulse;

/// <summary>
/// Works out when something is worth a message: a hot graphics card or processor, nearly full memory or video memory.
/// A message is given once when a value crosses its limit and not again until it is back to normal.
/// The on/off choice is saved as a small file in %LOCALAPPDATA%\ZenTune (the file exists = on). Off by default.
/// </summary>
public sealed class PulseAlerts
{
    private static bool? _cached;

    private static string FilePath => Path.Combine(AppPaths.Root, "pulse-alerts.txt");

    public static bool Enabled
    {
        get
        {
            if (_cached is bool known) return known;

            bool on;
            try { on = File.Exists(FilePath); }
            catch { on = false; }

            _cached = on;
            return on;
        }
        set
        {
            _cached = value;
            try
            {
                if (value) File.WriteAllText(FilePath, "1");
                else if (File.Exists(FilePath)) File.Delete(FilePath);
            }
            catch
            {
                // The choice then only lasts until ZenTune is closed
            }
        }
    }

    private readonly Dictionary<string, bool> _active = [];

    /// <summary>Messages (title, text) that should be shown for this reading. Usually none.</summary>
    public IReadOnlyList<(string Title, string Text)> Check(PulseSnapshot s)
    {
        var messages = new List<(string, string)>();

        double? gpuTemp = s.Gpu?.TemperatureC;
        Edge(messages, "gpu-hot", gpuTemp >= PulseAdvice.HotGpuC, gpuTemp < PulseAdvice.HotGpuC - 5,
            "Graphics card is hot", $"{gpuTemp:0} °C. If this keeps happening, check airflow and dust.");

        double? cpuTemp = s.Sensors?.CpuTemperatureC;
        Edge(messages, "cpu-hot", cpuTemp >= 95, cpuTemp < 90,
            "Processor is very hot", $"{cpuTemp:0} °C. It may slow itself down.");

        Edge(messages, "ram", s.MemoryPercent >= 92, s.MemoryPercent < 85,
            "Memory is nearly full", $"{s.MemoryPercent:0} % used. Close programs you do not need.");

        double? vram = s.Gpu?.VramPercent;
        Edge(messages, "vram", vram >= 95, vram < 88,
            "Video memory is nearly full", $"{vram:0} % used. Lower the texture quality if the game stutters.");

        return messages;
    }

    private void Edge(List<(string, string)> messages, string key, bool high, bool low, string title, string text)
    {
        _active.TryGetValue(key, out bool active);

        if (!active && high)
        {
            _active[key] = true;
            messages.Add((title, text));
        }
        else if (active && low)
        {
            _active[key] = false;
        }
    }
}
