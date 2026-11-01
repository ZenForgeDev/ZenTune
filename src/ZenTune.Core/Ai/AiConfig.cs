using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ZenTune.Core.Ai;

public sealed record AiSettings(string? ApiKey, string Model);

/// <summary>
/// The user's own Gemini API key and model name, kept in %LOCALAPPDATA%\ZenTune\ai.json.
/// The key is encrypted with Windows DPAPI, so only this Windows account on this PC can read it back.
/// ZenTune never ships a key: every user brings their own.
/// </summary>
public static class AiConfig
{
    public const string DefaultModel = "gemini-3.6-flash";

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ZenTune.Gemini.v1");

    private sealed class Stored
    {
        public string? ProtectedKey { get; set; }
        public string? Model { get; set; }
    }

    private static string FilePath => Path.Combine(AppPaths.Root, "ai.json");

    public static AiSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AiSettings(null, DefaultModel);

            var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(FilePath));

            string? key = null;
            var protectedKey = stored?.ProtectedKey;
            if (!string.IsNullOrEmpty(protectedKey))
            {
                var raw = ProtectedData.Unprotect(Convert.FromBase64String(protectedKey), Entropy, DataProtectionScope.CurrentUser);
                key = Encoding.UTF8.GetString(raw);
            }

            var model = stored?.Model;
            model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();
            return new AiSettings(key, model);
        }
        catch
        {
            // Unreadable file, or it was made by another Windows account: ask for the key again
            return new AiSettings(null, DefaultModel);
        }
    }

    /// <summary>Changes only the model and keeps the saved key.</summary>
    public static void SaveModel(string model)
    {
        var current = Load();
        Save(current.ApiKey, model);
    }

    public static void Save(string? apiKey, string model)
    {
        var stored = new Stored { Model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim() };

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(apiKey.Trim()), Entropy, DataProtectionScope.CurrentUser);
            stored.ProtectedKey = Convert.ToBase64String(bytes);
        }

        File.WriteAllText(FilePath, JsonSerializer.Serialize(stored, new JsonSerializerOptions { WriteIndented = true }));
    }
}
