using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ZenTune.Core.Cloud;
using ZenTune.Core.Driver;
using ZenTune.Core.Saves;

namespace ZenTune.App.Pages;

internal sealed partial class AdvancedPage
{
    private readonly StackPanel _driverList = new() { Spacing = 4 };
    private readonly StackPanel _driverBackups = new() { Spacing = 8 };
    private readonly List<(CheckBox Box, InstalledDriver Driver)> _driverBoxes = [];
    private readonly InfoBar _driverStatus = new() { IsOpen = false, IsClosable = true };
    private Button? _driverCreate;
    private bool _driversBusy;

    private UIElement DriverBackupSection()
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(Ui.Secondary(
            "Saves the drivers Windows has installed from outside Microsoft (graphics, network, audio, chipset ...) to the backup folder of this PC, " +
            "next to your game saves. After a Windows reinstall you can install ZenTune and put them back. " +
            "Backing up and restoring each show one Windows admin prompt. " +
            "Tip: keep a copy of the backup folder on a USB drive too, because you may need a network driver before the cloud works."));
        panel.Children.Add(_driverStatus);

        var choose = Ui.IconButton(Ui.IconRefresh, "Choose drivers to back up");
        choose.Click += (_, _) => LoadInstalledDrivers();
        var open = Ui.IconButton(Ui.IconFolder, "Show backups");
        open.Click += (_, _) =>
        {
            Directory.CreateDirectory(DriverBackup.Root);
            Ui.OpenFolder(DriverBackup.Root);
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(choose);
        buttons.Children.Add(open);
        panel.Children.Add(buttons);
        panel.Children.Add(_driverList);

        // Download the driver backups of one PC from the cloud (for example after a Windows reinstall)
        var pcs = new ComboBox { MinWidth = 220, PlaceholderText = "Choose the PC", IsEnabled = false };
        var look = Ui.IconButton(Ui.IconRefresh, "Find PCs in the cloud");
        var get = Ui.IconButton(Ui.IconSave, "Download driver backups");
        get.IsEnabled = false;
        pcs.SelectionChanged += (_, _) => get.IsEnabled = pcs.SelectedItem is string;

        look.Click += async (_, _) =>
        {
            look.IsEnabled = false;
            _driverStatus.Show(InfoBarSeverity.Informational, "Looking in the cloud…", "Using the account chosen on the Cloud page.");
            try
            {
                var names = await CloudSync.ListPcsAsync();
                pcs.Items.Clear();
                foreach (var n in names) pcs.Items.Add(n);
                pcs.IsEnabled = names.Count > 0;
                if (names.Count == 0)
                {
                    _driverStatus.Show(InfoBarSeverity.Warning, "Nothing found", "The cloud account has no ZenTune backups yet.");
                }
                else
                {
                    pcs.SelectedItem = names.FirstOrDefault(n => string.Equals(n, BackupLocation.PcName, StringComparison.OrdinalIgnoreCase)) ?? names[0];
                    _driverStatus.Show(InfoBarSeverity.Success, "PCs found", $"{names.Count} PC(s) have backups. Choose the one you want drivers from.");
                }
            }
            catch (Exception ex) { _driverStatus.Show(InfoBarSeverity.Error, "Could not look in the cloud", ex.Message); }
            finally { look.IsEnabled = true; }
        };

        get.Click += async (_, _) =>
        {
            if (pcs.SelectedItem is not string pc) return;
            get.IsEnabled = false;
            _driverStatus.Show(InfoBarSeverity.Informational, "Downloading…", $"Driver backups from {pc}. This can take a while.");
            try
            {
                var msg = await CloudSync.DownloadDriversAsync(pc);
                RefreshDriverBackups();
                _driverStatus.Show(InfoBarSeverity.Success, "Done", msg + ". They are in the list below.");
            }
            catch (Exception ex) { _driverStatus.Show(InfoBarSeverity.Error, "Download failed", ex.Message); }
            finally { get.IsEnabled = pcs.SelectedItem is string; }
        };

        panel.Children.Add(new TextBlock { Text = "Get driver backups from the cloud", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
        panel.Children.Add(Ui.Secondary("For a fresh Windows: connect your cloud account on the Cloud page first, then choose the PC name your backups were made on."));
        var cloudRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        cloudRow.Children.Add(look);
        cloudRow.Children.Add(pcs);
        cloudRow.Children.Add(get);
        panel.Children.Add(cloudRow);

        var cloud = new CheckBox { Content = "Include driver backups when uploading to the cloud (can be several GB)" };
        try { cloud.IsChecked = CloudSync.Load().IncludeDrivers; } catch { cloud.IsChecked = true; }
        cloud.Click += (_, _) =>
        {
            try
            {
                var s = CloudSync.Load();
                s.IncludeDrivers = cloud.IsChecked == true;
                CloudSync.Save(s);
            }
            catch (Exception ex) { _driverStatus.Show(InfoBarSeverity.Error, "Could not save the setting", ex.Message); }
        };
        panel.Children.Add(cloud);

        panel.Children.Add(new TextBlock { Text = "Driver backups", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
        panel.Children.Add(_driverBackups);
        RefreshDriverBackups();
        return panel;
    }

    private void LoadInstalledDrivers()
    {
        try
        {
            var all = DriverBackup.ListInstalled();
            _driverList.Children.Clear();
            _driverBoxes.Clear();

            var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var allBtn = new Button { Content = "Select all" };
            var noneBtn = new Button { Content = "Select none" };
            allBtn.Click += (_, _) => { foreach (var (b, _) in _driverBoxes) b.IsChecked = true; };
            noneBtn.Click += (_, _) => { foreach (var (b, _) in _driverBoxes) b.IsChecked = false; };
            top.Children.Add(allBtn);
            top.Children.Add(noneBtn);
            _driverList.Children.Add(top);

            string? cls = null;
            foreach (var d in all)
            {
                if (!string.Equals(d.ClassName, cls, StringComparison.OrdinalIgnoreCase))
                {
                    cls = d.ClassName;
                    _driverList.Children.Add(new TextBlock { Text = cls, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
                }
                string text = $"{d.Provider} · {d.Original}" + (d.Version.Length > 0 ? $" · {d.Version}" : "");
                var box = new CheckBox { Content = text, IsChecked = true };
                _driverBoxes.Add((box, d));
                _driverList.Children.Add(box);
            }

            _driverCreate = Ui.IconButton(Ui.IconSave, "Back up selected drivers", accent: true);
            _driverCreate.Margin = new Thickness(0, 8, 0, 0);
            _driverCreate.Click += async (_, _) => await CreateDriverBackupAsync();
            _driverList.Children.Add(_driverCreate);
        }
        catch (Exception ex)
        {
            _driverStatus.Show(InfoBarSeverity.Error, "Could not list the drivers", ex.Message);
        }
    }

    private async Task CreateDriverBackupAsync()
    {
        if (_driversBusy) return;
        var chosen = _driverBoxes.Where(x => x.Box.IsChecked == true).Select(x => x.Driver).ToList();
        if (chosen.Count == 0)
        {
            _driverStatus.Show(InfoBarSeverity.Warning, "Nothing chosen", "Tick at least one driver.");
            return;
        }

        _driversBusy = true;
        if (_driverCreate is not null) _driverCreate.IsEnabled = false;
        _driverStatus.Show(InfoBarSeverity.Informational, "Backing up drivers…", "Allow the Windows prompt. This can take a minute.");
        try
        {
            var label = $"{chosen.Count} drivers";
            var (backup, failed) = await DriverBackup.BackupAsync(chosen, label);
            string msg = $"{backup.Drivers.Count} drivers saved ({SaveBackups.Size(backup.TotalBytes)}).";
            if (failed > 0) msg += $" {failed} could not be exported by Windows and were left out.";
            _driverStatus.Show(failed > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success, "Driver backup done", msg);
            CloudSync.AutoUploadInBackground();
            RefreshDriverBackups();
        }
        catch (Exception ex)
        {
            _driverStatus.Show(InfoBarSeverity.Error, "Driver backup failed", ex.Message);
        }
        finally
        {
            _driversBusy = false;
            if (_driverCreate is not null) _driverCreate.IsEnabled = true;
        }
    }

    private void RefreshDriverBackups()
    {
        _driverBackups.Children.Clear();
        List<DriverBackupInfo> list;
        try { list = DriverBackup.ListBackups(); }
        catch (Exception ex)
        {
            _driverBackups.Children.Add(Ui.Secondary("Could not read the backups: " + ex.Message));
            return;
        }

        if (list.Count == 0)
        {
            _driverBackups.Children.Add(Ui.Secondary("No driver backups yet."));
            return;
        }

        foreach (var b in list)
        {
            var grid = new Grid { ColumnSpacing = 12 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new StackPanel { Spacing = 2 };
            text.Children.Add(new TextBlock
            {
                Text = $"{b.Created:d MMM yyyy HH:mm} · {b.Drivers.Count} drivers · {SaveBackups.Size(b.TotalBytes)}",
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            });
            text.Children.Add(Ui.Secondary(b.FromOtherPc ? $"From another PC: {b.Pc}" : $"This PC ({b.Pc})"));
            grid.Children.Add(text);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var restore = Ui.IconButton(Ui.IconUndo, "Restore");
            restore.Click += async (_, _) => await RestoreDriversAsync(b);
            var delete = Ui.IconButton(Ui.IconDelete, "Delete");
            delete.Click += async (_, _) => await DeleteDriverBackupAsync(b);
            actions.Children.Add(restore);
            actions.Children.Add(delete);
            Grid.SetColumn(actions, 1);
            grid.Children.Add(actions);

            _driverBackups.Children.Add(Ui.CardFrame(grid, new Thickness(16, 10, 16, 10)));
        }
    }

    private async Task RestoreDriversAsync(DriverBackupInfo b)
    {
        if (_driversBusy) return;
        string warn = b.FromOtherPc
            ? "This backup is from another PC. Drivers made for other hardware are added to Windows but do nothing, which is harmless. "
            : "";
        if (!await Ui.ConfirmAsync(XamlRoot, "Restore these drivers?",
                warn + $"{b.Drivers.Count} drivers are added to Windows and installed on matching devices. " +
                "Windows keeps a better matching (usually newer) driver that is already in use. Create a restore point first if you are unsure.",
                "Restore"))
            return;

        _driversBusy = true;
        _driverStatus.Show(InfoBarSeverity.Informational, "Restoring drivers…", "Allow the Windows prompt. This can take a few minutes.");
        try
        {
            var (ok, failed, restart) = await DriverBackup.RestoreAsync(b, b.Drivers);
            string msg = $"{ok} drivers added.";
            if (failed > 0) msg += $" {failed} failed.";
            if (restart) msg += " Restart Windows to finish.";
            _driverStatus.Show(failed > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success, "Restore done", msg);
        }
        catch (Exception ex)
        {
            _driverStatus.Show(InfoBarSeverity.Error, "Restore failed", ex.Message);
        }
        finally { _driversBusy = false; }
    }

    private async Task DeleteDriverBackupAsync(DriverBackupInfo b)
    {
        if (!await Ui.ConfirmAsync(XamlRoot, "Delete this driver backup?",
                "The files are removed from this PC's backup folder. Copies already in the cloud stay there.", "Delete", destructive: true))
            return;
        try
        {
            await Task.Run(() => DriverBackup.Delete(b));
            RefreshDriverBackups();
        }
        catch (Exception ex)
        {
            _driverStatus.Show(InfoBarSeverity.Error, "Could not delete", ex.Message);
        }
    }
}
