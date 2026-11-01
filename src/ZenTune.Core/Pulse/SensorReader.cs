using LibreHardwareMonitor.Hardware;

namespace ZenTune.Core.Pulse;

/// <summary>
/// Reads processor and motherboard sensors with LibreHardwareMonitorLib (MPL-2.0). Only created when the user has turned
/// sensors on. The library needs a sensor driver (PawnIO) for most processor sensors; without it the values stay empty.
/// Call from one thread at a time.
/// </summary>
internal sealed class SensorReader : IDisposable
{
    private readonly Computer _computer;

    private SensorReader(Computer computer) => _computer = computer;

    /// <summary>Returns null and a message for the user if the sensors could not be started.</summary>
    public static SensorReader? TryCreate(out string? problem)
    {
        problem = null;
        try
        {
            var computer = new Computer
            {
                IsCpuEnabled = true,
                IsMotherboardEnabled = true,
            };
            computer.Open();
            return new SensorReader(computer);
        }
        catch (Exception ex)
        {
            problem = "The sensors could not be started: " + ex.Message;
            return null;
        }
    }

    public SensorReading Read()
    {
        double? package = null, tctl = null, coreMax = null, coreAverage = null, ccd = null;
        double? hottestNumbered = null, power = null, clock = null;
        var clocks = new List<double>();
        var board = new List<NamedValue>();

        void Visit(IHardware hardware)
        {
            hardware.Update();

            bool isCpu = hardware.HardwareType == HardwareType.Cpu;
            bool isBoard = hardware.HardwareType is HardwareType.Motherboard or HardwareType.SuperIO or HardwareType.EmbeddedController;

            foreach (var sensor in hardware.Sensors)
            {
                if (sensor.Value is not float raw || !float.IsFinite(raw)) continue;
                double value = raw;
                string name = sensor.Name ?? "";

                if (isCpu)
                {
                    switch (sensor.SensorType)
                    {
                        case SensorType.Temperature:
                            // Intel: "CPU Package", "Core Max", "CPU Core #1" ... AMD: "Core (Tctl/Tdie)", "CCD1 (Tdie)" ...
                            if (name.Equals("CPU Package", StringComparison.OrdinalIgnoreCase)) package = value;
                            else if (name.StartsWith("Core (", StringComparison.OrdinalIgnoreCase)) tctl = value;
                            else if (name.Equals("Core Max", StringComparison.OrdinalIgnoreCase)) coreMax = value;
                            else if (name.Equals("Core Average", StringComparison.OrdinalIgnoreCase)) coreAverage = value;
                            else if (name.StartsWith("CCD", StringComparison.OrdinalIgnoreCase)) ccd = Math.Max(ccd ?? value, value);
                            else if (name.Contains("Core #", StringComparison.OrdinalIgnoreCase)
                                     && !name.Contains("Distance", StringComparison.OrdinalIgnoreCase))
                                hottestNumbered = Math.Max(hottestNumbered ?? value, value);
                            break;

                        case SensorType.Power:
                            // Without the sensor driver the library reports 0 W, which is "no reading", not a real value
                            if (value > 0 && (name.Equals("CPU Package", StringComparison.OrdinalIgnoreCase) || name.Equals("Package", StringComparison.OrdinalIgnoreCase)))
                                power = value;
                            break;

                        case SensorType.Clock:
                            // "CPU Core #1" (Intel) or "Core #1" (AMD); not "Bus Speed"
                            if (name.Contains("Core #", StringComparison.OrdinalIgnoreCase) && value > 0)
                            {
                                clock = Math.Max(clock ?? value, value);
                                clocks.Add(value);
                            }
                            break;
                    }
                }
                else if (isBoard && sensor.SensorType == SensorType.Temperature && value > 0 && value < 150)
                {
                    board.Add(new NamedValue(name, value));
                }
            }

            foreach (var sub in hardware.SubHardware) Visit(sub);
        }

        foreach (var hardware in _computer.Hardware) Visit(hardware);

        double? cpuTemp = package ?? tctl ?? coreMax ?? hottestNumbered ?? ccd ?? coreAverage;
        double? hottest = hottestNumbered ?? coreMax ?? ccd;

        string? problem = cpuTemp is null
            ? "The processor temperature could not be read. The sensor driver (PawnIO) may be missing, " +
              "or ZenTune may need to run as administrator."
            : null;

        double? clockAverage = clocks.Count > 0 ? clocks.Average() : null;

        return new SensorReading(cpuTemp, hottest, power, clock, board, problem, clockAverage);
    }

    public void Dispose()
    {
        try { _computer.Close(); }
        catch { /* closing a sensor driver must never stop ZenTune */ }
    }
}
