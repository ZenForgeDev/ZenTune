using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ZenTune.Core;
using ZenTune.Core.Ai;

namespace ZenTune.App.Pages;

/// <summary>
/// "Feedback": collects crash reports and settings into one zip on the desktop that the user can send to the developer.
/// Nothing is sent by ZenTune itself.
/// </summary>
internal sealed partial class FeedbackPage : UserControl
{
    private readonly InfoBar _status;
    private readonly TextBox _note;
    private readonly CheckBox _crashes;
    private readonly CheckBox _savedAi;
    private readonly CheckBox _gemini;
    private readonly Button _collect;
    private readonly StackPanel _result = new() { Spacing = 10 };
    private bool _busy;

    public FeedbackPage()
    {
        var (root, body, status) = Ui.Page("Feedback",
            "Something not working? Collect the details in one file and send it to the developer. ZenTune never sends anything by itself.");
        _status = status;

        _note = new TextBox
        {
            Header = "What went wrong? (optional)",
            PlaceholderText = "For example: Warzone does not start, or the AI left shadows on Ultra",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 110,
        };

        _crashes = new CheckBox { Content = "Call of Duty crash reports (error code and crash module, no memory dump)", IsChecked = true };
        _savedAi = new CheckBox { Content = "My saved AI settings (game settings only)", IsChecked = true };
        _gemini = new CheckBox
        {
            Content = "Let Gemini write a short English summary (uses one small request from your quota)",
            IsChecked = false,
        };
        bool hasKey = false;
        try { hasKey = !string.IsNullOrWhiteSpace(AiConfig.Load().ApiKey); } catch { /* no key */ }
        if (!hasKey)
        {
            _gemini.IsEnabled = false;
            ToolTipService.SetToolTip(_gemini, "Add your Gemini key in Advanced first.");
        }

        var options = new StackPanel { Spacing = 10 };
        options.Children.Add(_note);
        options.Children.Add(_crashes);
        options.Children.Add(_savedAi);
        options.Children.Add(_gemini);
        options.Children.Add(Ui.Secondary(
            "Never included: your Gemini key, game memory dumps, dxdiag files. Your Windows user name and PC name are replaced with <user> and <pc>. " +
            "You can read everything below after it is collected, before you send it."));

        _collect = Ui.IconButton(Ui.IconFolder, "Collect info", accent: true);
        _collect.Click += async (_, _) => await CollectAsync();
        options.Children.Add(_collect);

        body.Children.Add(Ui.CardFrame(options));
        body.Children.Add(_result);

        Content = root;
    }

    private async Task CollectAsync()
    {
        if (_busy) return;
        _busy = true;
        _collect.IsEnabled = false;
        _result.Children.Clear();
        _result.Children.Add(Ui.Busy());
        try
        {
            var result = await FeedbackCollector.CollectAsync(new FeedbackOptions(
                _note.Text ?? "",
                _crashes.IsChecked == true,
                _savedAi.IsChecked == true,
                _gemini.IsEnabled && _gemini.IsChecked == true));

            _status.Show(InfoBarSeverity.Success, "Feedback folder saved on desktop",
                $"{Path.GetFileName(result.ZipPath)} is ready. Send that file to the developer.");
            ShowResult(result);
        }
        catch (Exception ex)
        {
            _result.Children.Clear();
            _status.Show(InfoBarSeverity.Error, "Could not collect the info", ex.Message);
        }
        finally
        {
            _busy = false;
            _collect.IsEnabled = true;
        }
    }

    private void ShowResult(FeedbackResult result)
    {
        _result.Children.Clear();
        _result.Children.Add(Ui.SectionHeader("What is in " + Path.GetFileName(result.ZipPath)));
        _result.Children.Add(Ui.Secondary(string.Join(", ", result.Files)));

        var open = Ui.IconButton(Ui.IconFolder, "Show on desktop");
        open.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{result.ZipPath}\"") { UseShellExecute = true }); }
            catch (Exception ex) { _status.Show(InfoBarSeverity.Error, "Could not open the folder", ex.Message); }
        };
        _result.Children.Add(open);

        _result.Children.Add(new TextBox
        {
            Header = "report.txt",
            Text = result.Report,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 420,
        });
    }
}
