using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ZenTune.App.Pages;

/// <summary>"Settings": ZenTune's own settings (start with Windows, tray, Gemini). Windows tweaks stay on the Advanced page.</summary>
internal sealed partial class SettingsPage : UserControl
{
    private readonly InfoBar _status;

    public SettingsPage()
    {
        var (root, body, status) = Ui.Page("Settings",
            "ZenTune's own settings. Nothing here changes Windows except the optional startup task.");
        _status = status;

        body.Children.Add(AdvancedPage.Section("Color theme", "Default, Gamer, Vibrant and more colors for the whole app.", ThemeSection()));
        body.Children.Add(AdvancedPage.Section("Start ZenTune with Windows", "Start ZenTune at sign-in and keep it in the tray.", AutoStartSection()));
        body.Children.Add(AdvancedPage.Section("Gemini AI", "Your API key and which Gemini models ZenTune may use.", GeminiSection()));

        Content = root;
    }
}
