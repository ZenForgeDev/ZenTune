using System.Runtime.InteropServices;

namespace ZenTune.Core.Pulse;

/// <summary>
/// Windows performance counters through PDH (the same counters Task Manager reads).
/// Counters are added with their English names, so this works on every Windows language
/// (the counter names shown in the Norwegian performance tools are translated, the English ones always work).
/// </summary>
internal sealed class PdhQuery : IDisposable
{
    private const uint FormatDouble = 0x00000200;
    private const uint FormatNoCap100 = 0x00008000;
    private const uint MoreData = 0x800007D2;
    private const uint ValidData = 0;
    private const uint NewData = 1;

    [StructLayout(LayoutKind.Explicit)]
    private struct FmtValue
    {
        [FieldOffset(0)] public uint Status;
        [FieldOffset(8)] public double Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Item
    {
        public IntPtr Name;
        public FmtValue Value;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhOpenQueryW")]
    private static extern uint PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhAddEnglishCounterW")]
    private static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterValue")]
    private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, IntPtr type, out FmtValue value);

    [DllImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")]
    private static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr buffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);

    private IntPtr _query;

    public PdhQuery()
    {
        if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0)
            throw new InvalidOperationException("Windows performance counters could not be opened.");
    }

    /// <summary>Adds a counter such as \Processor Information(_Total)\% Processor Utility. Returns IntPtr.Zero if this PC does not have it.</summary>
    public IntPtr Add(string path) =>
        PdhAddEnglishCounter(_query, path, IntPtr.Zero, out var counter) == 0 ? counter : IntPtr.Zero;

    /// <summary>Takes a reading of every counter. Rates (per second, percent busy) need two readings, so the first value is empty.</summary>
    public void Collect() => PdhCollectQueryData(_query);

    /// <summary>The value of a single-instance counter, or null if there is no valid value yet.</summary>
    public double? Value(IntPtr counter, bool noCap = false)
    {
        if (counter == IntPtr.Zero) return null;

        uint rc = PdhGetFormattedCounterValue(counter, FormatDouble | (noCap ? FormatNoCap100 : 0), IntPtr.Zero, out var v);
        if (rc != 0 || (v.Status != ValidData && v.Status != NewData)) return null;
        return double.IsFinite(v.Value) ? v.Value : null;
    }

    /// <summary>All instances of a wildcard counter, for example one value per processor core.</summary>
    public List<(string Name, double Value)> Values(IntPtr counter, bool noCap = false)
    {
        var list = new List<(string Name, double Value)>();
        if (counter == IntPtr.Zero) return list;

        uint format = FormatDouble | (noCap ? FormatNoCap100 : 0);

        uint size = 0;
        uint rc = PdhGetFormattedCounterArray(counter, format, ref size, out _, IntPtr.Zero);
        if (rc != MoreData || size == 0) return list;

        // A little extra room: instances (such as new programs using the GPU) can appear between the two calls
        size *= 2;
        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            rc = PdhGetFormattedCounterArray(counter, format, ref size, out uint count, buffer);
            if (rc != 0) return list;

            int stride = Marshal.SizeOf<Item>();
            for (int i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<Item>(buffer + i * stride);
                if (item.Value.Status != ValidData && item.Value.Status != NewData) continue;
                if (!double.IsFinite(item.Value.Value)) continue;

                string name = Marshal.PtrToStringUni(item.Name) ?? "";
                list.Add((name, item.Value.Value));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return list;
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero)
        {
            PdhCloseQuery(_query);
            _query = IntPtr.Zero;
        }
    }
}
