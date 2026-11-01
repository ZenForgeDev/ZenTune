namespace ZenTune.Core;

/// <summary>Where ZenTune keeps its own data: %LOCALAPPDATA%\ZenTune.</summary>
public static class AppPaths
{
    public static string Root
    {
        get
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZenTune");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string Backups => Ensure(Path.Combine(Root, "Backups"));

    public static string ChangesFile => Path.Combine(Root, "changes.json");

    public static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static string Documents => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    public static string ProgramData => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

    private static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }
}
