using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ZenTune.Core.Ai;

/// <summary>
/// A tiny client for Google's Gemini API (generateContent). It sends one prompt and returns the text of the answer.
/// The API key goes in the x-goog-api-key header and is only ever sent to generativelanguage.googleapis.com.
/// </summary>
internal static class GeminiClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(120) };

    /// <summary>
    /// Asks Gemini and returns the text of its answer. Short problems on Google's side (busy, overloaded, a dropped
    /// connection, a blank answer) are retried up to two more times with a short pause, so people rarely see an error.
    /// Problems that retrying cannot fix (wrong key, unknown model) are reported straight away.
    /// </summary>
    public static async Task<string> GenerateJsonAsync(string apiKey, string model, string prompt, CancellationToken ct = default,
        IProgress<string>? progress = null)
    {
        const int attempts = 3;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await AttemptAsync(apiKey, model, prompt, ct);
            }
            catch (TransientGeminiException) when (attempt < attempts)
            {
                progress?.Report($"Gemini is busy, trying again ({attempt} of {attempts - 1})...");
                var pause = TimeSpan.FromSeconds(attempt == 1 ? 2 : 5) + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 800));
                await Task.Delay(pause, ct);
            }
        }
    }

    /// <summary>A problem that is likely to pass by itself. It is still an InvalidOperationException, so callers show its text if every try fails.</summary>
    private sealed class TransientGeminiException(string message) : InvalidOperationException(message);

    private static async Task<string> AttemptAsync(string apiKey, string model, string prompt, CancellationToken ct)
    {
        var url = ModelUrl(model);

        HttpStatusCode status;
        string body;
        try
        {
            (status, body) = await PostAsync(url, apiKey, Body(prompt, jsonMode: true), ct);

            // If Google rejects the JSON-mode field (its field names have changed before), ask again without it.
            // The prompt already demands JSON and ZenTune validates every value either way.
            if (status == HttpStatusCode.BadRequest && !IsKeyProblem(body))
                (status, body) = await PostAsync(url, apiKey, Body(prompt, jsonMode: false), ct);
        }
        catch (InvalidOperationException ex)
        {
            // No connection or no answer in time
            throw new TransientGeminiException(ex.Message);
        }

        if ((int)status is < 200 or >= 300)
        {
            var message = Explain(status, body, model);
            bool passes = !IsKeyProblem(body) &&
                          (IsBusy(status, body) || status == HttpStatusCode.TooManyRequests || (int)status >= 500);
            throw passes ? new TransientGeminiException(message) : new InvalidOperationException(message);
        }

        try
        {
            return ExtractText(body);
        }
        catch (InvalidOperationException ex)
        {
            throw new TransientGeminiException(ex.Message);
        }
        catch (JsonException)
        {
            throw new TransientGeminiException("Gemini's answer could not be read. Try again.");
        }
    }

    internal static string ModelUrl(string model) =>
        $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(model)}:generateContent";

    // ---------------------------------------------------------------- Model list and model test

    /// <summary>Asks Google which models this key can see (models.list), following the pages.</summary>
    internal static async Task<List<string>> ListModelIdsAsync(string apiKey, CancellationToken ct = default)
    {
        var ids = new List<string>();
        string? pageToken = null;

        for (int page = 0; page < 5; page++)
        {
            var url = "https://generativelanguage.googleapis.com/v1beta/models?pageSize=200" +
                      (pageToken is null ? "" : "&pageToken=" + Uri.EscapeDataString(pageToken));

            var (status, body) = await GetAsync(url, apiKey, ct);
            if ((int)status is < 200 or >= 300)
                throw new InvalidOperationException(Explain(status, body, "(model list)"));

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in models.EnumerateArray())
                {
                    if (!m.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String) continue;
                    var name = n.GetString() ?? "";
                    if (name.StartsWith("models/", StringComparison.Ordinal)) name = name["models/".Length..];
                    if (name.Length > 0 && SupportsGenerate(m)) ids.Add(name);
                }
            }

            pageToken = root.TryGetProperty("nextPageToken", out var next) && next.ValueKind == JsonValueKind.String
                ? next.GetString()
                : null;
            if (string.IsNullOrEmpty(pageToken)) break;
        }

        return ids;
    }

    private static bool SupportsGenerate(JsonElement model)
    {
        // Google's docs name this field "supportedGenerationMethods" in the REST reference and "supportedActions" in the SDKs
        foreach (var field in new[] { "supportedGenerationMethods", "supportedActions" })
        {
            if (model.TryGetProperty(field, out var methods) && methods.ValueKind == JsonValueKind.Array)
            {
                return methods.EnumerateArray().Any(x =>
                    x.ValueKind == JsonValueKind.String &&
                    string.Equals(x.GetString(), "generateContent", StringComparison.OrdinalIgnoreCase));
            }
        }
        return true; // the list does not say, so let the test decide
    }

    /// <summary>
    /// Asks one model one tiny question that needs a correct JSON answer, which is the format ZenTune relies on.
    /// A model that does not answer within 45 seconds is marked as an error. A model that is busy gets one more try after a few seconds.
    /// A rejected key stops everything (it would fail for every model); any other problem is reported for this model only.
    /// </summary>
    internal static async Task<ModelResult> TestModelAsync(string apiKey, string model, CancellationToken ct = default)
    {
        const string prompt = "Reply with only this JSON, with the number filled in and nothing else: {\"sum\": <2+3>}";
        var sw = new Stopwatch();

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(45));

                sw.Restart();
                var (status, body) = await PostAsync(ModelUrl(model), apiKey, Body(prompt, jsonMode: false), timeout.Token);
                sw.Stop();
                int ms = (int)sw.ElapsedMilliseconds;

                if (IsKeyProblem(body))
                    throw new KeyRejectedException(Explain(status, body, model));

                if ((int)status is >= 200 and < 300)
                {
                    string text;
                    try { text = ExtractText(body); }
                    catch (InvalidOperationException ex) { return new ModelResult(model, ModelStatus.BadAnswer, ms, ex.Message, DateTime.Now); }

                    return AnswersCorrectly(text)
                        ? new ModelResult(model, ModelStatus.Works, ms, "Answered correctly", DateTime.Now)
                        : new ModelResult(model, ModelStatus.BadAnswer, ms, "It answered, but not in the format ZenTune needs.", DateTime.Now);
                }

                bool busy = IsBusy(status, body);
                if (busy && attempt == 0)
                {
                    await Task.Delay(4000, ct);
                    continue;
                }

                var kind = busy ? ModelStatus.Busy : status switch
                {
                    HttpStatusCode.TooManyRequests => ModelStatus.Quota,
                    HttpStatusCode.NotFound => ModelStatus.NotFound,
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ModelStatus.Denied,
                    _ => ModelStatus.Error,
                };

                var detail = ErrorMessage(body).Replace('\n', ' ').Trim();
                if (detail.Length > 160) detail = detail[..160] + "…";
                return new ModelResult(model, kind, ms, detail, DateTime.Now);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Our own 45 second limit, not the user pressing Stop
                return new ModelResult(model, ModelStatus.Error, (int)sw.ElapsedMilliseconds, "No answer within 45 seconds.", DateTime.Now);
            }
            catch (InvalidOperationException ex) when (ex is not KeyRejectedException)
            {
                // Network trouble for this one model
                return new ModelResult(model, ModelStatus.Error, (int)sw.ElapsedMilliseconds, ex.Message, DateTime.Now);
            }
        }
    }

    /// <summary>Google says the model is overloaded right now. That passes, so it is not an error in the model.</summary>
    private static bool IsBusy(HttpStatusCode status, string body) =>
        status is HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout ||
        body.Contains("high demand", StringComparison.OrdinalIgnoreCase) ||
        body.Contains("overloaded", StringComparison.OrdinalIgnoreCase);

    private static bool AnswersCorrectly(string text)
    {
        int start = text.IndexOf('{');
        int end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return false;

        try
        {
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            if (!doc.RootElement.TryGetProperty("sum", out var sum)) return false;
            return sum.ValueKind == JsonValueKind.Number
                ? sum.TryGetInt32(out var n) && n == 5
                : sum.ValueKind == JsonValueKind.String && sum.GetString()?.Trim() == "5";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Body(string prompt, bool jsonMode)
    {
        var root = new JsonObject
        {
            ["contents"] = new JsonArray
            {
                new JsonObject
                {
                    ["parts"] = new JsonArray { new JsonObject { ["text"] = prompt } },
                },
            },
        };

        if (jsonMode)
            root["generationConfig"] = new JsonObject { ["responseMimeType"] = "application/json" };

        return root.ToJsonString();
    }

    private static async Task<(HttpStatusCode Status, string Body)> PostAsync(string url, string apiKey, string json, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("x-goog-api-key", apiKey);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            using var response = await Http.SendAsync(request, ct);
            return (response.StatusCode, await response.Content.ReadAsStringAsync(ct));
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException("Gemini took too long to answer. Try again in a moment.");
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Could not reach Gemini. Check your internet connection. ({ex.Message})");
        }
    }

    internal static async Task<(HttpStatusCode Status, string Body)> GetAsync(string url, string apiKey, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("x-goog-api-key", apiKey);

        try
        {
            using var response = await Http.SendAsync(request, ct);
            return (response.StatusCode, await response.Content.ReadAsStringAsync(ct));
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException("Gemini took too long to answer. Try again in a moment.");
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Could not reach Gemini. Check your internet connection. ({ex.Message})");
        }
    }

    private static bool IsKeyProblem(string body) =>
        body.Contains("API key not valid", StringComparison.OrdinalIgnoreCase) ||
        body.Contains("API_KEY_INVALID", StringComparison.OrdinalIgnoreCase);

    private static string ErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error) &&
                error.TryGetProperty("message", out var message) &&
                message.ValueKind == JsonValueKind.String)
                return message.GetString() ?? "";
        }
        catch { /* not JSON */ }
        return body.Length > 200 ? body[..200] : body;
    }

    private static string Explain(HttpStatusCode status, string body, string model)
    {
        if (IsKeyProblem(body))
            return "Gemini rejected the API key. Check it in Google AI Studio and paste it again under \"Gemini key\".";

        return status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                $"Gemini says this key is not allowed to do that. {ErrorMessage(body)}",
            HttpStatusCode.NotFound =>
                $"Gemini does not know a model called \"{model}\". Pick another model under Advanced → Gemini models (press \"Test all models\"). {ErrorMessage(body)}",
            HttpStatusCode.TooManyRequests =>
                "Gemini says you have used up its rate limit or quota for now. Wait a little, or check your limits in Google AI Studio.",
            >= HttpStatusCode.InternalServerError =>
                "Gemini is having trouble right now. Try again in a minute.",
            _ => $"Gemini returned an error ({(int)status}): {ErrorMessage(body)}",
        };
    }

    private static string ExtractText(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (root.TryGetProperty("candidates", out var candidates) &&
            candidates.ValueKind == JsonValueKind.Array && candidates.GetArrayLength() > 0)
        {
            var text = new StringBuilder();
            if (candidates[0].TryGetProperty("content", out var content) &&
                content.TryGetProperty("parts", out var parts) &&
                parts.ValueKind == JsonValueKind.Array)
            {
                foreach (var part in parts.EnumerateArray())
                {
                    // Skip the model's "thinking" parts if they are included
                    if (part.TryGetProperty("thought", out var thought) && thought.ValueKind == JsonValueKind.True) continue;
                    if (part.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                        text.Append(t.GetString());
                }
            }
            if (text.Length > 0) return text.ToString();
        }

        if (root.TryGetProperty("promptFeedback", out var feedback) &&
            feedback.TryGetProperty("blockReason", out var reason))
            throw new InvalidOperationException($"Gemini refused to answer ({reason.GetString()}).");

        throw new InvalidOperationException("Gemini sent an empty answer. Try again.");
    }
}

/// <summary>The key itself was rejected, so testing more models would only fail the same way.</summary>
internal sealed class KeyRejectedException(string message) : InvalidOperationException(message);
