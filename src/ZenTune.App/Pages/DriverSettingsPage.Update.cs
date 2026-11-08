using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ZenTune.Core.Driver;
using ZenTune.Core.Hardware;

namespace ZenTune.App.Pages;

/// <summary>The "NVIDIA driver" card: finds the right driver for this card, downloads it and checks its signature.</summary>
internal sealed partial class DriverSettingsPage
{
    private UIElement BuildDriverSection(GpuInfo gpu, bool isLaptop)
    {
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(new TextBlock
        {
            Text = $"{gpu.Name} · installed driver {(gpu.DriverVersion.Length > 0 ? gpu.DriverVersion : "unknown")}",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        stack.Children.Add(Ui.Secondary(
            "ZenTune only goes online when you press the button. It matches your card against NVIDIA's own list, keeps desktop and laptop drivers apart, " +
            "and offers a driver only if NVIDIA lists your exact card for it."));

        var kind = new ComboBox { MinWidth = 280, HorizontalAlignment = HorizontalAlignment.Left };
        kind.Items.Add("Game Ready driver (for games)");
        kind.Items.Add("Studio driver (for creative apps)");
        kind.SelectedIndex = 0;
        stack.Children.Add(kind);

        var check = Ui.IconButton(Ui.IconRefresh, "Check for new driver", accent: true);
        check.HorizontalAlignment = HorizontalAlignment.Left;
        stack.Children.Add(check);

        var result = new StackPanel { Spacing = 10 };
        stack.Children.Add(result);

        stack.Children.Add(BuildTelemetryPanel());

        check.Click += async (_, _) =>
        {
            check.IsEnabled = false;
            kind.IsEnabled = false;
            result.Children.Clear();
            result.Children.Add(Ui.Secondary("Asking NVIDIA ..."));
            try
            {
                // Read the installed driver again: the first scan is kept for the whole session and is old after an install
                var current = gpu;
                try
                {
                    var hw = await AppState.HardwareAsync(rescan: true);
                    current = hw.Gpus.FirstOrDefault(g => g.Vendor == GpuVendor.Nvidia && g.PnpId == gpu.PnpId)
                              ?? hw.Gpus.FirstOrDefault(g => g.Vendor == GpuVendor.Nvidia && g.Name == gpu.Name)
                              ?? gpu;
                }
                catch { /* use the earlier scan */ }

                var r = await NvidiaDriverUpdate.CheckAsync(current, isLaptop, kind.SelectedIndex == 1);
                ShowDriverResult(result, r);
            }
            catch (Exception ex)
            {
                result.Children.Clear();
                result.Children.Add(new InfoBar
                {
                    Severity = InfoBarSeverity.Error,
                    Title = "Could not check for a new driver",
                    Message = ex.Message,
                    IsOpen = true,
                    IsClosable = false,
                });
            }
            finally
            {
                check.IsEnabled = true;
                kind.IsEnabled = true;
            }
        };

        return stack;
    }

    private void ShowDriverResult(StackPanel host, NvidiaDriverCheck r)
    {
        host.Children.Clear();

        host.Children.Add(new InfoBar
        {
            Severity = !r.Ok ? InfoBarSeverity.Warning : r.UpdateAvailable ? InfoBarSeverity.Success : InfoBarSeverity.Informational,
            Title = !r.Ok ? "No driver offered" : r.UpdateAvailable ? "New driver found" : "You are up to date",
            Message = r.Message,
            IsOpen = true,
            IsClosable = false,
        });

        if (!r.Ok || r.Latest is not { } rel) return;

        host.Children.Add(Ui.Rows(
        [
            ("Your card", r.GpuName),
            ("Found in NVIDIA's list as", $"{r.MatchedName}{(rel.Kind.Length > 0 ? $" ({rel.Kind.ToLowerInvariant()} driver)" : "")}"),
            ("Support check", "NVIDIA lists your exact card for this driver"),
            ("Installed driver", r.Installed ?? "Unknown"),
            ("Newest driver", $"{rel.Version} · {rel.Name}"),
            ("Released", rel.Released.Length > 0 ? rel.Released : "Unknown"),
            ("Download size", rel.Size.Length > 0 ? rel.Size : "Unknown"),
        ]));

        var download = Ui.IconButton(Ui.IconSave, $"Download {rel.Version}", accent: r.UpdateAvailable);
        var bar = new ProgressBar { Minimum = 0, Maximum = 1, Visibility = Visibility.Collapsed };
        var after = new StackPanel { Spacing = 8 };

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(download);
        host.Children.Add(actions);
        host.Children.Add(bar);
        host.Children.Add(after);

        download.Click += async (_, _) =>
        {
            if (!await Ui.ConfirmAsync(XamlRoot, "Download the driver?",
                    $"{rel.Name} {rel.Version} is {(rel.Size.Length > 0 ? rel.Size : "a large download")}. It is saved in ZenTune's data folder, " +
                    "and its signature is checked before you can run it. Nothing is installed by this step.",
                    "Download"))
                return;

            download.IsEnabled = false;
            after.Children.Clear();
            bar.Value = 0;
            bar.Visibility = Visibility.Visible;
            var progress = new Progress<double>(v => bar.Value = v);

            try
            {
                string path = await NvidiaDriverUpdate.DownloadAsync(rel, progress);
                var (ok, detail) = await NvidiaDriverUpdate.VerifySignatureAsync(path);
                bar.Visibility = Visibility.Collapsed;

                if (!ok)
                {
                    after.Children.Add(new InfoBar
                    {
                        Severity = InfoBarSeverity.Error,
                        Title = "The download did not pass the signature check",
                        Message = detail + " ZenTune will not offer to run it. Delete the file and try again.",
                        IsOpen = true,
                        IsClosable = false,
                    });
                    Ui.OpenFolder(Path.GetDirectoryName(path)!);
                    return;
                }

                after.Children.Add(new InfoBar
                {
                    Severity = InfoBarSeverity.Success,
                    Title = $"Driver {rel.Version} downloaded and checked",
                    Message = $"{detail} Saved as {Path.GetFileName(path)}.",
                    IsOpen = true,
                    IsClosable = false,
                });

                var run = Ui.IconButton(Ui.IconGame, "Run installer", accent: true);
                var folder = Ui.IconButton(Ui.IconFolder, "Show in folder");
                folder.Click += (_, _) => Ui.OpenFolder(Path.GetDirectoryName(path)!);
                run.Click += async (_, _) => await RunInstallerAsync(path);

                var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                buttons.Children.Add(run);
                buttons.Children.Add(folder);
                after.Children.Add(buttons);

                after.Children.Add(AdvancedPage.Section(
                    "Install with fewer parts (new, not tested)",
                    "Leaves out the NVIDIA app, ShadowPlay and telemetry. Choose audio, PhysX and clean install.",
                    BuildInstallPanel(path)));
            }
            catch (Exception ex)
            {
                bar.Visibility = Visibility.Collapsed;
                after.Children.Add(new InfoBar
                {
                    Severity = InfoBarSeverity.Error,
                    Title = "Could not download the driver",
                    Message = ex.Message,
                    IsOpen = true,
                    IsClosable = false,
                });
            }
            finally
            {
                download.IsEnabled = true;
            }
        };
    }

    private async Task RunInstallerAsync(string path)
    {
        if (!await Ui.ConfirmAsync(XamlRoot, "Run NVIDIA's installer?",
                "NVIDIA's own installer opens and asks for administrator rights. Pick Custom install to leave out parts you don't need " +
                "(for example the NVIDIA app and telemetry), and tick Clean installation if you have had problems. " +
                "Your screens may go black for a few seconds, and Windows may ask you to restart.",
                "Run installer"))
            return;

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _status.Show(InfoBarSeverity.Error, "Could not start the installer", ex.Message);
        }
    }
}
