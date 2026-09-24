namespace Chronoscope;

// Where tags.csv, explorer-processes.csv and settings.json live (Data), and the mtc.exe exports
// (Cache): %LOCALAPPDATA%\Chronoscope.
public static class FileSystem
{
    private static readonly string AppRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Chronoscope");

    public static string AppDataDirectory { get; } = Path.Combine(AppRoot, "Data");
    public static string CacheDirectory { get; } = Path.Combine(AppRoot, "Cache");
}
