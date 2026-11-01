using System.Globalization;
using System.Text.RegularExpressions;

namespace ZenTune.Core.Ai;

/// <summary>
/// The "never touch" list. Whatever Gemini suggests, these settings are never shown to it and never written:
/// anything about the screen, the graphics driver, sound, controls, accounts and chat.
/// A wrong graphics setting is easy to undo; a wrong resolution or monitor setting can leave you with a black screen.
/// </summary>
internal static class AiGuard
{
    private static readonly Regex Forbidden = new(
        "resolution|refresh|monitor|adapter|display|fullscreen|window|driver|gamma|brightness|contrast|calibrat|hdr|" +
        "audio|voice|chat|subtitle|language|region|account|login|token|username|name|network|server|telemetry|" +
        "bind|key|sens|mouse|controller|fov|crosshair|aim",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsForbidden(string key) => Forbidden.IsMatch(key);

    private static bool IsNumber(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

    /// <summary>A setting that holds a number may only get a number back (and the other way round for words).</summary>
    public static bool SameShape(string current, string proposed) =>
        !IsNumber(current) || IsNumber(proposed);

    /// <summary>Frame-rate limits must be something sensible.</summary>
    public static bool SaneNumber(string key, string proposed)
    {
        if (!Regex.IsMatch(key, "fps", RegexOptions.IgnoreCase)) return true;
        return double.TryParse(proposed, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && n >= 30 && n <= 1000;
    }
}
