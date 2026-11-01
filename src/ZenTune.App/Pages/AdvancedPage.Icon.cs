using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ZenTune.App.Pages;

/// <summary>"App icon": choose between the gauge and the graph.</summary>
internal sealed partial class AdvancedPage
{
    private UIElement IconSection()
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(Ui.Secondary(
            "Choose the ZenTune icon. The window and the taskbar change right away, and ZenTune points its own desktop and Start menu shortcuts at the new icon. " +
            "The icon on the ZenTune.exe file itself in File Explorer always stays the graph."));

        var picker = new RadioButtons { MaxColumns = 2 };
        picker.Items.Add(IconChoiceItem(AppIconChoice.Graph, "Graph", "The default: a rising frame rate curve"));
        picker.Items.Add(IconChoiceItem(AppIconChoice.Gauge, "Gauge", "A speedometer"));
        picker.SelectedIndex = AppIcon.Current == AppIconChoice.Graph ? 0 : 1;

        picker.SelectionChanged += async (_, _) =>
        {
            var choice = picker.SelectedIndex == 0 ? AppIconChoice.Graph : AppIconChoice.Gauge;
            if (choice == AppIcon.Current) return;

            AppIcon.Choose(choice);
            int n = await Task.Run(() => AppIcon.UpdateShortcuts(choice));

            _status.Show(InfoBarSeverity.Success, "Icon changed",
                n > 0
                    ? $"The window and taskbar use the new icon now, and {n} {(n == 1 ? "shortcut was" : "shortcuts were")} updated. File Explorer can take a moment to show it."
                    : "The window and taskbar use the new icon now. No desktop or Start menu shortcut for this copy of ZenTune was found.");
        };

        panel.Children.Add(picker);
        return panel;
    }

    private static UIElement IconChoiceItem(AppIconChoice choice, string title, string subtitle)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Padding = new Thickness(0, 4, 24, 4) };

        var image = new Image { Width = 44, Height = 44 };
        try
        {
            var png = AppIcon.PngPath(choice);
            if (File.Exists(png)) image.Source = new BitmapImage(new Uri(png));
        }
        catch { /* the preview is optional */ }
        row.Children.Add(image);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title });
        text.Children.Add(Ui.Secondary(subtitle));
        row.Children.Add(text);

        return row;
    }
}
