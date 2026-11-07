using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ZenTune.Core.Cloud;
using ZenTune.Core.Saves;

namespace ZenTune.App.Pages;

/// <summary>
/// "Cloud": upload the backup folder to OneDrive, Google Drive or Dropbox through rclone (downloaded once, checked with a pinned SHA-256).
/// Uploads only add or update files in the cloud, they never delete anything there.
/// </summary>
internal sealed class CloudPage : UserControl
{
    private static readonly (string Label, string Type, string Slug, string[] Extra)[] Providers =
    [
        ("OneDrive", "onedrive", "onedrive", []),
        ("Google Drive", "drive", "gdrive", ["scope=drive"]),
        ("Dropbox", "dropbox", "dropbox", []),
        ("Elkjøp Cloud", "jottacloud", "elkjop", ["config_type=traditional", "config_traditional=elkjop", "config_non_standard=false"]),
    ];

    private readonly InfoBar _status;
    private readonly TextBlock _rcloneInfo;
    private readonly Button _download;
    private readonly ProgressBar _downloadBar;
    private readonly ComboBox _provider;
    private readonly Button _connect;
    private readonly ComboBox _accounts;
    private readonly Button _remove;
    private readonly ToggleSwitch _encrypt;
    private readonly ToggleSwitch _useForUpload;
    private readonly CheckBox _includeSettings;
    private readonly ToggleSwitch _auto;
    private readonly Button _upload;
    private readonly Button _fetch;
    private readonly ProgressBar _bar;
    private readonly TextBlock _progress;
    private readonly TextBlock _last;
    private readonly StackPanel _accountPanel;
    private readonly StackPanel _optionsPanel;
    private readonly StackPanel _transferPanel;
    private readonly Border _optionsCard;
    private readonly Border _transferCard;

    private CloudSettings _settings = CloudSync.Load();
    private bool _loading = true;
    private bool _busy;
    private bool _started;
    private CancellationTokenSource? _cts;

    public CloudPage()
    {
        var (root, body, status) = Ui.Page("Cloud",
            "Keep a copy of your backups in OneDrive, Google Drive or Dropbox. Uploads only add and update files in the cloud, they never delete anything there.");
        _status = status;

        // ---- rclone
        _rcloneInfo = new TextBlock { TextWrapping = TextWrapping.Wrap };
        _download = Ui.IconButton(Ui.IconRefresh, "Download rclone", accent: true);
        _download.Click += async (_, _) => await DownloadRcloneAsync();
        _downloadBar = new ProgressBar { Minimum = 0, Maximum = 1, Visibility = Visibility.Collapsed };

        var rcloneStack = new StackPanel { Spacing = 12 };
        rcloneStack.Children.Add(Ui.Secondary(
            "ZenTune uses rclone (free, open source, rclone.org) to talk to the cloud. It is not part of ZenTune. It is downloaded once from rclone's own website " +
            "(about 20 MB), checked against a fixed checksum, and kept in ZenTune's own folder. ZenTune never touches your own rclone setup if you have one."));
        rcloneStack.Children.Add(_rcloneInfo);
        rcloneStack.Children.Add(_download);
        rcloneStack.Children.Add(_downloadBar);
        body.Children.Add(Ui.CardFrame(rcloneStack));

        // ---- account
        _provider = new ComboBox { MinWidth = 180 };
        foreach (var p in Providers) _provider.Items.Add(p.Label);
        _provider.SelectedIndex = 0;

        _connect = Ui.IconButton(Ui.IconCheck, "Connect");
        _connect.Click += async (_, _) => await ConnectAsync();

        _accounts = new ComboBox { MinWidth = 220, Header = "Back up to" };
        _accounts.SelectionChanged += (_, _) => AccountChanged();

        _remove = Ui.IconButton(Ui.IconDelete, "Remove account");
        _remove.Click += async (_, _) => await RemoveAsync();

        var connectRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        connectRow.Children.Add(_provider);
        connectRow.Children.Add(_connect);

        var accountRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Bottom };
        accountRow.Children.Add(_accounts);
        accountRow.Children.Add(new Border { VerticalAlignment = VerticalAlignment.Bottom, Child = _remove });

        _accountPanel = new StackPanel { Spacing = 12 };
        _accountPanel.Children.Add(Ui.SectionHeader("Cloud account"));
        _accountPanel.Children.Add(Ui.Secondary(
            "Connect opens your browser, where you sign in to your own account. ZenTune never sees your password. " +
            "The sign-in is stored on this PC only, in ZenTune's own rclone file, which is never uploaded."));
        _accountPanel.Children.Add(connectRow);
        _accountPanel.Children.Add(accountRow);
        body.Children.Add(Ui.CardFrame(_accountPanel));

        // ---- options
        _useForUpload = new ToggleSwitch { Header = "Upload to this account", OnContent = "On", OffContent = "Off" };
        _useForUpload.Toggled += (_, _) => TargetChanged();

        _encrypt = new ToggleSwitch { Header = "Encrypt uploads to this account", OnContent = "On", OffContent = "Off" };
        _encrypt.Toggled += async (_, _) => await EncryptToggledAsync();

        _includeSettings = new CheckBox { Content = "Also include ZenTune's own settings (AI profiles, added games, change list) and game config backups" };
        _includeSettings.Checked += (_, _) => OptionsChanged();
        _includeSettings.Unchecked += (_, _) => OptionsChanged();

        _auto = new ToggleSwitch { Header = "Upload automatically after a save backup", OnContent = "On", OffContent = "Off" };
        _auto.Toggled += (_, _) => OptionsChanged();

        _optionsPanel = new StackPanel { Spacing = 12 };
        _optionsPanel.Children.Add(Ui.SectionHeader("Options"));
        _optionsPanel.Children.Add(Ui.Secondary(
            "With encryption, file names and contents are scrambled on this PC before they are sent, so even the cloud company cannot read them. " +
            "ZenTune makes the passwords for you and shows them once. If you lose them, the encrypted backups cannot be opened by anyone. " +
            "Files that were uploaded before you turned encryption on stay as they were."));
        _optionsPanel.Children.Add(Ui.Secondary("You can connect several accounts, for example OneDrive and Elkjøp Cloud. \"Upload now\" and auto-upload send to every account that has Upload switched on. The next two switches belong to the account picked under \"Back up to\"."));
        _optionsPanel.Children.Add(_useForUpload);
        _optionsPanel.Children.Add(_encrypt);
        _optionsPanel.Children.Add(_includeSettings);
        _optionsPanel.Children.Add(_auto);
        _optionsCard = Ui.CardFrame(_optionsPanel);
        body.Children.Add(_optionsCard);

        // ---- transfer
        _upload = Ui.IconButton(Ui.IconSave, "Upload now", accent: true);
        _upload.Click += async (_, _) => await UploadAsync();

        _fetch = Ui.IconButton(Ui.IconHistory, "Download backups from the cloud");
        _fetch.Click += async (_, _) => await FetchAsync();

        _bar = new ProgressBar { IsIndeterminate = true, Visibility = Visibility.Collapsed };
        _progress = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Ui.Res("TextFillColorSecondaryBrush") };
        _last = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Ui.Res("TextFillColorSecondaryBrush") };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(_upload);
        buttons.Children.Add(_fetch);

        _transferPanel = new StackPanel { Spacing = 12 };
        _transferPanel.Children.Add(Ui.SectionHeader("Transfer"));
        _transferPanel.Children.Add(Ui.Secondary(
            "Upload sends the backups of this PC. Download brings backups from your other PCs here, and never replaces a newer file with an older one. " +
            "Your own rclone login file and the Gemini key are never part of this."));
        _transferPanel.Children.Add(buttons);
        _transferPanel.Children.Add(_bar);
        _transferPanel.Children.Add(_progress);
        _transferPanel.Children.Add(_last);
        _transferCard = Ui.CardFrame(_transferPanel);
        body.Children.Add(_transferCard);

        Content = root;
        Loaded += async (_, _) =>
        {
            if (_started) return;
            _started = true;
            await RefreshAsync();
        };
    }

    // ---------------------------------------------------------------- State

    private async Task RefreshAsync()
    {
        _loading = true;
        try
        {
            _settings = CloudSync.Load();
            bool installed = await Task.Run(Rclone.IsInstalled);
            var remotes = installed ? await Rclone.ListRemotesAsync() : [];

            _rcloneInfo.Text = installed
                ? $"rclone {Rclone.Version} is ready."
                : "rclone is not downloaded yet. Press Download rclone to continue.";
            _download.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;

            _accountPanel.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;

            _accounts.Items.Clear();
            foreach (var r in remotes) _accounts.Items.Add(DisplayName(r));
            var idx = remotes.IndexOf(_settings.Remote);
            if (idx < 0 && remotes.Count > 0)
            {
                idx = 0;
                _settings.Remote = remotes[0];
                CloudSync.Save(_settings);
            }
            _accounts.SelectedIndex = idx;
            _remembered = remotes;

            bool hasAccount = installed && remotes.Count > 0 && idx >= 0;
            _accounts.Visibility = hasAccount ? Visibility.Visible : Visibility.Collapsed;
            _remove.Visibility = hasAccount ? Visibility.Visible : Visibility.Collapsed;
            _optionsCard.Visibility = hasAccount ? Visibility.Visible : Visibility.Collapsed;
            _transferCard.Visibility = hasAccount ? Visibility.Visible : Visibility.Collapsed;

            if (installed && remotes.Count == 0 && File.Exists(Rclone.ConfigPath))
            {
                var raw = await Rclone.RunAsync(["listremotes"], TimeSpan.FromSeconds(20));
                _status.Show(InfoBarSeverity.Warning, "No account found in rclone's list",
                    $"exit {raw.ExitCode}: {(raw.Output + raw.Error).Trim()}");
            }

            SetBusy(false);

            ShowTarget();
            _includeSettings.IsChecked = _settings.IncludeSettings;
            _auto.IsOn = _settings.AutoUpload;
            ShowLast();
        }
        catch (Exception ex)
        {
            _status.Show(InfoBarSeverity.Error, "Could not read the cloud settings", ex.Message);
        }
        finally
        {
            _loading = false;
        }
    }

    private List<string> _remembered = [];

    private static string DisplayName(string remote) => CloudSync.Label(remote);

    private void ShowLast()
    {
        var last = CloudSync.LastResult();
        _last.Text = last.Length > 0 ? "Last: " + last : "Nothing has been uploaded yet.";
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _download.IsEnabled = !busy;
        _connect.IsEnabled = !busy;
        _remove.IsEnabled = !busy;
        _upload.IsEnabled = !busy;
        _fetch.IsEnabled = !busy;
        _provider.IsEnabled = !busy;
        _accounts.IsEnabled = !busy;
        _bar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (!busy) _progress.Text = "";
    }

    private void ShowTarget()
    {
        if (string.IsNullOrEmpty(_settings.Remote)) return;
        var t = _settings.For(_settings.Remote);
        _useForUpload.IsOn = t.Enabled;
        _encrypt.IsOn = t.Encrypted;
    }

    private void TargetChanged()
    {
        if (_loading || string.IsNullOrEmpty(_settings.Remote)) return;
        _settings.For(_settings.Remote).Enabled = _useForUpload.IsOn;
        try { CloudSync.Save(_settings); }
        catch (Exception ex) { _status.Show(InfoBarSeverity.Error, "Could not save", ex.Message); }
    }

    private void OptionsChanged()
    {
        if (_loading) return;
        _settings.IncludeSettings = _includeSettings.IsChecked == true;
        _settings.AutoUpload = _auto.IsOn;
        try { CloudSync.Save(_settings); }
        catch (Exception ex) { _status.Show(InfoBarSeverity.Error, "Could not save", ex.Message); }
    }

    private void AccountChanged()
    {
        if (_loading || _accounts.SelectedIndex < 0 || _accounts.SelectedIndex >= _remembered.Count) return;
        _settings.Remote = _remembered[_accounts.SelectedIndex];
        _loading = true;
        ShowTarget();
        _loading = false;
        try { CloudSync.Save(_settings); }
        catch (Exception ex) { _status.Show(InfoBarSeverity.Error, "Could not save", ex.Message); }
    }

    // ---------------------------------------------------------------- Actions

    private async Task DownloadRcloneAsync()
    {
        if (_busy) return;
        SetBusy(true);
        _downloadBar.Visibility = Visibility.Visible;
        _downloadBar.Value = 0;
        try
        {
            await Rclone.DownloadAsync(p => DispatcherQueue.TryEnqueue(() =>
            {
                _downloadBar.IsIndeterminate = p < 0;
                if (p >= 0) _downloadBar.Value = p;
            }));
            _status.Show(InfoBarSeverity.Success, "rclone is ready", "The download was checked and unpacked.");
        }
        catch (Exception ex)
        {
            _status.Show(InfoBarSeverity.Error, "Could not download rclone", ex.Message);
        }
        finally
        {
            _downloadBar.Visibility = Visibility.Collapsed;
            SetBusy(false);
            await RefreshAsync();
        }
    }

    private async Task ConnectAsync()
    {
        if (_busy || _provider.SelectedIndex < 0) return;
        var p = Providers[_provider.SelectedIndex];
        var name = "zentune-" + p.Slug;

        SetBusy(true);
        _progress.Text = "Your browser should open now. Sign in there and come back here. This can take a few minutes.";
        try
        {
            await Rclone.ConnectAsync(name, p.Type, p.Extra);
            _settings.Remote = name;
            _settings.For(name).Enabled = true;
            CloudSync.Save(_settings);
            _status.Show(InfoBarSeverity.Success, $"{p.Label} connected", "You can upload now.");
        }
        catch (Exception ex)
        {
            _status.Show(InfoBarSeverity.Error, $"Could not connect {p.Label}", ex.Message);
        }
        finally
        {
            SetBusy(false);
            await RefreshAsync();
        }
    }

    private async Task RemoveAsync()
    {
        if (_busy || string.IsNullOrEmpty(_settings.Remote)) return;
        var label = DisplayName(_settings.Remote);
        if (!await Ui.ConfirmAsync(XamlRoot, $"Remove {label}?",
                "ZenTune forgets the sign-in on this PC. Nothing in the cloud is deleted, and encrypted backups can only be opened with the passwords from when you turned encryption on.",
                "Remove", destructive: true))
            return;

        SetBusy(true);
        try
        {
            await Rclone.DeleteRemoteAsync(_settings.Remote);
            _settings.Targets.Remove(_settings.Remote);
            _settings.Remote = "";
            CloudSync.Save(_settings);
            _status.Show(InfoBarSeverity.Success, $"{label} removed", "The sign-in is gone from this PC.");
        }
        catch (Exception ex)
        {
            _status.Show(InfoBarSeverity.Error, "Could not remove the account", ex.Message);
        }
        finally
        {
            SetBusy(false);
            await RefreshAsync();
        }
    }

    private async Task EncryptToggledAsync()
    {
        if (_loading || string.IsNullOrEmpty(_settings.Remote)) return;

        if (!_encrypt.IsOn)
        {
            _settings.For(_settings.Remote).Encrypted = false;
            CloudSync.Save(_settings);
            return;
        }

        try
        {
            if (await Rclone.CryptExistsAsync(_settings.Remote))
            {
                _settings.For(_settings.Remote).Encrypted = true;
                CloudSync.Save(_settings);
                return;
            }

            var password = Rclone.GeneratePassword();
            var salt = Rclone.GeneratePassword();
            if (!await ShowPasswordsAsync(password, salt))
            {
                SetToggle(false);
                return;
            }

            await Rclone.CreateCryptAsync(_settings.Remote, password, salt);
            _settings.For(_settings.Remote).Encrypted = true;
            CloudSync.Save(_settings);
            _status.Show(InfoBarSeverity.Success, "Encryption is on", "New uploads are encrypted. Keep the passwords safe.");
        }
        catch (Exception ex)
        {
            SetToggle(false);
            _status.Show(InfoBarSeverity.Error, "Could not turn on encryption", ex.Message);
        }
    }

    private void SetToggle(bool on)
    {
        _loading = true;
        _encrypt.IsOn = on;
        _loading = false;
    }

    private async Task<bool> ShowPasswordsAsync(string password, string salt)
    {
        var stack = new StackPanel { Spacing = 10, MinWidth = 420 };
        stack.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = "Save these two passwords somewhere safe (a password manager, for example). This is the only time ZenTune shows them. " +
                   "On another PC you need both to open the encrypted backups. Without them the backups are lost for good.",
        });
        stack.Children.Add(new TextBox { Header = "Password", Text = password, IsReadOnly = true });
        stack.Children.Add(new TextBox { Header = "Second password (salt)", Text = salt, IsReadOnly = true });
        var saved = new CheckBox { Content = "I have saved both passwords" };
        stack.Children.Add(saved);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Your encryption passwords",
            Content = stack,
            PrimaryButtonText = "Turn on encryption",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            IsPrimaryButtonEnabled = false,
        };
        saved.Checked += (_, _) => dialog.IsPrimaryButtonEnabled = true;
        saved.Unchecked += (_, _) => dialog.IsPrimaryButtonEnabled = false;

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task UploadAsync()
    {
        if (_busy) return;
        if (CloudSync.Busy)
        {
            _status.Show(InfoBarSeverity.Informational, "Already uploading", "An upload is already running in the background. Try again in a moment.");
            return;
        }

        SetBusy(true);
        _progress.Text = "Starting the upload…";
        try
        {
            var done = await Task.Run(() => CloudSync.UploadAsync(line => DispatcherQueue.TryEnqueue(() => _progress.Text = Short(line))));
            _status.Show(InfoBarSeverity.Success, "Upload finished", done ?? "Done.");
        }
        catch (Exception ex)
        {
            _status.Show(InfoBarSeverity.Error, "Could not upload", ex.Message);
        }
        finally
        {
            SetBusy(false);
            ShowLast();
        }
    }

    private async Task FetchAsync()
    {
        if (_busy) return;
        SetBusy(true);
        _progress.Text = "Starting the download…";
        try
        {
            var done = await Task.Run(() => CloudSync.DownloadAsync(line => DispatcherQueue.TryEnqueue(() => _progress.Text = Short(line))));
            _status.Show(InfoBarSeverity.Success, done, "Open the Saves page to see backups from your other PCs.");
        }
        catch (Exception ex)
        {
            _status.Show(InfoBarSeverity.Error, "Could not download", ex.Message);
        }
        finally
        {
            SetBusy(false);
            ShowLast();
        }
    }

    private static string Short(string line) => line.Length > 160 ? line[..160] + "…" : line;
}
