namespace TimeViewer;

// Everything persisted to settings.json. SettingsService owns the reading and writing;
// this type is only the shape of the file.
public class AppSettings
{
    public Dictionary<string, string> TagColors { get; set; } = new();
    public string MtcExePath { get; set; } = @"C:\Program Files\ManicTime\mtc.exe";

    // Where the heatmap JSON is written for Obsidian to pick up. Must be a folder INSIDE the
    // vault - dataviewjs' dv.io.load() resolves vault-relative paths only.
    public string ObsidianExportPath { get; set; } = "";
    public bool ObsidianExportEnabled { get; set; } = false;

    // The tagging files. Empty means the default, beside settings.json in the app data folder;
    // a path here lets them live elsewhere, e.g. in a synced folder shared between machines.
    public string TagsCsvPath { get; set; } = "";
    public string ExplorerRulesCsvPath { get; set; } = "";
}
