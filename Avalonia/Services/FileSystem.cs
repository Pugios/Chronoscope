namespace TimeViewer;

// Stand-in for MAUI's Essentials FileSystem, resolving to the SAME folders the MAUI build used,
// so an existing install keeps its tags.csv, explorer-processes.csv and settings.json.
//
// An unpackaged MAUI app on Windows puts them under
//   %LOCALAPPDATA%\<PublisherName>\<ApplicationId>\Data   (and ...\Cache)
// where PublisherName is the assembly's Company - which defaults to the assembly name,
// "TimeViewer" - and ApplicationId is com.pugio.timeviewer. Changing either orphans every
// user's data, exactly as the note on ApplicationId in the MAUI csproj warns.
public static class FileSystem
{
    private const string Publisher = "TimeViewer";
    private const string ApplicationId = "com.pugio.timeviewer";

    private static readonly string AppRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Publisher,
        ApplicationId);

    public static string AppDataDirectory { get; } = Path.Combine(AppRoot, "Data");
    public static string CacheDirectory { get; } = Path.Combine(AppRoot, "Cache");
}
