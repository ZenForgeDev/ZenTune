using System.Diagnostics;

namespace ZenTune.Core.Pulse;

/// <summary>
/// Finds the programs that use the most processor and memory. Read only: it looks at the list of running programs
/// the way Task Manager does. Processes that Windows does not let us look at are skipped.
/// The work is done every second reading; in between the last answer is returned. Call from one thread at a time.
/// </summary>
internal sealed class ProcessSampler
{
    private const int Top = 5;

    private Dictionary<int, TimeSpan> _lastCpuTime = [];
    private DateTime _lastWhen = DateTime.MinValue;
    private int _calls;

    private IReadOnlyList<ProgramUse> _byCpu = [];
    private IReadOnlyList<ProgramUse> _byMemory = [];

    public (IReadOnlyList<ProgramUse> ByCpu, IReadOnlyList<ProgramUse> ByMemory) Sample()
    {
        _calls++;
        if (_lastWhen != DateTime.MinValue && _calls % 2 != 0) return (_byCpu, _byMemory);

        try
        {
            Measure();
        }
        catch
        {
            // The list is a nice extra; if it fails the rest of Pulse keeps working
        }

        return (_byCpu, _byMemory);
    }

    private void Measure()
    {
        var now = DateTime.UtcNow;
        double seconds = (now - _lastWhen).TotalSeconds;
        bool canCompare = _lastWhen != DateTime.MinValue && seconds > 0.2;
        int cores = Math.Max(1, Environment.ProcessorCount);

        var cpuTimes = new Dictionary<int, TimeSpan>();
        var perProgram = new Dictionary<string, (double Cpu, ulong Memory)>(StringComparer.OrdinalIgnoreCase);

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                int id = process.Id;
                if (id == 0) continue; // the "Idle" entry is not a program

                string name = process.ProcessName;

                TimeSpan cpuTime = TimeSpan.Zero;
                try { cpuTime = process.TotalProcessorTime; } catch { /* not allowed to look at this one */ }

                ulong memory = 0;
                try { memory = (ulong)Math.Max(0, process.WorkingSet64); } catch { /* same */ }

                cpuTimes[id] = cpuTime;

                double cpu = 0;
                if (canCompare && _lastCpuTime.TryGetValue(id, out var before))
                    cpu = Math.Max(0, (cpuTime - before).TotalSeconds) / seconds / cores * 100.0;

                perProgram[name] = perProgram.TryGetValue(name, out var sum)
                    ? (sum.Cpu + cpu, sum.Memory + memory)
                    : (cpu, memory);
            }
            catch
            {
                // The program closed while we looked at it
            }
            finally
            {
                process.Dispose();
            }
        }

        _lastCpuTime = cpuTimes;
        _lastWhen = now;

        var all = perProgram.Select(p => new ProgramUse(p.Key, Math.Min(100, p.Value.Cpu), p.Value.Memory)).ToList();

        // The first round has nothing to compare with, so it has no processor numbers yet
        _byCpu = canCompare
            ? all.Where(p => p.CpuPercent >= 0.05).OrderByDescending(p => p.CpuPercent).Take(Top).ToList()
            : [];
        _byMemory = all.OrderByDescending(p => p.MemoryBytes).Take(Top).ToList();
    }
}
