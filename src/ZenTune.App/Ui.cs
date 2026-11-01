using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ZenTune.App;

/// <summary>Small building blocks shared by all pages, so they look the same everywhere.</summary>
internal static class Ui
{
    public const string IconGame = "";
    public const string IconCpu = "";
    public const string IconMemory = "";
    public const string IconLaptop = "";
    public const string IconMonitor = "";
    public const string IconRefresh = "";
    public const string IconCopy = "";
    public const string IconWarning = "";
    public const string IconInfo = "";
    public const string IconCheck = "";
    public const string IconSave = "";
    public const string IconHistory = "";
    public const string IconDelete = "";
    public const string IconFolder = "";
    public const string IconUndo = "";
    public const string IconBroom = "";
    public const string IconHealth = "";

    public static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    public static Style StyleRes(string key) => (Style)Application.Current.Resources[key];

    // ---------------------------------------------------------------- Page frame

    /// <summary>A scrollable page with a title, a subtitle and optional buttons on the right.</summary>
    public static (ScrollViewer Root, StackPanel Body, InfoBar Status) Page(string title, string subtitle, params Button[] buttons)
    {
        var body = new StackPanel { MaxWidth = 1040, Padding = new Thickness(36, 8, 36, 36), Spacing = 16 };

        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var titles = new StackPanel { Spacing = 2 };
        titles.Children.Add(new TextBlock { Text = title, Style = StyleRes("TitleTextBlockStyle") });
        titles.Children.Add(new TextBlock { Text = subtitle, Foreground = Res("TextFillColorSecondaryBrush"), TextWrapping = TextWrapping.Wrap });
        header.Children.Add(titles);

        for (int i = 0; i < buttons.Length; i++)
        {
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            buttons[i].VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(buttons[i], i + 1);
            header.Children.Add(buttons[i]);
        }
        body.Children.Add(header);

        var status = new InfoBar { IsOpen = false, IsClosable = true };
        body.Children.Add(status);

        return (new ScrollViewer { Content = body }, body, status);
    }

    public static void Show(this InfoBar bar, InfoBarSeverity severity, string title, string message)
    {
        bar.Severity = severity;
        bar.Title = title;
        bar.Message = message;
        bar.IsOpen = true;
    }

    public static ProgressRing Busy() => new()
    {
        IsActive = true,
        Width = 48,
        Height = 48,
        Margin = new Thickness(0, 80, 0, 0),
        HorizontalAlignment = HorizontalAlignment.Center,
    };

    // ---------------------------------------------------------------- Controls

    public static Button IconButton(string glyph, string text, bool accent = false)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14 });
        content.Children.Add(new TextBlock { Text = text });

        var button = new Button { Content = content };
        if (accent) button.Style = StyleRes("AccentButtonStyle");
        return button;
    }

    public static TextBlock SectionHeader(string text) => new()
    {
        Text = text,
        Style = StyleRes("SubtitleTextBlockStyle"),
        Margin = new Thickness(0, 12, 0, 0),
    };

    public static TextBlock Secondary(string text) => new()
    {
        Text = text,
        Foreground = Res("TextFillColorSecondaryBrush"),
        TextWrapping = TextWrapping.Wrap,
    };

    public static Border CardFrame(UIElement child, Thickness? padding = null) => new()
    {
        Background = Res("CardBackgroundFillColorDefaultBrush"),
        BorderBrush = Res("CardStrokeColorDefaultBrush"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = padding ?? new Thickness(24, 20, 24, 20),
        Child = child,
    };

    /// <summary>Caption with icon, a big title, label/value rows and optional notes.</summary>
    public static Border Card(
        string glyph,
        string caption,
        string title,
        IReadOnlyList<(string Label, string Value)> rows,
        bool accent = false,
        IReadOnlyList<string>? notes = null)
    {
        var stack = new StackPanel { Spacing = 14 };

        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        head.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14, Foreground = Res("AccentTextFillColorPrimaryBrush") });
        head.Children.Add(new TextBlock
        {
            Text = caption.ToUpperInvariant(),
            FontSize = 11,
            CharacterSpacing = 60,
            FontWeight = FontWeights.SemiBold,
            Foreground = Res("TextFillColorSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        stack.Children.Add(head);

        stack.Children.Add(new TextBlock
        {
            Text = title,
            Style = StyleRes(accent ? "TitleTextBlockStyle" : "SubtitleTextBlockStyle"),
            TextWrapping = TextWrapping.Wrap,
        });

        if (rows.Count > 0) stack.Children.Add(Rows(rows));

        if (notes is { Count: > 0 })
        {
            foreach (var note in notes)
                stack.Children.Add(new InfoBar { Severity = InfoBarSeverity.Informational, Message = note, IsOpen = true, IsClosable = false });
        }

        return CardFrame(stack);
    }

    public static Grid Rows(IReadOnlyList<(string Label, string Value)> rows)
    {
        var grid = new Grid { ColumnSpacing = 24, RowSpacing = 6 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        for (int i = 0; i < rows.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var label = new TextBlock { Text = rows[i].Label, Foreground = Res("TextFillColorSecondaryBrush") };
            Grid.SetRow(label, i);

            var value = new TextBlock { Text = rows[i].Value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);

            grid.Children.Add(label);
            grid.Children.Add(value);
        }
        return grid;
    }

    // ---------------------------------------------------------------- Dialogs

    public static async Task<bool> ConfirmAsync(XamlRoot root, string title, string message, string confirm, bool destructive = false)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = confirm,
            CloseButtonText = "Cancel",
            DefaultButton = destructive ? ContentDialogButton.Close : ContentDialogButton.Primary,
            RequestedTheme = ElementTheme.Dark,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    public static void OpenFolder(string path)
    {
        try
        {
            if (Directory.Exists(path))
                System.Diagnostics.Process.Start("explorer.exe", $"\"{path}\"");
        }
        catch { /* nothing to do */ }
    }

    public static string When(DateTime t) =>
        t.Date == DateTime.Today ? $"Today {t:HH:mm}" :
        t.Date == DateTime.Today.AddDays(-1) ? $"Yesterday {t:HH:mm}" :
        t.ToString("d MMM yyyy HH:mm");
}
