using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using ZenTune.Core;

namespace ZenTune.App;

internal enum AppIconChoice { Gauge, Graph }

/// <summary>
/// The ZenTune icon. The graph is built into ZenTune.exe; the user can switch to the gauge under Advanced.
/// The window and taskbar change at once, and ZenTune also points its own desktop and Start menu shortcuts at the chosen icon.
/// </summary>
internal static class AppIcon
{
    private static Window? _window;
    private static Image? _titleIcon;

    public static AppIconChoice Current { get; private set; } = AppIconChoice.Graph;

    private static string SettingFile => Path.Combine(AppPaths.Root, "app-icon.txt");

    public static string IcoPath(AppIconChoice c) =>
        Path.Combine(AppContext.BaseDirectory, "Assets", c == AppIconChoice.Graph ? "ZenTune-graph.ico" : "ZenTune-gauge.ico");

    public static string PngPath(AppIconChoice c) =>
        Path.Combine(AppContext.BaseDirectory, "Assets", c == AppIconChoice.Graph ? "ZenTune-graph.png" : "ZenTune-gauge.png");

    /// <summary>Called once by the main window: loads the saved choice and applies it.</summary>
    public static void Attach(Window window, Image titleIcon)
    {
        _window = window;
        _titleIcon = titleIcon;

        try
        {
            if (File.Exists(SettingFile) && Enum.TryParse<AppIconChoice>(File.ReadAllText(SettingFile).Trim(), out var saved))
                Current = saved;
        }
        catch { /* use the default */ }

        ApplyToWindow();

        // A new Setup.exe resets the shortcuts to the graph (the icon built into ZenTune.exe), so put the chosen icon back
        if (Current != AppIconChoice.Graph)
        {
            var choice = Current;
            _ = Task.Run(() => UpdateShortcuts(choice));
        }
    }

    public static void Choose(AppIconChoice choice)
    {
        Current = choice;
        try { File.WriteAllText(SettingFile, choice.ToString()); } catch { /* the choice just will not be remembered */ }
        ApplyToWindow();
    }

    private static void ApplyToWindow()
    {
        try
        {
            var ico = IcoPath(Current);
            if (_window is not null && File.Exists(ico))
            {
                try { _window.AppWindow.SetIcon(ico); } catch { /* the Win32 call below does the taskbar */ }
                SetNativeIcons(ico);
            }

            var png = PngPath(Current);
            if (_titleIcon is not null && File.Exists(png)) _titleIcon.Source = new BitmapImage(new Uri(png));
        }
        catch { /* the icon is only cosmetic, so it must never stop the app */ }
    }

    // The taskbar button and Alt+Tab use the window's own Win32 icons, which AppWindow.SetIcon does not always update,
    // so they are set directly as well. The handles are kept so the old ones can be freed when the icon changes.
    private static IntPtr _bigIcon;
    private static IntPtr _smallIcon;

    private static void SetNativeIcons(string icoPath)
    {
        if (_window is null) return;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);

        var big = LoadImageW(IntPtr.Zero, icoPath, ImageIcon, GetSystemMetrics(SmCxIcon), GetSystemMetrics(SmCyIcon), LoadFromFile);
        var small = LoadImageW(IntPtr.Zero, icoPath, ImageIcon, GetSystemMetrics(SmCxSmIcon), GetSystemMetrics(SmCySmIcon), LoadFromFile);
        if (big == IntPtr.Zero || small == IntPtr.Zero)
        {
            if (big != IntPtr.Zero) DestroyIcon(big);
            if (small != IntPtr.Zero) DestroyIcon(small);
            return;
        }

        SendMessageW(hwnd, WmSetIcon, (IntPtr)IconBig, big);
        SendMessageW(hwnd, WmSetIcon, (IntPtr)IconSmall, small);
        SetClassLongPtrW(hwnd, GclpHIcon, big);
        SetClassLongPtrW(hwnd, GclpHIconSm, small);

        var oldBig = _bigIcon;
        var oldSmall = _smallIcon;
        _bigIcon = big;
        _smallIcon = small;
        if (oldBig != IntPtr.Zero) DestroyIcon(oldBig);
        if (oldSmall != IntPtr.Zero) DestroyIcon(oldSmall);
    }

    private const uint ImageIcon = 1;
    private const uint LoadFromFile = 0x10;
    private const uint WmSetIcon = 0x0080;
    private const int IconSmall = 0;
    private const int IconBig = 1;
    private const int GclpHIcon = -14;
    private const int GclpHIconSm = -34;
    private const int SmCxIcon = 11;
    private const int SmCyIcon = 12;
    private const int SmCxSmIcon = 49;
    private const int SmCySmIcon = 50;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImageW(IntPtr hInstance, string name, uint type, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SetClassLongPtrW(IntPtr hWnd, int index, IntPtr newValue);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    /// <summary>
    /// Points every shortcut to this copy of ZenTune.exe (desktop, Start menu, pinned items) at the chosen icon.
    /// Returns how many shortcuts were changed. Shortcuts in folders ZenTune may not write to are skipped.
    /// </summary>
    public static int UpdateShortcuts(AppIconChoice choice)
    {
        try
        {
            var exe = Environment.ProcessPath;
            var ico = IcoPath(choice);
            if (string.IsNullOrEmpty(exe) || !File.Exists(ico)) return 0;

            static string Q(string s) => "'" + s.Replace("'", "''") + "'";

            var script = $$"""
$exe = {{Q(exe)}}
$ico = {{Q(ico)}}
$dirs = @(
  [Environment]::GetFolderPath('Desktop'),
  [Environment]::GetFolderPath('CommonDesktopDirectory'),
  [Environment]::GetFolderPath('Programs'),
  [Environment]::GetFolderPath('CommonPrograms'),
  (Join-Path $env:APPDATA 'Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar'),
  (Join-Path $env:APPDATA 'Microsoft\Internet Explorer\Quick Launch\User Pinned\StartMenu')
)
$shell = New-Object -ComObject WScript.Shell
$count = 0
foreach ($d in $dirs) {
  if (-not $d -or -not (Test-Path $d)) { continue }
  Get-ChildItem $d -Filter *.lnk -Recurse -ErrorAction SilentlyContinue | ForEach-Object {
    try {
      $l = $shell.CreateShortcut($_.FullName)
      if ($l.TargetPath -ieq $exe) { $l.IconLocation = "$ico,0"; $l.Save(); $count++ }
    } catch { }
  }
}
$count
""";

            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
            };

            using var process = Process.Start(psi);
            if (process is null) return 0;

            var read = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(20000))
            {
                try { process.Kill(); } catch { }
                return 0;
            }

            int.TryParse(read.Result.Trim(), out int count);

            // Ask Windows to redraw its icons, so the change shows without signing out
            try { Process.Start(new ProcessStartInfo("ie4uinit.exe", "-show") { CreateNoWindow = true, UseShellExecute = false }); } catch { }

            return count;
        }
        catch
        {
            return 0;
        }
    }
}
