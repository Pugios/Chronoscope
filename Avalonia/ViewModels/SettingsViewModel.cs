using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Threading;

namespace TimeViewer.ViewModels;

// Where the data comes from and where the heatmap goes: the rarely changed half of the MAUI
// SettingsPage (the tags and colours have their own page). Nothing is written until Save.
public partial class SettingsViewModel : ViewModelBase
{
    private readonly SettingsService _settingsService;
    private readonly DataService _dataService;
    private readonly VaultExportService _vaultExportService;
    private readonly DialogService _dialogs;
    private readonly MainWindowViewModel _shell;

    public SettingsViewModel(SettingsService settingsService, DataService dataService,
        VaultExportService vaultExportService, DialogService dialogs, MainWindowViewModel shell)
    {
        _settingsService = settingsService;
        _dataService = dataService;
        _vaultExportService = vaultExportService;
        _dialogs = dialogs;
        _shell = shell;
    }

    public override Task OnNavigatedToAsync()
    {
        MtcExePath = _settingsService.MtcExePath;
        ObsidianExportPath = _settingsService.ObsidianExportPath;
        ObsidianExportEnabled = _settingsService.ObsidianExportEnabled;
        TagsCsvPath = _settingsService.TagsCsvPath;
        ExplorerRulesCsvPath = _settingsService.ExplorerRulesCsvPath;

        // The page lives on in the history, so it can be navigated to again: never subscribe twice
        _vaultExportService.StatusChanged -= OnExportStatusChanged;
        _vaultExportService.StatusChanged += OnExportStatusChanged;
        UpdateExportStatus();
        return Task.CompletedTask;
    }

    // The service outlives this page; a handler left attached would keep every visit alive
    public override void OnNavigatedFrom() => _vaultExportService.StatusChanged -= OnExportStatusChanged;

    //%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // ManicTime Path
    //%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

    [ObservableProperty]
    public partial string MtcExePath { get; set; } = "";

    [RelayCommand]
    private async Task BrowseMtc()
    {
        var path = await _dialogs.PickFileAsync("Select mtc.exe", ".exe");
        if (path is not null)
            MtcExePath = path;
    }

    //%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Obsidian Vault Export
    //%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ObsidianExportPathDisplay))]
    public partial string ObsidianExportPath { get; set; } = "";

    public string ObsidianExportPathDisplay =>
        string.IsNullOrWhiteSpace(ObsidianExportPath) ? "Not set" : ObsidianExportPath;

    [ObservableProperty]
    public partial bool ObsidianExportEnabled { get; set; }

    [RelayCommand]
    private async Task BrowseVault()
    {
        var path = await _dialogs.PickFolderAsync("Select a folder inside your Obsidian vault");
        if (path is not null)
            ObsidianExportPath = path;
    }

    // What the export last did. Describes the SAVED settings, not the edits on screen: those are
    // not what the export runs with until Save.
    [ObservableProperty]
    public partial string ExportStatus { get; private set; } = "";

    [ObservableProperty]
    public partial bool ExportFailed { get; private set; }

    // Raised from wherever the export finished; the bound text has to change on the UI thread
    private void OnExportStatusChanged() => Dispatcher.UIThread.Post(UpdateExportStatus);

    private void UpdateExportStatus()
    {
        ExportFailed = false;

        if (!_settingsService.ObsidianExportEnabled)
        {
            ExportStatus = "The export is switched off";
            return;
        }

        if (string.IsNullOrWhiteSpace(_settingsService.ObsidianExportPath))
        {
            ExportStatus = "Choose a vault folder to start exporting";
            return;
        }

        DateTime? written = _vaultExportService.LastWrittenAt;

        if (_vaultExportService.LastError is string error)
        {
            ExportFailed = true;
            ExportStatus = $"Failed {When(_vaultExportService.LastErrorAt!.Value)}: {error}"
                + (written is null ? "" : $"{Environment.NewLine}File last written {When(written.Value)}");
            return;
        }

        ExportStatus = written is null
            ? "Not exported yet - it runs the next time the data is loaded"
            : $"Last exported {When(written.Value)}";
    }

    private static string When(DateTime time) =>
        time.Date == DateTime.Today ? $"today at {time:t}" : time.ToString("g");

    //%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Tagging Files
    //%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // tags.csv (process -> tag) and explorer-processes.csv (the Subprocess rules). Pointing
    // either at an existing file elsewhere - a synced folder, a backup - uses that file as is.

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TagsCsvLocation))]
    public partial string TagsCsvPath { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExplorerRulesCsvLocation))]
    public partial string ExplorerRulesCsvPath { get; set; } = "";

    public string TagsCsvLocation => Describe(TagsCsvPath, SettingsService.DefaultTagsCsvPath);
    public string ExplorerRulesCsvLocation => Describe(ExplorerRulesCsvPath, SettingsService.DefaultExplorerRulesCsvPath);

    private static string Describe(string path, string defaultPath) =>
        SettingsService.PathsEqual(path, defaultPath) ? $"{path}  (default)" : path;

    [RelayCommand]
    private async Task BrowseTagsFile()
    {
        var path = await PickTaggingFileAsync("Select tags.csv", DataService.TagsHeader);
        if (path is not null) TagsCsvPath = path;
    }

    [RelayCommand]
    private async Task BrowseExplorerRulesFile()
    {
        var path = await PickTaggingFileAsync("Select explorer-processes.csv", DataService.ExplorerHeader);
        if (path is not null) ExplorerRulesCsvPath = path;
    }

    [RelayCommand]
    private void ResetTagsFile() => TagsCsvPath = SettingsService.DefaultTagsCsvPath;

    [RelayCommand]
    private void ResetExplorerRulesFile() => ExplorerRulesCsvPath = SettingsService.DefaultExplorerRulesCsvPath;

    [RelayCommand]
    private Task OpenTagsFolder() => _dialogs.OpenFolderAsync(Path.GetDirectoryName(TagsCsvPath) ?? "");

    [RelayCommand]
    private Task OpenExplorerRulesFolder() => _dialogs.OpenFolderAsync(Path.GetDirectoryName(ExplorerRulesCsvPath) ?? "");

    private async Task<string?> PickTaggingFileAsync(string title, string header)
    {
        var path = await _dialogs.PickFileAsync(title, ".csv");
        if (path is null) return null;

        if (!DataService.HasHeader(path, header))
        {
            await _dialogs.AlertAsync("Not a tagging file",
                $"{Path.GetFileName(path)} does not start with the expected columns:{Environment.NewLine}{header}");
            return null;
        }

        return path;
    }

    //%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Save
    //%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    [RelayCommand]
    private async Task Save()
    {
        bool exportChanged =
            ObsidianExportPath != _settingsService.ObsidianExportPath
            || ObsidianExportEnabled != _settingsService.ObsidianExportEnabled;

        _settingsService.MtcExePath = MtcExePath;
        _settingsService.ObsidianExportPath = ObsidianExportPath;
        _settingsService.ObsidianExportEnabled = ObsidianExportEnabled;

        bool filesMoved =
            !SettingsService.PathsEqual(TagsCsvPath, _settingsService.TagsCsvPath)
            || !SettingsService.PathsEqual(ExplorerRulesCsvPath, _settingsService.ExplorerRulesCsvPath);
        _settingsService.TagsCsvPath = TagsCsvPath;
        _settingsService.ExplorerRulesCsvPath = ExplorerRulesCsvPath;

        await _settingsService.SaveAsync();

        // Everything on screen was built from the old files; the next page to ask reloads
        if (filesMoved)
            await _dataService.InvalidateAsync();

        // A newly chosen folder (or a freshly enabled export) should not stay empty until the
        // next reload. Moved tagging files skip this on their own: the cache is empty until then.
        if (exportChanged)
            _vaultExportService.RequestExport();

        await _shell.CloseAsync();
    }

    [RelayCommand]
    private Task Discard() => _shell.CloseAsync();
}
