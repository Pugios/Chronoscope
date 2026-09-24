using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TimeViewer.ViewModels;

// The top level pages the navigation pane switches between
public enum AppSection { Day, Statistics, Settings }

// Shell of the app: which page is on screen, the back stack beneath it, and the window pin.
// Replaces MAUI's Shell routing. The pane jumps between sections (clearing the stack, as a
// Shell tab would); a page pushes onto the stack to drill in, which is what enables Back.
public partial class MainWindowViewModel : ViewModelBase
{
    private readonly SettingsService _settingsService;
    private readonly DataService _dataService;
    private readonly VaultExportService _vaultExportService;
    private readonly DialogService _dialogs;

    // The day view keeps its state (the day on screen) across trips to the other pages, the way
    // the MAUI Shell's root page did. The others are built fresh on each visit, as MAUI's
    // transient pages were.
    private readonly DayViewModel _day;

    private readonly Stack<ViewModelBase> _backStack = new();

    public MainWindowViewModel(SettingsService settingsService, DataService dataService,
        VaultExportService vaultExportService, DialogService dialogs)
    {
        _settingsService = settingsService;
        _dataService = dataService;
        _vaultExportService = vaultExportService;
        _dialogs = dialogs;
        _day = new DayViewModel(settingsService, dataService, dialogs);
    }

    [ObservableProperty]
    public partial ViewModelBase? CurrentPage { get; private set; }

    [ObservableProperty]
    public partial AppSection Section { get; private set; } = AppSection.Day;

    [ObservableProperty]
    public partial bool CanGoBack { get; private set; }

    // Bound to the window's Topmost - the MAUI build did this with SetWindowPos on Windows only
    [ObservableProperty]
    public partial bool IsPinned { get; set; }

    public Task StartAsync() => ShowAsync(_day, AppSection.Day, clearStack: true);

    public Task NavigateToSectionAsync(AppSection section)
    {
        if (section == Section && _backStack.Count == 0 && CurrentPage is not null)
            return Task.CompletedTask;

        ViewModelBase page = section switch
        {
            AppSection.Statistics => new StatisticsViewModel(_settingsService, _dataService, _vaultExportService, _dialogs),
            AppSection.Settings => new SettingsViewModel(_settingsService, _dataService, _dialogs, this),
            _ => _day
        };

        return ShowAsync(page, section, clearStack: true);
    }

    // Drill into a sub page (the Explorer rules of one process). Back returns to the caller.
    public Task PushAsync(ViewModelBase page)
    {
        if (CurrentPage is not null) _backStack.Push(CurrentPage);
        return ShowAsync(page, Section, clearStack: false);
    }

    // Leaves the current page: back to whatever pushed it, or to the day view from a section
    [RelayCommand]
    public Task GoBackAsync()
    {
        if (_backStack.Count > 0)
            return ShowAsync(_backStack.Pop(), Section, clearStack: false);

        return ShowAsync(_day, AppSection.Day, clearStack: true);
    }

    private async Task ShowAsync(ViewModelBase page, AppSection section, bool clearStack)
    {
        CurrentPage?.OnNavigatedFrom();
        if (clearStack) _backStack.Clear();

        CurrentPage = page;
        Section = section;
        CanGoBack = _backStack.Count > 0;

        await page.OnNavigatedToAsync();
    }
}
