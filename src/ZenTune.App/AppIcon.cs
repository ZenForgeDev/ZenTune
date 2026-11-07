using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using ZenTune.Core;

namespace ZenTune.App;

/// <summary>
/// The ZenTune icon (one fixed icon, the graph). It is built into ZenTune.exe, so the window and taskbar read it from the exe itself
/// and never depend on the Assets folder. The title bar uses Assets\ZenTune-graph.png.
/// </summary>
internal static class AppIcon
{
    private static Window? _window;
    private static Image? _titleIcon;

    // Written by older versions that let you pick an icon. If it exists, shortcuts are put back to the built-in icon once.
    private static string OldSettingFile => Path.Combine(AppPaths.Root, "app-icon.txt");

    private static string PngPath => Path.Combine(AppContext.BaseDirectory, "Assets", "ZenTune-graph.png");

    /// <summary>Called once by the main window.</summary>
    public static void Attach(Window window, Image titleIcon)
    {
        _window = window;
        _titleIcon = titleIcon;

        ApplyToWindow();

        try
        {
            if (File.Exists(OldSettingFile))
            {
                File.Delete(OldSettingFile);
                _ = Task.Run(ResetShortcuts);
            }
        }
        catch { /* only a one-time cleanup */ }
    }

    private static void ApplyToWindow()
    {
        try
        {
            SetNativeIcons();

            if (_titleIcon is not null && File.Exists(PngPath)) _titleIcon.Source = new BitmapImage(new Uri(PngPath));
        }
        catch { /* the icon is only cosmetic, so it must never stop the app */ }
    }

    // The taskbar button and Alt+Tab use the window's own Win32 icons, so they are set directly from the icon built into ZenTune.exe.
    private static void SetNativeIcons()
    {
        var exe = Environment.ProcessPath;
        if (_window is null || string.IsNullOrEmpty(exe)) return;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);

        var big = LoadFromExe(exe, GetSystemMetrics(SmCxIcon), GetSystemMetrics(SmCyIcon));
        var small = LoadFromExe(exe, GetSystemMetrics(SmCxSmIcon), GetSystemMetrics(SmCySmIcon));
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
        // The handles stay alive as long as the window does, so they are not freed here.
    }

    private static IntPtr LoadFromExe(string exe, int cx, int cy)
    {
        var handles = new IntPtr[1];
        var ids = new uint[1];
        uint n = PrivateExtractIconsW(exe, 0, cx, cy, handles, ids, 1, 0);
        return n >= 1 ? handles[0] : IntPtr.Zero;
    }

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
    private static extern uint PrivateExtractIconsW(string file, int index, int cx, int cy, IntPtr[] icons, uint[] ids, uint count, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SetClassLongPtrW(IntPtr hWnd, int index, IntPtr newValue);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    /// <summary>
    /// One-time cleanup for people who once picked the gauge icon: points every shortcut to this copy of ZenTune.exe
    /// (desktop, Start menu, pinned items) back at the icon built into the exe. Shortcuts ZenTune may not write to are skipped.
    /// </summary>
    private static void ResetShortcuts()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;

            static string Q(string s) => "'" + s.Replace("'", "''") + "'";

            var script = $$"""
$exe = {{Q(exe)}}
$dirs = @(
  [Environment]::GetFolderPath('Desktop'),
  [Environment]::GetFolderPath('CommonDesktopDirectory'),
  [Environment]::GetFolderPath('Programs'),
  [Environment]::GetFolderPath('CommonPrograms'),
  (Join-Path $env:APPDATA 'Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar'),
  (Join-Path $env:APPDATA 'Microsoft\Internet Explorer\Quick Launch\User Pinned\StartMenu')
)
$shell = New-Object -ComObject WScript.Shell
foreach ($d in $dirs) {
  if (-not $d -or -not (Test-Path $d)) { continue }
  Get-ChildItem $d -Filter *.lnk -Recurse -ErrorAction SilentlyContinue | ForEach-Object {
    try {
      $l = $shell.CreateShortcut($_.FullName)
      if ($l.TargetPath -ieq $exe) { $l.IconLocation = "$exe,0"; $l.Save() }
    } catch { }
  }
}
""";

            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            };

            using var process = Process.Start(psi);
            if (process is null) return;
            if (!process.WaitForExit(20000))
            {
                try { process.Kill(); } catch { }
                return;
            }

            // Ask Windows to redraw its icons, so the change shows without signing out
            try { Process.Start(new ProcessStartInfo("ie4uinit.exe", "-show") { CreateNoWindow = true, UseShellExecute = false }); } catch { }
        }
        catch { /* only a one-time cleanup */ }
    }
}
