using System.Security.Cryptography;
using System.Text;

namespace ZenTune.App;

/// <summary>
/// Only one ZenTune per program file runs at a time (it keeps a tray icon, so two copies would give two icons).
/// Starting ZenTune again just brings the window of the one that is already running to the front.
/// A copy in another folder (for example a fresh build next to an installed one) counts as a different program and starts normally.
/// </summary>
internal static class SingleInstance
{
    private static Mutex? _mutex;
    private static EventWaitHandle? _show;

    /// <summary>Returns true when this is the first copy. If another copy is running it is told to show its window and false is returned.</summary>
    public static bool TryStart()
    {
        try
        {
            var path = (Environment.ProcessPath ?? "zentune").ToLowerInvariant();
            var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..16];

            _mutex = new Mutex(true, $@"Local\ZenTune.{id}", out bool created);
            _show = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\ZenTune.Show.{id}");

            if (created) return true;

            _show.Set();
            return false;
        }
        catch
        {
            return true; // if this check fails for any reason, just start
        }
    }

    /// <summary>Calls <paramref name="show"/> (on a background thread) every time someone starts ZenTune a second time.</summary>
    public static void ListenForSecondStart(Action show)
    {
        var handle = _show;
        if (handle is null) return;

        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    handle.WaitOne();
                    show();
                }
                catch
                {
                    return;
                }
            }
        })
        { IsBackground = true, Name = "ZenTune second start" };
        thread.Start();
    }
}
