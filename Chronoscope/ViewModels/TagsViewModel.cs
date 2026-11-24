using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Collections;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Chronoscope.ViewModels;

// Tags, their colours and groups, and the process -> tag table: the part of the MAUI SettingsPage
// that is edited all the time; the rarely touched paths live on the Settings page now.
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
            EnsureColors();
            await LoadProcessesAsync();
            LoadAvailableTags();

            // The board is built on the first visit only. Coming back to this page - from the
            // Explorer rules it opened, or by Back - must find the edits still there, unsaved
            // ones included; it only gains whatever tags appeared meanwhile.
            if (!_boardLoaded)
            {
                LoadBoard();   // after the processes: loading them is what reads tag-groups.csv
                _boardLoaded = true;
            }
            else
            {
                AddNewTagsToBoard();
            }
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

    // Make sure every known tag has a colour before the board is built, including tags that
    // exist only inside an Explorer rule and never reached tags.csv
    private void EnsureColors()
    {
        foreach (var tag in _dataService.KnownTags)
            _settingsService.GetTagColor(tag);
    }

    private void CleanupColors()
    {
        var usedTags = _dataService.KnownTags;
        string[] exceptions = ["Remaining", "No Clue"];

        // A grouped tag keeps its colour even before any process uses it: it was made on purpose
        var tagsToRemove = AllTags
            .Select(c => c.Tag)
            .Except(usedTags)
            .Except(exceptions)
            .Except(_dataService.GroupByTag.Keys)
            .ToList();

        foreach (var tag in tagsToRemove)
            _settingsService.DeleteTagColor(tag);
    }

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Tags & Groups Board
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Every tag is a chip with its colour; a group is a tinted box holding its tags side by side,
    // with an editable name and a colour of its own. The view drags chips around and reports where
    // they were dropped (DropOnTag / DropOnGroup / DropOnEmpty); everything else happens here.
    //
    // Each tag is in at most one group. A group exists for as long as it holds a tag: dragging
    // its last tag out dissolves it. Nothing is written until Save - the groups to tag-groups.csv
    // beside tags.csv, the colours to settings.json.

    // What the board shows, in order: the groups, then the loose tags, each sorted by name, and
    // last the cell that adds a new tag
    public ObservableCollection<object> Board { get; } = new();

    public AddTagCell AddTag { get; } = new();

    private readonly List<TagGroupBox> _groups = new();
    private readonly List<TagColorRow> _loose = new();

    private IEnumerable<TagColorRow> AllTags => _groups.SelectMany(g => g.Tags).Concat(_loose);

    // What was loaded, to tell at Save whether the groups file needs writing at all
    private Dictionary<string, string> _loadedGroups = new();

    private void LoadBoard()
    {
        _loadedGroups = _dataService.GroupByTag.ToDictionary(kv => kv.Key, kv => kv.Value);

        foreach (var group in _groups) group.PropertyChanged -= OnGroupChanged;
        _groups.Clear();
        _loose.Clear();

        // Every tag with a colour (the colour-only "Remaining" included, as the colour list always
        // had it), plus any grouped tag no longer used anywhere, so a stale entry can be cleared
        var tags = _settingsService.TagColors.Keys.Concat(_loadedGroups.Keys).Distinct().ToList();
        var groupsByName = new Dictionary<string, TagGroupBox>();

        foreach (var tag in tags)
        {
            var chip = new TagColorRow { Tag = tag, Color = Color.Parse(_settingsService.GetTagColor(tag)) };

            if (_loadedGroups.TryGetValue(tag, out var name))
            {
                if (!groupsByName.TryGetValue(name, out var group))
                {
                    group = NewGroupBox(name, Color.Parse(_settingsService.GetGroupColor(name)));
                    groupsByName[name] = group;
                }
                group.Tags.Add(chip);
            }
            else
            {
                _loose.Add(chip);
            }
        }

        RebuildBoard();
    }

    private bool _boardLoaded;

    // Tags that came into being while this page was away (an Explorer rule naming a new one)
    private void AddNewTagsToBoard()
    {
        var onBoard = AllTags.Select(t => t.Tag).ToHashSet();
        var added = _settingsService.TagColors.Keys.Where(t => !onBoard.Contains(t)).ToList();
        if (added.Count == 0) return;

        foreach (var tag in added)
            _loose.Add(new TagColorRow { Tag = tag, Color = Color.Parse(_settingsService.GetTagColor(tag)) });
        RebuildBoard();
    }

    private TagGroupBox NewGroupBox(string name, Color color)
    {
        var group = new TagGroupBox { Name = name, CommittedName = name, Color = color };
        group.PropertyChanged += OnGroupChanged;
        _groups.Add(group);
        return group;
    }

    // Lays the board out again after a move: groups first, then loose tags, each by name, and
    // every chip tinted for where it now sits
    private void RebuildBoard()
    {
        _groups.RemoveAll(g =>
        {
            if (g.Tags.Count > 0) return false;
            g.PropertyChanged -= OnGroupChanged;
            return true;
        });

        foreach (var group in _groups)
        {
            var sorted = group.Tags.OrderBy(t => t.Tag, StringComparer.CurrentCultureIgnoreCase).ToList();
            group.Tags.Clear();
            foreach (var chip in sorted)
            {
                chip.Group = group;
                group.Tags.Add(chip);
            }
            group.LayOut();
        }
        foreach (var chip in _loose) chip.Group = null;

        Board.Clear();
        foreach (var group in _groups.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase))
            Board.Add(group);
        foreach (var chip in _loose.OrderBy(t => t.Tag, StringComparer.CurrentCultureIgnoreCase))
            Board.Add(chip);
        Board.Add(AddTag);
    }

    // The Add Tag cell's name, once typed: a new loose tag with a colour of its own, and a choice
    // in the process table's tag picker. A name already on the board adds nothing.
    //
    // A tag is only kept for good once something uses it - a process, an Explorer rule, or a
    // group. One added here and left loose and unused is tidied away when the page is left.
    public void AddNewTag(string name)
    {
        name = name.Trim();
        AddTag.IsEditing = false;
        AddTag.Name = "";

        bool fromPicker = _selectAfterAdd;
        _selectAfterAdd = false;
        if (name.Length == 0) return;

        var existing = AllTags.FirstOrDefault(t => string.Equals(t.Tag, name, StringComparison.CurrentCultureIgnoreCase));
        if (existing is null)
        {
            _loose.Add(new TagColorRow { Tag = name, Color = Color.Parse(_settingsService.GetTagColor(name)) });
            _addedTags.Add(name);
            if (!AvailableTags.Contains(name))
                AvailableTags = AvailableTags.Append(name).Order().ToArray();
            RebuildBoard();
        }

        // Started from the process table's picker: come back with the tag chosen there, ready to
        // Assign - the one just made, or the existing one of that name
        if (fromPicker)
        {
            var tag = existing?.Tag ?? name;
            if (AvailableTags.Contains(tag)) SelectedChoice = tag;
        }
    }

    // Escape, or nothing typed: the cell closes and nothing changes
    public void CancelAddTag()
    {
        AddTag.Name = "";
        AddTag.IsEditing = false;
        _selectAfterAdd = false;
    }

    // Tags made here this visit; the picker keeps them even when it is reloaded on a return to
    // the page, though nothing uses them yet
    private readonly HashSet<string> _addedTags = new();

    // A chip dropped on another chip: join that chip's group, or - if it has none - found a new
    // group with it. The new group takes the colour of the tag it was dropped on.
    public void DropOnTag(TagColorRow dragged, TagColorRow target)
    {
        if (dragged == target) return;

        if (target.Group is { } group)
        {
            DropOnGroup(dragged, group);
            return;
        }

        Detach(dragged);
        _loose.Remove(target);
        var created = NewGroupBox(UniqueGroupName("Group"), target.Color);
        created.Tags.Add(target);
        created.Tags.Add(dragged);
        RebuildBoard();
    }

    public void DropOnGroup(TagColorRow dragged, TagGroupBox group)
    {
        if (dragged.Group == group) return;

        Detach(dragged);
        group.Tags.Add(dragged);
        RebuildBoard();
    }

    // Dropped on the board's free space: out of its group, back among the loose tags
    public void DropOnEmpty(TagColorRow dragged)
    {
        if (dragged.Group is null) return;

        Detach(dragged);
        _loose.Add(dragged);
        RebuildBoard();
    }

    private void Detach(TagColorRow chip)
    {
        if (chip.Group is { } group) group.Tags.Remove(chip);
        else _loose.Remove(chip);
    }

    private string UniqueGroupName(string stem)
    {
        var taken = _groups.Select(g => g.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        if (!taken.Contains(stem)) return stem;

        for (int i = 2; ; i++)
            if (!taken.Contains($"{stem} {i}")) return $"{stem} {i}";
    }

    // A rename is only accepted if it leaves a name, and one no other group uses - two groups of
    // the same name would be one group in tag-groups.csv. Anything else goes back to the old name.
    private void OnGroupChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not TagGroupBox group) return;

        if (e.PropertyName == nameof(TagGroupBox.Name))
        {
            var name = group.Name.Trim();
            bool taken = _groups.Any(g => g != group && string.Equals(g.Name, name, StringComparison.CurrentCultureIgnoreCase));

            if (name.Length == 0 || taken)
            {
                group.Name = group.CommittedName;   // raises this again, with a valid name
                return;
            }

            if (name != group.Name) { group.Name = name; return; }
            if (name == group.CommittedName) return;

            group.CommittedName = name;
            RebuildBoard();   // keeps the groups sorted by name
        }
    }

    private async Task SaveGroupsAsync()
    {
        var groups = _groups
            .SelectMany(g => g.Tags.Select(t => (t.Tag, g.CommittedName)))
            .ToDictionary(x => x.Tag, x => x.CommittedName);

        bool changed = groups.Count != _loadedGroups.Count
                       || groups.Any(kv => !_loadedGroups.TryGetValue(kv.Key, out var g) || g != kv.Value);
        if (changed)
            await _dataService.ReplaceTagGroupsAsync(groups);

        // Colours for the groups that exist after the save; a renamed or dissolved group's old
        // colour goes
        foreach (var group in _groups)
            _settingsService.SetGroupColor(group.CommittedName, group.Hex);

        var inUse = _groups.Select(g => g.CommittedName).ToHashSet();
        foreach (var stale in _settingsService.GroupColors.Keys.Where(g => !inUse.Contains(g)).ToList())
            _settingsService.DeleteGroupColor(stale);
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

        // Assignments made but not yet saved stay visible when the table is rebuilt on a return
        // to this page; they are still waiting in _pendingTagChanges either way
        var byProcess = rows.ToDictionary(r => r.Process);
        foreach (var pending in _pendingTagChanges)
            if (byProcess.TryGetValue(pending.Process, out var row)) row.Tag = pending.Tag;

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
    [NotifyPropertyChangedFor(nameof(TagChoices))]
    public partial string[] AvailableTags { get; private set; } = [];

    private void LoadAvailableTags()
    {
        // KnownTags, not CachedTags: a tag invented inside an Explorer rule lives only in
        // explorer-processes.csv, and used to be missing from this picker
        AvailableTags = _dataService.KnownTags.Union(_addedTags).Order().ToArray();
    }

    // What the picker lists: every tag, then "Add Tag". That last one is an object of its own
    // rather than a string, so a tag that happens to be called "Add Tag" can never be mistaken
    // for it.
    public object[] TagChoices => [.. AvailableTags, AddTagChoice.Instance];

    [ObservableProperty]
    public partial object? SelectedChoice { get; set; }

    // The tag the picker holds, for Assign; null while nothing (or "Add Tag") is chosen
    public string? SelectedTag => SelectedChoice as string;

    private object? _previousChoice;
    private bool _selectAfterAdd;

    // Raised when "Add Tag" is picked; the view scrolls up to the board and opens its Add Tag cell
    public event Action? AddTagRequested;

    partial void OnSelectedChoiceChanged(object? oldValue, object? newValue)
    {
        if (newValue is not AddTagChoice)
        {
            _previousChoice = newValue;
            return;
        }

        // "Add Tag" is an action, not a choice: the picker goes back to what it held - after this
        // change has finished, since the ComboBox is still in the middle of applying it - and
        // the new tag is selected once it is made
        _selectAfterAdd = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => SelectedChoice = _previousChoice);
        AddTagRequested?.Invoke();
    }

    private readonly List<TagsTable> _pendingTagChanges = new();

    [ObservableProperty]
    public partial int PendingChangeCount { get; private set; }

    // Assign Tag to selected Processes
    [RelayCommand]
    private async Task AssignTag()
    {
        // New tags are made with the board's Add Tag cell, which also puts them in this picker
        var tagToUse = SelectedTag;

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
        await SaveChangesAsync();
        await _shell.CloseAsync();
    }

    private async Task SaveChangesAsync()
    {
        // Save Color Changes. Each of these only marks the settings dirty; the single awaited
        // SaveAsync below is what actually writes, so leaving this page always lands one file.
        foreach (var row in AllTags)
        {
            _settingsService.SetTagColor(row.Tag, row.Hex);
        }
        await SaveGroupsAsync();   // writes tag-groups.csv, and marks the group colours dirty
        await _settingsService.SaveAsync();

        // Save Tag Changes
        if (_pendingTagChanges.Any())
        {
            await _dataService.ApplyTagChangesAsync(_pendingTagChanges);
        }
        ForgetChanges();
    }

    [RelayCommand]
    private Task Discard()
    {
        ForgetChanges();
        return _shell.CloseAsync();
    }

    // Saved, or thrown away: either way nothing is pending any more, and should this page come
    // back by Back or Forward it rebuilds from what is stored
    private void ForgetChanges()
    {
        _pendingTagChanges.Clear();
        PendingChangeCount = 0;
        _boardLoaded = false;
    }

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Leaving With Unsaved Changes
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Leaving this page any way other than Save or Discard - another section, Back / Forward, the
    // mouse's thumb buttons, closing the window - asks first if anything was changed.

    public bool HasUnsavedChanges
    {
        get
        {
            if (_pendingTagChanges.Count > 0) return true;
            if (!_boardLoaded) return false;

            // Colours are compared as colours, not as text: settings.json may hold "#a1b2c3"
            // where the picker hands back "#A1B2C3"
            static bool Same(string? stored, Color shown) =>
                stored is not null && Color.TryParse(stored, out var c) && c == shown;

            if (AllTags.Any(t => !Same(_settingsService.TagColors.GetValueOrDefault(t.Tag), t.Color)))
                return true;
            if (_groups.Any(g => !Same(_settingsService.GroupColors.GetValueOrDefault(g.CommittedName), g.Color)))
                return true;

            var groups = _groups.SelectMany(g => g.Tags.Select(t => (t.Tag, g.CommittedName))).ToList();
            return groups.Count != _loadedGroups.Count
                   || groups.Any(x => !_loadedGroups.TryGetValue(x.Tag, out var g) || g != x.CommittedName);
        }
    }

    public override async Task<bool> ConfirmLeaveAsync()
    {
        if (!HasUnsavedChanges) return true;

        var choice = await _dialogs.AskAsync("Unsaved changes",
            "You changed tags, colours or groups on this page without saving. Save them before leaving?",
            "Save", "Don't save", "Cancel");

        switch (choice)
        {
            case DialogChoice.Primary:
                try
                {
                    await SaveChangesAsync();
                    return true;
                }
                catch (Exception ex)
                {
                    // Nothing was lost: stay on the page with the edits, so Save can be retried
                    await _dialogs.AlertAsync("Could not save", ex.Message);
                    return false;
                }
            case DialogChoice.Secondary:
                ForgetChanges();
                return true;
            default:
                return false;   // stay on the page, edits intact
        }
    }
}


// %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
// Classes to Bind to the UI
// %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

// One tag chip on the board: its colour, and the group it currently sits in (null when loose).
// The chip's background is its group's tint, so the tags of one group read as one block.
public partial class TagColorRow : ObservableObject
{
    public required string Tag { get; init; }

    [ObservableProperty]
    public partial Color Color { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChipBackground))]
    public partial TagGroupBox? Group { get; set; }

    // "#RRGGBB", the form settings.json has always held
    public string Hex => $"#{Color.R:X2}{Color.G:X2}{Color.B:X2}";

    // A neutral wash when loose, the group's colour when grouped
    public IBrush ChipBackground => Group?.ChipTint ?? LooseChip;

    private static readonly IBrush LooseChip = new ImmutableSolidColorBrush(Color.FromArgb(0x22, 0x80, 0x80, 0x80));

    // The group's colour changed: the tint follows
    internal void RefreshBackground() => OnPropertyChanged(nameof(ChipBackground));
}

// One group on the board: a tinted box with its name, its colour and its tags
public partial class TagGroupBox : ObservableObject
{
    // Edited in place on the board; TagsViewModel checks every change and puts back the last
    // accepted name (CommittedName) if the new one is empty or already taken
    [ObservableProperty]
    public partial string Name { get; set; } = "";

    public string CommittedName { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Background))]
    [NotifyPropertyChangedFor(nameof(ChipTint))]
    public partial Color Color { get; set; }

    partial void OnColorChanged(Color value)
    {
        foreach (var chip in Tags) chip.RefreshBackground();
    }

    public ObservableCollection<TagColorRow> Tags { get; } = new();

    // What the box draws: its name-and-colour cell first, then its tags, in a grid kept as close
    // to square as it can be. The name cell counts as a cell, so the tags fill in beside it:
    // three tags make a 2 x 2 with the name in the first corner.
    public ObservableCollection<object> Cells { get; } = new();

    [ObservableProperty]
    public partial int Columns { get; private set; } = 1;

    public GroupHeaderCell Header => _header ??= new GroupHeaderCell { Group = this };
    private GroupHeaderCell? _header;

    public void LayOut()
    {
        Cells.Clear();
        Cells.Add(Header);
        foreach (var chip in Tags) Cells.Add(chip);
        Columns = (int)Math.Ceiling(Math.Sqrt(Cells.Count));
    }

    public string Hex => $"#{Color.R:X2}{Color.G:X2}{Color.B:X2}";

    // The box: a light wash of the group's colour. Its chips: a stronger one, so they stand out
    // within it but are still plainly of the same colour.
    public IBrush Background => new ImmutableSolidColorBrush(Color.FromArgb(0x26, Color.R, Color.G, Color.B));
    public IBrush ChipTint => new ImmutableSolidColorBrush(Color.FromArgb(0x55, Color.R, Color.G, Color.B));
}

// The first cell of a group box: its colour and its name, with no outline of their own
public class GroupHeaderCell
{
    public required TagGroupBox Group { get; init; }
}

// The process table picker's last entry, which opens the board's Add Tag cell
public sealed class AddTagChoice
{
    public static readonly AddTagChoice Instance = new();
    private AddTagChoice() { }
    public override string ToString() => "Add Tag";
}

// The board's last cell: "Add Tag", which turns into a name box when clicked
public partial class AddTagCell : ObservableObject
{
    [ObservableProperty]
    public partial bool IsEditing { get; set; }

    [ObservableProperty]
    public partial string Name { get; set; } = "";
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
