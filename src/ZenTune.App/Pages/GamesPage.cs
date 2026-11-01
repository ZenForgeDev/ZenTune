using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using ZenTune.Core;
using ZenTune.Core.Games;
using ZenTune.Core.Presets;

namespace ZenTune.App.Pages;

/// <summary>"Games": supported games found on Steam and Epic, with settings backups and shader resets.</summary>
internal sealed partial class GamesPage : UserControl
{
    private readonly StackPanel _results = new() { Spacing = 12 };
    private readonly InfoBar _status;
    private readonly Button _rescan;
    private readonly Button _supported;
    private GameScanResult? _scan;

    public GamesPage()
    {
        _rescan = Ui.IconButton(Ui.IconRefresh, "Scan again", accent: true);
        _rescan.Click += async (_, _) => await ScanAsync();

        _supported = Ui.IconButton(Ui.IconGame, "Supported games");
        _supported.IsEnabled = false; // until the first scan has finished
        _supported.Click += async (_, _) => await ShowSupportedAsync();

        var (root, body, status) = Ui.Page("Games", "Supported games on Steam and Epic. Back up their settings before you change anything.", _supported, _rescan);
        _status = status;
        body.Children.Add(_results);
        Content = root;

        _ = ScanAsync();
    }

    private async Task ScanAsync()
    {
        _rescan.IsEnabled = false;
        _results.Children.Clear();
        _results.Children.Add(Ui.Busy());

        try
        {
            var scan = await Task.Run(GameScanner.Scan);
            _scan = scan;
            _supported.IsEnabled = true;
            try { _profile = HardwareProfile.From(await AppState.HardwareAsync()); }
            catch { _profile = null; } // presets still work, just without a recommendation
            Render(scan);
            _ = LoadOrphanNamesAsync(scan.OldShaderCaches);
        }
        catch (Exception ex)
        {
            _results.Children.Clear();
            _status.Show(InfoBarSeverity.Error, "Could not look for games", ex.Message);
        }
        finally
        {
            _rescan.IsEnabled = true;
        }
    }

    // ---------------------------------------------------------------- Layout

    private readonly Dictionary<OrphanCache, TextBlock> _orphanLabels = new();
    private HardwareProfile? _profile;

    private void Render(GameScanResult scan)
    {
        _results.Children.Clear();
        _orphanLabels.Clear();

        if (!scan.SteamFound && !scan.EpicFound)
        {
            _results.Children.Add(new InfoBar
            {
                Severity = InfoBarSeverity.Warning,
                Title = "No game launchers found",
                Message = "ZenTune looks for Steam and the Epic Games Launcher. Neither seems to be installed.",
                IsOpen = true,
                IsClosable = false,
            });
        }

        if (scan.Supported.Count == 0)
        {
            _results.Children.Add(Ui.Secondary("None of the supported games are installed yet."));
        }

        foreach (var game in scan.Supported)
            _results.Children.Add(GameCard(game));

        if (scan.NotInstalled.Count > 0)
        {
            _results.Children.Add(Ui.Secondary(
                $"{scan.NotInstalled.Count} more supported games are not installed. Press \"Supported games\" at the top to see and search the full list."));
        }

        if (scan.OldShaderCaches.Count > 0)
            _results.Children.Add(OldFilesSection(scan.OldShaderCaches));
    }

    // ---------------------------------------------------------------- Supported games list

    /// <summary>A searchable list of every game ZenTune supports, plus other games it found but cannot tune yet.</summary>
    private async Task ShowSupportedAsync()
    {
        if (_scan is not { } scan) return;

        var search = new TextBox { PlaceholderText = "Search games", Margin = new Thickness(0, 0, 0, 12) };
        var list = new StackPanel { Spacing = 2 };
        var body = new StackPanel { MinWidth = 480 };
        body.Children.Add(search);
        body.Children.Add(new ScrollViewer { Content = list, MaxHeight = 440, Padding = new Thickness(0, 0, 16, 0) });

        void AddGroup(string title, IEnumerable<(string Name, string Detail)> rows)
        {
            var items = rows.ToList();
            if (items.Count == 0) return;

            list.Children.Add(new TextBlock
            {
                Text = $"{title.ToUpperInvariant()} · {items.Count}",
                FontSize = 11,
                CharacterSpacing = 60,
                FontWeight = FontWeights.SemiBold,
                Foreground = Ui.Res("TextFillColorSecondaryBrush"),
                Margin = new Thickness(0, list.Children.Count == 0 ? 0 : 14, 0, 4),
            });

            foreach (var (name, detail) in items)
            {
                var row = new StackPanel { Spacing = 0, Padding = new Thickness(0, 4, 0, 4) };
                row.Children.Add(new TextBlock { Text = name, TextWrapping = TextWrapping.Wrap });
                if (detail.Length > 0) row.Children.Add(Ui.Secondary(detail));
                list.Children.Add(row);
            }
        }

        void Fill()
        {
            list.Children.Clear();
            string q = search.Text.Trim();
            bool Hit(string name, string extra = "") =>
                q.Length == 0 ||
                name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                extra.Contains(q, StringComparison.OrdinalIgnoreCase);

            AddGroup("Installed", scan.Supported
                .Where(g => Hit(g.Name, g.Definition.Engine))
                .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => (g.Name, $"{g.App.Store}" + (g.Definition.Engine.Length > 0 ? $" · {g.Definition.Engine}" : ""))));

            AddGroup("Supported, not installed", scan.NotInstalled
                .Where(d => Hit(d.Name, d.Engine))
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .Select(d => (d.Name, d.Engine)));

            AddGroup("Found on this PC, not supported yet", scan.OtherGames
                .Where(a => Hit(a.Name))
                .Select(a => (a.Name, $"{a.Store}" + (a.FullyInstalled ? "" : " · still downloading"))));

            if (list.Children.Count == 0)
                list.Children.Add(Ui.Secondary($"No games match \"{q}\"."));
        }

        search.TextChanged += (_, _) => Fill();
        Fill();

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Supported games",
            Content = body,
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
            RequestedTheme = ElementTheme.Dark,
        };
        dialog.Opened += (_, _) => search.Focus(FocusState.Programmatic);
        await dialog.ShowAsync();
    }

    // ---------------------------------------------------------------- One game

    private UIElement GameCard(DetectedGame game)
    {
        var grid = new Grid { ColumnSpacing = 24 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        grid.Children.Add(Cover(game.App.CoverImage));

        var info = new StackPanel { Spacing = 10 };
        Grid.SetColumn(info, 1);
        grid.Children.Add(info);

        var holder = Ui.CardFrame(grid);
        Fill(info, game, holder);
        return holder;
    }

    /// <summary>(Re)builds the right side of a game card. Called again after every action on that game.</summary>
    private void Fill(StackPanel info, DetectedGame game, Border holder)
    {
        info.Children.Clear();

        info.Children.Add(new TextBlock { Text = game.Name, Style = Ui.StyleRes("SubtitleTextBlockStyle"), TextWrapping = TextWrapping.Wrap });

        var install = game.App.FullyInstalled ? "Installed" : "Still downloading";
        info.Children.Add(Ui.Secondary($"{game.App.Store} · {install}" + (game.Definition.Engine.Length > 0 ? $" · {game.Definition.Engine}" : "")));

        var backups = Backups.List(game.Id);
        var rows = new List<(string, string)>
        {
            ("Settings", game.ConfigFiles.Count > 0
                ? $"{game.ConfigFiles.Count} {(game.ConfigFiles.Count == 1 ? "file" : "files")} in {ShortPath(game.ConfigFolder)}"
                : "Not created yet. Start the game once and come back."),
            ("Shader cache", game.ShaderFiles.Count > 0 ? ShaderCache.Size(game.ShaderBytes)
                : game.Definition.InGameShaderReset is not null ? "Managed by the game"
                : game.Definition.UsesDriverCache is not null ? "Kept in the graphics driver's cache" : "Empty"),
            ("Backups", backups.Count == 0 ? "None yet" : $"{backups.Count} · latest {Ui.When(backups[0].Created)}"),
        };
        info.Children.Add(Ui.Rows(rows));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        info.Children.Add(buttons);

        // Back up
        var backup = Ui.IconButton(Ui.IconSave, "Back up settings", accent: true);
        backup.IsEnabled = game.ConfigFiles.Count > 0;
        backup.Click += async (_, _) => await RunAsync(backup, info, game, holder, async () =>
        {
            var b = await Task.Run(() => Backups.Create(game, "Manual backup"));
            return ("Settings backed up", $"{b.Files.Count} files from {game.Name} were saved.");
        });
        buttons.Children.Add(backup);

        // Restore (a menu with the backups)
        var restore = Ui.IconButton(Ui.IconHistory, "Restore");
        restore.IsEnabled = backups.Count > 0;
        if (backups.Count > 0)
        {
            var menu = new MenuFlyout();
            foreach (var b in backups.Take(15))
            {
                var item = new MenuFlyoutItem { Text = $"{Ui.When(b.Created)} · {b.Label}" };
                item.Click += async (_, _) =>
                {
                    bool ok = await Ui.ConfirmAsync(XamlRoot, $"Restore {game.Name} settings?",
                        $"The settings from {Ui.When(b.Created)} will replace the current ones. " +
                        "ZenTune backs up the current settings first, so you can go back.", "Restore");
                    if (!ok) return;

                    await RunAsync(restore, info, game, holder, async () =>
                    {
                        await Task.Run(() => Backups.Restore(game, b));
                        return ("Settings restored", $"{game.Name} will use the settings from {Ui.When(b.Created)} next time it starts.");
                    });
                };
                menu.Items.Add(item);
            }
            menu.Items.Add(new MenuFlyoutSeparator());
            var openBackups = new MenuFlyoutItem { Text = "Open backups folder", Icon = new FontIcon { Glyph = Ui.IconFolder } };
            openBackups.Click += (_, _) => Ui.OpenFolder(Path.Combine(AppPaths.Backups, game.Id));
            menu.Items.Add(openBackups);
            restore.Flyout = menu;
        }
        buttons.Children.Add(restore);

        // Reset shaders
        if (game.ShaderFiles.Count == 0 && game.Definition.InGameShaderReset is { } howTo)
        {
            bool automatable = game.Definition.ShaderRebuildEdit is not null;
            var guide = Ui.IconButton(automatable ? Ui.IconBroom : Ui.IconInfo, automatable ? "Reset shaders" : "How to reset shaders");
            bool canAutomate = game.Definition.ShaderRebuildEdit is not null && game.ConfigFiles.Count > 0;
            guide.Click += async (_, _) =>
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = $"Reset shaders in {game.Name}",
                    Content = new TextBlock
                    {
                        Text = canAutomate
                            ? "ZenTune can make the game think the graphics driver changed. It then runs shader pre-loading again on the next start. " +
                              "Your settings are backed up first, and only one line is changed.\n\nThis is new, so tell us if the game does not start pre-loading.\n\n" +
                              "You can also do it yourself: " + howTo
                            : howTo,
                        TextWrapping = TextWrapping.Wrap,
                    },
                    PrimaryButtonText = canAutomate ? "Do it for me" : "",
                    CloseButtonText = canAutomate ? "Cancel" : "OK",
                    DefaultButton = ContentDialogButton.Primary,
                    RequestedTheme = ElementTheme.Dark,
                };

                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

                await RunAsync(guide, info, game, holder, async () =>
                {
                    int files = await Task.Run(() => ShaderCache.ForceRebuild(game));
                    return ("Shader rebuild requested",
                        $"Changed {files} settings {(files == 1 ? "file" : "files")}. Start {game.Name}: it should run shader pre-loading. {game.Definition.ShaderNote}");
                });
            };
            buttons.Children.Add(guide);
        }
        else
        {
        bool driverCache = game.ShaderFiles.Count == 0 && game.Definition.UsesDriverCache is not null;
        var shaders = Ui.IconButton(Ui.IconBroom, "Reset shaders");
        shaders.IsEnabled = game.ShaderFiles.Count > 0 || driverCache;
        shaders.Click += async (_, _) =>
        {
            string message = driverCache
                ? $"{game.Definition.UsesDriverCache} Close all games first. {game.Definition.ShaderNote}"
                : $"This deletes {ShaderCache.Size(game.ShaderBytes)} of saved shaders. {game.Definition.ShaderNote} " +
                  "Use this if the game stutters a lot, shows graphics glitches, or crashes after a driver update. Your settings and saves are not touched.";

            bool ok = await Ui.ConfirmAsync(XamlRoot, $"Reset shaders for {game.Name}?", message, "Reset shaders", destructive: true);
            if (!ok) return;

            await RunAsync(shaders, info, game, holder, async () =>
            {
                if (driverCache && GameScanner.IsRunning(game))
                    throw new InvalidOperationException($"Close {game.Name} first. The shader cache is in use while the game runs.");

                var (freed, skipped) = driverCache
                    ? await Task.Run(ShaderCache.ClearDriverCache)
                    : await Task.Run(() => ShaderCache.Reset(game));
                return ("Shaders reset", $"Freed {ShaderCache.Size(freed)}. {game.Definition.ShaderNote}" +
                                         (skipped > 0 ? $" {skipped} files were in use and were left alone." : ""));
            });
        };
        buttons.Children.Add(shaders);
        }

        // Open folder
        if (game.ConfigFolder is not null && Directory.Exists(game.ConfigFolder))
        {
            var open = Ui.IconButton(Ui.IconFolder, "Settings folder");
            open.Click += (_, _) => Ui.OpenFolder(game.ConfigFolder);
            buttons.Children.Add(open);
        }

        AddPresets(info, game, holder);
        AddAiSection(info, game, holder);
    }

    // ---------------------------------------------------------------- Presets

    private void AddPresets(StackPanel info, DetectedGame game, Border holder)
    {
        var tuner = PresetService.For(game);
        if (tuner is null)
        {
            info.Children.Add(Ui.Secondary(game.ConfigFiles.Count == 0
                ? "Graphics presets: start the game once first."
                : "Graphics presets for this game are coming in a later version."));
            return;
        }

        info.Children.Add(new TextBlock
        {
            Text = "GRAPHICS PRESETS",
            FontSize = 11,
            CharacterSpacing = 60,
            FontWeight = FontWeights.SemiBold,
            Foreground = Ui.Res("TextFillColorSecondaryBrush"),
            Margin = new Thickness(0, 8, 0, 0),
        });

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        PresetKind? recommended = _profile is null ? null : PresetInfo.Recommended(_profile);
        PresetKind? active = _profile is null ? null : ActivePreset(tuner, game, _profile);

        foreach (var kind in Enum.GetValues<PresetKind>())
        {
            string label = PresetInfo.Title(kind) + (kind == active ? " ✓" : "") + (kind == recommended ? " ★" : "");
            var button = new Button { Content = label };
            if (kind == active) button.Style = Ui.StyleRes("AccentButtonStyle");

            string tip = PresetInfo.Description(kind);
            if (kind == active) tip += " This is what the game uses now.";
            if (kind == recommended) tip += " Recommended for your PC.";
            ToolTipService.SetToolTip(button, tip);
            button.Click += async (_, _) => await PreviewPresetAsync(button, info, game, holder, tuner, kind);
            row.Children.Add(button);
        }
        info.Children.Add(row);
        info.Children.Add(Ui.Secondary(active is null
            ? "✓ marks the preset the game uses now (none right now: your own settings).  ★ is recommended for your PC."
            : "✓ marks the preset the game uses now.  ★ is recommended for your PC."));
    }

    /// <summary>The preset whose settings the game uses right now, if any.</summary>
    private static PresetKind? ActivePreset(IGameTuner tuner, DetectedGame game, HardwareProfile profile)
    {
        try
        {
            foreach (var kind in Enum.GetValues<PresetKind>())
            {
                var plan = tuner.Plan(game, kind, profile);
                if (plan.Changes.Count > 0 && !plan.Effective.Any()) return kind;
            }
        }
        catch { /* unreadable settings: no marker */ }
        return null;
    }

    private bool _presetBusy;

    private async Task PreviewPresetAsync(Button button, StackPanel info, DetectedGame game, Border holder, IGameTuner tuner, PresetKind kind)
    {
        // One preset at a time: a second click while the first is open or writing would back up the new settings as "before"
        if (_presetBusy) return;
        _presetBusy = true;
        try { await PreviewPresetCoreAsync(button, info, game, holder, tuner, kind); }
        finally { _presetBusy = false; }
    }

    private async Task PreviewPresetCoreAsync(Button button, StackPanel info, DetectedGame game, Border holder, IGameTuner tuner, PresetKind kind)
    {
        PresetPlan plan;
        try
        {
            var profile = _profile ?? HardwareProfile.From(await AppState.HardwareAsync());
            plan = await Task.Run(() => tuner.Plan(game, kind, profile));
        }
        catch (Exception ex)
        {
            _status.Show(InfoBarSeverity.Error, "Could not prepare the preset", ex.Message);
            return;
        }

        var changes = plan.Effective.ToList();
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(new TextBlock { Text = plan.Summary, TextWrapping = TextWrapping.Wrap });

        if (kind == PresetKind.Handheld && _profile is { IsHandheld: false })
        {
            body.Children.Add(new InfoBar
            {
                Severity = InfoBarSeverity.Warning,
                Title = "This PC is not a handheld",
                Message = "The Handheld preset is made for small screens and low power. On a big monitor it looks blurry. " +
                          "ZenTune keeps the FPS limit at 60 here, because lower limits can make G-Sync/FreeSync monitors flicker.",
                IsOpen = true,
                IsClosable = false,
            });
        }

        if (changes.Count == 0)
        {
            body.Children.Add(Ui.Secondary("Your settings already match this preset. Nothing to change."));
        }
        else
        {
            body.Children.Add(new TextBlock
            {
                Text = plan.GuideOnly
                    ? $"{changes.Count} {(changes.Count == 1 ? "setting" : "settings")} to change in the game's own menu:"
                    : $"{changes.Count} {(changes.Count == 1 ? "setting changes" : "settings change")}. Your current settings are backed up first.",
                Style = Ui.StyleRes("BodyStrongTextBlockStyle"),
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
                item.Children.Add(Ui.Secondary(c.Why));
                body.Children.Add(item);
            }
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"{PresetInfo.Title(kind)} preset · {game.Name}",
            Content = new ScrollViewer { Content = body, MaxHeight = 460, Padding = new Thickness(0, 0, 16, 0) },
            PrimaryButtonText = changes.Count > 0 && !plan.GuideOnly ? "Apply preset" : "",
            CloseButtonText = changes.Count > 0 && !plan.GuideOnly ? "Cancel" : "OK",
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = ElementTheme.Dark,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        await RunAsync(button, info, game, holder, async () =>
        {
            int n = await Task.Run(() => PresetService.Apply(plan));
            return ($"{PresetInfo.Title(kind)} preset applied",
                $"Changed {n} settings in {game.Name}. Start the game to see it. To go back, use Restore → \"Before {PresetInfo.Title(kind)} preset\".");
        });
    }

    /// <summary>Runs an action for one game, shows the result and refreshes that game's card.</summary>
    private async Task RunAsync(Button button, StackPanel info, DetectedGame game, Border holder, Func<Task<(string Title, string Message)>> action)
    {
        button.IsEnabled = false;
        try
        {
            var (title, message) = await action();
            _status.Show(InfoBarSeverity.Success, title, message);
        }
        catch (Exception ex)
        {
            _status.Show(InfoBarSeverity.Error, "That did not work", ex.Message);
        }

        try
        {
            var fresh = await Task.Run(() => GameScanner.Refresh(game));
            Fill(info, fresh, holder);
        }
        catch
        {
            button.IsEnabled = true;
        }
    }

    private static FrameworkElement Cover(string? image)
    {
        const double w = 100, h = 150;
        if (image is not null && File.Exists(image))
        {
            return new Border
            {
                Width = w,
                Height = h,
                CornerRadius = new CornerRadius(6),
                VerticalAlignment = VerticalAlignment.Top,
                // An image brush follows the rounded corners; a child Image would not be clipped
                Background = new ImageBrush { ImageSource = new BitmapImage(new Uri(image)), Stretch = Stretch.UniformToFill },
            };
        }

        return new Border
        {
            Width = w,
            Height = h,
            CornerRadius = new CornerRadius(6),
            VerticalAlignment = VerticalAlignment.Top,
            Background = Ui.Res("ControlFillColorSecondaryBrush"),
            Child = new FontIcon
            {
                Glyph = Ui.IconGame,
                FontSize = 32,
                Foreground = Ui.Res("TextFillColorTertiaryBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }

    private static string ShortPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return path.StartsWith(home, StringComparison.OrdinalIgnoreCase) ? "~" + path[home.Length..] : path;
    }

    // ---------------------------------------------------------------- Old files

    private UIElement OldFilesSection(IReadOnlyList<OrphanCache> caches)
    {
        var panel = new StackPanel { Spacing = 8 };
        long total = caches.Sum(c => c.Bytes);

        var header = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titles = new StackPanel { Spacing = 2 };
        titles.Children.Add(new TextBlock { Text = $"Old files · {ShaderCache.Size(total)}", Style = Ui.StyleRes("SubtitleTextBlockStyle") });
        titles.Children.Add(Ui.Secondary("Left behind by Steam games that are no longer installed: shader caches and game folders. " +
                                         "Shader caches are always safe to delete. If you plan to install a game again, keep its folder; Steam can reuse the files."));
        header.Children.Add(titles);

        var deleteAll = Ui.IconButton(Ui.IconDelete, "Delete all shader caches");
        deleteAll.IsEnabled = caches.Any(c => c.Kind == LeftoverKind.ShaderCache);
        deleteAll.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(deleteAll, 1);
        header.Children.Add(deleteAll);
        panel.Children.Add(header);

        var rows = new List<(OrphanCache Cache, Border Row, Button Button)>();
        foreach (var cache in caches)
        {
            var grid = new Grid { ColumnSpacing = 16 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var name = new TextBlock
            {
                Text = cache.Kind == LeftoverKind.GameFolder ? cache.AppId : $"Steam app {cache.AppId}",
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            label.Children.Add(name);
            label.Children.Add(Ui.Secondary(cache.Kind == LeftoverKind.GameFolder ? "Game folder" : "Shader cache"));
            if (cache.Kind == LeftoverKind.ShaderCache) _orphanLabels[cache] = name;
            grid.Children.Add(label);

            var size = Ui.Secondary(ShaderCache.Size(cache.Bytes));
            size.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(size, 1);
            grid.Children.Add(size);

            var delete = new Button { Content = new FontIcon { Glyph = Ui.IconDelete, FontSize = 14 }, VerticalAlignment = VerticalAlignment.Center };
            ToolTipService.SetToolTip(delete, "Delete");
            Grid.SetColumn(delete, 2);
            grid.Children.Add(delete);

            var row = Ui.CardFrame(grid, new Thickness(20, 10, 12, 10));
            rows.Add((cache, row, delete));
            panel.Children.Add(row);

            delete.Click += async (_, _) =>
            {
                if (cache.Kind == LeftoverKind.GameFolder &&
                    !await Ui.ConfirmAsync(XamlRoot, $"Delete the {cache.AppId} folder?",
                        $"This deletes {ShaderCache.Size(cache.Bytes)} in {cache.Folder}. Steam does not have this game installed. " +
                        "Anything inside (mods, ReShade, screenshots) is deleted too.", "Delete", destructive: true))
                    return;

                delete.IsEnabled = false;
                try
                {
                    long freed = await Task.Run(() => ShaderCache.DeleteOrphan(cache));
                    row.Visibility = Visibility.Collapsed;
                    _status.Show(InfoBarSeverity.Success, "Deleted", $"Freed {ShaderCache.Size(freed)}.");
                }
                catch (Exception ex)
                {
                    _status.Show(InfoBarSeverity.Error, "Could not delete", ex.Message);
                    delete.IsEnabled = true;
                }
            };
        }

        deleteAll.Click += async (_, _) =>
        {
            // Only shader caches: game folders can hold mods or files someone wants to keep, so they are deleted one by one
            var left = rows.Where(r => r.Row.Visibility == Visibility.Visible && r.Cache.Kind == LeftoverKind.ShaderCache).ToList();
            if (left.Count == 0) return;

            bool ok = await Ui.ConfirmAsync(XamlRoot, "Delete all old shader caches?",
                $"This deletes {ShaderCache.Size(left.Sum(r => r.Cache.Bytes))} from {left.Count} games you no longer have installed. " +
                "If you install one of them again, Steam rebuilds its cache.", "Delete all", destructive: true);
            if (!ok) return;

            deleteAll.IsEnabled = false;
            long freed = 0;
            foreach (var r in left)
            {
                try
                {
                    freed += await Task.Run(() => ShaderCache.DeleteOrphan(r.Cache));
                    r.Row.Visibility = Visibility.Collapsed;
                }
                catch { r.Button.IsEnabled = true; }
            }
            _status.Show(InfoBarSeverity.Success, "Old files deleted", $"Freed {ShaderCache.Size(freed)}.");
        };

        return panel;
    }

    /// <summary>Steam no longer knows the names of uninstalled games, so ask the Steam store.</summary>
    private async Task LoadOrphanNamesAsync(IReadOnlyList<OrphanCache> caches)
    {
        foreach (var cache in caches.Where(c => c.Kind == LeftoverKind.ShaderCache).Take(40))
        {
            var name = await SteamNames.LookupAsync(cache.AppId);
            if (name is null) continue;
            cache.Name = name;
            if (_orphanLabels.TryGetValue(cache, out var label))
                label.Text = name;
        }
    }
}
