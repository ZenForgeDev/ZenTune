using System.Diagnostics;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ZenTune.App.Pages;

/// <summary>"Color theme": pick Default, Gamer, Vibrant and more. Applied when ZenTune starts.</summary>
internal sealed partial class SettingsPage
{
    private readonly List<(ThemeDef Theme, Border Frame)> _themeTiles = [];
    private Button? _restart;

    private UIElement ThemeSection()
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(Ui.Secondary(
            "Colors for the whole app: buttons, switches, icons and a soft glow behind the window. \"Default\" is the normal look. " +
            "A new theme shows after ZenTune restarts."));

        var grid = new VariableSizedWrapGrid
        {
            Orientation = Orientation.Horizontal,
            ItemWidth = 168,
            ItemHeight = 108,
            MaximumRowsOrColumns = -1,
        };

        foreach (var theme in AppTheme.All)
        {
            var preview = new Border
            {
                Height = 56,
                CornerRadius = new CornerRadius(6),
                Background = AppTheme.Gradient(theme, 0xFF),
            };
            var dot = new Border
            {
                Width = 18,
                Height = 18,
                CornerRadius = new CornerRadius(9),
                Background = new SolidColorBrush(theme.Accent),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 8, 8),
            };
            var previewGrid = new Grid();
            previewGrid.Children.Add(preview);
            previewGrid.Children.Add(dot);

            var content = new StackPanel { Spacing = 6 };
            content.Children.Add(previewGrid);
            content.Children.Add(new TextBlock { Text = theme.Name, FontWeight = FontWeights.SemiBold });

            var button = new Button
            {
                Content = content,
                Width = 156,
                Padding = new Thickness(8),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
                BorderThickness = new Thickness(0),
            };

            var frame = new Border
            {
                Child = button,
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(2),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
                Margin = new Thickness(0, 0, 4, 4),
            };
            _themeTiles.Add((theme, frame));

            var chosen = theme;
            button.Click += (_, _) => ThemeChosen(chosen);
            grid.Children.Add(frame);
        }
        panel.Children.Add(grid);

        _restart = Ui.IconButton(Ui.IconRefresh, "Restart ZenTune to apply", accent: true);
        _restart.Visibility = Visibility.Collapsed;
        _restart.HorizontalAlignment = HorizontalAlignment.Left;
        _restart.Click += (_, _) => RestartNow();
        panel.Children.Add(_restart);

        MarkSelectedTheme(AppTheme.Current);
        return panel;
    }

    private void MarkSelectedTheme(ThemeDef selected)
    {
        foreach (var (theme, frame) in _themeTiles)
        {
            frame.BorderBrush = theme.Id == selected.Id
                ? Ui.Res("AccentFillColorDefaultBrush")
                : new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
        }
    }

    private void ThemeChosen(ThemeDef theme)
    {
        try
        {
            AppTheme.Save(theme);
            MarkSelectedTheme(theme);

            bool changed = theme.Id != AppTheme.Current.Id;
            if (_restart is not null) _restart.Visibility = changed ? Visibility.Visible : Visibility.Collapsed;

            _status.Show(InfoBarSeverity.Success, $"{theme.Name} theme saved",
                changed ? "Restart ZenTune to see it." : "This is the theme you have now.");
        }
        catch (Exception ex)
        {
            _status.Show(InfoBarSeverity.Error, "Could not save the theme", ex.Message);
        }
    }

    /// <summary>Starts a new copy a few seconds from now and quits this one (a normal close would only hide ZenTune in the tray).</summary>
    private void RestartNow()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is null || App.MainWin is null)
            {
                _status.Show(InfoBarSeverity.Warning, "Could not restart", "Close ZenTune from the tray icon (Exit) and open it again.");
                return;
            }

            Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 4 >nul & start \"\" \"{exe}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            });
            App.MainWin.ExitForRestart();
        }
        catch (Exception ex)
        {
            _status.Show(InfoBarSeverity.Error, "Could not restart", ex.Message);
        }
    }
}
