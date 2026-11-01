using ZenTune.Core.Hardware;

namespace ZenTune.App;

/// <summary>Data shared between pages, so the hardware scan only runs when needed.</summary>
internal static class AppState
{
    private static Task<HardwareSnapshot>? _hardware;

    public static Task<HardwareSnapshot> HardwareAsync(bool rescan = false)
    {
        if (rescan || _hardware is null || _hardware.IsFaulted)
            _hardware = Task.Run(HardwareDetector.Detect);
        return _hardware;
    }
}
