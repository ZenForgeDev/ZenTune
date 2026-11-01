using System.Globalization;
using System.Text;
using System.Text.Json;
using ZenTune.Core.Measure;

namespace ZenTune.Core.Ai;

/// <summary>How the second recording compares with the first, according to Gemini.</summary>
/// <param name="Verdict">One of "better", "worse", "same", "mixed" (the second recording compared with the first).</param>
public sealed record MeasureAnalysis(string Verdict, string Explanation, IReadOnlyList<string> Tips);

/// <summary>
/// "Compare with AI" on the Measure page. Sends only the numbers of two recordings (FPS, frame times, stutters),
/// the game's program name and the two names the user gave the recordings. No files, no frame lists, nothing personal.
/// The differences are worked out here, not by the AI, and the answer is checked before it is shown.
/// </summary>
public static class MeasureAdvisor
{
    private static readonly string[] Verdicts = ["better", "worse", "same", "mixed"];

    public static async Task<MeasureAnalysis> CompareAsync(CaptureRecord first, CaptureRecord second,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var ai = AiConfig.Load();
        if (string.IsNullOrWhiteSpace(ai.ApiKey))
            throw new InvalidOperationException("Add your Gemini key first.");

        var prompt = BuildPrompt(first, second);
        var json = await GeminiClient.GenerateJsonAsync(ai.ApiKey!, ai.Model, prompt, ct, progress);
        return Parse(json);
    }

    internal static string BuildPrompt(CaptureRecord first, CaptureRecord second)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You compare two FPS recordings of a game on a Windows PC. They were made with PresentMon, one after the other.");
        sb.AppendLine("Recording 1 is the starting point. Recording 2 is the later one. Say how recording 2 compares with recording 1.");
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("- Use only the numbers below. You do not know what the user changed between the recordings, so never state what caused a difference. " +
                      "The names are the user's own words and may hint at it.");
        sb.AppendLine("- Higher FPS is better. Lower frame times and fewer stutters are better.");
        sb.AppendLine("- A change under 3% is normal run-to-run noise: call it about the same.");
        sb.AppendLine("- 1% low close to the average FPS means smooth. A large gap, or many stutters per minute, means uneven.");
        sb.AppendLine("- If the programs differ or the lengths differ by more than 20 seconds, say the comparison is less reliable.");
        sb.AppendLine("- Some recordings also say how busy the processor and graphics card were while recording. Use that as a hint about what limits the FPS: " +
                      "a graphics card near 100% means it is the limit; a processor with its busiest thread near 100% while the graphics card is not busy means the processor is the limit. " +
                      "It is a hint, never a certainty, and it does not tell what changed between the recordings.");
        sb.AppendLine("- Plain English for a gamer. Short sentences. No invented facts.");
        sb.AppendLine();
        sb.AppendLine("Reply with only this JSON: {\"verdict\": \"better\" | \"worse\" | \"same\" | \"mixed\", " +
                      "\"explanation\": \"2 to 4 sentences\", \"tips\": [\"0 to 3 short next steps\"]}");
        sb.AppendLine("\"mixed\" means some numbers got better and others worse.");
        sb.AppendLine();

        Describe(sb, "Recording 1", first);
        Describe(sb, "Recording 2", second);

        sb.AppendLine("Change from recording 1 to recording 2 (computed by ZenTune):");
        Change(sb, "Average FPS", first.Stats.AvgFps, second.Stats.AvgFps);
        Change(sb, "1% low FPS", first.Stats.Low1Fps, second.Stats.Low1Fps);
        Change(sb, "0.1% low FPS", first.Stats.Low01Fps, second.Stats.Low01Fps);
        Change(sb, "Median frame time", first.Stats.MedianMs, second.Stats.MedianMs);
        Change(sb, "99th percentile frame time", first.Stats.P99Ms, second.Stats.P99Ms);
        Change(sb, "Longest frame", first.Stats.MaxMs, second.Stats.MaxMs);
        Change(sb, "Stutters per minute", first.Stats.StuttersPerMinute, second.Stats.StuttersPerMinute);
        return sb.ToString();
    }

    private static void Describe(StringBuilder sb, string title, CaptureRecord r)
    {
        var s = r.Stats;
        sb.AppendLine($"{title}: name \"{Clean(r.Label, 40)}\", program {Clean(r.Process, 40)}, length {N(s.Seconds, "0")} s, {s.Frames} frames");
        sb.AppendLine($"  average {N(s.AvgFps, "0.0")} FPS, 1% low {N(s.Low1Fps, "0.0")} FPS, 0.1% low {N(s.Low01Fps, "0.0")} FPS");
        sb.AppendLine($"  median frame {N(s.MedianMs, "0.00")} ms, 99th percentile {N(s.P99Ms, "0.00")} ms, longest {N(s.MaxMs, "0.0")} ms");
        sb.AppendLine($"  stutters {s.Stutters} ({N(s.StuttersPerMinute, "0.0")} per minute; a stutter is a frame longer than twice the median)");
        if (r.Pulse is { } p)
        {
            var parts = new List<string>
            {
                $"processor {N(p.CpuAverage, "0")}% on average (highest {N(p.CpuHighest, "0")}%), busiest thread {N(p.BusiestThreadAverage, "0")}% on average",
            };
            if (p.GpuAverage is double g) parts.Add($"graphics card {N(g, "0")}% on average (highest {N(p.GpuHighest ?? g, "0")}%)");
            if (p.GpuTemperatureHighest is double gt) parts.Add($"graphics card temperature highest {N(gt, "0")} C");
            if (p.CpuTemperatureHighest is double ct) parts.Add($"processor temperature highest {N(ct, "0")} C");
            parts.Add($"memory highest {N(p.MemoryHighestPercent, "0")}%");
            if (p.VideoMemoryHighestPercent is double vm) parts.Add($"video memory highest {N(vm, "0")}%");
            sb.AppendLine("  while recording: " + string.Join(", ", parts));
        }
        sb.AppendLine();
    }

    private static void Change(StringBuilder sb, string name, double a, double b)
    {
        if (a <= 0)
        {
            sb.AppendLine($"  {name}: {N(a, "0.0")} -> {N(b, "0.0")}");
            return;
        }
        double pct = (b - a) / a * 100.0;
        sb.AppendLine($"  {name}: {N(a, "0.0#")} -> {N(b, "0.0#")} ({pct.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture)}%)");
    }

    private static string N(double v, string format) => v.ToString(format, CultureInfo.InvariantCulture);

    /// <summary>User text goes into the prompt, so keep it short and on one line.</summary>
    private static string Clean(string text, int max)
    {
        var sb = new StringBuilder();
        foreach (char c in text ?? "")
        {
            if (sb.Length >= max) break;
            sb.Append(char.IsControl(c) || c == '"' ? ' ' : c);
        }
        return sb.ToString().Trim();
    }

    /// <summary>Reads Gemini's answer. Anything that is not the expected shape is rejected instead of shown.</summary>
    internal static MeasureAnalysis Parse(string text)
    {
        int start = text.IndexOf('{');
        int end = text.LastIndexOf('}');
        if (start < 0 || end <= start) throw BadAnswer();

        try
        {
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw BadAnswer();

            string verdict = root.TryGetProperty("verdict", out var v) && v.ValueKind == JsonValueKind.String
                ? (v.GetString() ?? "").Trim().ToLowerInvariant()
                : "";
            if (!Verdicts.Contains(verdict)) throw BadAnswer();

            string explanation = root.TryGetProperty("explanation", out var e) && e.ValueKind == JsonValueKind.String
                ? (e.GetString() ?? "").Trim()
                : "";
            if (explanation.Length == 0) throw BadAnswer();
            if (explanation.Length > 900) explanation = explanation[..900].TrimEnd() + "…";

            var tips = new List<string>();
            if (root.TryGetProperty("tips", out var t) && t.ValueKind == JsonValueKind.Array)
            {
                foreach (var tip in t.EnumerateArray())
                {
                    if (tip.ValueKind != JsonValueKind.String) continue;
                    var s = (tip.GetString() ?? "").Trim();
                    if (s.Length == 0) continue;
                    tips.Add(s.Length > 220 ? s[..220].TrimEnd() + "…" : s);
                    if (tips.Count == 3) break;
                }
            }

            return new MeasureAnalysis(verdict, explanation, tips);
        }
        catch (JsonException)
        {
            throw BadAnswer();
        }
    }

    private static InvalidOperationException BadAnswer() =>
        new("Gemini's answer was not in the format ZenTune expects. Press the button again.");
}
