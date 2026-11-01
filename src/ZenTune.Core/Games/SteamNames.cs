using System.Text.Json;

namespace ZenTune.Core.Games;

/// <summary>
/// Looks up the name of a Steam game that is not installed any more (Steam's own files no longer have it).
/// Uses Steam's public store page data. Without internet the app id is shown instead.
/// </summary>
public static class SteamNames
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };

    public static async Task<string?> LookupAsync(string appId)
    {
        try
        {
            var json = await Http.GetStringAsync($"https://store.steampowered.com/api/appdetails?appids={appId}&filters=basic");
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(appId, out var entry) &&
                entry.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.True &&
                entry.TryGetProperty("data", out var data) &&
                data.TryGetProperty("name", out var name))
            {
                return name.GetString();
            }
        }
        catch { /* offline or delisted */ }
        return null;
    }
}
