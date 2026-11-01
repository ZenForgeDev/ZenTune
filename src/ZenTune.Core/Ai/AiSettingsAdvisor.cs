using System.Globalization;
using System.Text;
using System.Text.Json;
using ZenTune.Core.Games;
using ZenTune.Core.Presets;

namespace ZenTune.Core.Ai;

/// <summary>One suggestion from Gemini, before ZenTune has checked it.</summary>
public sealed record AiProposal(string Key, string Value, string Why);

/// <summary>What happened when the AI settings button was pressed.</summary>
public sealed record AiOutcome(PresetPlan Plan, BackupInfo? Backup, int Rejected, string Model);

/// <summary>
/// "Try to get the best settings": asks Gemini for settings that fit this PC and the chosen mode, checks every
/// suggestion, backs up the current settings and writes the ones that passed.
/// </summary>
public static partial class AiSettingsAdvisor
{
    private const string FirstBackupLabel = "Original settings (before first AI run)";

    /// <summary>Only Call of Duty so far: its settings files list what each setting accepts, which is what makes this safe.</summary>
    public static bool Supports(DetectedGame game)
    {
        try { return new CallOfDutyTuner().Supports(game); }
        catch { return false; }
    }

    public static async Task<AiOutcome> RunAsync(DetectedGame game, AiMode mode, HardwareProfile hw, CancellationToken ct = default,
        IProgress<string>? progress = null)
    {
        var tuner = new CallOfDutyTuner();
        if (!tuner.Supports(game))
            throw new InvalidOperationException("AI settings only work for Call of Duty so far. Start the game once so it creates its settings file.");

        EnsureClosed(game);

        var config = AiConfig.Load();
        var apiKey = config.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Add your Gemini API key first (the \"Gemini key\" button).");

        var settings = tuner.DescribeForAi(game);
        if (settings.Count == 0)
            throw new InvalidOperationException("Found no settings the AI is allowed to change in this game's files.");

        var prompt = BuildPrompt(game, mode, hw, settings);
        // An answer that is not the JSON we asked for is asked for again (up to three tries in all)
        string summary;
        List<AiProposal> proposals;
        for (int attempt = 1; ; attempt++)
        {
            var reply = await GeminiClient.GenerateJsonAsync(apiKey, config.Model, prompt, ct, progress);
            try
            {
                (summary, proposals) = ParseReply(reply);
                break;
            }
            catch (InvalidOperationException) when (attempt < 3)
            {
                progress?.Report($"Gemini's answer was unclear, asking again ({attempt} of 2)...");
            }
        }

        var plan = tuner.PlanFromAi(game, mode, summary, proposals, out int rejected);

        BackupInfo? backup = null;
        if (plan.Effective.Any())
        {
            // The game may have been started while Gemini was thinking
            EnsureClosed(game);
            backup = MakeBackup(game, mode);
            tuner.Write(plan);
        }

        return new AiOutcome(plan, backup, rejected, config.Model);
    }

    private static void EnsureClosed(DetectedGame game)
    {
        if (GameScanner.IsRunning(game))
            throw new InvalidOperationException($"Close {game.Name} first. Games write their settings back when they quit.");
    }

    /// <summary>
    /// The very first AI run keeps a permanent backup of your own settings. Later runs make normal automatic backups
    /// (the newest 15 are kept), so pressing the button twice never loses your original.
    /// </summary>
    private static BackupInfo MakeBackup(DetectedGame game, AiMode mode)
    {
        bool first = !Backups.List(game.Id).Any(b => b.Label == FirstBackupLabel);
        return first
            ? Backups.Create(game, FirstBackupLabel, automatic: false)
            : Backups.Create(game, $"Before AI · {AiModeInfo.Title(mode)}", automatic: true);
    }

    // ---------------------------------------------------------------- Prompt

    private static string BuildPrompt(DetectedGame game, AiMode mode, HardwareProfile hw, IReadOnlyList<CallOfDutyTuner.AiSetting> settings)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();

        sb.AppendLine($"You are tuning the graphics settings of {game.Name} on one specific Windows PC.");
        sb.AppendLine("The game is recent and you may not know it well. Use the setting names, the values each one allows, and the PC below to make careful choices.");
        sb.AppendLine();
        sb.AppendLine("GOAL");
        sb.AppendLine(AiModeInfo.Goal(mode, hw.RefreshHz));
        sb.AppendLine();
        sb.AppendLine("THE PC");
        sb.AppendLine($"- Graphics card: {hw.GpuName} ({hw.Vendor}), {hw.VramGb.ToString("0.#", inv)} GB video memory, class: {hw.Tier}");
        sb.AppendLine($"- Screen: {hw.ScreenWidth}x{hw.ScreenHeight} at {hw.RefreshHz} Hz");
        sb.AppendLine($"- Best upscaler for this card: {hw.Upscaler}");
        sb.AppendLine($"- Frame generation supported: {(hw.FrameGeneration ? "yes" : "no")}");
        sb.AppendLine($"- Handheld PC: {(hw.IsHandheld ? "yes" : "no")}");
        sb.AppendLine();
        sb.AppendLine("RULES");
        sb.AppendLine("- Only change settings from the list below, and use each key exactly as written.");
        sb.AppendLine("- The new value must be one the list allows. For \"one of ...\" lists, copy the value character for character. For \"N to M\", use a number in that range. For \"true or false\", use true or false.");
        sb.AppendLine("- Only change settings that help the goal. Leave everything else alone.");
        sb.AppendLine("- If you are not sure what a setting does, leave it alone.");
        sb.AppendLine("- Quality presets that bundle other settings are not on the list, and changing one setting never changes another. If a goal needs a lower shadow, reflection, lighting or effects setting, set that setting itself.");
        sb.AppendLine("- Give each change a reason of one short plain-English sentence, written for a gamer, without jargon.");
        sb.AppendLine("- Answer with JSON only. No markdown, no text around it.");
        sb.AppendLine();
        sb.AppendLine("JSON SHAPE");
        sb.AppendLine("{\"summary\": \"one or two sentences about the approach\", \"changes\": [{\"key\": \"SettingKey\", \"value\": \"NewValue\", \"why\": \"short reason\"}]}");
        sb.AppendLine();
        sb.AppendLine("SETTINGS (key | current value | allowed values)");

        foreach (var s in settings.Take(400))
            sb.AppendLine($"{s.Key} | {s.Current} | {s.Allowed}");

        return sb.ToString();
    }

    // ---------------------------------------------------------------- Reply

    private static (string Summary, List<AiProposal> Changes) ParseReply(string text)
    {
        int start = text.IndexOf('{');
        int end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
            throw new InvalidOperationException("Gemini's answer was not in the format ZenTune expected. Try again.");

        try
        {
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            var root = doc.RootElement;

            string summary = root.TryGetProperty("summary", out var s) ? AsText(s) : "";
            var changes = new List<AiProposal>();

            if (root.TryGetProperty("changes", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in list.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    if (!item.TryGetProperty("key", out var key) || !item.TryGetProperty("value", out var value)) continue;
                    string why = item.TryGetProperty("why", out var w) ? AsText(w) : "";
                    changes.Add(new AiProposal(AsText(key), AsText(value), why));
                }
            }

            return (summary.Trim().Length > 0 ? summary.Trim() : "Settings suggested by Gemini.", changes);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Gemini's answer was not in the format ZenTune expected. Try again.");
        }
    }

    private static string AsText(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString() ?? "",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => e.GetRawText(),
        _ => "",
    };
}
