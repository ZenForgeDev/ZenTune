using System.Runtime.InteropServices;
using System.Text;

namespace ZenTune.Core.Pulse;

/// <summary>One reading from an NVIDIA graphics card. A value is null when the driver does not report it.</summary>
internal sealed record NvidiaReading(
    string Name,
    double? UsagePercent,
    double? VramUsedBytes,
    double? VramTotalBytes,
    double? TemperatureC,
    double? PowerWatts,
    double? ClockMhz,
    double? FanPercent);

/// <summary>
/// Reads an NVIDIA card through NVML, the library that ships with the NVIDIA driver and that nvidia-smi uses.
/// It only reads; it never changes any setting. The library is loaded from System32 or the NVIDIA folder by full path.
/// If NVML is missing (no NVIDIA driver) TryCreate returns null and ZenTune falls back to Windows' own GPU counters.
/// </summary>
internal sealed class NvmlReader : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Utilization { public uint Gpu; public uint Memory; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryInfo { public ulong Total; public ulong Free; public ulong Used; }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int InitFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ShutdownFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CountFn(out uint count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int HandleFn(uint index, out IntPtr device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)] private delegate int NameFn(IntPtr device, StringBuilder name, uint length);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int TempFn(IntPtr device, int sensor, out uint temp);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int UtilFn(IntPtr device, out Utilization utilization);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int MemFn(IntPtr device, out MemoryInfo memory);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int UIntFn(IntPtr device, out uint value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ClockFn(IntPtr device, int type, out uint mhz);

    private IntPtr _library;
    private readonly IntPtr _device;
    private readonly string _name;
    private readonly ShutdownFn? _shutdown;
    private readonly TempFn? _temp;
    private readonly UtilFn? _util;
    private readonly MemFn? _mem;
    private readonly UIntFn? _power;
    private readonly UIntFn? _fan;
    private readonly ClockFn? _clock;

    private NvmlReader(IntPtr library, IntPtr device, string name, ShutdownFn? shutdown,
        TempFn? temp, UtilFn? util, MemFn? mem, UIntFn? power, UIntFn? fan, ClockFn? clock)
    {
        _library = library;
        _device = device;
        _name = name;
        _shutdown = shutdown;
        _temp = temp;
        _util = util;
        _mem = mem;
        _power = power;
        _fan = fan;
        _clock = clock;
    }

    private static IEnumerable<string> Candidates()
    {
        yield return Path.Combine(Environment.SystemDirectory, "nvml.dll");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "NVIDIA Corporation", "NVSMI", "nvml.dll");
    }

    private static T? Export<T>(IntPtr library, string name) where T : Delegate =>
        NativeLibrary.TryGetExport(library, name, out var address) ? Marshal.GetDelegateForFunctionPointer<T>(address) : null;

    /// <summary>Returns a reader for the NVIDIA card with the most video memory, or null if there is no NVIDIA driver.</summary>
    public static NvmlReader? TryCreate()
    {
        IntPtr library = IntPtr.Zero;
        try
        {
            foreach (var path in Candidates())
            {
                if (File.Exists(path) && NativeLibrary.TryLoad(path, out library)) break;
                library = IntPtr.Zero;
            }
            if (library == IntPtr.Zero) return null;

            var init = Export<InitFn>(library, "nvmlInit_v2");
            var count = Export<CountFn>(library, "nvmlDeviceGetCount_v2");
            var handle = Export<HandleFn>(library, "nvmlDeviceGetHandleByIndex_v2");
            var shutdown = Export<ShutdownFn>(library, "nvmlShutdown");
            if (init is null || count is null || handle is null || init() != 0)
            {
                NativeLibrary.Free(library);
                return null;
            }

            var name = Export<NameFn>(library, "nvmlDeviceGetName");
            var mem = Export<MemFn>(library, "nvmlDeviceGetMemoryInfo");

            if (count(out uint n) != 0 || n == 0)
            {
                shutdown?.Invoke();
                NativeLibrary.Free(library);
                return null;
            }

            // Pick the card with the most video memory (the main graphics card)
            IntPtr best = IntPtr.Zero;
            ulong bestMemory = 0;
            for (uint i = 0; i < n; i++)
            {
                if (handle(i, out var device) != 0) continue;
                ulong total = 0;
                if (mem is not null && mem(device, out var info) == 0) total = info.Total;
                if (best == IntPtr.Zero || total > bestMemory)
                {
                    best = device;
                    bestMemory = total;
                }
            }
            if (best == IntPtr.Zero)
            {
                shutdown?.Invoke();
                NativeLibrary.Free(library);
                return null;
            }

            string gpuName = "NVIDIA graphics card";
            if (name is not null)
            {
                var sb = new StringBuilder(96);
                if (name(best, sb, (uint)sb.Capacity) == 0 && sb.Length > 0) gpuName = sb.ToString();
            }

            return new NvmlReader(library, best, gpuName, shutdown,
                Export<TempFn>(library, "nvmlDeviceGetTemperature"),
                Export<UtilFn>(library, "nvmlDeviceGetUtilizationRates"),
                mem,
                Export<UIntFn>(library, "nvmlDeviceGetPowerUsage"),
                Export<UIntFn>(library, "nvmlDeviceGetFanSpeed"),
                Export<ClockFn>(library, "nvmlDeviceGetClockInfo"));
        }
        catch
        {
            if (library != IntPtr.Zero)
            {
                try { NativeLibrary.Free(library); } catch { /* nothing to do */ }
            }
            return null;
        }
    }

    public NvidiaReading Read()
    {
        double? usage = null, used = null, total = null, temp = null, power = null, clock = null, fan = null;

        try
        {
            if (_util is not null && _util(_device, out var u) == 0) usage = u.Gpu;
            if (_mem is not null && _mem(_device, out var m) == 0) { used = m.Used; total = m.Total; }
            if (_temp is not null && _temp(_device, 0, out uint t) == 0) temp = t; // 0 = the GPU core sensor
            if (_power is not null && _power(_device, out uint mw) == 0) power = mw / 1000.0; // milliwatts
            if (_clock is not null && _clock(_device, 0, out uint mhz) == 0) clock = mhz;    // 0 = graphics clock
            if (_fan is not null && _fan(_device, out uint f) == 0) fan = f;
        }
        catch
        {
            // A driver that misbehaves must never take the app down; the values stay empty
        }

        return new NvidiaReading(_name, usage, used, total, temp, power, clock, fan);
    }

    public void Dispose()
    {
        if (_library == IntPtr.Zero) return;
        try { _shutdown?.Invoke(); } catch { /* nothing to do */ }
        try { NativeLibrary.Free(_library); } catch { /* nothing to do */ }
        _library = IntPtr.Zero;
    }
}
