using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TimeViewer.ViewModels;

// The top level pages the navigation pane switches between
public enum AppSection { Day, Statistics, Tags, Settings }

// Shell of the app: which page is on screen, the history around it, and the window pin.
// Replaces MAUI's Shell routing.
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

    [ObservableProperty]
    public partial bool CanGoForward { get; private set; }

    // Bound to the window's Topmost - the MAUI build did this with SetWindowPos on Windows only
    [ObservableProperty]
    public partial bool IsPinned { get; set; }

    public Task StartAsync() => VisitAsync(new HistoryEntry(_day, AppSection.Day));

    public Task NavigateToSectionAsync(AppSection section)
    {
        if (section == Section && CurrentPage is not null && _history[_index].Page is not ExplorerRulesViewModel)
            return Task.CompletedTask;

        ViewModelBase page = section switch
        {
            AppSection.Statistics => new StatisticsViewModel(_settingsService, _dataService, _vaultExportService, _dialogs),
            AppSection.Tags => new TagsViewModel(_settingsService, _dataService, _dialogs, this),
            AppSection.Settings => new SettingsViewModel(_settingsService, _dialogs, this),
            _ => _day
        };

        return VisitAsync(new HistoryEntry(page, section));
    }

    // Drill into a sub page (the Explorer rules of one process), within the current section
    public Task PushAsync(ViewModelBase page) => VisitAsync(new HistoryEntry(page, Section));

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // History
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Browser style: every page visited is an entry, Back and Forward (the pane's arrow and the
    // mouse's thumb buttons) walk it, and visiting a new page drops whatever lay ahead. Entries
    // keep their view model, so going back to Tags finds the same page, unsaved edits included,
    // the way returning to the MAUI SettingsPage from the Explorer page did.

    private const int MaxHistory = 50;

    private sealed record HistoryEntry(ViewModelBase Page, AppSection Section);

    private readonly List<HistoryEntry> _history = new();
    private int _index = -1;

    [RelayCommand]
    public Task GoBackAsync() => _index > 0 ? MoveToAsync(_index - 1) : Task.CompletedTask;

    [RelayCommand]
    public Task GoForwardAsync() =>
        _index < _history.Count - 1 ? MoveToAsync(_index + 1) : Task.CompletedTask;

    // A page finishing itself (Save, Discard, Confirm, Cancel): back to where it was opened from,
    // and the page is gone - Forward must not bring back an editor whose work is already done.
    // The very first page has nothing behind it, so there it falls back to the day view.
    public Task CloseAsync()
    {
        if (_index <= 0)
            return VisitAsync(new HistoryEntry(_day, AppSection.Day));

        _history.RemoveRange(_index, _history.Count - _index);
        return MoveToAsync(_index - 1);
    }

    private Task VisitAsync(HistoryEntry entry)
    {
        // Anything ahead of the current page is no longer reachable, as in a browser
        if (_index < _history.Count - 1)
            _history.RemoveRange(_index + 1, _history.Count - _index - 1);

        _history.Add(entry);
        if (_history.Count > MaxHistory) _history.RemoveAt(0);

        return MoveToAsync(_history.Count - 1);
    }

    private async Task MoveToAsync(int index)
    {
        CurrentPage?.OnNavigatedFrom();

        _index = index;
        var entry = _history[index];
        CurrentPage = entry.Page;
        Section = entry.Section;
        CanGoBack = _index > 0;
        CanGoForward = _index < _history.Count - 1;

        await entry.Page.OnNavigatedToAsync();
    }
}
