using ZenTune.Core.Games;
using ZenTune.Core.Presets;

namespace ZenTune.Core.Ai;

/// <summary>Thumbs up: save the exact settings that worked, and put them back later without asking Gemini.</summary>
public static partial class AiSettingsAdvisor
{
    /// <summary>
    /// Saves the game's settings as they are right now (just after an AI run) as a named profile.
    /// Every setting the AI may touch is stored, so using the profile later gives exactly this result.
    /// </summary>
    public static SavedAiProfile Save(DetectedGame game, AiOutcome outcome, AiMode mode, HardwareProfile hw)
    {
        var tuner = new CallOfDutyTuner();
        var reasons = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in outcome.Plan.Changes) reasons[c.Setting] = c.Why;

        var settings = tuner.DescribeForAi(game)
            .Select(s => new SavedAiSetting(s.Key, s.Current, reasons.GetValueOrDefault(s.Key, "")))
            .ToList();
        if (settings.Count == 0)
            throw new InvalidOperationException("Found no settings to save.");

        var now = DateTime.Now;
        var profile = new SavedAiProfile(
            Id: Guid.NewGuid().ToString("N"),
            GameId: game.Id,
            GameName: game.Name,
            Mode: mode,
            Name: $"{AiModeInfo.Title(mode)} · {now:d MMM yyyy HH:mm}",
            Gpu: hw.GpuName,
            Screen: $"{hw.ScreenWidth}x{hw.ScreenHeight} @ {hw.RefreshHz} Hz",
            Model: outcome.Model,
            Summary: outcome.Plan.Summary,
            Saved: now,
            Settings: settings);

        AiSavedProfiles.Add(profile);
        return profile;
    }

    /// <summary>Saves the settings the game has right now, without any AI run (the "Save current settings" button).</summary>
    public static SavedAiProfile SaveCurrent(DetectedGame game, HardwareProfile hw)
    {
        var tuner = new CallOfDutyTuner();
        if (!tuner.Supports(game))
            throw new InvalidOperationException("Start the game once so it creates its settings file.");
        EnsureClosed(game);

        var settings = tuner.DescribeForAi(game)
            .Select(s => new SavedAiSetting(s.Key, s.Current, ""))
            .ToList();
        if (settings.Count == 0)
            throw new InvalidOperationException("Found no settings to save.");

        var now = DateTime.Now;
        var profile = new SavedAiProfile(
            Id: Guid.NewGuid().ToString("N"),
            GameId: game.Id,
            GameName: game.Name,
            Mode: AiMode.Balanced,
            Name: $"My settings · {now:d MMM yyyy HH:mm}",
            Gpu: hw.GpuName,
            Screen: $"{hw.ScreenWidth}x{hw.ScreenHeight} @ {hw.RefreshHz} Hz",
            Model: "",
            Summary: "Settings saved by you.",
            Saved: now,
            Settings: settings,
            Manual: true);

        AiSavedProfiles.Add(profile);
        return profile;
    }

    /// <summary>
    /// Puts a saved profile back. Every value is checked again against the game's current settings files, because a game
    /// update can rename or remove settings. Backs up first, like a normal AI run.
    /// </summary>
    public static AiOutcome ApplySaved(DetectedGame game, SavedAiProfile saved)
    {
        var tuner = new CallOfDutyTuner();
        if (!tuner.Supports(game))
            throw new InvalidOperationException("AI settings only work for Call of Duty so far. Start the game once so it creates its settings file.");

        EnsureClosed(game);

        var proposals = saved.Settings.Select(s => new AiProposal(s.Key, s.Value, s.Why)).ToList();
        var plan = tuner.PlanFromAi(game, saved.Mode, saved.Summary, proposals, out int rejected);

        BackupInfo? backup = null;
        if (plan.Effective.Any())
        {
            EnsureClosed(game);
            backup = saved.Manual
                ? Backups.Create(game, $"Before saved settings · {saved.Name}", automatic: true)
                : MakeBackup(game, saved.Mode);
            tuner.Write(plan);
        }

        return new AiOutcome(plan, backup, rejected, saved.Model);
    }
}
