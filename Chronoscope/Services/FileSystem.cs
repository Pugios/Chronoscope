using System.Diagnostics;

namespace Chronoscope;

// Where tags.csv, explorer-processes.csv and settings.json live (Data), and the mtc.exe exports
// (Cache): %LOCALAPPDATA%\Chronoscope.
//
// Up to 1.0 the app was TimeViewer, and before that a MAUI app, which put its files under
// %LOCALAPPDATA%\TimeViewer\com.pugio.timeviewer (publisher + application id). That folder is
// moved here once, on the first start after the rename, so an existing setup carries over.
public static class FileSystem
{
    // Static initializers run in textual order: these two must stay above AppRoot
    private static readonly string LocalAppData =
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private static readonly string AppRoot = ResolveAppRoot();

    public static string AppDataDirectory { get; } = Path.Combine(AppRoot, "Data");
    public static string CacheDirectory { get; } = Path.Combine(AppRoot, "Cache");

    private static string ResolveAppRoot()
    {
        string root = Path.Combine(LocalAppData, "Chronoscope");
        string legacyParent = Path.Combine(LocalAppData, "TimeViewer");
        string legacy = Path.Combine(legacyParent, "com.pugio.timeviewer");

        if (Directory.Exists(root) || !Directory.Exists(legacy))
            return root;

        try
        {
            Directory.Move(legacy, root);

            // Only the now empty publisher folder is left; anything else in it is not ours
            if (!Directory.EnumerateFileSystemEntries(legacyParent).Any())
                Directory.Delete(legacyParent);

            return root;
        }
        catch (Exception ex)
        {
            // Something holds a file open (an old TimeViewer still running, an editor on
            // tags.csv). Keep working from the old folder rather than starting empty; the move is
            // tried again on the next start.
            Debug.WriteLine($"Could not move the TimeViewer data folder: {ex.Message}");
            return legacy;
        }
    }
}
