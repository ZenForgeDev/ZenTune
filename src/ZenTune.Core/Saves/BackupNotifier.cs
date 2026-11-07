namespace ZenTune.Core.Saves;

/// <summary>
/// Optional Windows notification when ZenTune makes a backup by itself (after a game closes, or on the schedule).
/// OFF by default; the user turns it on on the Saves page. The app window sets <see cref="Sink"/> to show the message
/// through the tray icon. Backups the user starts by hand never show a notification, since the user sees the result right away.
/// </summary>
public static class BackupNotifier
{
    private static string FlagFile => Path.Combine(AppPaths.Root, "notify-backups-on.txt");

    /// <summary>Set by the app window. Null when there is no tray icon.</summary>
    public static Action<string, string>? Sink { get; set; }

    public static bool Enabled => File.Exists(FlagFile);

    public static void SetEnabled(bool on)
    {
        if (on) File.WriteAllText(FlagFile, "on");
        else
        {
            try { File.Delete(FlagFile); }
            catch { /* checked again at the next message */ }
        }
    }

    public static void Send(string title, string text)
    {
        if (!Enabled) return;
        try { Sink?.Invoke(title, text); }
        catch { /* a notification must never stop a backup */ }
    }
}
