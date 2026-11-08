using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ZenTune.Core.Driver;
using ZenTune.Core.Games;

namespace ZenTune.App.Pages;

internal sealed partial class DriverSettingsPage
{
    /// <summary>
    /// Settings for one program only, kept in that program's own profile in the NVIDIA driver. The global settings above stay as they are;
    /// "Same as global" removes ZenTune's value from the game's profile.
    /// </summary>
    private UIElement BuildGameSection()
    {
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(Ui.Secondary(
            "Choose a game and its program (.exe) to give it settings of its own, for example 'Prefer maximum performance' only for the games that need it. " +
            "If NVIDIA already has a profile for the program, ZenTune writes into it; otherwise it makes a profile called \"ZenTune - game.exe\". " +
            "Settings NVIDIA ships in its own game profiles are not shown here and stay as they are until you change them. " +
            "Per-game settings are new and not tested on many games yet."));

        var gameBox = new ComboBox { MinWidth = 320, PlaceholderText = "Looking for your games ...", IsEnabled = false };
        var exeBox = new ComboBox { MinWidth = 320, PlaceholderText = "Choose the program", IsEnabled = false };
        var showAll = new CheckBox { Content = "Show all programs in the game folder", IsEnabled = false };
        var typed = new TextBox { MinWidth = 220, PlaceholderText = "or type a name, for example game.exe" };
        var useTyped = new Button { Content = "Use" };

        var panel = new StackPanel { Spacing = 8 };
        var games = new List<(string Name, string Dir)>();
        var programs = new List<ProgramEntry>();
        var shown = new List<ProgramEntry>();

        void FillPrograms()
        {
            exeBox.Items.Clear();
            shown.Clear();
            var list = showAll.IsChecked == true ? programs : MainPrograms(programs);
            foreach (var p in list)
            {
                shown.Add(p);
                exeBox.Items.Add(p.Label);
            }
            exeBox.IsEnabled = shown.Count > 0;
            exeBox.PlaceholderText = shown.Count > 0 ? "Choose the program" : "No programs found - type the name below";
        }
        showAll.Click += (_, _) => FillPrograms();

        stack.Children.Add(gameBox);
        stack.Children.Add(exeBox);
        stack.Children.Add(showAll);
        var manual = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        manual.Children.Add(typed);
        manual.Children.Add(useTyped);
        stack.Children.Add(manual);
        stack.Children.Add(panel);

        async Task ShowAsync(string exeText)
        {
            string exe;
            try { exe = NvidiaDrs.CleanExe(exeText); }
            catch (Exception ex)
            {
                _status.Show(InfoBarSeverity.Warning, "Not a program name", ex.Message);
                return;
            }

            _reloadGame = () => ShowAsync(exe);
            panel.Children.Clear();
            panel.Children.Add(Ui.Busy());

            try
            {
                var (values, exists) = await Task.Run(() =>
                {
                    var v = NvidiaDrs.ReadForApp(exe, NvidiaCatalog.All.Select(x => x.Id), out bool has);
                    return (v, has);
                });

                _populating = true;
                try
                {
                    panel.Children.Clear();
                    panel.Children.Add(new TextBlock
                    {
                        Text = exe,
                        FontWeight = FontWeights.SemiBold,
                    });
                    panel.Children.Add(Ui.Secondary(exists
                        ? "The driver has a profile that lists this program. Changes are saved in it."
                        : "The driver has no profile for this program yet. ZenTune makes one when you change something."));

                    string? group = null;
                    foreach (var setting in NvidiaCatalog.All)
                    {
                        if (setting.Group != group)
                        {
                            group = setting.Group;
                            panel.Children.Add(new TextBlock { Text = group, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 0) });
                        }
                        uint? current = values.TryGetValue(setting.Id, out var v) ? v : null;
                        panel.Children.Add(SettingRow(setting, current, exe));
                    }

                    var reset = Ui.IconButton(Ui.IconUndo, "Reset this game");
                    reset.IsEnabled = exists;
                    reset.Margin = new Thickness(0, 8, 0, 0);
                    reset.Click += async (_, _) =>
                    {
                        if (!await Ui.ConfirmAsync(XamlRoot, "Reset this game?",
                                $"This removes every value shown here from the driver's profile for {exe}, so it follows the global settings again.", "Reset"))
                            return;
                        try
                        {
                            var writes = NvidiaCatalog.AllIds().Distinct().Select(id => new DriverWrite(id, null)).ToList();
                            await Task.Run(() => NvidiaDrs.Apply($"NVIDIA ({exe}): reset all settings from ZenTune", writes, exe));
                            _status.Show(InfoBarSeverity.Success, "Game reset", $"{exe} follows the global settings again.");
                        }
                        catch (Exception ex)
                        {
                            _status.Show(InfoBarSeverity.Error, "Could not reset the game", ex.Message);
                        }
                        await ShowAsync(exe);
                    };
                    panel.Children.Add(reset);
                }
                finally { _populating = false; }
            }
            catch (Exception ex)
            {
                panel.Children.Clear();
                _status.Show(InfoBarSeverity.Error, "Could not read the game's profile", ex.Message);
            }
        }

        gameBox.SelectionChanged += (_, _) =>
        {
            exeBox.Items.Clear();
            programs.Clear();
            panel.Children.Clear();
            if (gameBox.SelectedIndex < 0 || gameBox.SelectedIndex >= games.Count) return;

            programs.AddRange(FindPrograms(games[gameBox.SelectedIndex].Dir));
            showAll.IsChecked = false;
            showAll.IsEnabled = programs.Count > 0;
            FillPrograms();
        };
        exeBox.SelectionChanged += async (_, _) =>
        {
            int i = exeBox.SelectedIndex;
            if (i >= 0 && i < shown.Count) await ShowAsync(shown[i].Name);
        };
        useTyped.Click += async (_, _) => await ShowAsync(typed.Text);
        typed.KeyDown += async (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter) await ShowAsync(typed.Text);
        };

        _ = FillGamesAsync();
        return stack;

        async Task FillGamesAsync()
        {
            try
            {
                _gameScan ??= Task.Run(GameScanner.Scan);
                var scan = await _gameScan;

                games.AddRange(scan.Supported.Select(g => (g.Name, g.App.InstallDir))
                    .Concat(scan.OtherGames.Select(a => (a.Name, a.InstallDir)))
                    .Where(g => !string.IsNullOrWhiteSpace(g.Item2))
                    .OrderBy(g => g.Item1, StringComparer.OrdinalIgnoreCase));

                foreach (var g in games) gameBox.Items.Add(g.Name);
                gameBox.IsEnabled = games.Count > 0;
                gameBox.PlaceholderText = games.Count > 0 ? "Choose a game" : "No games found - type a program name below";
            }
            catch
            {
                _gameScan = null;
                gameBox.PlaceholderText = "Could not look for games - type a program name below";
            }
        }
    }

    private sealed record ProgramEntry(string Name, long Bytes, bool Running)
    {
        public string Label => $"{Name}  ·  {Bytes / (1024 * 1024)} MB" + (Running ? "  ·  running now" : "");
    }

    /// <summary>
    /// The short list: programs that run right now first, then the biggest ones (the game itself is nearly always one of the biggest;
    /// launchers, crash reporters and helpers are small). Falls back to everything when nothing is big enough.
    /// </summary>
    private static List<ProgramEntry> MainPrograms(List<ProgramEntry> all)
    {
        var main = all.Where(p => p.Running)
            .Concat(all.Where(p => !p.Running && p.Bytes >= 30L * 1024 * 1024).OrderByDescending(p => p.Bytes).Take(10))
            .ToList();
        return main.Count > 0 ? main : all.OrderByDescending(p => p.Bytes).Take(15).ToList();
    }

    /// <summary>The .exe files in a game's folder (up to four levels down), without installers, crash reporters and anti-cheat helpers.</summary>
    private static List<ProgramEntry> FindPrograms(string folder)
    {
        string[] skip = ["crash", "unins", "setup", "redist", "vcredist", "vc_redist", "dxsetup", "prereq", "easyanticheat", "start_protected", "uninstall", "qtwebengine", "dotnet", "oalinst", "benchmark"];

        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var p in System.Diagnostics.Process.GetProcesses())
            {
                running.Add(p.ProcessName + ".exe");
                p.Dispose();
            }
        }
        catch { /* the list still works without it */ }

        var best = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!Directory.Exists(folder)) return [];
            var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true };
            foreach (var path in Directory.EnumerateFiles(folder, "*.exe", options).Take(400))
            {
                var name = Path.GetFileName(path);
                if (skip.Any(s => name.Contains(s, StringComparison.OrdinalIgnoreCase))) continue;
                long size = 0;
                try { size = new FileInfo(path).Length; } catch { /* size stays 0 */ }
                if (!best.TryGetValue(name, out var old) || size > old) best[name] = size;
            }
        }
        catch { /* best effort; the name can be typed */ }

        return best.Select(kv => new ProgramEntry(kv.Key, kv.Value, running.Contains(kv.Key)))
            .OrderByDescending(p => p.Running).ThenByDescending(p => p.Bytes).ToList();
    }
}
