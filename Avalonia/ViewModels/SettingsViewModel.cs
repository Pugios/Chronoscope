using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TimeViewer.ViewModels;

// Where the data comes from and where the heatmap goes: the rarely changed half of the MAUI
// SettingsPage (the tags and colours have their own page). Nothing is written until Save.
public partial class SettingsViewModel : ViewModelBase
{
    private readonly SettingsService _settingsService;
    private readonly DialogService _dialogs;
    private readonly MainWindowViewModel _shell;

    public SettingsViewModel(SettingsService settingsService, DialogService dialogs, MainWindowViewModel shell)
    {
        _settingsService = settingsService;
        _dialogs = dialogs;
        _shell = shell;
    }

    public override Task OnNavigatedToAsync()
    {
        MtcExePath = _settingsService.MtcExePath;
        ObsidianExportPath = _settingsService.ObsidianExportPath;
        ObsidianExportEnabled = _settingsService.ObsidianExportEnabled;
        return Task.CompletedTask;
    }

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

    //%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Save
    //%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    [RelayCommand]
    private async Task Save()
    {
        _settingsService.MtcExePath = MtcExePath;
        _settingsService.ObsidianExportPath = ObsidianExportPath;
        _settingsService.ObsidianExportEnabled = ObsidianExportEnabled;
        await _settingsService.SaveAsync();

        await _shell.GoBackAsync();
    }

    [RelayCommand]
    private Task Discard() => _shell.GoBackAsync();
}
