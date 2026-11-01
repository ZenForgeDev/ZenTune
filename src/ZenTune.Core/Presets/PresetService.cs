using ZenTune.Core.Games;

namespace ZenTune.Core.Presets;

/// <summary>Finds the right tuner for a game and applies presets safely.</summary>
public static class PresetService
{
    private static readonly IGameTuner[] Tuners =
    [
        new UnrealTuner(), new CallOfDutyTuner(), new WarframeTuner(), new GtaVEnhancedTuner(), new WarThunderTuner(), new DeltaForceGuide(), new Cs2Tuner(),
    ];

    public static IGameTuner? For(DetectedGame game)
    {
        foreach (var t in Tuners)
        {
            try { if (t.Supports(game)) return t; }
            catch { /* unreadable file: treat as unsupported */ }
        }
        return null;
    }

    /// <summary>
    /// Writes a preset. The game must be closed. The current settings are backed up first,
    /// so Restore on the game card always brings them back.
    /// </summary>
    public static int Apply(PresetPlan plan)
    {
        if (plan.GuideOnly) return 0; // guide-only games are never written to

        var tuner = For(plan.Game) ?? throw new InvalidOperationException($"ZenTune has no presets for {plan.Game.Name} yet.");

        if (GameScanner.IsRunning(plan.Game))
            throw new InvalidOperationException($"Close {plan.Game.Name} first. Games write their settings back when they quit.");

        int count = plan.Effective.Count();
        if (count == 0) return 0;

        Backups.Create(plan.Game, $"Before {PresetInfo.Title(plan.Kind)} preset", automatic: true);
        tuner.Write(plan);
        return count;
    }
}
