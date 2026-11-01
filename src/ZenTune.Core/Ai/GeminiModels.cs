using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ZenTune.Core.Ai;

/// <summary>What happened when one model was asked a tiny question.</summary>
public enum ModelStatus
{
    /// <summary>Answered, in the JSON format ZenTune needs.</summary>
    Works,
    /// <summary>Answered, but not in the format ZenTune needs.</summary>
    BadAnswer,
    /// <summary>The model exists, but this key has no quota for it right now (often: not part of the free tier).</summary>
    Quota,
    NotFound,
    /// <summary>Google says the model is overloaded right now. It passes.</summary>
    Busy,
    /// <summary>The key is not allowed to use this model.</summary>
    Denied,
    Error,
}

public sealed record ModelResult(string Model, ModelStatus Status, int Milliseconds, string Detail, DateTime Tested);

/// <summary>One entry of the model drop-down: the model name to use, and the text to show.</summary>
public sealed record ModelChoice(string Id, string Label);

/// <summary>
/// Finds out which Gemini models work for the user's own key, so people pick from a list instead of typing names.
/// Results are kept in %LOCALAPPDATA%\ZenTune\ai-models.json.
/// </summary>
public static class GeminiModels
{
    // Models that make pictures, speech or music, or are not plain text models, are not what ZenTune needs
    private static readonly Regex NotForText = new(
        "tts|image|live|embed|transcribe|robotics|computer|audio|omni|banana|aqa|imagen|veo|lyria|native|translate",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static string FilePath => Path.Combine(AppPaths.Root, "ai-models.json");

    public static string StatusText(ModelResult r) => r.Status switch
    {
        ModelStatus.Works => $"Works · {(r.Milliseconds / 1000.0).ToString("0.0", CultureInfo.CurrentCulture)} s",
        ModelStatus.BadAnswer => "Odd answer",
        ModelStatus.Quota => "No quota",
        ModelStatus.NotFound => r.Detail.Contains("no longer available", StringComparison.OrdinalIgnoreCase) ? "Retired" : "Not found",
        ModelStatus.Busy => "Busy",
        ModelStatus.Denied => "No access",
        _ => "Error",
    };

    public static List<ModelResult> LoadResults()
    {
        try
        {
            if (!File.Exists(FilePath)) return [];
            return JsonSerializer.Deserialize<List<ModelResult>>(File.ReadAllText(FilePath), Json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Keeps old results for models that were not tested this time, and replaces the rest.</summary>
    private static void SaveMerged(IReadOnlyCollection<string> allModels, List<ModelResult> fresh)
    {
        try
        {
            var kept = LoadResults().Where(old => allModels.Contains(old.Model) && fresh.All(n => n.Model != old.Model));
            var merged = kept.Concat(fresh).OrderBy(r => r.Model, StringComparer.OrdinalIgnoreCase).ToList();
            File.WriteAllText(FilePath, JsonSerializer.Serialize(merged, Json));
        }
        catch { /* results are a convenience; the test itself already worked */ }
    }

    /// <summary>The Gemini text models this key can see, according to Google.</summary>
    public static async Task<List<string>> ListAsync(string apiKey, CancellationToken ct = default)
    {
        var ids = await GeminiClient.ListModelIdsAsync(apiKey, ct);
        return ids
            .Where(id => id.StartsWith("gemini", StringComparison.OrdinalIgnoreCase) && !NotForText.IsMatch(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Asks every model one tiny question, one after the other (gently, so free keys are not rate-limited), and reports each
    /// result as it comes in. Each test uses one small request from the user's quota. Results found so far are saved even if stopped.
    /// </summary>
    public static async Task<List<ModelResult>> TestAllAsync(string apiKey, IProgress<ModelResult>? progress, CancellationToken ct = default, IProgress<string>? starting = null)
    {
        var models = await ListAsync(apiKey, ct);

        // The model ZenTune would use right now is always tested too, even if Google does not list it for this key
        foreach (var extra in new[] { AiConfig.DefaultModel, AiConfig.Load().Model })
            if (!models.Contains(extra, StringComparer.OrdinalIgnoreCase)) models.Add(extra);
        if (models.Count == 0)
            throw new InvalidOperationException("Google did not list any Gemini text models for this key.");

        var results = new List<ModelResult>();
        try
        {
            foreach (var model in models)
            {
                ct.ThrowIfCancellationRequested();
                starting?.Report(model);
                var result = await GeminiClient.TestModelAsync(apiKey, model, ct);
                results.Add(result);
                progress?.Report(result);
                await Task.Delay(300, ct);
            }
        }
        finally
        {
            if (results.Count > 0) SaveMerged(models, results);
        }

        return results;
    }

    /// <summary>Preview models can be changed or removed by Google without warning.</summary>
    public static bool IsPreview(string model) =>
        model.Contains("preview", StringComparison.OrdinalIgnoreCase) || model.Contains("-exp", StringComparison.OrdinalIgnoreCase);

    /// <summary>The fastest model that answered correctly, whatever it is.</summary>
    public static ModelResult? Fastest(IEnumerable<ModelResult> results) =>
        results.Where(r => r.Status == ModelStatus.Works).OrderBy(r => r.Milliseconds).FirstOrDefault();

    /// <summary>
    /// The model ZenTune recommends: the fastest one that works and is not a preview. If only previews work, the fastest of those.
    /// Speed is one test, and says nothing about quality.
    /// </summary>
    public static ModelResult? Best(IEnumerable<ModelResult> results)
    {
        var working = results.Where(r => r.Status == ModelStatus.Works).OrderBy(r => r.Milliseconds).ToList();
        return working.FirstOrDefault(r => !IsPreview(r.Model)) ?? working.FirstOrDefault();
    }

    /// <summary>
    /// What the model drop-down shows: the models that worked (fastest first), plus the model in use now even if it
    /// has not been tested, so the saved choice is never lost.
    /// </summary>
    public static List<ModelChoice> Choices(string current)
    {
        var results = LoadResults();
        var working = results.Where(r => r.Status == ModelStatus.Works).OrderBy(r => r.Milliseconds).ToList();
        var best = Best(results);

        var list = new List<ModelChoice>();
        foreach (var r in working)
            list.Add(new ModelChoice(r.Model, $"{r.Model} · {StatusText(r)}" + (IsPreview(r.Model) ? " · preview" : "") + (best is not null && r.Model == best.Model ? " ★" : "")));

        if (!list.Any(c => c.Id.Equals(current, StringComparison.OrdinalIgnoreCase)))
        {
            var known = results.FirstOrDefault(r => r.Model.Equals(current, StringComparison.OrdinalIgnoreCase));
            list.Insert(0, new ModelChoice(current, $"{current} · " + (known is null ? "not tested" : StatusText(known))));
        }

        return list;
    }
}
