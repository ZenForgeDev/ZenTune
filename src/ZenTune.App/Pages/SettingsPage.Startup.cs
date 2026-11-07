using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ZenTune.Core.Advanced;

namespace ZenTune.App.Pages;

/// <summary>"Start with Windows": start ZenTune at sign-in (quietly in the tray) and keep it in the tray when the window is closed.</summary>
internal sealed partial class SettingsPage
{
    private ToggleSwitch? _autoStart;
    private ToggleSwitch? _keepTray;
    private bool _loadingAuto = true;

    private UIElement AutoStartSection()
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(Ui.Secondary(
            "ZenTune can start a few seconds after you sign in to Windows. It then waits quietly in the tray (the icon next to the clock, maybe behind the small arrow) " +
            "until you open it. It uses a Task Scheduler task, which you can remove here again. Windows may ask for admin rights once."));

        _autoStart = new ToggleSwitch { Header = "Start ZenTune when I sign in to Windows", OnContent = "On", OffContent = "Off", IsEnabled = false };
        _autoStart.Toggled += async (_, _) => await AutoStartToggledAsync();
        panel.Children.Add(_autoStart);

        _keepTray = new ToggleSwitch
        {
            Header = "Keep ZenTune in the tray when I close the window",
            OnContent = "On",
            OffContent = "Off",
            IsOn = AppAutoStart.KeepInTray,
        };
        _keepTray.Toggled += (_, _) =>
        {
            if (_loadingAuto) return;
            try
            {
                AppAutoStart.KeepInTray = _keepTray.IsOn;
                _status.Show(InfoBarSeverity.Success, _keepTray.IsOn ? "ZenTune stays in the tray" : "ZenTune quits when you close the window",
                    _keepTray.IsOn
                        ? "The close button now hides ZenTune. To quit it, right-click the tray icon and choose Exit."
                        : "The close button quits ZenTune again.");
            }
            catch (Exception ex)
            {
                _status.Show(InfoBarSeverity.Error, "Could not save the setting", ex.Message);
            }
        };
        panel.Children.Add(_keepTray);

        _ = LoadAutoStartAsync();
        return panel;
    }

    private async Task LoadAutoStartAsync()
    {
        var on = await Task.Run(AppAutoStart.IsEnabled);
        _loadingAuto = true;
        _autoStart!.IsOn = on;
        _autoStart.IsEnabled = true;
        _loadingAuto = false;

        if (on)
        {
            // The task starts one specific copy of ZenTune.exe. Tell the user if that is not the copy that is running now.
            var target = await Task.Run(AppAutoStart.TaskTarget);
            var me = Environment.ProcessPath;
            if (target is not null && me is not null && !string.Equals(target, me, StringComparison.OrdinalIgnoreCase))
                _status.Show(InfoBarSeverity.Informational, "Autostart uses another copy of ZenTune",
                    $"It starts {target}. To use this copy instead, switch it off and on again.");
        }
    }

    private async Task AutoStartToggledAsync()
    {
        if (_loadingAuto || _autoStart is null) return;

        bool wanted = _autoStart.IsOn;
        _autoStart.IsEnabled = false;
        try
        {
            await Task.Run(() => { if (wanted) AppAutoStart.Enable(); else AppAutoStart.Disable(); });

            _status.Show(InfoBarSeverity.Success,
                wanted ? "ZenTune will start when you sign in" : "ZenTune no longer starts with Windows",
                wanted ? $"It starts about 15 seconds after sign-in, quietly in the tray ({Environment.ProcessPath})." : "The startup task was removed.");
        }
        catch (Exception ex)
        {
            // Put the switch back to what Windows really has
            _loadingAuto = true;
            _autoStart.IsOn = !wanted;
            _loadingAuto = false;

            _status.Show(ex is OperationCanceledException ? InfoBarSeverity.Warning : InfoBarSeverity.Error,
                "The startup setting was not changed", ex.Message);
        }
        finally
        {
            _autoStart.IsEnabled = true;
        }
    }
}
