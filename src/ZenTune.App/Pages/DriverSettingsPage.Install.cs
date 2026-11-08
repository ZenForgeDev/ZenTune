using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ZenTune.Core.Driver;

namespace ZenTune.App.Pages;

/// <summary>Install the downloaded NVIDIA driver with fewer parts, and turn NVIDIA's telemetry tasks off.</summary>
internal sealed partial class DriverSettingsPage
{
    // ---------------------------------------------------------------- Install with fewer parts

    private UIElement BuildInstallPanel(string installerPath)
    {
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(Ui.Secondary(
            "ZenTune unpacks NVIDIA's installer with 7-Zip, removes the parts you don't want and runs NVIDIA's own setup silently. " +
            "Always left out: the NVIDIA app, GeForce Experience, ShadowPlay and the telemetry part."));

        var audio = new CheckBox { Content = "HDMI / DisplayPort audio driver", IsChecked = true };
        var physx = new CheckBox { Content = "PhysX (used by some games)", IsChecked = true };
        var clean = new CheckBox { Content = "Clean installation (resets the driver's own settings, also the ones ZenTune wrote)", IsChecked = false };
        stack.Children.Add(audio);
        stack.Children.Add(physx);
        stack.Children.Add(clean);

        var go = Ui.IconButton(Ui.IconCheck, "Unpack and install", accent: true);
        go.HorizontalAlignment = HorizontalAlignment.Left;
        var info = new StackPanel { Spacing = 8 };
        stack.Children.Add(go);
        stack.Children.Add(info);

        go.Click += async (_, _) =>
        {
            go.IsEnabled = false;
            try
            {
                await InstallAsync(installerPath,
                    new InstallOptions(audio.IsChecked == true, physx.IsChecked == true, clean.IsChecked == true), info);
            }
            finally
            {
                go.IsEnabled = true;
            }
        };

        return stack;
    }

    private async Task InstallAsync(string installerPath, InstallOptions options, StackPanel info)
    {
        info.Children.Clear();

        void Message(InfoBarSeverity severity, string title, string message) =>
            info.Children.Add(new InfoBar { Severity = severity, Title = title, Message = message, IsOpen = true, IsClosable = false });

        try
        {
            string? sevenZip = NvidiaInstaller.FindSevenZip();
            if (sevenZip is null)
            {
                Message(InfoBarSeverity.Warning, "7-Zip is needed to unpack NVIDIA's installer",
                    "ZenTune did not find 7-Zip. It can install it for you with winget (free, and winget checks the download), or you can install it from 7-zip.org.");

                var getIt = Ui.IconButton(Ui.IconSave, "Install 7-Zip with winget");
                getIt.HorizontalAlignment = HorizontalAlignment.Left;
                getIt.Click += async (_, _) =>
                {
                    getIt.IsEnabled = false;
                    try
                    {
                        if (await NvidiaInstaller.InstallSevenZipAsync())
                        {
                            info.Children.Clear();
                            Message(InfoBarSeverity.Success, "7-Zip is installed", "Press Unpack and install again.");
                        }
                        else
                        {
                            _status.Show(InfoBarSeverity.Error, "7-Zip was not found after the install", "Install it from 7-zip.org and try again.");
                            getIt.IsEnabled = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        _status.Show(InfoBarSeverity.Error, "Could not install 7-Zip", ex.Message);
                        getIt.IsEnabled = true;
                    }
                };
                info.Children.Add(getIt);
                return;
            }

            Message(InfoBarSeverity.Informational, "Unpacking the installer ...", "This takes a minute. Nothing is installed yet.");
            string folder = await NvidiaInstaller.ExtractAsync(installerPath, sevenZip);
            var plan = NvidiaInstaller.Plan(folder, options);

            info.Children.Clear();
            bool ok = await Ui.ConfirmAsync(XamlRoot, "Install this driver now?",
                $"Will be installed: {string.Join(", ", plan.Keep)}\n\n" +
                $"Left out: {(plan.Remove.Count > 0 ? string.Join(", ", plan.Remove) : "nothing found to leave out")}\n\n" +
                "NVIDIA's setup runs silently and asks for administrator rights. It takes a few minutes, the screens may flicker or go black, " +
                "and Windows may ask you to restart afterwards. Save your work first.",
                "Install");
            if (!ok)
            {
                Message(InfoBarSeverity.Informational, "Cancelled", "Nothing was installed.");
                return;
            }

            NvidiaInstaller.Prune(plan);
            Message(InfoBarSeverity.Informational, "Installing ...", "NVIDIA's setup is running. Wait until this message changes.");
            int code = await NvidiaInstaller.RunSetupAsync(folder, options);

            info.Children.Clear();
            if (code == 0)
                Message(InfoBarSeverity.Success, "Driver installed", "NVIDIA's setup finished. Restart Windows if anything looks wrong.");
            else if (code == 1)
                Message(InfoBarSeverity.Success, "Driver installed, restart needed", "Restart Windows to finish.");
            else
                Message(InfoBarSeverity.Error, "NVIDIA's setup did not finish",
                    $"Setup returned code {code}. The driver you had before should still be in use. If you try again, leave the options as they are and send me the code.");
        }
        catch (Exception ex)
        {
            info.Children.Clear();
            Message(InfoBarSeverity.Error, "The install did not finish", ex.Message);
        }
    }

    // ---------------------------------------------------------------- Telemetry

    private UIElement BuildTelemetryPanel()
    {
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new TextBlock { Text = "Telemetry", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        stack.Children.Add(Ui.Secondary(
            "NVIDIA's driver adds scheduled tasks (named NvTm...) that report crashes and usage. ZenTune can turn them off and on again. " +
            "Part of the reporting is built into NVIDIA's display container, which can't be switched off from the outside, so this lowers it but does not remove it. " +
            "New drivers may turn the tasks on again."));

        var look = Ui.IconButton(Ui.IconRefresh, "Look for telemetry tasks");
        look.HorizontalAlignment = HorizontalAlignment.Left;
        var list = new StackPanel { Spacing = 8 };
        stack.Children.Add(look);
        stack.Children.Add(list);

        async Task LookAsync()
        {
            list.Children.Clear();
            look.IsEnabled = false;
            try
            {
                var tasks = await Task.Run(NvidiaTelemetry.FindTasks);
                if (tasks.Count == 0)
                {
                    list.Children.Add(Ui.Secondary("No NVIDIA telemetry tasks were found. Newer drivers often have none."));
                    return;
                }

                list.Children.Add(Ui.Rows(tasks.Select(t => (t.Name.TrimStart('\\'), t.Disabled ? "Off" : "On")).ToList()));

                var on = tasks.Where(t => !t.Disabled).Select(t => t.Name).ToList();
                if (on.Count == 0) return;

                var off = Ui.IconButton(Ui.IconCheck, $"Turn off {on.Count} task(s)", accent: true);
                off.HorizontalAlignment = HorizontalAlignment.Left;
                off.Click += async (_, _) =>
                {
                    off.IsEnabled = false;
                    try
                    {
                        await Task.Run(() => NvidiaTelemetry.Disable(on));
                        _status.Show(InfoBarSeverity.Success, "Telemetry tasks turned off",
                            "You can turn them on again on the System check page (undo list).");
                        await LookAsync();
                    }
                    catch (Exception ex)
                    {
                        _status.Show(InfoBarSeverity.Error, "Could not turn the tasks off", ex.Message);
                        off.IsEnabled = true;
                    }
                };
                list.Children.Add(off);
            }
            catch (Exception ex)
            {
                list.Children.Add(Ui.Secondary($"Could not read the scheduled tasks: {ex.Message}"));
            }
            finally
            {
                look.IsEnabled = true;
            }
        }

        look.Click += async (_, _) => await LookAsync();
        return stack;
    }
}
