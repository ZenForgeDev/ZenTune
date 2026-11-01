using System.Globalization;
using System.Text.RegularExpressions;
using ZenTune.Core.Ai;
using ZenTune.Core.Games;

namespace ZenTune.Core.Presets;

/// <summary>
/// The AI side of the Call of Duty tuner. It reuses the tuner's file reader and its "allowed values" check,
/// so Gemini can only ever suggest values the game itself offers.
/// </summary>
public sealed partial class CallOfDutyTuner
{
    /// <summary>One setting Gemini is allowed to look at: its name, the value now, and what the game accepts.</summary>
    public sealed record AiSetting(string Key, string Current, string Allowed);

    private static Dictionary<string, Line> MergedLines(DetectedGame game)
    {
        // Newest file first; the same setting can exist in several files
        var lines = new Dictionary<string, Line>(StringComparer.Ordinal);
        foreach (var file in Files(game))
            foreach (var (key, line) in Parse(File.ReadAllText(file)))
                lines.TryAdd(key, line);
        return lines;
    }

    /// <summary>The settings Gemini may change. Settings on the never-touch list are left out.</summary>
    public IReadOnlyList<AiSetting> DescribeForAi(DetectedGame game)
    {
        var result = new List<AiSetting>();
        foreach (var (key, line) in MergedLines(game))
        {
            if (AiGuard.IsForbidden(key)) continue;

            // GraphicsQuality is only the label of the in-game preset. Writing it does not change the single settings, but the AI
            // treated it as if it did (Warzone kept shadows on Very_High after "Minimum"), so the AI is not shown it.
            if (key == "GraphicsQuality") continue;

            string? allowed;
            if (line.Comment.StartsWith("one of ", StringComparison.Ordinal) ||
                Regex.IsMatch(line.Comment, @"^-?[\d.]+ to -?[\d.]+$"))
                allowed = line.Comment;
            else if (line.Value is "true" or "false")
                allowed = "true or false";
            else if (Regex.IsMatch(key, "fps", RegexOptions.IgnoreCase) &&
                     double.TryParse(line.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                allowed = "a whole number of frames per second, 30 to 1000";
            else
                continue; // no list of allowed values, so there is nothing safe to offer

            result.Add(new AiSetting(key, line.Value, allowed));
        }
        return result;
    }

    /// <summary>
    /// Turns Gemini's suggestions into a plan. Every suggestion is checked: the setting must exist, must not be on the
    /// never-touch list, and the value must be one the game allows. Anything else is dropped and counted in <paramref name="rejected"/>.
    /// </summary>
    public PresetPlan PlanFromAi(DetectedGame game, AiMode mode, string summary, IEnumerable<AiProposal> proposals, out int rejected)
    {
        var files = Files(game).ToList();
        if (files.Count == 0) throw new InvalidOperationException("Start the game once so it creates its settings file.");

        var lines = MergedLines(game);
        var plan = new PresetPlan { Game = game, Kind = mode.ToPresetKind(), Summary = summary };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        rejected = 0;

        foreach (var p in proposals)
        {
            var key = p.Key.Trim();
            var value = p.Value.Trim();

            if (key.Length == 0 || !seen.Add(key) ||
                AiGuard.IsForbidden(key) ||
                !lines.TryGetValue(key, out var line))
            {
                rejected++;
                continue;
            }

            var allowed = Allowed(line.Comment, value, line.Value);
            if (allowed is null || !AiGuard.SameShape(line.Value, allowed) || !AiGuard.SaneNumber(key, allowed))
            {
                rejected++;
                continue;
            }

            var label = Regex.Replace(key, "(?<=[a-z0-9])(?=[A-Z])", " ");
            plan.Changes.Add(new PlannedChange(files[0], key, label, line.Value, allowed, p.Why.Trim()));
        }

        return plan;
    }
}
