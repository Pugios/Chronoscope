using Avalonia.Collections;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TimeViewer.ViewModels;

// Tag colours and the process -> tag table, the part of the MAUI SettingsPage that is edited
// all the time; the rarely touched paths live on the Settings page now.
// Nothing is written until Save; leaving any other way discards the edits, as Back did.
public partial class TagsViewModel : ViewModelBase
{
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Loading
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    private readonly SettingsService _settingsService;
    private readonly DataService _dataService;
    private readonly DialogService _dialogs;
    private readonly MainWindowViewModel _shell;

    public TagsViewModel(SettingsService settingsService, DataService dataService,
        DialogService dialogs, MainWindowViewModel shell)
    {
        _settingsService = settingsService;
        _dataService = dataService;
        _dialogs = dialogs;
        _shell = shell;
    }

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    public override async Task OnNavigatedToAsync()
    {
        IsBusy = true;
        try
        {
            LoadColors();
            await LoadProcessesAsync();
            LoadAvailableTags();
        }
        catch (Exception ex)
        {
            await _dialogs.AlertAsync("ManicTime Error", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public override void OnNavigatedFrom() => CleanupColors();

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Tag Colors
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

    [ObservableProperty]
    public partial TagColorRow[] TagColors { get; private set; } = [];

    private void LoadColors()
    {
        // Make sure every known tag has a colour before the swatches are built, including tags
        // that exist only inside an Explorer rule and never reached tags.csv
        foreach (var tag in _dataService.KnownTags)
            _settingsService.GetTagColor(tag);

        TagColors = _settingsService.TagColors
            .Select(a => new TagColorRow { Tag = a.Key, Color = Color.Parse(a.Value) })
            .ToArray();
    }

    private void CleanupColors()
    {
        var usedTags = _dataService.KnownTags;
        string[] exceptions = ["Remaining", "No Clue"];

        var tagsToRemove = TagColors
            .Select(c => c.Tag)
            .Except(usedTags)
            .Except(exceptions);

        foreach (var tag in tagsToRemove)
            _settingsService.DeleteTagColor(tag);
    }

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Process Tag Table
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

    // A collection view rather than the bare array: it is what the grid sorts, and what the
    // search box filters (the MAUI grid's per-column filter menus)
    [ObservableProperty]
    public partial DataGridCollectionView ProcessRows { get; private set; } = new(Array.Empty<ProcessRow>());

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    partial void OnSearchTextChanged(string value) => ProcessRows.Refresh();

    private bool MatchesSearch(object item) =>
        string.IsNullOrWhiteSpace(SearchText)
        || item is ProcessRow row
            && (row.Process.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
                || row.Tag.Contains(SearchText, StringComparison.OrdinalIgnoreCase));

    // Set by the view from the grid's selection, which is not bindable
    public IReadOnlyList<ProcessRow> SelectedRows { get; set; } = [];

    private async Task LoadProcessesAsync()
    {
        IReadOnlyList<AppsTagsTable> data = await _dataService.GetMergedDataAsync(forceReload: false);

        var rows = data
            .GroupBy(a => a.Process)
            .Select(g =>
            {
                var totalTime = TimeSpan.FromSeconds(g.Sum(a => a.DurationSeconds));
                return new ProcessRow
                {
                    Process = g.Key,
                    RootProcess = g.First().OriginalProcess,
                    Tag = g.First().Tag,
                    TotalTime = totalTime.Days > 0
                            ? totalTime.ToString(@"d\d\ hh\:mm")
                            : totalTime.ToString(@"hh\:mm"),
                    LastUsed = g.Max(a => a.End).ToString("yyyy-MM-dd HH:mm"),

                    TotalSeconds = totalTime.TotalSeconds,
                    LastUsedDate = g.Max(a => a.End)
                };
            })
            .OrderBy(r => r.Process)
            .ToArray();

        var view = new DataGridCollectionView(rows) { Filter = MatchesSearch };
        // The grid selects whatever the view calls current, which starts at the first row - and
        // a preselected row is one Assign would silently retag
        view.MoveCurrentToPosition(-1);
        ProcessRows = view;
    }

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Assign Tag to Processes
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

    [ObservableProperty]
    public partial string[] AvailableTags { get; private set; } = [];

    private void LoadAvailableTags()
    {
        // KnownTags, not CachedTags: a tag invented inside an Explorer rule lives only in
        // explorer-processes.csv, and used to be missing from this picker
        AvailableTags = _dataService.KnownTags.ToArray();
    }

    [ObservableProperty]
    public partial string? SelectedTag { get; set; }

    [ObservableProperty]
    public partial string NewTag { get; set; } = "";

    private readonly List<TagsTable> _pendingTagChanges = new();

    [ObservableProperty]
    public partial int PendingChangeCount { get; private set; }

    // Assign Tag to selected Processes
    [RelayCommand]
    private async Task AssignTag()
    {
        var tagToUse = string.IsNullOrWhiteSpace(NewTag) ? SelectedTag : NewTag.Trim();

        if (string.IsNullOrEmpty(tagToUse)) return;

        foreach (var row in SelectedRows)
        {
            if (row.Process != row.RootProcess)
            {
                await _dialogs.AlertAsync($"Not changing Tag for {row.Process}",
                    $"Tags for {row.RootProcess} Subprocess are managed via 'Configure Subprocesses'");
                continue;
            }

            row.Tag = tagToUse;
            _pendingTagChanges.Add(new TagsTable { Process = row.Process, Tag = tagToUse });
        }
        PendingChangeCount = _pendingTagChanges.Count;

        // If it is a new Tag
        if (!AvailableTags.Contains(tagToUse))
        {
            // Update Available Tags List
            AvailableTags = AvailableTags.Append(tagToUse).ToArray();

            // Update Color Collection
            string newColor = _settingsService.GetTagColor(tagToUse);
            TagColors = TagColors.Append(new TagColorRow { Color = Color.Parse(newColor), Tag = tagToUse }).ToArray();
        }

        NewTag = "";
    }

    //%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Turn Process into Explorer Process
    //%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    [RelayCommand]
    private async Task ConfigureSubprocesses()
    {
        if (SelectedRows.Count != 1)
        {
            await _dialogs.AlertAsync("Configure Subprocesses", "Select exactly one process in the table first.");
            return;
        }

        await _shell.PushAsync(new ExplorerRulesViewModel(_dataService, _shell, SelectedRows[0].RootProcess));
    }

    //%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Save
    //%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    [RelayCommand]
    private async Task Save()
    {
        // Save Color Changes. Each of these only marks the settings dirty; the single awaited
        // SaveAsync below is what actually writes, so leaving this page always lands one file.
        foreach (var row in TagColors)
        {
            _settingsService.SetTagColor(row.Tag, row.Hex);
        }
        await _settingsService.SaveAsync();

        // Save Tag Changes
        if (_pendingTagChanges.Any())
        {
            await _dataService.ApplyTagChangesAsync(_pendingTagChanges);
        }
        await _shell.CloseAsync();
    }

    [RelayCommand]
    private Task Discard() => _shell.CloseAsync();
}


// %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
// Classes to Bind to the UI
// %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

public partial class TagColorRow : ObservableObject
{
    public required string Tag { get; init; }

    [ObservableProperty]
    public partial Color Color { get; set; }

    // "#RRGGBB", the form settings.json has always held
    public string Hex => $"#{Color.R:X2}{Color.G:X2}{Color.B:X2}";
}

public partial class ProcessRow : ObservableObject
{
    public required string Process { get; init; }
    public required string RootProcess { get; init; }

    [ObservableProperty]
    public partial string Tag { get; set; } = "";

    public required string TotalTime { get; init; }
    public required string LastUsed { get; init; }
    // For Sorting
    public double TotalSeconds { get; init; }
    public DateTime LastUsedDate { get; init; }
}
