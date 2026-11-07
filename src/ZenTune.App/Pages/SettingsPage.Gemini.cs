using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ZenTune.Core.Ai;

namespace ZenTune.App.Pages;

/// <summary>"Gemini models": tests which models work for the user's key and lets them pick one from a list.</summary>
internal sealed partial class SettingsPage
{
    private readonly StackPanel _geminiResults = new() { Spacing = 6 };
    private CancellationTokenSource? _geminiCts;

    private UIElement GeminiSection()
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(Ui.Secondary(
            "The experimental AI best settings on the Games page ask Google Gemini. Google has many models and they change often, " +
            "so ZenTune can ask which ones your own key can use and test each with one tiny question. " +
            "Each test uses one small request from your quota. Speed comes from a single test and says nothing about quality."));

        // ---- Model picker
        bool filling = false;
        var picker = new ComboBox { Header = "Model used by AI best settings", MinWidth = 380 };

        void FillPicker()
        {
            filling = true;
            picker.Items.Clear();
            var current = AiConfig.Load().Model;
            var choices = GeminiModels.Choices(current);
            foreach (var c in choices) picker.Items.Add(new ComboBoxItem { Content = c.Label, Tag = c.Id });
            picker.SelectedIndex = Math.Max(0, choices.FindIndex(c => c.Id == current));
            filling = false;
        }

        picker.SelectionChanged += (_, _) =>
        {
            if (filling) return;
            if (picker.SelectedItem is ComboBoxItem { Tag: string id })
            {
                AiConfig.SaveModel(id);
                _status.Show(InfoBarSeverity.Success, "Model saved", $"AI best settings will use {id}.");
            }
        };
        FillPicker();

        // ---- Buttons
        var test = Ui.IconButton(Ui.IconRefresh, "Test all models", accent: true);
        var stop = new Button { Content = "Stop", Visibility = Visibility.Collapsed };
        var key = Ui.IconButton(Ui.IconInfo, "Gemini key");
        key.Click += async (_, _) =>
        {
            await GeminiDialogs.AskForKeyAsync(XamlRoot);
            FillPicker();
        };

        stop.Click += (_, _) => _geminiCts?.Cancel();

        test.Click += async (_, _) =>
        {
            if (_geminiCts is not null) return;

            var apiKey = AiConfig.Load().ApiKey;
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                if (!await GeminiDialogs.AskForKeyAsync(XamlRoot)) return;
                FillPicker();
                apiKey = AiConfig.Load().ApiKey;
                if (string.IsNullOrWhiteSpace(apiKey)) return;
            }

            _geminiCts = new CancellationTokenSource();
            var token = _geminiCts.Token;
            test.IsEnabled = false;
            picker.IsEnabled = false;
            stop.Visibility = Visibility.Visible;

            var live = new List<ModelResult>();
            RenderGeminiResults(live, () => { });
            var starting = new Progress<string>(m =>
                _status.Show(InfoBarSeverity.Informational, "Testing models…", $"Now asking {m}. Each model gets up to 45 seconds."));
            var progress = new Progress<ModelResult>(r =>
            {
                live.Add(r);
                RenderGeminiResults(live, () => { });
            });

            _status.Show(InfoBarSeverity.Informational, "Testing models…",
                "Each model gets one tiny question, one after the other. This can take a minute.");

            try
            {
                var keyText = apiKey!;
                var all = await Task.Run(() => GeminiModels.TestAllAsync(keyText, progress, token, starting));
                RenderGeminiResults(all, FillPicker);

                var best = GeminiModels.Best(all);
                if (best is null)
                {
                    _status.Show(InfoBarSeverity.Warning, "No model answered correctly",
                        "None of the models worked with this key right now. Look at the reasons below, and check your key and limits in Google AI Studio.");
                }
                else
                {
                    _status.Show(InfoBarSeverity.Success, "Done",
                        $"{all.Count(r => r.Status == ModelStatus.Works)} of {all.Count} models work. The fastest was {best.Model}.");
                }
            }
            catch (OperationCanceledException)
            {
                RenderGeminiResults(GeminiModels.LoadResults(), FillPicker);
                _status.Show(InfoBarSeverity.Informational, "Stopped", "The results found so far are saved.");
            }
            catch (Exception ex)
            {
                RenderGeminiResults(GeminiModels.LoadResults(), FillPicker);
                _status.Show(InfoBarSeverity.Error, "Could not test the models", ex.Message);
            }
            finally
            {
                _geminiCts?.Dispose();
                _geminiCts = null;
                test.IsEnabled = true;
                picker.IsEnabled = true;
                stop.Visibility = Visibility.Collapsed;
                FillPicker();
            }
        };

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(test);
        actions.Children.Add(stop);
        actions.Children.Add(key);

        panel.Children.Add(picker);
        panel.Children.Add(actions);
        panel.Children.Add(_geminiResults);

        RenderGeminiResults(GeminiModels.LoadResults(), FillPicker);
        return panel;
    }

    private void RenderGeminiResults(IReadOnlyList<ModelResult> results, Action refreshPicker)
    {
        _geminiResults.Children.Clear();

        if (results.Count == 0)
        {
            _geminiResults.Children.Add(Ui.Secondary("Not tested yet. Press \"Test all models\" to see which models work with your key."));
            return;
        }

        var best = GeminiModels.Best(results);
        if (best is not null)
        {
            var head = new StackPanel { Spacing = 4, Margin = new Thickness(0, 4, 0, 4) };
            head.Children.Add(new TextBlock
            {
                Text = $"Recommended: {best.Model} ({GeminiModels.StatusText(best)})",
                Style = Ui.StyleRes("BodyStrongTextBlockStyle"),
                TextWrapping = TextWrapping.Wrap,
            });

            var use = Ui.IconButton(Ui.IconCheck, "Use this model");
            use.HorizontalAlignment = HorizontalAlignment.Left;
            use.Click += (_, _) =>
            {
                AiConfig.SaveModel(best.Model);
                refreshPicker();
                _status.Show(InfoBarSeverity.Success, "Model saved", $"AI best settings will use {best.Model}.");
            };
            head.Children.Add(use);

            var fastest = GeminiModels.Fastest(results);
            if (fastest is not null && fastest.Model != best.Model)
            {
                head.Children.Add(Ui.Secondary(
                    $"{fastest.Model} was faster ({GeminiModels.StatusText(fastest)}), but it is a preview model. Google can change or remove those without warning, so ZenTune recommends a stable one."));
            }
            _geminiResults.Children.Add(head);
        }

        _geminiResults.Children.Add(Ui.Secondary($"Last tested {Ui.When(results.Max(r => r.Tested))}"));

        var ordered = results
            .OrderBy(r => r.Status == ModelStatus.Works ? 0 : 1)
            .ThenBy(r => r.Status == ModelStatus.Works ? r.Milliseconds : 0)
            .ThenBy(r => r.Model, StringComparer.OrdinalIgnoreCase);

        foreach (var r in ordered)
        {
            var grid = new Grid { ColumnSpacing = 12 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new StackPanel { Spacing = 1 };
            text.Children.Add(new TextBlock { Text = r.Model, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            if (r.Status != ModelStatus.Works && r.Detail.Length > 0)
                text.Children.Add(Ui.Secondary(r.Detail));
            else if (GeminiModels.IsPreview(r.Model))
                text.Children.Add(Ui.Secondary("Preview model"));
            grid.Children.Add(text);

            var state = new TextBlock
            {
                Text = GeminiModels.StatusText(r),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = r.Status == ModelStatus.Works ? Ui.Res("SystemFillColorSuccessBrush") : Ui.Res("TextFillColorSecondaryBrush"),
            };
            Grid.SetColumn(state, 1);
            grid.Children.Add(state);

            _geminiResults.Children.Add(Ui.CardFrame(grid, new Thickness(16, 8, 16, 8)));
        }
    }
}
