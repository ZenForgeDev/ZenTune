using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ZenTune.Core.Advanced;
using ZenTune.Core.Changes;
using ZenTune.Core.Games;

namespace ZenTune.App.Pages;

/// <summary>
/// "Advanced": Windows tweaks adapted from Chris Titus Tech's WinUtil, the PC cleaner and outside tools.
/// Everything that changes Windows lands in the same undo list as System check.
/// </summary>
internal sealed partial class AdvancedPage : UserControl
{
    private readonly StackPanel _tweaks = new() { Spacing = 8 };
    private readonly StackPanel _cleaner = new() { Spacing = 8 };
    private readonly StackPanel _startup = new() { Spacing = 8 };
    private readonly InfoBar _status;
    private bool _loading;
    private bool _loadingStartup;

    public AdvancedPage()
    {
        var (root, body, status) = Ui.Page("Advanced",
            "ZenTune's own settings, plus Windows tweaks, a PC cleaner and extra tools. Everything that changes Windows can be undone.");
        _status = status;

        // ZenTune's own settings first, then everything that changes Windows.
        body.Children.Add(Ui.SectionHeader("ZenTune"));
        body.Children.Add(Section("App icon", "Choose the icon for the window, taskbar and shortcuts.", IconSection()));
        body.Children.Add(Section("Gemini AI", "Your API key and which Gemini models ZenTune may use.", GeminiSection()));

        body.Children.Add(Ui.SectionHeader("Windows"));
        body.Children.Add(new InfoBar
        {
            Severity = InfoBarSeverity.Warning,
            Title = "These change Windows itself",
            Message = "Create a restore point before you start. Changes that need admin rights show one Windows prompt. " +
                      "Undo puts back the exact values you had before.",
            IsOpen = true,
            IsClosable = false,
        });

        body.Children.Add(Section("Windows tweaks", "Restore point, power plan and privacy settings from WinUtil.", _tweaks));

        var startup = new StackPanel { Spacing = 8 };
        startup.Children.Add(Ui.Secondary(
            "Windows waits a while before it starts the apps in your startup list. With Fast startup on, an app is started by Task Scheduler " +
            "as soon as you sign in instead. The original entry is switched off so the app doesn't start twice. " +
            "Windows asks for admin rights once per app. After the next sign-in, the time each app started shows here. " +
            "Arguments lets you choose the options an app starts with, for example --start-minimized. " +
            "Apps installed for all users work too, but only for your account; Windows parts and security software are left alone."));
        startup.Children.Add(_startup);
        body.Children.Add(Section("Startup apps", "Start your apps faster after you sign in.", startup));

        var cleaner = new StackPanel { Spacing = 8 };
        cleaner.Children.Add(Ui.Secondary("Only caches and leftovers that Windows and apps rebuild on their own. Your files, downloads, saves and settings are never touched."));
        cleaner.Children.Add(_cleaner);
        body.Children.Add(Section("PC cleaner", "Remove temporary files and caches.", cleaner));

        body.Children.Add(Section("Tools", "Outside tools ZenTune can start for you.", ShutUpRow()));

        body.Children.Add(new TextBlock
        {
            Text = "Tweaks adapted from WinUtil by Chris Titus Tech (MIT license).",
            Foreground = Ui.Res("TextFillColorTertiaryBrush"),
            Margin = new Thickness(0, 16, 0, 0),
        });

        Content = root;

        _ = LoadTweaksAsync();
        _ = LoadStartupAsync();
        _ = ScanCleanerAsync();
    }

    public void Reload()
    {
        if (!_loading) _ = LoadTweaksAsync();
        if (!_loadingStartup) _ = LoadStartupAsync();
    }

    // ---------------------------------------------------------------- Startup apps

    private async Task LoadStartupAsync()
    {
        _loadingStartup = true;
        try
        {
            var entries = await Task.Run(StartupApps.List);
            RenderStartup(entries);
        }
        catch (Exception ex)
        {
            _startup.Children.Clear();
            _startup.Children.Add(Ui.Secondary("Could not read the startup apps: " + ex.Message));
        }
        finally
        {
            _loadingStartup = false;
        }
    }

    private void RenderStartup(List<StartupEntry> entries)
    {
        _startup.Children.Clear();

        if (entries.Count == 0)
        {
            _startup.Children.Add(Ui.Secondary("No startup apps found."));
            return;
        }

        foreach (var entry in entries)
            _startup.Children.Add(StartupRow(entry));
    }

    private UIElement StartupRow(StartupEntry entry)
    {
        string timing =
            entry.SecondsAfterSignIn is { } secs ? $"Started {secs:0} s after sign-in"
            : entry.Running ? "Running, but it did not start at sign-in"
            : "Not running";

        string state =
            entry.FastOn ? "Fast startup is on"
            : !entry.Enabled ? "Switched off in Windows"
            : entry.Where;

        string detail = $"{state} · {timing}\n{entry.Command}";
        if (entry.CustomArgs is not null)
            detail += "\nArguments you set: " + (entry.CustomArgs.Length == 0 ? "(none)" : entry.CustomArgs);

        var toggle = new ToggleSwitch
        {
            IsOn = entry.FastOn,
            OnContent = "Fast startup",
            OffContent = "Fast startup",
            IsEnabled = entry.FastOn || (entry.Enabled && entry.Problem is null),
            VerticalAlignment = VerticalAlignment.Center,
        };

        // Attached after IsOn is set, so showing the list never triggers a change
        toggle.Toggled += async (_, _) =>
        {
            bool turnOn = toggle.IsOn;
            toggle.IsEnabled = false;
            try
            {
                if (turnOn)
                {
                    await Task.Run(() => StartupApps.MakeFast(entry));
                    _status.Show(InfoBarSeverity.Success, "Fast startup is on for " + entry.Name,
                        "It will start from Task Scheduler the next time you sign in. Sign out and back in to try it.");
                }
                else
                {
                    var change = StartupApps.ActiveChange(entry.Key)
                        ?? throw new InvalidOperationException("ZenTune has no record of this change.");
                    await Task.Run(() => Tweaks.Undo(change));
                    _status.Show(InfoBarSeverity.Success, "Fast startup is off for " + entry.Name,
                        "Windows starts it the normal way again.");
                }
            }
            catch (OperationCanceledException ex)
            {
                _status.Show(InfoBarSeverity.Informational, "Nothing was changed", ex.Message);
            }
            catch (Exception ex)
            {
                _status.Show(InfoBarSeverity.Error, "That did not work", ex.Message);
            }
            await LoadStartupAsync();
        };

        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock { Text = entry.Name, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(Ui.Secondary(detail));
        if (entry.Problem is not null && !entry.FastOn)
        {
            text.Children.Add(new TextBlock
            {
                Text = entry.Problem,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Ui.Res("SystemFillColorCautionBrush"),
            });
        }
        grid.Children.Add(text);

        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
        var argsButton = new Button
        {
            Content = "Arguments",
            IsEnabled = entry.FastOn || (entry.Enabled && entry.Problem is null),
            VerticalAlignment = VerticalAlignment.Center,
        };
        argsButton.Click += async (_, _) => await EditArgumentsAsync(entry);
        controls.Children.Add(argsButton);
        controls.Children.Add(toggle);

        Grid.SetColumn(controls, 1);
        grid.Children.Add(controls);

        return Ui.CardFrame(grid, new Thickness(20, 12, 20, 12));
    }

    private async Task EditArgumentsAsync(StartupEntry entry)
    {
        var box = new TextBox
        {
            Text = entry.EffectiveArgs,
            PlaceholderText = "No arguments",
            MinWidth = 420,
        };

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(Ui.Secondary(entry.Exe ?? entry.Command));
        panel.Children.Add(box);
        panel.Children.Add(Ui.Secondary(entry.FastOn
            ? "The new arguments are used the next time you sign in. Windows asks for admin rights once."
            : "Saving also turns on Fast startup for this app, because Task Scheduler is what starts it with your arguments. " +
              "Undo (turn Fast startup off) puts the original entry back as it was."));

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Start arguments · " + entry.Name,
            Content = panel,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = ElementTheme.Dark,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        string text = box.Text;
        try
        {
            if (entry.FastOn) await Task.Run(() => StartupApps.SetArguments(entry, text));
            else await Task.Run(() => StartupApps.MakeFast(entry, text));

            _status.Show(InfoBarSeverity.Success, "Arguments saved for " + entry.Name,
                "They are used the next time you sign in. If the app starts twice, turn Fast startup off for it.");
        }
        catch (OperationCanceledException ex)
        {
            _status.Show(InfoBarSeverity.Informational, "Nothing was changed", ex.Message);
        }
        catch (Exception ex)
        {
            _status.Show(InfoBarSeverity.Error, "That did not work", ex.Message);
        }
        await LoadStartupAsync();
    }

    // ---------------------------------------------------------------- Tweaks

    private async Task LoadTweaksAsync()
    {
        _loading = true;
        try
        {
            var states = await Task.Run(() =>
                (Tweaks: AdvancedTweaks.All.Select(t => (Tweak: t, State: AdvancedTweaks.State(t))).ToList(),
                 Ultimate: AdvancedTweaks.UltimateActive()));
            RenderTweaks(states.Tweaks, states.Ultimate);
        }
        catch (Exception ex)
        {
            _status.Show(InfoBarSeverity.Error, "Could not read the current settings", ex.Message);
        }
        finally
        {
            _loading = false;
        }
    }

    private void RenderTweaks(List<(AdvancedTweak Tweak, TweakState State)> tweaks, bool ultimateOn)
    {
        _tweaks.Children.Clear();

        // Before you start
        _tweaks.Children.Add(Ui.SectionHeader("Before you start"));
        var restore = Ui.IconButton(Ui.IconHistory, "Create restore point", accent: true);
        restore.Click += async (_, _) => await RunAsync(restore, async () =>
        {
            await Task.Run(AdvancedTweaks.CreateRestorePoint);
            return ("Restore point created", "If something goes wrong, you can go back to it in Windows: search for \"Create a restore point\" → System Restore.");
        });
        _tweaks.Children.Add(Row("Restore point",
            "A snapshot of Windows settings you can go back to. Takes about a minute.", null, restore));

        // Power plan
        _tweaks.Children.Add(Ui.SectionHeader("Power"));
        var ultimate = ultimateOn ? Ui.IconButton(Ui.IconUndo, "Undo") : Ui.IconButton(Ui.IconCheck, "Turn on", accent: true);
        ultimate.Click += async (_, _) => await RunAsync(ultimate, async () =>
        {
            if (ultimateOn)
            {
                var change = ChangeJournal.Active().First(c => c.Kind == "ultimate");
                await Task.Run(() => Tweaks.Undo(change));
                return ("Ultimate Performance turned off", "Your previous power plan is back.");
            }
            await Task.Run(AdvancedTweaks.EnableUltimate);
            return ("Ultimate Performance is on", "Windows now keeps the processor at full readiness all the time.");
        });
        _tweaks.Children.Add(Row(
            "Ultimate Performance power plan" + (ultimateOn ? " · On" : ""),
            "Microsoft's hidden power plan that removes the small delays when the processor wakes up from power saving.",
            "Uses more power when the PC is idle. On a desktop the FPS gain is usually small. Not for laptops on battery.",
            ultimate));

        // Tweak groups in their defined order
        foreach (var group in tweaks.GroupBy(t => t.Tweak.Group))
        {
            _tweaks.Children.Add(Ui.SectionHeader(group.Key));
            foreach (var (tweak, state) in group)
                _tweaks.Children.Add(TweakRow(tweak, state));
        }
    }

    private UIElement TweakRow(AdvancedTweak tweak, TweakState state)
    {
        Button button;
        string title = tweak.Title;

        switch (state)
        {
            case TweakState.AppliedByZenTune:
                title += " · On";
                button = Ui.IconButton(Ui.IconUndo, "Undo");
                button.Click += async (_, _) => await RunAsync(button, async () =>
                {
                    var change = AdvancedTweaks.ActiveChange(tweak.Id)
                        ?? throw new InvalidOperationException("ZenTune has no record of this change.");
                    await Task.Run(() => Tweaks.Undo(change));
                    return ("Undone", tweak.Title + Followup(tweak));
                });
                break;

            case TweakState.Applied:
                title += " · Already set";
                button = new Button { Content = "Already set", IsEnabled = false };
                break;

            default:
                button = Ui.IconButton(Ui.IconCheck, "Apply");
                button.Click += async (_, _) => await RunAsync(button, async () =>
                {
                    if (tweak.Confirm is not null &&
                        !await Ui.ConfirmAsync(XamlRoot, tweak.Title + "?", tweak.Confirm, "Continue", destructive: true))
                        throw new OperationCanceledException("You cancelled, so nothing was changed.");

                    var problems = await Task.Run(() => AdvancedTweaks.Apply(tweak));
                    if (problems.Length > 0)
                        throw new InvalidOperationException("Some parts did not apply:\n" + problems);
                    return ("Done", tweak.Title + Followup(tweak));
                });
                break;
        }

        string description = tweak.Description + (tweak.NeedsAdmin ? " Needs admin." : "");
        return Row(title, description, tweak.Caution, button);
    }

    private static string Followup(AdvancedTweak tweak) =>
        tweak.RestartNeeded ? ". Restart the PC to finish."
        : tweak.SignOutNeeded ? ". Sign out and back in to see every change."
        : ".";

    private async Task RunAsync(Button button, Func<Task<(string Title, string Message)>> action)
    {
        button.IsEnabled = false;
        try
        {
            var (title, message) = await action();
            _status.Show(InfoBarSeverity.Success, title, message);
        }
        catch (OperationCanceledException ex)
        {
            _status.Show(InfoBarSeverity.Informational, "Nothing was changed", ex.Message);
        }
        catch (Exception ex)
        {
            _status.Show(InfoBarSeverity.Error, "That did not work", ex.Message);
        }
        await LoadTweaksAsync();
    }

    // ---------------------------------------------------------------- Cleaner

    private async Task ScanCleanerAsync()
    {
        _cleaner.Children.Clear();
        var ring = new ProgressRing { IsActive = true, Width = 32, Height = 32, HorizontalAlignment = HorizontalAlignment.Left };
        _cleaner.Children.Add(ring);

        List<CleanScan> scans;
        try
        {
            scans = await Task.Run(Cleaner.Scan);
        }
        catch (Exception ex)
        {
            _cleaner.Children.Clear();
            _cleaner.Children.Add(Ui.Secondary("Could not scan: " + ex.Message));
            return;
        }

        _cleaner.Children.Clear();
        var boxes = new List<(CheckBox Box, CleanScan Scan)>();
        var list = new StackPanel { Spacing = 4 };

        var summary = new TextBlock { Style = Ui.StyleRes("BodyStrongTextBlockStyle") };
        void UpdateSummary()
        {
            long total = boxes.Where(b => b.Box.IsChecked == true).Sum(b => b.Scan.Bytes);
            int count = boxes.Count(b => b.Box.IsChecked == true);
            summary.Text = count == 0 ? "Nothing selected" : $"{count} selected · at least {ShaderCache.Size(total)}";
        }

        foreach (var scan in scans)
        {
            var t = scan.Target;
            string size = t.SizeUnknown ? "Size unknown"
                : !scan.Readable && scan.Bytes == 0 ? "Size shown after cleaning"
                : ShaderCache.Size(scan.Bytes);

            var text = new StackPanel { Spacing = 2 };
            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.Children.Add(new TextBlock { Text = t.Title + (t.NeedsAdmin ? "  (admin)" : ""), FontWeight = FontWeights.SemiBold });
            var sizeText = Ui.Secondary(size);
            Grid.SetColumn(sizeText, 1);
            head.Children.Add(sizeText);
            text.Children.Add(head);
            text.Children.Add(Ui.Secondary(t.Description));

            bool worthIt = t.SizeUnknown || scan.Bytes > 0 || !scan.Readable;
            var box = new CheckBox
            {
                Content = text,
                IsChecked = t.Recommended && worthIt,
                IsEnabled = worthIt,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            box.Checked += (_, _) => UpdateSummary();
            box.Unchecked += (_, _) => UpdateSummary();
            boxes.Add((box, scan));
            list.Children.Add(Ui.CardFrame(box, new Thickness(16, 10, 20, 10)));
        }

        var clean = Ui.IconButton(Ui.IconBroom, "Clean selected", accent: true);
        var rescan = Ui.IconButton(Ui.IconRefresh, "Scan again");
        rescan.Click += async (_, _) => await ScanCleanerAsync();

        clean.Click += async (_, _) =>
        {
            var chosen = boxes.Where(b => b.Box.IsChecked == true).ToList();
            if (chosen.Count == 0) return;

            long total = chosen.Sum(c => c.Scan.Bytes);
            bool slow = chosen.Any(c => c.Scan.Target.Id == "dism");
            bool ok = await Ui.ConfirmAsync(XamlRoot, "Clean the selected items?",
                $"This deletes at least {ShaderCache.Size(total)}. Files that are in use are skipped." +
                (chosen.Any(c => c.Scan.Target.NeedsAdmin) ? " Windows asks for admin rights once." : "") +
                (slow ? " Old Windows components can take 5–15 minutes; ZenTune stays usable meanwhile." : ""),
                "Clean");
            if (!ok) return;

            clean.IsEnabled = false;
            rescan.IsEnabled = false;
            _status.Show(InfoBarSeverity.Informational, "Cleaning…", "This can take a moment.");
            try
            {
                long freed = await Task.Run(() => Cleaner.Clean(chosen.Select(c => c.Scan.Target).ToList()));
                _status.Show(InfoBarSeverity.Success, "Cleaned", $"Freed {ShaderCache.Size(freed)}.");
            }
            catch (OperationCanceledException ex)
            {
                _status.Show(InfoBarSeverity.Informational, "Nothing was cleaned", ex.Message);
            }
            catch (Exception ex)
            {
                _status.Show(InfoBarSeverity.Error, "Cleaning did not finish", ex.Message);
            }
            await ScanCleanerAsync();
        };

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(clean);
        actions.Children.Add(rescan);

        var footer = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        summary.VerticalAlignment = VerticalAlignment.Center;
        footer.Children.Add(summary);
        Grid.SetColumn(actions, 1);
        footer.Children.Add(actions);

        _cleaner.Children.Add(list);
        _cleaner.Children.Add(footer);
        UpdateSummary();
    }

    // ---------------------------------------------------------------- Tools

    private UIElement ShutUpRow()
    {
        var button = Ui.IconButton(Ui.IconSave, "Download and run");
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try
            {
                await ExternalTools.RunShutUp10Async();
                _status.Show(InfoBarSeverity.Success, "O&O ShutUp10++ started",
                    "Tip: Actions → Apply only recommended settings is a safe start. Make a restore point first.");
            }
            catch (Exception ex)
            {
                _status.Show(InfoBarSeverity.Error, "Could not start O&O ShutUp10++", ex.Message);
            }
            finally
            {
                button.IsEnabled = true;
            }
        };

        return Row("O&O ShutUp10++",
            "A free privacy tool with hundreds of switches for Windows. ZenTune downloads the newest version from O&O's own website and starts it, like WinUtil does.",
            "Made by O&O Software and not open source. Changes made in it are not in ZenTune's undo list; it has its own undo.",
            button);
    }

    // ---------------------------------------------------------------- Layout

    /// <summary>A collapsed section: title and one line of explanation, content opens on click.</summary>
    private static Expander Section(string title, string summary, UIElement content)
    {
        var header = new StackPanel { Spacing = 2, Padding = new Thickness(0, 6, 0, 6) };
        header.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
        header.Children.Add(Ui.Secondary(summary));
        return new Expander
        {
            Header = header,
            Content = content,
            IsExpanded = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
    }

    private static UIElement Row(string title, string detail, string? caution, Button button)
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(Ui.Secondary(detail));
        if (caution is not null)
        {
            text.Children.Add(new TextBlock
            {
                Text = caution,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Ui.Res("SystemFillColorCautionBrush"),
            });
        }
        grid.Children.Add(text);

        button.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(button, 1);
        grid.Children.Add(button);

        return Ui.CardFrame(grid, new Thickness(20, 14, 20, 14));
    }
}
