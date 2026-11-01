using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using ZenTune.Core.Ai;
using ZenTune.Core.Games;

namespace ZenTune.App.Pages;

/// <summary>Share and import for "My saved AI settings".</summary>
internal sealed partial class GamesPage
{
    private async Task ShareSavedAiAsync(SavedAiProfile saved)
    {
        string code;
        try { code = AiShare.Export(saved); }
        catch (Exception ex)
        {
            _status.Show(InfoBarSeverity.Error, "Could not share it", ex.Message);
            return;
        }

        var box = new TextBox
        {
            Text = code,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = false,
            MaxHeight = 120,
            MinWidth = 440,
        };
        ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);

        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(new TextBlock
        {
            Text = $"Send this code to your friend. They open {saved.GameName} in ZenTune and press Import. " +
                   "The code is long, so a chat message may cut it off: if that happens, use \"Save as file\" and send the file instead.",
            TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(box);
        body.Children.Add(Ui.Secondary(
            "The code only holds game settings, no names or files. Your friend's ZenTune checks every value against the game's own rules before it uses them."));

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Share · {saved.Name}",
            Content = body,
            PrimaryButtonText = "Copy code",
            SecondaryButtonText = "Save as file",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = ElementTheme.Dark,
        };

        var answer = await dialog.ShowAsync();
        try
        {
            if (answer == ContentDialogResult.Primary)
            {
                var package = new DataPackage();
                package.SetText(code);
                Clipboard.SetContent(package);
                _status.Show(InfoBarSeverity.Success, "Code copied", "Paste it in a message to your friend.");
            }
            else if (answer == ContentDialogResult.Secondary)
            {
                var path = await Task.Run(() => AiShare.SaveToFile(saved));
                _status.Show(InfoBarSeverity.Success, "File saved on your desktop", $"{Path.GetFileName(path)} is ready to send to your friend.");
            }
        }
        catch (Exception ex)
        {
            _status.Show(InfoBarSeverity.Error, "Could not share it", ex.Message);
        }
    }

    private async Task ImportSavedAiAsync(StackPanel host, StackPanel info, DetectedGame game, Border holder)
    {
        var box = new TextBox
        {
            PlaceholderText = "Paste the code here, or the path to a .zentune file",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 130,
            MinWidth = 440,
        };
        var problem = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = Ui.Res("SystemFillColorCriticalBrush"),
            Visibility = Visibility.Collapsed,
        };

        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(new TextBlock
        {
            Text = $"Paste a ZenTune code from a friend. It is added to your list for {game.Name}. Nothing is changed in the game until you press Use.",
            TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(box);
        body.Children.Add(problem);

        while (true)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Import settings",
                Content = body,
                PrimaryButtonText = "Import",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                RequestedTheme = ElementTheme.Dark,
            };
            var answer = await dialog.ShowAsync();
            dialog.Content = null; // so the same body can go into the next dialog if the code was not valid
            if (answer != ContentDialogResult.Primary) return;

            try
            {
                var imported = AiShare.Import(box.Text, game.Id, game.Name);
                AiSavedProfiles.Add(imported);
                RenderSavedAi(host, info, game, holder);
                _status.Show(InfoBarSeverity.Success, "Imported",
                    $"\"{imported.Name}\" is in \"My saved AI settings\". Press Use to put these settings in the game.");
                return;
            }
            catch (Exception ex)
            {
                problem.Text = ex.Message;
                problem.Visibility = Visibility.Visible;
            }
        }
    }
}
