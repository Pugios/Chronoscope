namespace Chronoscope;

// Everything persisted to settings.json. SettingsService owns the reading and writing;
// this type is only the shape of the file.
public class AppSettings
{
    public Dictionary<string, string> TagColors { get; set; } = new();
    public string MtcExePath { get; set; } = @"C:\Program Files\ManicTime\mtc.exe";

    // How often the day view reloads from ManicTime while it is on screen, in minutes. A file
    // written before this existed has no such key, so it keeps this default.
    public int RefreshMinutes { get; set; } = 5;

    // Where the heatmap JSON is written for Obsidian to pick up. Must be a folder INSIDE the
    // vault - dataviewjs' dv.io.load() resolves vault-relative paths only.
    public string ObsidianExportPath { get; set; } = "";
    public bool ObsidianExportEnabled { get; set; } = false;

    // The tagging files. Empty means the default, beside settings.json in the app data folder;
    // a path here lets them live elsewhere, e.g. in a synced folder shared between machines.
    public string TagsCsvPath { get; set; } = "";
    public string ExplorerRulesCsvPath { get; set; } = "";

    // The Statistics page's arrangement: tags in the order the user put them (any tag not listed
    // follows, biggest first), and the ones set aside at the bottom. Both apply to every year.
    public List<string> StatisticsTagOrder { get; set; } = new();
    public List<string> StatisticsHiddenTags { get; set; } = new();
}
