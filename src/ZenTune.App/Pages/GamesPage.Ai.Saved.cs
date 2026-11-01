using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ZenTune.Core.Ai;
using ZenTune.Core.Games;
using ZenTune.Core.Presets;

namespace ZenTune.App.Pages;

/// <summary>"My saved AI settings": the results the user gave a thumbs up, listed on the game card.</summary>
internal sealed partial class GamesPage
{
    // The list on each game's card, so it can be refreshed right after a thumbs up
    private readonly Dictionary<string, StackPanel> _aiSavedHosts = new();

    private void RenderSavedAi(StackPanel host, StackPanel info, DetectedGame game, Border holder)
    {
        host.Children.Clear();

        host.Children.Add(new TextBlock
        {
            Text = "\U0001F44D MY SAVED AI SETTINGS",
            FontSize = 11,
            CharacterSpacing = 60,
            FontWeight = FontWeights.SemiBold,
            Foreground = Ui.Res("TextFillColorSecondaryBrush"),
        });

        var saveNow = Ui.IconButton(Ui.IconSave, "Save current settings");
        ToolTipService.SetToolTip(saveNow, "Saves the settings the game has right now, so you can come back to them. Close the game first so it has written them.");
        saveNow.Click += async (_, _) => await SaveCurrentAiAsync(saveNow, host, info, game, holder);

        var import = new Button { Content = "Import" };
        ToolTipService.SetToolTip(import, "Adds settings a friend shared with you as a ZenTune code or file.");
        import.Click += async (_, _) => await ImportSavedAiAsync(host, info, game, holder);

        var topRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        topRow.Children.Add(saveNow);
        topRow.Children.Add(import);
        host.Children.Add(topRow);

        var list = AiSavedProfiles.ForGame(game.Id);
        if (list.Count == 0)
        {
            host.Children.Add(Ui.Secondary(
                "Nothing saved yet. Press \"Save current settings\" to keep what the game has now, or press \"\U0001F44D Save as good\" in an AI result window. " +
                "Saved settings show up here and you can put the exact same ones back with one click."));
            return;
        }

        foreach (var saved in list)
        {
            var grid = new Grid { ColumnSpacing = 12 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new StackPanel { Spacing = 1 };
            text.Children.Add(new TextBlock { Text = saved.Name, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            text.Children.Add(Ui.Secondary(
                $"{saved.Settings.Count} settings · {saved.Gpu} · {saved.Screen} · {(saved.Manual ? "saved by you" : saved.Model)} · saved {Ui.When(saved.Saved)}"));
            if (_profile is not null && !saved.FitsThisPc(_profile))
                text.Children.Add(Ui.Secondary("Made on a different graphics card or screen resolution than this PC has now."));
            grid.Children.Add(text);

            var use = Ui.IconButton(Ui.IconCheck, "Use");
            ToolTipService.SetToolTip(use, "Puts these exact settings back. Your current settings are backed up first.");
            use.Click += async (_, _) => await UseSavedAiAsync(use, info, game, holder, saved);

            var share = new Button { Content = "Share" };
            ToolTipService.SetToolTip(share, "Gives you a code or a file to send to a friend, who can add it with Import.");
            share.Click += async (_, _) => await ShareSavedAiAsync(saved);

            var remove = new Button { Content = "Remove" };
            ToolTipService.SetToolTip(remove, "Removes this entry from the list. The game's settings are not changed.");
            remove.Click += async (_, _) => await RemoveSavedAiAsync(host, info, game, holder, saved);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            buttons.Children.Add(use);
            buttons.Children.Add(share);
            buttons.Children.Add(remove);
            Grid.SetColumn(buttons, 1);
            grid.Children.Add(buttons);

            host.Children.Add(Ui.CardFrame(grid, new Thickness(16, 8, 16, 8)));
        }
    }

    private async Task UseSavedAiAsync(Button button, StackPanel info, DetectedGame game, Border holder, SavedAiProfile saved)
    {
        if (_presetBusy) return;
        _presetBusy = true;
        try
        {
            await RunAsync(button, info, game, holder, async () =>
            {
                var result = await Task.Run(() => AiSettingsAdvisor.ApplySaved(game, saved));

                int n = result.Plan.Effective.Count();
                string skipped = result.Rejected > 0
                    ? $" {result.Rejected} saved {(result.Rejected == 1 ? "setting was" : "settings were")} skipped because the game no longer has or allows {(result.Rejected == 1 ? "it" : "them")}."
                    : "";

                if (n == 0)
                    return ("Nothing to change", $"{game.Name} already has these settings (\"{saved.Name}\").{skipped}");

                return ("Saved settings applied",
                    $"Changed {n} settings in {game.Name} to \"{saved.Name}\". Start the game to try them. To go back, use Restore.{skipped}");
            });
        }
        finally
        {
            _presetBusy = false;
        }
    }

    private async Task RemoveSavedAiAsync(StackPanel host, StackPanel info, DetectedGame game, Border holder, SavedAiProfile saved)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Remove saved settings?",
            Content = new TextBlock
            {
                Text = $"\"{saved.Name}\" is removed from the list. {game.Name}'s own settings are not changed.",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            RequestedTheme = ElementTheme.Dark,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            AiSavedProfiles.Delete(saved.Id);
            RenderSavedAi(host, info, game, holder);
        }
        catch (Exception ex)
        {
            _status.Show(InfoBarSeverity.Error, "Could not remove it", ex.Message);
        }
    }

    /// <summary>"Save current settings": remembers what the game has now, with no AI involved.</summary>
    private async Task SaveCurrentAiAsync(Button button, StackPanel host, StackPanel info, DetectedGame game, Border holder)
    {
        button.IsEnabled = false;
        try
        {
            var hw = _profile ?? HardwareProfile.From(await AppState.HardwareAsync());
            var saved = await Task.Run(() => AiSettingsAdvisor.SaveCurrent(game, hw));
            RenderSavedAi(host, info, game, holder);
            _status.Show(InfoBarSeverity.Success, "Saved",
                $"\"{saved.Name}\" is in \"My saved AI settings\". Press Use there to put these exact settings back.");
        }
        catch (Exception ex)
        {
            button.IsEnabled = true;
            _status.Show(InfoBarSeverity.Error, "Could not save it", ex.Message);
        }
    }

    /// <summary>Thumbs up: remembers the exact settings that are in the game's files right now.</summary>
    private async Task SaveAiFavouriteAsync(StackPanel info, DetectedGame game, Border holder, AiOutcome outcome, AiMode mode)
    {
        try
        {
            var hw = _profile ?? HardwareProfile.From(await AppState.HardwareAsync());
            var saved = await Task.Run(() => AiSettingsAdvisor.Save(game, outcome, mode, hw));

            if (_aiSavedHosts.TryGetValue(game.Id, out var host))
                RenderSavedAi(host, info, game, holder);

            _status.Show(InfoBarSeverity.Success, "Saved \U0001F44D",
                $"\"{saved.Name}\" is in \"My saved AI settings\" on {game.Name}'s card. Press Use there to get exactly these settings back, without asking Gemini.");
        }
        catch (Exception ex)
        {
            _status.Show(InfoBarSeverity.Error, "Could not save it", ex.Message);
        }
    }
}
