using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ZenTune.Core;
using ZenTune.Core.Cloud;
using ZenTune.Core.Saves;

namespace ZenTune.App.Pages;

/// <summary>
/// "Saves": back up and restore the save games of any game. Backups go to ZenTune-backup\&lt;PC name&gt;\Saves in Documents, or in a folder the user picks.
/// Where the saves are comes from the Ludusavi database (downloaded once), plus games the user adds by hand.
/// </summary>
internal sealed partial class SavesPage : UserControl
{
    private readonly InfoBar _status;
    private readonly TextBlock _dbInfo;
    private readonly TextBlock _pathInfo;
    private readonly ToggleSwitch _autoBackup;
    private readonly TextBlock _autoInfo;
    private readonly ToggleSwitch _scheduleToggle;
    private readonly ComboBox _scheduleEvery;
    private readonly Button _runNow;
    private readonly ToggleSwitch _notifyToggle;
    private readonly TextBlock _scheduleInfo;
    private readonly StackPanel _legacyHost = new() { Spacing = 8 };
    private readonly TextBox _search;
    private readonly StackPanel _list = new() { Spacing = 10 };
    private readonly Button _scan;
    private readonly Button _update;
    private readonly Button _backupAll;

    private List<DetectedSave> _found = [];
    private Dictionary<string, List<SaveBackup>> _backups = new(StringComparer.OrdinalIgnoreCase);
    private bool _started;
    private bool _busy;

    public SavesPage()
    {
        _scan = Ui.IconButton(Ui.IconRefresh, "Scan again");
        _scan.Click += async (_, _) => await ScanAsync(forceUpdate: false);

        _update = Ui.IconButton(Ui.IconRefresh, "Update game list");
        _update.Click += async (_, _) => await ScanAsync(forceUpdate: true);

        _backupAll = Ui.IconButton(Ui.IconSave, "Back up all", accent: true);
        _backupAll.Click += async (_, _) => await BackUpAllAsync();

        var (root, body, status) = Ui.Page("Saves",
            "Back up the save games of any game. Backups are kept in a ZenTune-backup folder with one folder per PC, and Restore puts them back where they came from.",
            _update, _scan, _backupAll);
        _status = status;

        _dbInfo = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Ui.Res("TextFillColorSecondaryBrush") };
        _pathInfo = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Ui.Res("TextFillColorSecondaryBrush") };

        var openFolder = Ui.IconButton(Ui.IconFolder, "Open backup folder");
        openFolder.Click += (_, _) => Ui.OpenFolder(SaveBackups.Root);

        var add = Ui.IconButton(Ui.IconGame, "Add a game");
        add.Click += async (_, _) => await AddGameAsync();

        var chooseFolder = Ui.IconButton(Ui.IconFolder, "Backup folder");
        chooseFolder.Click += async (_, _) => await ChooseFolderAsync();

        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        tools.Children.Add(openFolder);
        tools.Children.Add(chooseFolder);
        tools.Children.Add(add);

        var infoStack = new StackPanel { Spacing = 12 };
        infoStack.Children.Add(_dbInfo);
        infoStack.Children.Add(_pathInfo);
        infoStack.Children.Add(Ui.Secondary(
            "ZenTune only looks at whether save folders exist and copies the files. It never touches the game itself, so it is safe with anti-cheat. " +
            "Close a game before you restore its save. Steam Cloud and other cloud saves can overwrite a restored save the next time the game starts."));
        infoStack.Children.Add(tools);

        var autoStack = new StackPanel { Spacing = 12 };

        _autoBackup = new ToggleSwitch
        {
            Header = "Back up a game's saves automatically when it closes",
            OnContent = "On",
            OffContent = "Off",
            IsOn = AutoBackup.Enabled,
        };
        _autoBackup.Toggled += (_, _) => AutoBackupToggled();
        _autoInfo = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Ui.Res("TextFillColorSecondaryBrush") };
        autoStack.Children.Add(_autoBackup);
        autoStack.Children.Add(Ui.Secondary(
            "Off by default. ZenTune only looks at the list of running programs (like Task Manager) to see when a game stops, and never touches the game. " +
            "A few seconds after it closes, the saves are copied if they changed. It works for games found in Steam or Epic, and only while ZenTune is running " +
            "(turn on \"Start ZenTune with Windows\" under Advanced to keep it in the tray). The 15 newest automatic backups per game are kept, and your own backups are never deleted."));
        autoStack.Children.Add(_autoInfo);
        UpdateAutoInfo();

        _scheduleToggle = new ToggleSwitch
        {
            Header = "Back up all games on a schedule",
            OnContent = "On",
            OffContent = "Off",
            IsOn = ScheduledBackup.Enabled,
        };
        _scheduleEvery = new ComboBox { MinWidth = 160 };
        _scheduleEvery.Items.Add("Every day");
        _scheduleEvery.Items.Add("Every week");
        _scheduleEvery.SelectedIndex = ScheduledBackup.EveryDays == 7 ? 1 : 0;
        _scheduleToggle.Toggled += (_, _) => ScheduleChanged();
        _scheduleEvery.SelectionChanged += (_, _) => ScheduleChanged();
        _scheduleInfo = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Ui.Res("TextFillColorSecondaryBrush") };
        _runNow = Ui.IconButton(Ui.IconSave, "Run backup now");
        _runNow.Click += async (_, _) => await RunBackupNowAsync();

        var scheduleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        scheduleRow.Children.Add(_scheduleEvery);
        scheduleRow.Children.Add(_runNow);
        autoStack.Children.Add(_scheduleToggle);
        autoStack.Children.Add(scheduleRow);
        autoStack.Children.Add(Ui.Secondary(
            "Off by default. It backs up every game whose saves changed since its last backup, and only runs while ZenTune is running. " +
            "These count as automatic backups (the 15 newest per game are kept)."));
        autoStack.Children.Add(_scheduleInfo);
        UpdateScheduleInfo();

        _notifyToggle = new ToggleSwitch
        {
            Header = "Show a Windows notification when ZenTune backs up by itself",
            OnContent = "On",
            OffContent = "Off",
            IsOn = BackupNotifier.Enabled,
        };
        _notifyToggle.Toggled += (_, _) => BackupNotifier.SetEnabled(_notifyToggle.IsOn);
        autoStack.Children.Add(_notifyToggle);
        autoStack.Children.Add(Ui.Secondary(
            "Off by default. Applies to automatic backups after a game closes and to scheduled backups, not to backups you start yourself. " +
            "It needs the ZenTune tray icon (next to the clock). If Windows is set to Do Not Disturb, it may not show."));

        // Keeps the status line fresh while this page is open
        var autoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        autoTimer.Tick += (_, _) => { UpdateAutoInfo(); UpdateScheduleInfo(); };
        Loaded += (_, _) => { UpdateAutoInfo(); UpdateScheduleInfo(); autoTimer.Start(); };
        Unloaded += (_, _) => autoTimer.Stop();
        body.Children.Add(Ui.CardFrame(infoStack));
        body.Children.Add(AdvancedPage.Section("Automatic backups",
            "Back up when a game closes, on a schedule, and notifications. All off by default.", autoStack));
        body.Children.Add(_legacyHost);

        _search = new TextBox { PlaceholderText = "Search games" };
        _search.TextChanged += (_, _) => Render();
        body.Children.Add(_search);

        body.Children.Add(_list);

        Content = root;
        Loaded += async (_, _) =>
        {
            if (_started) return;
            _started = true;
            await ScanAsync(forceUpdate: false);
        };
    }

    private void AutoBackupToggled()
    {
        try
        {
            AutoBackup.SetEnabled(_autoBackup.IsOn);
            UpdateAutoInfo();
        }
        catch (Exception ex)
        {
            _status.Show(InfoBarSeverity.Error, "Could not change automatic backup", ex.Message);
        }
    }

    private async Task RunBackupNowAsync()
    {
        if (_busy) return;
        _runNow.IsEnabled = false;
        _scheduleInfo.Text = "Backing up… this can take a while for many games.";
        _scheduleInfo.Visibility = Visibility.Visible;
        try
        {
            var result = await ScheduledBackup.RunNowAsync();
            _backups = await Task.Run(SaveBackups.ListAll);
            Render();
            _status.Show(InfoBarSeverity.Success, "Backup finished", result);
        }
        catch (Exception ex)
        {
            _status.Show(InfoBarSeverity.Error, "Could not back up", ex.Message);
        }
        finally
        {
            _runNow.IsEnabled = true;
            UpdateScheduleInfo();
        }
    }

    private void ScheduleChanged()
    {
        try
        {
            ScheduledBackup.Configure(_scheduleToggle.IsOn, _scheduleEvery.SelectedIndex == 1 ? 7 : 1);
            UpdateScheduleInfo();
        }
        catch (Exception ex)
        {
            _status.Show(InfoBarSeverity.Error, "Could not change the schedule", ex.Message);
        }
    }

    private void UpdateScheduleInfo()
    {
        var status = ScheduledBackup.Status();
        var last = ScheduledBackup.LastResult();
        var text = status;
        if (last.Length > 0) text += (text.Length > 0 ? "\n" : "") + "Last: " + last;
        _scheduleInfo.Text = text;
        _scheduleInfo.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateAutoInfo()
    {
        var status = AutoBackup.Status();
        var last = AutoBackup.LastResult();
        var text = status;
        if (last.Length > 0) text += (text.Length > 0 ? "\n" : "") + "Last: " + last;
        _autoInfo.Text = text;
        _autoInfo.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------------------------------------------------------- Scan

    private async Task ScanAsync(bool forceUpdate)
    {
        if (_busy) return;
        SetBusy(true);
        _list.Children.Clear();
        _list.Children.Add(Ui.Busy());

        try
        {
            var db = SaveManifest.LoadCached();

            if (db is null || forceUpdate)
            {
                try
                {
                    db = await SaveManifest.UpdateAsync();
                }
                catch (Exception ex)
                {
                    if (db is null)
                        throw new InvalidOperationException(
                            "ZenTune needs to download the list of save locations once (a few MB). Check your internet connection and press Update game list. " + ex.Message);

                    _status.Show(InfoBarSeverity.Warning, "Could not update the game list", "Using the list from last time. " + ex.Message);
                }
            }

            var custom = CustomSaves.Load();
            _found = await Task.Run(() => SaveScanner.Scan(db, custom));
            _backups = await Task.Run(SaveBackups.ListAll);
            UpdateBackupInfo();

            _dbInfo.Text = $"{db.Games.Count:N0} games in the save database (updated {Ui.When(db.Updated)}). " +
                           $"Found save files for {_found.Count} of them on this PC.";
            Render();
        }
        catch (Exception ex)
        {
            _list.Children.Clear();
            _status.Show(InfoBarSeverity.Error, "Could not look for saves", ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _scan.IsEnabled = !busy;
        _update.IsEnabled = !busy;
        _backupAll.IsEnabled = !busy;
    }

    // ---------------------------------------------------------------- List

    private void Render()
    {
        _list.Children.Clear();

        // Games with saves on this PC, plus games that only have backups left (for example after an uninstall)
        var games = new SortedDictionary<string, (DetectedSave? Found, List<SaveBackup> Backups)>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in _found)
            games[f.Name] = (f, _backups.TryGetValue(f.Name, out var b) ? b : []);
        foreach (var (name, list) in _backups)
            if (!games.ContainsKey(name)) games[name] = (null, list);

        var filter = _search.Text?.Trim() ?? "";
        var shown = games
            .Where(g => filter.Length == 0 || g.Key.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (shown.Count == 0)
        {
            _list.Children.Add(Ui.Secondary(games.Count == 0
                ? "No save files found yet. If a game is missing, press Add a game and choose its save folder."
                : "No games match your search."));
            return;
        }

        foreach (var (name, entry) in shown)
            _list.Children.Add(GameCard(name, entry.Found, entry.Backups));
    }

    private Border GameCard(string name, DetectedSave? found, List<SaveBackup> backups)
    {
        var stack = new StackPanel { Spacing = 10 };

        var top = new Grid { ColumnSpacing = 12 };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var info = new StackPanel { Spacing = 2 };
        info.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        info.Children.Add(Ui.Secondary(Describe(found, backups)));
        top.Children.Add(info);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(buttons, 1);
        top.Children.Add(buttons);
        stack.Children.Add(top);

        var backupList = new StackPanel { Spacing = 6, Visibility = Visibility.Collapsed };
        bool built = false;

        if (found is not null)
        {
            var backUp = Ui.IconButton(Ui.IconSave, "Back up");
            backUp.IsEnabled = !found.TooLarge;
            if (found.TooLarge) ToolTipService.SetToolTip(backUp, "Too big to back up here (more than 8 GB or 20,000 files).");
            backUp.Click += async (_, _) => await BackUpAsync(found);
            buttons.Children.Add(backUp);

            if (found.Custom)
            {
                var forget = Ui.IconButton(Ui.IconDelete, "Remove from list");
                forget.Click += async (_, _) =>
                {
                    CustomSaves.Remove(found.Name);
                    await ScanAsync(forceUpdate: false);
                };
                buttons.Children.Add(forget);
            }
        }

        if (backups.Count > 0)
        {
            var toggle = Ui.IconButton(Ui.IconHistory, $"Backups ({backups.Count})");
            toggle.Click += (_, _) =>
            {
                if (!built)
                {
                    foreach (var b in backups) backupList.Children.Add(BackupRow(b));
                    built = true;
                }
                backupList.Visibility = backupList.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            };
            buttons.Children.Add(toggle);
        }

        stack.Children.Add(backupList);
        return Ui.CardFrame(stack, new Thickness(20, 14, 20, 14));
    }

    private static string Describe(DetectedSave? found, List<SaveBackup> backups)
    {
        var parts = new List<string>();
        if (found is not null)
        {
            parts.Add($"{found.Files.Count} files");
            parts.Add(SaveScanner.Size(found.Bytes));
            parts.Add($"changed {Ui.When(found.Newest)}");
            if (found.Custom) parts.Add("added by you");
            if (found.TooLarge) parts.Add("too big to back up here");
        }
        else
        {
            parts.Add("not found on this PC right now");
        }

        if (backups.Count > 0)
            parts.Add($"last backup {Ui.When(backups[0].Created)}");
        else
            parts.Add("no backups yet");

        return string.Join(" · ", parts);
    }

    private Grid BackupRow(SaveBackup backup)
    {
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = string.IsNullOrWhiteSpace(backup.Label) ? "" : $" · {backup.Label}";
        var text = new TextBlock
        {
            Text = $"{Ui.When(backup.Created)}{label} · {backup.Files.Count} files · {SaveBackups.Size(backup.TotalBytes)}" +
                   (backup.Automatic ? " · automatic" : "") +
                   (backup.FromOtherPc ? $" · from {backup.Pc}" : ""),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(text);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        Grid.SetColumn(buttons, 1);

        var restore = Ui.IconButton(Ui.IconUndo, "Restore");
        restore.Click += async (_, _) => await RestoreAsync(backup);

        var show = Ui.IconButton(Ui.IconFolder, "Show");
        show.Click += (_, _) => Ui.OpenFolder(backup.Folder);

        var delete = Ui.IconButton(Ui.IconDelete, "Delete");
        delete.Click += async (_, _) => await DeleteAsync(backup);

        buttons.Children.Add(restore);
        buttons.Children.Add(show);
        buttons.Children.Add(delete);
        row.Children.Add(buttons);
        return row;
    }

    // ---------------------------------------------------------------- Actions

    private async Task BackUpAsync(DetectedSave save)
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            var backup = await Task.Run(() => SaveBackups.Create(save.Name, save.Files, "Manual backup"));
            _backups = await Task.Run(SaveBackups.ListAll);
            Render();

            CloudSync.AutoUploadInBackground();

            var skipped = backup.Skipped > 0 ? $" {backup.Skipped} files could not be copied (in use?)." : "";
            _status.Show(backup.Skipped > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success,
                $"{save.Name} backed up",
                $"{backup.Files.Count} files ({SaveBackups.Size(backup.TotalBytes)}) saved in {SaveBackups.Root}.{skipped}");
        }
        catch (Exception ex)
        {
            _status.Show(InfoBarSeverity.Error, $"Could not back up {save.Name}", ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task BackUpAllAsync()
    {
        if (_busy) return;

        var todo = _found.Where(f => !f.TooLarge).ToList();
        if (todo.Count == 0)
        {
            _status.Show(InfoBarSeverity.Informational, "Nothing to back up", "No save files were found.");
            return;
        }

        long bytes = todo.Sum(f => f.Bytes);
        if (!await Ui.ConfirmAsync(XamlRoot, "Back up all saves?",
                $"This copies the saves of {todo.Count} games ({SaveBackups.Size(bytes)}) to {SaveBackups.Root}. It can take a while.",
                "Back up all"))
            return;

        SetBusy(true);
        try
        {
            int done = 0, failed = 0;
            await Task.Run(() =>
            {
                foreach (var save in todo)
                {
                    try { SaveBackups.Create(save.Name, save.Files, "Backup of all games"); done++; }
                    catch { failed++; }
                }
            });

            _backups = await Task.Run(SaveBackups.ListAll);
            Render();
            if (done > 0) CloudSync.AutoUploadInBackground();
            _status.Show(failed > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success,
                "Backup finished",
                $"{done} games backed up" + (failed > 0 ? $", {failed} could not be backed up." : "."));
        }
        catch (Exception ex)
        {
            _status.Show(InfoBarSeverity.Error, "Could not back up", ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task RestoreAsync(SaveBackup backup)
    {
        if (_busy) return;

        var folders = backup.Files
            .Select(f => Path.GetDirectoryName(f.Original) ?? "")
            .Where(d => d.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var where = string.Join("\n", folders.Take(3)) + (folders.Count > 3 ? $"\n… and {folders.Count - 3} more folders" : "");

        var message =
            $"This puts {backup.Files.Count} files from the backup of {backup.GameName} ({Ui.When(backup.Created)}) back where they came from, " +
            $"and overwrites the save that is there now:\n\n{where}\n\n" +
            "Close the game first. ZenTune makes a safety backup of the current save before it restores, so you can undo this.";

        if (backup.FromOtherPc)
            message += $"\n\nThis backup was made on {backup.Pc}, not on this PC ({BackupLocation.PcName}). " +
                       "The files are written to the exact folders listed above, which may belong to another Windows user. Only continue if that is what you want.";

        if (!await Ui.ConfirmAsync(XamlRoot, "Are you sure you want to restore this save?", message, "Restore", destructive: true))
            return;

        SetBusy(true);
        try
        {
            var (restored, failed) = await Task.Run(() => SaveBackups.Restore(backup));
            _backups = await Task.Run(SaveBackups.ListAll);
            Render();

            if (failed == 0)
                _status.Show(InfoBarSeverity.Success, $"{backup.GameName} restored",
                    $"{restored} files were put back. A safety backup of the earlier save is in the list.");
            else
                _status.Show(InfoBarSeverity.Warning, $"{backup.GameName} partly restored",
                    $"{restored} files were put back and {failed} could not be written. Is the game still running?");
        }
        catch (Exception ex)
        {
            _status.Show(InfoBarSeverity.Error, $"Could not restore {backup.GameName}", ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task DeleteAsync(SaveBackup backup)
    {
        if (_busy) return;

        if (!await Ui.ConfirmAsync(XamlRoot, "Delete this backup?",
                $"The backup of {backup.GameName} from {Ui.When(backup.Created)} is removed for good. Your current save is not touched.",
                "Delete", destructive: true))
            return;

        try
        {
            await Task.Run(() => SaveBackups.Delete(backup));
            _backups = await Task.Run(SaveBackups.ListAll);
            Render();
            _status.Show(InfoBarSeverity.Success, "Backup deleted", $"{backup.GameName} · {Ui.When(backup.Created)}");
        }
        catch (Exception ex)
        {
            _status.Show(InfoBarSeverity.Error, "Could not delete the backup", ex.Message);
        }
    }

    // ---------------------------------------------------------------- Backup folder

    private void UpdateBackupInfo()
    {
        _pathInfo.Text = $"Backups made on this PC ({BackupLocation.PcName}) are kept in {SaveBackups.Root}. " +
                         "Backups from other PCs sit next to it in the same ZenTune-backup folder and are marked \"from\" the PC's name.";

        _legacyHost.Children.Clear();
        if (!BackupLocation.HasLegacyToOffer()) return;

        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(new TextBlock
        {
            Text = $"Backups from an older ZenTune version were found in {BackupLocation.LegacySaves}. " +
                   $"Copy them to {SaveBackups.Root}? The old ones are not deleted or changed.",
            TextWrapping = TextWrapping.Wrap,
        });

        var copy = Ui.IconButton(Ui.IconSave, "Copy them", accent: true);
        copy.Click += async (_, _) => await CopyLegacyAsync();

        var skip = new Button { Content = "Don't ask again" };
        skip.Click += (_, _) =>
        {
            BackupLocation.MarkLegacyHandled();
            _legacyHost.Children.Clear();
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(copy);
        buttons.Children.Add(skip);
        stack.Children.Add(buttons);

        _legacyHost.Children.Add(Ui.CardFrame(stack));
    }

    private async Task CopyLegacyAsync()
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            var legacy = BackupLocation.LegacySaves;
            var target = SaveBackups.Root;
            var (copied, skipped, failed) = await Task.Run(() => BackupLocation.CopyBackups(legacy, target));
            if (failed == 0) BackupLocation.MarkLegacyHandled();

            _backups = await Task.Run(SaveBackups.ListAll);
            UpdateBackupInfo();
            Render();

            if (failed == 0)
                _status.Show(InfoBarSeverity.Success, "Older backups copied",
                    $"{copied} backups were copied ({skipped} were already there). The old folder {legacy} was not touched. " +
                    "Delete it yourself once you have checked that everything is in the list.");
            else
                _status.Show(InfoBarSeverity.Warning, "Some backups were not copied",
                    $"{copied} copied, {failed} could not be copied (not enough space, or a file is in use?). Nothing was deleted. You can press Copy them again.");
        }
        catch (Exception ex)
        {
            _status.Show(InfoBarSeverity.Error, "Could not copy the older backups", ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>Lets the user pick where backups are kept (for example a OneDrive folder). The path is pasted, like in Add a game.</summary>
    private async Task ChooseFolderAsync()
    {
        if (_busy) return;

        var box = new TextBox
        {
            Header = "Backup folder",
            Text = BackupLocation.IsDefault ? "" : BackupLocation.Base,
            PlaceholderText = @"C:\Users\You\OneDrive\Backups",
        };
        var copy = new CheckBox
        {
            Content = "Copy my existing backups to the new folder (the old ones stay where they are)",
            IsChecked = true,
        };

        var form = new StackPanel { Spacing = 12, MinWidth = 440 };
        form.Children.Add(box);
        form.Children.Add(Ui.Secondary(
            "ZenTune makes a folder called ZenTune-backup inside it, with one folder per PC. " +
            "A folder inside OneDrive, Google Drive or Dropbox is uploaded by that app on its own. " +
            "Leave the box empty to go back to your Documents folder."));
        form.Children.Add(Ui.Secondary($"Right now: {SaveBackups.Root}"));
        form.Children.Add(copy);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Where should backups be kept?",
            Content = form,
            PrimaryButtonText = "Use this folder",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = ElementTheme.Dark,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var text = (box.Text ?? "").Trim().Trim('"');
        var oldSaves = BackupLocation.SavesRoot;

        try
        {
            if (text.Length == 0)
            {
                BackupLocation.SetBase(null);
            }
            else
            {
                var error = BackupLocation.Validate(text);
                if (error is not null)
                {
                    _status.Show(InfoBarSeverity.Warning, "Folder not changed", error);
                    return;
                }
                BackupLocation.SetBase(text);
            }

            var newSaves = BackupLocation.SavesRoot;
            string copyNote = "";
            SetBusy(true);
            try
            {
                if (copy.IsChecked == true)
                {
                    var (copied, skipped, failed) = await Task.Run(() => BackupLocation.CopyBackups(oldSaves, newSaves));
                    copyNote = failed == 0
                        ? $" {copied} existing backups were copied."
                        : $" {copied} existing backups were copied, {failed} could not be copied. The old ones are still in {oldSaves}.";
                }
            }
            finally
            {
                SetBusy(false);
            }

            _backups = await Task.Run(SaveBackups.ListAll);
            UpdateBackupInfo();
            Render();
            _status.Show(InfoBarSeverity.Success, "Backup folder changed", $"New backups go to {SaveBackups.Root}.{copyNote}");
        }
        catch (Exception ex)
        {
            _status.Show(InfoBarSeverity.Error, "Could not change the backup folder", ex.Message);
        }
    }

    /// <summary>For games the database does not know: the user gives a name and pastes the save folder.</summary>
    private async Task AddGameAsync()
    {
        if (_busy) return;

        var name = new TextBox { Header = "Game name", PlaceholderText = "For example: My Indie Game" };
        var path = new TextBox
        {
            Header = "Save folder (or file)",
            PlaceholderText = @"C:\Users\You\AppData\Local\MyIndieGame\Saves",
        };

        var form = new StackPanel { Spacing = 12, MinWidth = 420 };
        form.Children.Add(name);
        form.Children.Add(path);
        form.Children.Add(Ui.Secondary("Copy the folder path from the address bar in Explorer and paste it here."));

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Add a game",
            Content = form,
            PrimaryButtonText = "Add",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = ElementTheme.Dark,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var gameName = name.Text?.Trim() ?? "";
        var gamePath = (path.Text ?? "").Trim().Trim('"');

        if (gameName.Length == 0 || gamePath.Length == 0)
        {
            _status.Show(InfoBarSeverity.Warning, "Nothing added", "Both a name and a folder are needed.");
            return;
        }
        if (!Directory.Exists(gamePath) && !File.Exists(gamePath))
        {
            _status.Show(InfoBarSeverity.Warning, "Nothing added", "That folder does not exist.");
            return;
        }
        if (gamePath.TrimEnd('\\').Length <= 2)
        {
            _status.Show(InfoBarSeverity.Warning, "Nothing added", "Choose the game's own save folder, not a whole drive.");
            return;
        }

        CustomSaves.Add(gameName, gamePath);
        await ScanAsync(forceUpdate: false);
        _status.Show(InfoBarSeverity.Success, "Game added", $"{gameName} is in the list now.");
    }
}
