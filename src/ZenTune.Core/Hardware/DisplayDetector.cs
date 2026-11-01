using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ZenTune.Core.Hardware;

/// <summary>
/// Reads every active monitor: name, connection, current and native mode, max refresh rate and HDR state.
/// Uses the Windows DisplayConfig API, the same source the Windows display settings page uses.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class DisplayDetector
{
    public static List<DisplayInfo> DetectDisplays(List<string> warnings)
    {
        var result = new List<DisplayInfo>();

        int err = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount);
        if (err != 0)
        {
            warnings.Add($"Could not read displays (error {err}).");
            return result;
        }

        var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
        var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];

        err = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
        if (err != 0)
        {
            warnings.Add($"Could not read displays (error {err}).");
            return result;
        }

        for (int i = 0; i < pathCount; i++)
        {
            var path = paths[i];
            try
            {
                result.Add(ReadDisplay(path));
            }
            catch (Exception ex)
            {
                warnings.Add($"Could not read one display: {ex.Message}");
            }
        }

        return result;
    }

    private static DisplayInfo ReadDisplay(DISPLAYCONFIG_PATH_INFO path)
    {
        // Monitor name and connection type
        var target = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
        target.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
        target.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>();
        target.header.adapterId = path.targetInfo.adapterId;
        target.header.id = path.targetInfo.id;
        DisplayConfigGetDeviceInfo(ref target);

        // GDI name (\\.\DISPLAY1) so we can ask for display modes
        var source = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
        source.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
        source.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>();
        source.header.adapterId = path.sourceInfo.adapterId;
        source.header.id = path.sourceInfo.id;
        DisplayConfigGetDeviceInfo(ref source);

        // HDR ("advanced color")
        var color = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO();
        color.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO;
        color.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO>();
        color.header.adapterId = path.targetInfo.adapterId;
        color.header.id = path.targetInfo.id;
        bool colorOk = DisplayConfigGetDeviceInfo(ref color) == 0;

        bool hdrSupported = colorOk && (color.value & 0x1) != 0;
        bool hdrEnabled = colorOk && (color.value & 0x2) != 0;
        int bits = colorOk ? (int)color.bitsPerColorChannel : 0;

        string gdiName = source.viewGdiDeviceName ?? "";

        // Current mode
        var current = NewDevMode();
        bool haveCurrent = gdiName.Length > 0 && EnumDisplaySettings(gdiName, ENUM_CURRENT_SETTINGS, ref current);

        // Which GPU the cable is plugged into (\\?\PCI#VEN_10DE&DEV_2C02#...)
        var adapter = new DISPLAYCONFIG_ADAPTER_NAME();
        adapter.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_ADAPTER_NAME;
        adapter.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_ADAPTER_NAME>();
        adapter.header.adapterId = path.targetInfo.adapterId;
        adapter.header.id = 0;
        string adapterKey = DisplayConfigGetDeviceInfo(ref adapter) == 0
            ? HardwareIds.VendorDeviceKey(adapter.adapterDevicePath)
            : "";

        // All modes: native = largest resolution, max refresh = highest Hz at native
        int nativeW = 0, nativeH = 0, maxHz = 0, maxHzCurrent = 0;
        var allModes = new List<(int W, int H, int Hz)>();
        if (gdiName.Length > 0)
        {
            var mode = NewDevMode();
            var all = allModes;
            for (int n = 0; EnumDisplaySettings(gdiName, n, ref mode); n++)
                all.Add(((int)mode.dmPelsWidth, (int)mode.dmPelsHeight, (int)mode.dmDisplayFrequency));

            if (all.Count > 0)
            {
                var largest = all.OrderByDescending(m => (long)m.W * m.H).First();
                nativeW = largest.W;
                nativeH = largest.H;
                maxHz = all.Where(m => m.W == nativeW && m.H == nativeH).Max(m => m.Hz);
            }
        }

        int width = haveCurrent ? (int)current.dmPelsWidth : nativeW;
        int height = haveCurrent ? (int)current.dmPelsHeight : nativeH;

        maxHzCurrent = allModes.Where(m => m.W == width && m.H == height).Select(m => m.Hz).DefaultIfEmpty(0).Max();

        double hz = path.targetInfo.refreshRate.Denominator != 0
            ? (double)path.targetInfo.refreshRate.Numerator / path.targetInfo.refreshRate.Denominator
            : haveCurrent ? current.dmDisplayFrequency : 0;

        bool primary = haveCurrent && current.dmPositionX == 0 && current.dmPositionY == 0;

        string name = string.IsNullOrWhiteSpace(target.monitorFriendlyDeviceName)
            ? (target.outputTechnology == OUTPUT_INTERNAL ? "Built-in display" : "Unknown monitor")
            : target.monitorFriendlyDeviceName.Trim();

        return new DisplayInfo(
            name,
            gdiName,
            primary,
            ConnectionName(target.outputTechnology),
            width,
            height,
            Math.Round(hz, 2),
            nativeW,
            nativeH,
            maxHz,
            maxHzCurrent,
            hdrSupported,
            hdrEnabled,
            bits,
            adapterKey);
    }

    private static string ConnectionName(uint tech) => tech switch
    {
        0 => "VGA",
        4 => "DVI",
        5 => "HDMI",
        6 or 11 or OUTPUT_INTERNAL => "Built-in",
        10 => "DisplayPort",
        15 => "Wireless (Miracast)",
        16 or 17 => "Indirect",
        18 => "USB-C (DisplayPort)",
        _ => "Other"
    };

    internal static DEVMODE NewDevMode() => new() { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };

    // ---------------------------------------------------------------- Win32

    private const uint QDC_ONLY_ACTIVE_PATHS = 0x2;
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO = 9;
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_ADAPTER_NAME = 4;
    private const uint OUTPUT_INTERNAL = 0x80000000;
    internal const int ENUM_CURRENT_SETTINGS = -1;

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_RATIONAL
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public DISPLAYCONFIG_RATIONAL refreshRate;
        public uint scanLineOrdering;
        public int targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    // We never read the mode contents, but the buffer must have the exact native size (64 bytes).
    [StructLayout(LayoutKind.Explicit, Size = 48)]
    private struct DISPLAYCONFIG_MODE_UNION
    {
        [FieldOffset(0)] public ulong alignment;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_MODE_INFO
    {
        public uint infoType;
        public uint id;
        public LUID adapterId;
        public DISPLAYCONFIG_MODE_UNION modeInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public uint type;
        public uint size;
        public LUID adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags;
        public uint outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string monitorDevicePath;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_ADAPTER_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string adapterDevicePath;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint value;               // bit 0 supported, bit 1 enabled
        public uint colorEncoding;
        public uint bitsPerColorChannel;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements,
        [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
        ref uint numModeInfoArrayElements,
        [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
        IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO requestPacket);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_ADAPTER_NAME requestPacket);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplaySettingsW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);
}
