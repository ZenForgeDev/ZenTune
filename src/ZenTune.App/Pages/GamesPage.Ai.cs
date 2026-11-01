using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ZenTune.Core.Ai;
using ZenTune.Core.Games;
using ZenTune.Core.Presets;

namespace ZenTune.App.Pages;

/// <summary>The experimental "AI best settings" section of a game card.</summary>
internal sealed partial class GamesPage
{
    private AiMode _aiMode = AiMode.Balanced;

    private void AddAiSection(StackPanel info, DetectedGame game, Border holder)
    {
        if (!AiSettingsAdvisor.Supports(game)) return;

        info.Children.Add(new TextBlock
        {
            Text = "EXPERIMENTAL · AI BEST SETTINGS",
            FontSize = 11,
            CharacterSpacing = 60,
            FontWeight = FontWeights.SemiBold,
            Foreground = Ui.Res("TextFillColorSecondaryBrush"),
            Margin = new Thickness(0, 8, 0, 0),
        });

        var mode = new ComboBox { MinWidth = 170 };
        foreach (var m in AiModeInfo.Picker) mode.Items.Add(AiModeInfo.Title(m));
        mode.SelectedIndex = Math.Max(0, Array.IndexOf(AiModeInfo.Picker, _aiMode));
        mode.SelectionChanged += (_, _) =>
        {
            if (mode.SelectedIndex >= 0) _aiMode = AiModeInfo.Picker[mode.SelectedIndex];
        };
        ToolTipService.SetToolTip(mode, "What the AI should aim for: Ultra Performance, Performance, Balanced or Quality.");

        var best = Ui.IconButton(Ui.IconGame, "Try to get the best settings", accent: true);
        ToolTipService.SetToolTip(best, "Asks Gemini for settings that fit your PC and the mode on the left. Your current settings are backed up first.");
        best.Click += async (_, _) => await RunAiAsync(best, info, game, holder, _aiMode);

        var fps = Ui.IconButton(Ui.IconRefresh, "Get me the best FPS");
        ToolTipService.SetToolTip(fps, "Asks Gemini for the highest possible frame rate and lowest input lag, whatever it costs in picture quality.");
        fps.Click += async (_, _) => await RunAiAsync(fps, info, game, holder, AiMode.BestFps);

        var key = Ui.IconButton(Ui.IconInfo, "Gemini key");
        ToolTipService.SetToolTip(key, "Your own Google Gemini API key and the model to use.");
        key.Click += async (_, _) => await AskForGeminiKeyAsync();

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(mode);
        row.Children.Add(best);
        row.Children.Add(fps);
        row.Children.Add(key);
        info.Children.Add(row);

        info.Children.Add(Ui.Secondary(
            "Asks Google Gemini to pick settings and applies them right away. ZenTune backs up your settings first, only writes values the game itself allows, " +
            "and never touches screen, driver, sound or control settings. Gemini can be wrong and may not know a new game well: use Restore to go back."));

        // Settings the user gave a thumbs up, kept in their own list so they are easy to find again
        var saved = new StackPanel { Spacing = 6, Margin = new Thickness(0, 8, 0, 0) };
        info.Children.Add(saved);
        _aiSavedHosts[game.Id] = saved;
        RenderSavedAi(saved, info, game, holder);
    }

    private async Task RunAiAsync(Button button, StackPanel info, DetectedGame game, Border holder, AiMode mode)
    {
        // One thing at a time, the same guard the presets use
        if (_presetBusy) return;
        _presetBusy = true;
        try
        {
            if (string.IsNullOrWhiteSpace(AiConfig.Load().ApiKey) && !await AskForGeminiKeyAsync())
                return;

            AiOutcome? outcome = null;
            await RunAsync(button, info, game, holder, async () =>
            {
                _status.Show(InfoBarSeverity.Informational, "Asking Gemini…",
                    $"Looking for {AiModeInfo.Title(mode)} settings for your PC. This takes a few seconds.");

                var profile = _profile ?? HardwareProfile.From(await AppState.HardwareAsync());
                // Created here on the screen's thread, so its messages come back to the screen ("Gemini is busy, trying again...")
                var progress = new Progress<string>(message =>
                    _status.Show(InfoBarSeverity.Informational, "Asking Gemini…", message));
                var result = await Task.Run(() => AiSettingsAdvisor.RunAsync(game, mode, profile, default, progress));
                outcome = result;

                int n = result.Plan.Effective.Count();
                if (n == 0)
                {
                    return ("Nothing to change",
                        result.Rejected > 0
                            ? $"Gemini's {result.Rejected} suggestions were not allowed by the game, so nothing was changed. Try again."
                            : "Gemini did not find anything it wanted to change, or your settings already match. Nothing was changed.");
                }

                return ("AI settings applied",
                    $"Changed {n} settings in {game.Name} ({AiModeInfo.Title(mode)}). Start the game to try them. " +
                    "To go back, use Restore.");
            });

            var done = outcome;
            if (done is null || done.Backup is null || !done.Plan.Effective.Any()) return;

            await ShowAiResultAsync(button, info, game, holder, done, mode);
        }
        finally
        {
            _presetBusy = false;
        }
    }

    /// <summary>Shows what Gemini changed and why, with a one-click way back.</summary>
    private async Task ShowAiResultAsync(Button button, StackPanel info, DetectedGame game, Border holder, AiOutcome outcome, AiMode mode)
    {
        var changes = outcome.Plan.Effective.ToList();

        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(new TextBlock { Text = outcome.Plan.Summary, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new TextBlock
        {
            Text = $"{changes.Count} {(changes.Count == 1 ? "setting was" : "settings were")} changed. Your old settings were backed up first.",
            Style = Ui.StyleRes("BodyStrongTextBlockStyle"),
            TextWrapping = TextWrapping.Wrap,
        });

        foreach (var c in changes)
        {
            var item = new StackPanel { Spacing = 1 };
            item.Children.Add(new TextBlock
            {
                Text = $"{c.Label}:  {c.OldValue ?? "(not set)"}  →  {c.NewValue}",
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            });
            if (c.Why.Length > 0) item.Children.Add(Ui.Secondary(c.Why));
            body.Children.Add(item);
        }

        if (outcome.Rejected > 0)
        {
            body.Children.Add(Ui.Secondary(
                $"{outcome.Rejected} other {(outcome.Rejected == 1 ? "suggestion was" : "suggestions were")} skipped because the game does not allow them."));
        }
        body.Children.Add(Ui.Secondary($"Suggested by {outcome.Model}. AI can be wrong, so trust what you see in the game."));
        body.Children.Add(Ui.Secondary("Happy with them in the game? Press \"\U0001F44D Save as good\" and ZenTune keeps these exact settings in \"My saved AI settings\" on this game's card, so you can get them back without asking Gemini again."));

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"{AiModeInfo.Title(mode)} · {game.Name}",
            Content = new ScrollViewer { Content = body, MaxHeight = 460, Padding = new Thickness(0, 0, 16, 0) },
            PrimaryButtonText = "\U0001F44D Save as good",
            SecondaryButtonText = "Undo changes",
            CloseButtonText = "Keep, don't save",
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = ElementTheme.Dark,
        };

        var answer = await dialog.ShowAsync();
        if (answer == ContentDialogResult.Primary)
        {
            await SaveAiFavouriteAsync(info, game, holder, outcome, mode);
            return;
        }
        if (answer != ContentDialogResult.Secondary) return;

        var backup = outcome.Backup!;
        await RunAsync(button, info, game, holder, async () =>
        {
            await Task.Run(() => Backups.Restore(game, backup));
            return ("Settings restored", $"{game.Name} is back to the settings it had before the AI run.");
        });
    }

    /// <summary>Asks for the user's own Gemini API key and model. Returns true if a key is saved.</summary>
    private Task<bool> AskForGeminiKeyAsync() => GeminiDialogs.AskForKeyAsync(XamlRoot);
}
