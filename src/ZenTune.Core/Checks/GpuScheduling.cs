using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ZenTune.Core.Checks;

public enum HagsState { Unknown, NotSupported, Off, On }

/// <summary>
/// Reads the real state of hardware-accelerated GPU scheduling straight from the graphics kernel
/// (the same source Windows uses), instead of guessing from the registry. The HwSchMode registry value
/// is often missing, and Windows sometimes hides the switch in Settings.
/// </summary>
[SupportedOSPlatform("windows")]
public static class GpuScheduling
{
    public static HagsState Query()
    {
        try
        {
            var enumArgs = new D3DKMT_ENUMADAPTERS2();
            if (D3DKMTEnumAdapters2(ref enumArgs) != 0 || enumArgs.NumAdapters == 0)
                return HagsState.Unknown;

            int itemSize = Marshal.SizeOf<D3DKMT_ADAPTERINFO>();
            var buffer = Marshal.AllocHGlobal(itemSize * (int)enumArgs.NumAdapters);
            try
            {
                enumArgs.pAdapters = buffer;
                if (D3DKMTEnumAdapters2(ref enumArgs) != 0) return HagsState.Unknown;

                bool anySupported = false, anyEnabled = false;
                for (int i = 0; i < enumArgs.NumAdapters; i++)
                {
                    var info = Marshal.PtrToStructure<D3DKMT_ADAPTERINFO>(buffer + i * itemSize);
                    try
                    {
                        var caps = QueryCaps(info.hAdapter);
                        if (caps is null) continue;
                        bool supported = (caps.Value & 0x1) != 0;
                        bool enabled = (caps.Value & 0x2) != 0;
                        anySupported |= supported;
                        anyEnabled |= supported && enabled;
                    }
                    finally
                    {
                        var close = new D3DKMT_CLOSEADAPTER { hAdapter = info.hAdapter };
                        D3DKMTCloseAdapter(ref close);
                    }
                }

                if (anyEnabled) return HagsState.On;
                return anySupported ? HagsState.Off : HagsState.NotSupported;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            return HagsState.Unknown; // older Windows without these functions
        }
    }

    private static uint? QueryCaps(uint adapter)
    {
        var data = Marshal.AllocHGlobal(sizeof(uint));
        try
        {
            Marshal.WriteInt32(data, 0);
            var q = new D3DKMT_QUERYADAPTERINFO
            {
                hAdapter = adapter,
                Type = KMTQAITYPE_WDDM_2_7_CAPS,
                pPrivateDriverData = data,
                PrivateDriverDataSize = sizeof(uint),
            };
            if (D3DKMTQueryAdapterInfo(ref q) != 0) return null;
            return (uint)Marshal.ReadInt32(data);
        }
        finally
        {
            Marshal.FreeHGlobal(data);
        }
    }

    private const uint KMTQAITYPE_WDDM_2_7_CAPS = 70;

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_ADAPTERINFO
    {
        public uint hAdapter;
        public LUID AdapterLuid;
        public uint NumOfSources;
        public int bPrecisePresentRegionsPreferred;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_ENUMADAPTERS2
    {
        public uint NumAdapters;
        public IntPtr pAdapters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_QUERYADAPTERINFO
    {
        public uint hAdapter;
        public uint Type;
        public IntPtr pPrivateDriverData;
        public uint PrivateDriverDataSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_CLOSEADAPTER { public uint hAdapter; }

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTEnumAdapters2(ref D3DKMT_ENUMADAPTERS2 args);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTQueryAdapterInfo(ref D3DKMT_QUERYADAPTERINFO args);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTCloseAdapter(ref D3DKMT_CLOSEADAPTER args);
}
