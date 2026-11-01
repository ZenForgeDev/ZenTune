using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ZenTune.Core;

namespace ZenTune.App;

/// <summary>
/// The "Welcome to ZenTune Beta!" message shown when the app launches. The checkbox "Don't show this on next launch"
/// is ticked by default; the choice is kept in %LOCALAPPDATA%\ZenTune\welcome-hidden.txt (delete the file to see it again).
/// </summary>
internal static class WelcomeDialog
{
    private static string FlagFile => Path.Combine(AppPaths.Root, "welcome-hidden.txt");

    private static bool IsHidden()
    {
        try { return File.Exists(FlagFile); }
        catch { return false; }
    }

    private static void SetHidden(bool hidden)
    {
        try
        {
            if (hidden) File.WriteAllText(FlagFile, "The welcome message is hidden. Delete this file to see it again.");
            else if (File.Exists(FlagFile)) File.Delete(FlagFile);
        }
        catch { /* best effort: at worst the message shows once more */ }
    }

    public static async Task ShowIfNeededAsync(XamlRoot root)
    {
        if (IsHidden()) return;

        var body = new StackPanel { Spacing = 10, MinWidth = 460 };
        body.Children.Add(new TextBlock
        {
            Text = "Thank you for testing ZenTune! It is a free, open-source game settings optimizer for Windows. " +
                   "This is a beta, so you may run into bugs.",
            TextWrapping = TextWrapping.Wrap,
        });

        string[] points =
        [
            "ZenTune changes game settings files and some Windows settings. Everything is backed up first and can be undone: restore your backups on the Games page and press Undo on the System check page.",
            "Before you try the Advanced tweaks, create a Windows restore point (Advanced has a button for it).",
            "ZenTune never injects anything into your games. It only edits settings files and Windows settings.",
            "The AI features are optional. They need your own free Gemini key (Advanced → Gemini AI) and work for Call of Duty for now.",
            "Pulse, the sensors and Measure only read numbers from Windows. They have NOT been tested with anti-cheat. Before you play online games with strict anti-cheat (like Call of Duty), close the Pulse popout, keep the sensors off and press Remove sensor driver on the Pulse page.",
            "Something not working? Open Feedback, press Collect info and send the zip file from your desktop to the developer.",
        ];
        foreach (var point in points)
            body.Children.Add(Ui.Secondary("•  " + point));

        var dontShow = new CheckBox { Content = "Don't show this on next launch", IsChecked = true, Margin = new Thickness(0, 6, 0, 0) };
        body.Children.Add(dontShow);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Welcome to ZenTune Beta!",
            Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 460 },
            PrimaryButtonText = "Let's go",
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = ElementTheme.Dark,
        };

        await dialog.ShowAsync();
        SetHidden(dontShow.IsChecked == true);
    }
}
