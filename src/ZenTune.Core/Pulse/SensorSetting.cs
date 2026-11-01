namespace ZenTune.Core.Pulse;

/// <summary>
/// The "Turn on sensors" choice. Off by default. Saved as a small file in %LOCALAPPDATA%\ZenTune
/// (the file exists = on), so it is remembered between launches.
/// </summary>
public static class SensorSetting
{
    private static bool? _cached;

    private static string FilePath => Path.Combine(AppPaths.Root, "sensors-enabled.txt");

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
}
