using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace ZenTune.App;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        // The app is in English, so numbers and dates use English formatting (3.7 GHz, not 3,7 GHz)
        var english = new CultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentCulture = english;
        CultureInfo.DefaultThreadCurrentUICulture = english;
        CultureInfo.CurrentCulture = english;
        CultureInfo.CurrentUICulture = english;

        // Any crash is written to %LOCALAPPDATA%\ZenTune\crash.log and shown in a message box,
        // so the app never just disappears without saying why.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportCrash(e.ExceptionObject as Exception);
        UnhandledException += (_, e) => ReportCrash(e.Exception);

        try
        {
            InitializeComponent();
        }
        catch (Exception ex)
        {
            ReportCrash(ex);
            throw;
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _window = new MainWindow();
            _window.Activate();
        }
        catch (Exception ex)
        {
            ReportCrash(ex);
            Exit();
        }
    }

    private static bool _reported;

    public static void ReportCrash(Exception? ex)
    {
        if (_reported || ex is null) return;
        _reported = true;

        string text = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n{ex}\n";
        string path = "";
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZenTune");
            Directory.CreateDirectory(dir);
            path = Path.Combine(dir, "crash.log");
            File.AppendAllText(path, text + "\n");
        }
        catch { /* best effort */ }

        MessageBoxW(IntPtr.Zero,
            $"ZenTune ran into a problem and has to close.\n\n{ex.GetType().Name}: {ex.Message}" +
            (path.Length > 0 ? $"\n\nDetails were saved to:\n{path}" : ""),
            "ZenTune", 0x10);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
