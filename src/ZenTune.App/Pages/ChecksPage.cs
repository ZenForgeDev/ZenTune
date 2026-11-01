using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ZenTune.Core.Changes;
using ZenTune.Core.Checks;
using ZenTune.Core.Games;

namespace ZenTune.App.Pages;

/// <summary>"System check": Windows and hardware settings that cost FPS, with one-click fixes that can be undone.</summary>
internal sealed partial class ChecksPage : UserControl
{
    private readonly StackPanel _results = new() { Spacing = 8 };
    private readonly InfoBar _status;
    private readonly Button _rescan;
    private bool _loading;

    public ChecksPage()
    {
        _rescan = Ui.IconButton(Ui.IconRefresh, "Check again", accent: true);
        _rescan.Click += async (_, _) => await LoadAsync(rescanHardware: true);

        var (root, body, status) = Ui.Page("System check", "Windows and hardware settings that affect your games. Every fix can be undone.", _rescan);
        _status = status;
        body.Children.Add(_results);
        Content = root;

        _ = LoadAsync(rescanHardware: false);
    }

    public void Reload()
    {
        if (!_loading) _ = LoadAsync(rescanHardware: false);
    }

    private async Task LoadAsync(bool rescanHardware)
    {
        _loading = true;
        _rescan.IsEnabled = false;
        if (_results.Children.Count == 0) _results.Children.Add(Ui.Busy());

        try
        {
            var hw = await AppState.HardwareAsync(rescanHardware);
            var (checks, changes, driverCache) = await Task.Run(() =>
                (SystemChecks.Run(hw), ChangeJournal.Active(), ShaderCache.DriverCacheSize()));
            Render(checks, changes, driverCache);
        }
        catch (Exception ex)
        {
            _results.Children.Clear();
            _status.Show(InfoBarSeverity.Error, "The check failed", ex.Message);
        }
        finally
        {
            _rescan.IsEnabled = true;
            _loading = false;
        }
    }

    private void Render(List<CheckResult> checks, IReadOnlyList<ChangeRecord> changes, long driverCache)
    {
        _results.Children.Clear();

        int warnings = checks.Count(c => c.Status == CheckStatus.Warning);
        int tips = checks.Count(c => c.Status == CheckStatus.Info);
        int good = checks.Count(c => c.Status == CheckStatus.Good);

        var summary = new List<string>();
        if (warnings > 0) summary.Add($"{warnings} to fix");
        if (tips > 0) summary.Add($"{tips} {(tips == 1 ? "tip" : "tips")}");
        summary.Add($"{good} look good");
        _results.Children.Add(new TextBlock
        {
            Text = warnings == 0 ? "Everything important looks right. " + string.Join(" · ", summary) : string.Join(" · ", summary),
            Style = Ui.StyleRes("BodyStrongTextBlockStyle"),
        });

        // "Ready to play" first: Secure Boot, TPM, driver, free disk space, power plan, Game Mode, memory speed
        var ready = checks.Where(c => c.Preflight).OrderByDescending(c => c.Status).ToList();
        if (ready.Count > 0)
        {
            int readyOk = ready.Count(c => c.Status == CheckStatus.Good);
            AddGroup($"Ready to play · {readyOk} of {ready.Count} fine", ready);
        }

        checks = checks.Where(c => !c.Preflight).ToList();
        AddGroup("Needs attention", checks.Where(c => c.Status == CheckStatus.Warning));
        AddGroup("Tips", checks.Where(c => c.Status == CheckStatus.Info));

        // Driver shader cache
        _results.Children.Add(Ui.SectionHeader("Shader cache"));
        _results.Children.Add(DriverCacheRow(driverCache));

        AddGroup("Looks good", checks.Where(c => c.Status == CheckStatus.Good));

        if (changes.Count > 0)
        {
            _results.Children.Add(Ui.SectionHeader("Changes made by ZenTune"));
            foreach (var change in changes)
                _results.Children.Add(ChangeRow(change));
        }
    }

    private void AddGroup(string title, IEnumerable<CheckResult> items)
    {
        var list = items.ToList();
        if (list.Count == 0) return;
        _results.Children.Add(Ui.SectionHeader(title));
        foreach (var item in list)
            _results.Children.Add(CheckRow(item));
    }

    private UIElement CheckRow(CheckResult check)
    {
        var (glyph, brush) = check.Status switch
        {
            CheckStatus.Warning => (Ui.IconWarning, "SystemFillColorCautionBrush"),
            CheckStatus.Info => (Ui.IconInfo, "AccentTextFillColorPrimaryBrush"),
            _ => (Ui.IconCheck, "SystemFillColorSuccessBrush"),
        };

        Button? button = null;
        if (check.Action is not null && check.ActionLabel is not null)
        {
            button = new Button { Content = check.ActionLabel, VerticalAlignment = VerticalAlignment.Center };
            if (check.Status == CheckStatus.Warning && check.ActionChangesSystem)
                button.Style = Ui.StyleRes("AccentButtonStyle");

            button.Click += async (_, _) =>
            {
                if (check.ConfirmText is not null)
                {
                    bool ok = await Ui.ConfirmAsync(XamlRoot, check.ActionLabel ?? "Confirm", check.ConfirmText, "Continue");
                    if (!ok) return;
                }

                button.IsEnabled = false;
                try
                {
                    await Task.Run(check.Action);
                    if (check.ActionChangesSystem)
                    {
                        _status.Show(InfoBarSeverity.Success, "Done", $"{check.ActionLabel}. You can undo it at the bottom of this page.");
                        await LoadAsync(rescanHardware: check.Id.StartsWith("refresh"));
                    }
                    else if (check.ActionDoneMessage is not null)
                    {
                        _status.Show(InfoBarSeverity.Success, "Done", check.ActionDoneMessage);
                        await LoadAsync(rescanHardware: false);
                    }
                }
                catch (Exception ex)
                {
                    _status.Show(InfoBarSeverity.Error, "That did not work", ex.Message);
                }
                finally
                {
                    button.IsEnabled = true;
                }
            };
        }

        return Row(glyph, brush, check.Title, check.Detail, button);
    }

    private UIElement DriverCacheRow(long bytes)
    {
        var button = Ui.IconButton(Ui.IconBroom, "Clear");
        button.IsEnabled = bytes > 0;
        button.Click += async (_, _) =>
        {
            bool ok = await Ui.ConfirmAsync(XamlRoot, "Clear the driver shader cache?",
                $"This deletes {ShaderCache.Size(bytes)} of shaders saved by your graphics driver for all games. " +
                "Every game rebuilds them on its next start, so the first few minutes can stutter a bit. " +
                "Close your games first; files in use are skipped.",
                "Clear", destructive: true);
            if (!ok) return;

            button.IsEnabled = false;
            try
            {
                var (freed, skipped) = await Task.Run(ShaderCache.ClearDriverCache);
                _status.Show(InfoBarSeverity.Success, "Driver shader cache cleared",
                    $"Freed {ShaderCache.Size(freed)}." + (skipped > 0 ? $" {skipped} files were in use and were left alone." : ""));
                await LoadAsync(rescanHardware: false);
            }
            catch (Exception ex)
            {
                _status.Show(InfoBarSeverity.Error, "Could not clear the cache", ex.Message);
                button.IsEnabled = true;
            }
        };

        return Row(Ui.IconBroom, "AccentTextFillColorPrimaryBrush",
            $"Driver shader cache · {ShaderCache.Size(bytes)}",
            "Shaders your graphics driver saved for all games. Clear it if many games stutter or show glitches after a driver update. " +
            "To reset a single game, use Reset shaders on the Games page.",
            button);
    }

    private UIElement ChangeRow(ChangeRecord change)
    {
        var button = Ui.IconButton(Ui.IconUndo, "Undo");
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try
            {
                await Task.Run(() => Tweaks.Undo(change));
                _status.Show(InfoBarSeverity.Success, "Undone", change.Title);
                await LoadAsync(rescanHardware: change.Kind == "refresh");
            }
            catch (Exception ex)
            {
                _status.Show(InfoBarSeverity.Error, "Could not undo", ex.Message);
                button.IsEnabled = true;
            }
        };

        return Row(Ui.IconHistory, "TextFillColorSecondaryBrush", change.Title, Ui.When(change.When), button);
    }

    private static UIElement Row(string glyph, string brushKey, string title, string detail, Button? button)
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new FontIcon { Glyph = glyph, FontSize = 18, Foreground = Ui.Res(brushKey), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
        grid.Children.Add(icon);

        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(Ui.Secondary(detail));
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        if (button is not null)
        {
            Grid.SetColumn(button, 2);
            grid.Children.Add(button);
        }

        return Ui.CardFrame(grid, new Thickness(20, 14, 20, 14));
    }
}
