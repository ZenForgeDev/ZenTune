using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ZenTune.Core.Driver;

/// <summary>
/// Switches G-Sync on or off the way the NVIDIA Control Panel does it, in the order the driver expects, without admin rights:
/// 1) mode + "allow", 2) wait while the driver disconnects and reconnects the screens, 3) the global feature switch,
/// 4) redraw the screens with the display settings Windows had before, 5) preferred refresh rate + the requested mode.
/// The order and the redraw follow the open-source GsyncStatusTrayToggle (MIT licence), which was checked against the
/// Control Panel's own driver database changes.
/// </summary>
[SupportedOSPlatform("windows")]
public static class NvidiaGSync
{
    /// <summary>"Preferred refresh rate": 1 = highest available.</summary>
    public const uint PreferredRefresh = 0x0064B541;

    /// <summary>0 = off, 1 = fullscreen only, 2 = fullscreen and windowed.</summary>
    public static void Apply(uint mode)
    {
        if (mode > 2) throw new ArgumentOutOfRangeException(nameof(mode));
        bool on = mode != 0;
        string label = mode switch { 0 => "off", 1 => "fullscreen only", _ => "fullscreen and windowed" };

        // How the screens look right now, so they can be put back after the driver has reconnected them
        var saved = DisplayConfig.Capture();

        var group1 = new[]
        {
            new DriverWrite(NvidiaCatalog.VrrMode, mode),
            new DriverWrite(NvidiaCatalog.VrrAppOverride, on ? 0u : 4u),
        };
        var group2 = new[] { new DriverWrite(NvidiaCatalog.VrrFeature, on ? 1u : 0u) };
        var group3 = on
            ? new[] { new DriverWrite(PreferredRefresh, 1u), new DriverWrite(NvidiaCatalog.VrrRequestState, mode) }
            : new[] { new DriverWrite(NvidiaCatalog.VrrRequestState, 0u) };

        NvidiaDrs.ApplyStaged($"NVIDIA: G-Sync → {label}", [group1, group2, group3], i =>
        {
            if (i == 0) DisplayConfig.WaitForReturn(saved);
            else if (i == 1) DisplayConfig.Reapply(saved);
        });

        // The driver can leave a screen on a lower refresh rate after reconnecting it
        Thread.Sleep(1200);
        if (DisplayConfig.RateDropped(saved)) DisplayConfig.Reapply(saved);
    }

    // ------------------------------------------------------------ Windows display configuration (CCD API)

    private static class DisplayConfig
    {
        private const uint QdcOnlyActivePaths = 0x00000002;
        private const uint SdcApply = 0x00000080;
        private const uint SdcUseSuppliedDisplayConfig = 0x00000020;
        private const uint SdcNoOptimization = 0x00000100;
        private const int ErrorInsufficientBuffer = 122;

        [StructLayout(LayoutKind.Sequential)]
        internal struct Luid { public uint Low; public int High; }

        [StructLayout(LayoutKind.Sequential)]
        internal struct PathSource { public Luid AdapterId; public uint Id; public uint ModeInfoIdx; public uint StatusFlags; }

        [StructLayout(LayoutKind.Sequential)]
        internal struct PathTarget
        {
            public Luid AdapterId; public uint Id; public uint ModeInfoIdx; public uint OutputTechnology; public uint Rotation;
            public uint Scaling; public uint RefreshNum; public uint RefreshDen; public uint ScanLineOrdering;
            public uint TargetAvailable; public uint StatusFlags;
        }

        // 72 bytes, as Windows lays it out
        [StructLayout(LayoutKind.Sequential)]
        internal struct PathInfo { public PathSource Source; public PathTarget Target; public uint Flags; }

        // 64 bytes: the first fields are named, the rest is carried along untouched
        [StructLayout(LayoutKind.Sequential, Size = 64)]
        internal struct ModeInfo { public uint InfoType; public uint Id; public Luid AdapterId; }

        [DllImport("user32.dll")]
        private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);

        [DllImport("user32.dll")]
        private static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] PathInfo[] paths,
            ref uint numModes, [Out] ModeInfo[] modes, IntPtr topology);

        [DllImport("user32.dll")]
        private static extern int SetDisplayConfig(uint numPaths, [In] PathInfo[] paths, uint numModes, [In] ModeInfo[] modes, uint flags);

        internal sealed class Config
        {
            internal PathInfo[] Paths = [];
            internal ModeInfo[] Modes = [];
        }

        /// <summary>The active screens and their modes, or null when Windows will not tell.</summary>
        public static Config? Capture()
        {
            try
            {
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out uint np, out uint nm) != 0) return null;
                    var paths = new PathInfo[np];
                    var modes = new ModeInfo[nm];
                    int rc = QueryDisplayConfig(QdcOnlyActivePaths, ref np, paths, ref nm, modes, IntPtr.Zero);
                    if (rc == 0)
                    {
                        Array.Resize(ref paths, (int)np);
                        Array.Resize(ref modes, (int)nm);
                        return new Config { Paths = paths, Modes = modes };
                    }
                    if (rc != ErrorInsufficientBuffer) return null;
                }
            }
            catch
            {
                // no screen information: carry on without redrawing
            }
            return null;
        }

        /// <summary>Waits while the driver disconnects and reconnects the screens (about two to four seconds).</summary>
        public static void WaitForReturn(Config? saved)
        {
            Thread.Sleep(1500);
            long end = Environment.TickCount64 + 8000;
            while (Environment.TickCount64 < end)
            {
                var now = Capture();
                if (now is not null && (saved is null || now.Paths.Length == saved.Paths.Length)) break;
                Thread.Sleep(300);
            }
            Thread.Sleep(600);
        }

        /// <summary>Applies the saved screen setup again. This makes Windows and the driver set the modes up from scratch.</summary>
        public static void Reapply(Config? saved)
        {
            if (saved is null || saved.Paths.Length == 0) return;
            try
            {
                SetDisplayConfig((uint)saved.Paths.Length, saved.Paths, (uint)saved.Modes.Length, saved.Modes,
                    SdcApply | SdcUseSuppliedDisplayConfig | SdcNoOptimization);
            }
            catch
            {
                // a failed redraw is not an error: the settings are saved already
            }
        }

        /// <summary>True when a screen now runs at a lower refresh rate than before.</summary>
        public static bool RateDropped(Config? saved)
        {
            var now = Capture();
            if (now is null || saved is null || now.Paths.Length != saved.Paths.Length) return false;
            for (int i = 0; i < now.Paths.Length; i++)
            {
                if (Rate(now.Paths[i]) + 0.5 < Rate(saved.Paths[i])) return true;
            }
            return false;
        }

        private static double Rate(PathInfo p) =>
            p.Target.RefreshDen == 0 ? 0 : p.Target.RefreshNum / (double)p.Target.RefreshDen;
    }
}
