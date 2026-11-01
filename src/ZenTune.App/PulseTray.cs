using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using ZenTune.Core.Pulse;

namespace ZenTune.App;

/// <summary>
/// The ZenTune icon next to the clock. Left click opens ZenTune; right click shows a menu:
/// Open ZenTune, Pulse popout, Start/Stop logging, Exit. Uses the plain Windows tray API (Shell_NotifyIcon).
/// Must be created and disposed on the window's own thread, which is also the thread that receives its messages.
/// </summary>
internal sealed class PulseTray : IDisposable
{
    private const uint WM_TRAY = 0x8000 + 1; // WM_APP + 1
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONUP = 0x0205;

    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    private const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_INFO = 0x10;
    private const uint NIIF_INFO = 1;

    private const uint MF_STRING = 0, MF_CHECKED = 0x8, MF_SEPARATOR = 0x800;
    private const uint TPM_RIGHTBUTTON = 0x2, TPM_BOTTOMALIGN = 0x20, TPM_RETURNCMD = 0x100;

    private const uint CmdOpen = 1, CmdPopout = 2, CmdLogging = 3, CmdExit = 4, CmdAlerts = 5;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEX windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClassW(string className, IntPtr instance);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? name);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(uint message, ref NOTIFYICONDATA data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconExW(string file, int index, out IntPtr large, out IntPtr small, uint count);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIconW(IntPtr instance, IntPtr name);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr menu, uint flags, UIntPtr id, string? text);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const string ClassName = "ZenTunePulseTrayWindow";

    private readonly Action _open;
    private readonly Action _exit;
    private readonly WndProcDelegate _proc; // kept in a field so it is not cleaned up while Windows can still call it
    private readonly uint _taskbarCreated;
    private readonly IntPtr _instance;

    private IntPtr _hwnd;
    private IntPtr _icon;
    private bool _ownsIcon;
    private bool _added;
    private bool _disposed;

    // Alerts (a speech bubble when something gets too hot or full). Off by default; measures in the background while on.
    private readonly DispatcherQueue? _queue = DispatcherQueue.GetForCurrentThread();
    private readonly PulseAlerts _alerts = new();
    private bool _alertsRunning;

    public PulseTray(Action open, Action exit)
    {
        _open = open;
        _exit = exit;
        _proc = WndProc;
        _instance = GetModuleHandleW(null);
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated"); // sent when Explorer restarts, the icon must be added again

        var windowClass = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = _instance,
            lpszClassName = ClassName,
        };
        RegisterClassExW(ref windowClass);

        // A window that is never shown (parent HWND_MESSAGE = -3): it only receives the tray icon's messages
        _hwnd = CreateWindowExW(0, ClassName, "ZenTune Pulse tray", 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, _instance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) throw new InvalidOperationException("Could not create the tray window.");

        LoadIcon();
        AddIcon();

        if (PulseAlerts.Enabled) StartAlerts(showBubble: false);
    }

    private void LoadIcon()
    {
        try
        {
            string? path = Environment.ProcessPath;
            if (path is not null && ExtractIconExW(path, 0, out var large, out var small, 1) > 0)
            {
                _icon = small != IntPtr.Zero ? small : large;
                _ownsIcon = true;
                if (small != IntPtr.Zero && large != IntPtr.Zero) DestroyIcon(large);
            }
        }
        catch
        {
            // Falls back to the standard icon below
        }

        if (_icon == IntPtr.Zero) _icon = LoadIconW(IntPtr.Zero, new IntPtr(32512)); // the standard application icon
    }

    private NOTIFYICONDATA Data(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = WM_TRAY,
        hIcon = _icon,
        szTip = "ZenTune",
        szInfo = "",
        szInfoTitle = "",
    };

    private void AddIcon()
    {
        var data = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP);
        _added = Shell_NotifyIconW(NIM_ADD, ref data);
    }

    /// <summary>A speech bubble above the icon.</summary>
    public void Notify(string title, string text)
    {
        if (!_added || _disposed) return;

        var data = Data(NIF_INFO);
        data.szInfoTitle = title.Length > 60 ? title[..60] : title;
        data.szInfo = text.Length > 250 ? text[..250] : text;
        data.dwInfoFlags = NIIF_INFO;
        Shell_NotifyIconW(NIM_MODIFY, ref data);
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (msg == WM_TRAY)
            {
                int mouse = (int)(lParam.ToInt64() & 0xFFFF);
                if (mouse == WM_LBUTTONUP) _open();
                else if (mouse == WM_RBUTTONUP) ShowMenu();
                return IntPtr.Zero;
            }

            if (_taskbarCreated != 0 && msg == _taskbarCreated)
            {
                AddIcon();
                return IntPtr.Zero;
            }
        }
        catch
        {
            // A problem in a menu choice must never take the app down
        }

        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        IntPtr menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;

        try
        {
            AppendMenuW(menu, MF_STRING, new UIntPtr(CmdOpen), "Open ZenTune");
            AppendMenuW(menu, MF_STRING, new UIntPtr(CmdPopout), "Pulse popout");
            AppendMenuW(menu, MF_STRING, new UIntPtr(CmdLogging), PulseMonitor.Shared.IsLogging ? "Stop Pulse logging" : "Start Pulse logging");
            AppendMenuW(menu, MF_STRING | (PulseAlerts.Enabled ? MF_CHECKED : 0), new UIntPtr(CmdAlerts), "Pulse alerts (hot or full)");
            AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, null);
            AppendMenuW(menu, MF_STRING, new UIntPtr(CmdExit), "Exit");

            GetCursorPos(out var point);
            SetForegroundWindow(_hwnd); // without this the menu does not close when you click somewhere else
            uint command = TrackPopupMenu(menu, TPM_RIGHTBUTTON | TPM_BOTTOMALIGN | TPM_RETURNCMD, point.X, point.Y, 0, _hwnd, IntPtr.Zero);
            PostMessageW(_hwnd, 0, IntPtr.Zero, IntPtr.Zero);

            switch (command)
            {
                case CmdOpen: _open(); break;
                case CmdPopout: PulsePopout.OpenFromTray(); break;
                case CmdLogging: ToggleLogging(); break;
                case CmdAlerts: ToggleAlerts(); break;
                case CmdExit: _exit(); break;
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private void ToggleAlerts()
    {
        if (PulseAlerts.Enabled)
        {
            PulseAlerts.Enabled = false;
            StopAlerts();
            Notify("Pulse alerts are off", "ZenTune no longer measures in the background.");
        }
        else
        {
            PulseAlerts.Enabled = true;
            StartAlerts(showBubble: true);
        }
    }

    /// <summary>Starts measuring in the background and shows a bubble when the graphics card or processor gets hot or memory gets full.</summary>
    private async void StartAlerts(bool showBubble)
    {
        if (_alertsRunning) return;
        _alertsRunning = true;

        try
        {
            await Pages.PulsePage.ConfigureMonitorAsync();
        }
        catch
        {
            // Alerts work without the extra information
        }

        if (_disposed || !_alertsRunning || !PulseAlerts.Enabled)
        {
            _alertsRunning = false;
            return;
        }

        PulseMonitor.Shared.Updated += OnPulse;
        PulseMonitor.Shared.AddViewer();

        if (showBubble)
            Notify("Pulse alerts are on", "You get a message when the graphics card or processor is hot, or memory is nearly full. ZenTune measures in the background while this is on.");
    }

    private void StopAlerts()
    {
        if (!_alertsRunning) return;

        _alertsRunning = false;
        PulseMonitor.Shared.Updated -= OnPulse;
        PulseMonitor.Shared.RemoveViewer();
    }

    // Called on the monitor's own thread
    private void OnPulse(PulseSnapshot snapshot)
    {
        try
        {
            foreach (var (title, text) in _alerts.Check(snapshot))
                _queue?.TryEnqueue(() => Notify(title, text));
        }
        catch
        {
            // A problem with a message must never stop the measuring
        }
    }

    private async void ToggleLogging()
    {
        try
        {
            var monitor = PulseMonitor.Shared;
            if (monitor.IsLogging)
            {
                var result = monitor.StopLogging();
                if (result is not null) Notify("Pulse log saved", $"{result.Readings} readings saved in {result.Folder}");
            }
            else
            {
                await Pages.PulsePage.ConfigureMonitorAsync();
                string folder = monitor.StartLogging(1);
                Notify("Pulse logging started", $"Saving to {folder}");
            }
        }
        catch (Exception ex)
        {
            Notify("Pulse logging did not work", ex.Message);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopAlerts();

        if (_added)
        {
            var data = Data(0);
            Shell_NotifyIconW(NIM_DELETE, ref data);
            _added = false;
        }

        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
        UnregisterClassW(ClassName, _instance);

        if (_ownsIcon && _icon != IntPtr.Zero) DestroyIcon(_icon);
        _icon = IntPtr.Zero;
    }
}
