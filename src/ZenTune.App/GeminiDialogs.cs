using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ZenTune.Core.Ai;

namespace ZenTune.App;

/// <summary>Dialogs for the Gemini key, shared by the Games and Advanced pages.</summary>
internal static class GeminiDialogs
{
    /// <summary>Asks for the user's own Gemini API key and lets them pick a model. Returns true if a key is saved.</summary>
    public static async Task<bool> AskForKeyAsync(XamlRoot root)
    {
        var current = AiConfig.Load();

        var key = new PasswordBox { Header = "Gemini API key", PlaceholderText = "Paste your key here", Password = current.ApiKey ?? "" };

        var choices = GeminiModels.Choices(current.Model);
        var model = new ComboBox { Header = "Model", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var c in choices) model.Items.Add(new ComboBoxItem { Content = c.Label, Tag = c.Id });
        model.SelectedIndex = Math.Max(0, choices.FindIndex(c => c.Id == current.Model));

        var body = new StackPanel { Spacing = 12, MinWidth = 440 };
        body.Children.Add(new TextBlock
        {
            Text = "The AI settings feature uses your own Google Gemini key, so ZenTune stays free and nobody shares a key. " +
                   "Google's free keys have usage limits, which you can see in Google AI Studio.",
            TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(new HyperlinkButton
        {
            Content = "Get a key in Google AI Studio",
            NavigateUri = new Uri("https://aistudio.google.com/apikey"),
            Padding = new Thickness(0),
        });
        body.Children.Add(key);
        body.Children.Add(model);
        body.Children.Add(Ui.Secondary(
            "Not sure which model to use? Save your key, then open Advanced → Gemini models and press \"Test all models\". " +
            "ZenTune tries every model your key can see and shows which ones work, and which is fastest. ★ marks the fastest one that worked."));
        body.Children.Add(Ui.Secondary(
            "What is sent to Google: your graphics card, screen, and the names and values of the game's graphics settings. " +
            "No files and nothing personal. The key is stored encrypted for your Windows account only."));

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Gemini key",
            Content = body,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = ElementTheme.Dark,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return false;

        string id = (model.SelectedItem as ComboBoxItem)?.Tag as string ?? current.Model;
        AiConfig.Save(key.Password, id);
        return !string.IsNullOrWhiteSpace(key.Password);
    }
}
