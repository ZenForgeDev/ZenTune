using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ZenTune.App.Pages;
using ZenTune.Core.Advanced;
using ZenTune.Core.Pulse;
using ZenTune.Core.Saves;

namespace ZenTune.App;

public sealed partial class MainWindow : Window
{
    // Pages are created once and kept, so switching back is instant and keeps their state
    private readonly Dictionary<string, UIElement> _pages = new();

    public MainWindow()
    {
        InitializeComponent();

        SystemBackdrop = new MicaBackdrop();
        AppTheme.ApplyBackground(RootGrid);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        var v = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = v is null ? "" : $"{v.Major}.{v.Minor}.{v.Build}";

        SizeForDpi(1180, 860);

        AppIcon.Attach(this, TitleIcon);

        Nav.SelectedItem = Nav.MenuItems.OfType<NavigationViewItem>().First();

        // The welcome message needs the window to be ready, so it is shown once the content has loaded
        if (Content is FrameworkElement content) content.Loaded += OnContentLoaded;

        // The tray icon (bottom right, next to the clock) with a right-click menu for Pulse
        try { _tray = new PulseTray(ShowFromTray, () => { _exiting = true; Application.Current.Exit(); }); }
        catch { /* the app works without a tray icon */ }

        // Automatic backup when a game closes (off unless the user turned it on on the Saves page)
        try { AutoBackup.StartIfEnabled(); }
        catch { /* the app works without it */ }

        // Scheduled backup (off unless the user turned it on on the Saves page)
        try { ScheduledBackup.StartIfEnabled(); }
        catch { /* the app works without it */ }

        // Optional notification when a backup is made by itself
        BackupNotifier.Sink = (title, text) => DispatcherQueue.TryEnqueue(() =>
        {
            try { _tray?.Notify(title, text); }
            catch { /* a notification must never cause trouble */ }
        });

        // If "Keep ZenTune in the tray" is on, the close button hides the window instead of quitting.
        // The event must be answered right away (no waiting), so this only decides and hides.
        AppWindow.Closing += (_, args) =>
        {
            if (_exiting || _tray is null || !AppAutoStart.KeepInTray) return;
            args.Cancel = true;
            try { AppWindow.Hide(); }
            catch { args.Cancel = false; }
        };

        // Starting ZenTune a second time brings this window back instead of opening another copy
        SingleInstance.ListenForSecondStart(() => DispatcherQueue.TryEnqueue(ShowFromTray));

        // A Pulse log that is still running gets its summary written when the window closes
        Closed += (_, _) =>
        {
            PulsePopout.CloseIfOpen();
            try { AutoBackup.Stop(); }
            catch { /* closing must never fail because of the watcher */ }
            try { ScheduledBackup.Stop(); }
            catch { /* closing must never fail because of the schedule */ }
            try { _tray?.Dispose(); }
            catch { /* closing must never fail because of the tray icon */ }
            try { PulseMonitor.Shared.StopLogging(); }
            catch { /* closing must never fail because of a log */ }
        };
    }

    /// <summary>Quits for real (also when "keep in tray" is on). Used when ZenTune restarts itself.</summary>
    public void ExitForRestart()
    {
        _exiting = true;
        Application.Current.Exit();
    }

    private bool _welcomeChecked;
    private bool _exiting;
    private PulseTray? _tray;

    /// <summary>Brings the window back, also when it is minimized or hidden in the tray.</summary>
    private void ShowFromTray()
    {
        try { AppWindow.Show(); }
        catch { /* Activate below shows it anyway */ }

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        ShowWindow(hwnd, 9); // restore
        Activate();
    }

    /// <summary>Used when Windows starts ZenTune at sign-in: hide the window right away and wait in the tray. Does nothing without a tray icon.</summary>
    public void StartHidden()
    {
        if (_tray is null) return;

        // No welcome message while ZenTune is hidden
        _welcomeChecked = true;
        try { AppWindow.Hide(); }
        catch { /* the window just stays visible */ }
    }

    private async void OnContentLoaded(object sender, RoutedEventArgs e)
    {
        if (_welcomeChecked) return;
        _welcomeChecked = true;

        try
        {
            await WelcomeDialog.ShowIfNeededAsync(((FrameworkElement)sender).XamlRoot);
        }
        catch
        {
            // A welcome message must never stop the app from working
        }
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item || item.Tag is not string tag) return;

        if (!_pages.TryGetValue(tag, out var page))
        {
            page = tag switch
            {
                "checks" => new ChecksPage(),
                "games" => new GamesPage(),
                "saves" => new SavesPage(),
                "cloud" => new CloudPage(),
                "advanced" => new AdvancedPage(),
                "settings" => new SettingsPage(),
                "handheld" => new HandheldPage(),
                "driver" => new DriverSettingsPage(),
                "measure" => new MeasurePage(),
                "pulse" => new PulsePage(),
                "feedback" => new FeedbackPage(),
                _ => new PcPage(),
            };
            _pages[tag] = page;
        }
        else if (page is ChecksPage checks)
        {
            // Settings may have changed on the other pages or in Windows
            checks.Reload();
        }
        else if (page is AdvancedPage advanced)
        {
            advanced.Reload();
        }
        else if (page is HandheldPage handheld)
        {
            handheld.Reload();
        }
        else if (page is DriverSettingsPage driver)
        {
            driver.Reload();
        }

        Nav.Content = page;
    }

    private void SizeForDpi(int width, int height)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        double scale = GetDpiForWindow(hwnd) / 96.0;
        if (scale <= 0) scale = 1;

        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(width * scale), (int)(height * scale)));
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hwnd, int command);
}
