using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ZenTune.App.Pages;
using ZenTune.Core.Pulse;

namespace ZenTune.App;

public sealed partial class MainWindow : Window
{
    // Pages are created once and kept, so switching back is instant and keeps their state
    private readonly Dictionary<string, UIElement> _pages = new();

    public MainWindow()
    {
        InitializeComponent();

        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        var v = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = v is null ? "" : $"{v.Major}.{v.Minor}";

        SizeForDpi(1180, 860);

        AppIcon.Attach(this, TitleIcon);

        Nav.SelectedItem = Nav.MenuItems[0];

        // The welcome message needs the window to be ready, so it is shown once the content has loaded
        if (Content is FrameworkElement content) content.Loaded += OnContentLoaded;

        // The tray icon (bottom right, next to the clock) with a right-click menu for Pulse
        try { _tray = new PulseTray(ShowFromTray, () => Application.Current.Exit()); }
        catch { /* the app works without a tray icon */ }

        // A Pulse log that is still running gets its summary written when the window closes
        Closed += (_, _) =>
        {
            PulsePopout.CloseIfOpen();
            try { _tray?.Dispose(); }
            catch { /* closing must never fail because of the tray icon */ }
            try { PulseMonitor.Shared.StopLogging(); }
            catch { /* closing must never fail because of a log */ }
        };
    }

    private bool _welcomeChecked;
    private PulseTray? _tray;

    /// <summary>Brings the window back, also when it is minimized.</summary>
    private void ShowFromTray()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        ShowWindow(hwnd, 9); // restore
        Activate();
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
                "advanced" => new AdvancedPage(),
                "handheld" => new HandheldPage(),
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
