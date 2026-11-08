using System.Runtime.Versioning;
using ZenTune.Core.Changes;

namespace ZenTune.Core.Driver;

/// <summary>
/// The two Windows graphics options that work with every graphics card (Settings → Display → Graphics):
/// variable refresh rate and optimizations for windowed games. Stored for the current user only, no admin needed.
/// The change goes through <see cref="Tweaks"/>, so it is recorded and can be undone.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsGraphics
{
    private const string Key = @"Software\Microsoft\DirectX\UserGpuPreferences";
    private const string Name = "DirectXUserGlobalSettings";

    public const string VariableRefreshRate = "VRROptimizeEnable";
    public const string WindowedOptimizations = "SwapEffectUpgradeEnable";

    /// <summary>True/false when the option is stored, null when Windows has no value for it.</summary>
    public static bool? Get(string token)
    {
        var map = Parse(Tweaks.ReadUserString(Key, Name));
        return map.TryGetValue(token, out var v) ? v == "1" : null;
    }

    /// <summary>Turns one option on or off and keeps every other option in the same value as it was.</summary>
    public static void Set(string token, bool on, string title)
    {
        var current = Tweaks.ReadUserString(Key, Name) ?? "";
        var parts = current.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).ToList();

        string entry = $"{token}={(on ? 1 : 0)}";
        int index = parts.FindIndex(p => p.StartsWith(token + "=", StringComparison.OrdinalIgnoreCase));
        if (index >= 0) parts[index] = entry;
        else parts.Add(entry);

        Tweaks.SetUserString(Key, Name, string.Join(";", parts) + ";", title);
    }

    private static Dictionary<string, string> Parse(string? value)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(value)) return map;

        foreach (var part in value.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq > 0) map[part[..eq].Trim()] = part[(eq + 1)..].Trim();
        }
        return map;
    }
}
