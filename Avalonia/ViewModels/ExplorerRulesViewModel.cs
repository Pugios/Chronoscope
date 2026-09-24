using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TimeViewer.ViewModels;

// Explorer rules for one multi-purpose process (a browser, a file manager): re-tag its activity
// by the document it had open, with a live preview of what the rules do. Ported from the MAUI
// ExplorerSettingsPage. Edits a working copy; only Confirm writes it back.
public partial class ExplorerRulesViewModel : ViewModelBase
{
    private readonly DataService _dataService;
    private readonly MainWindowViewModel _shell;

    public ExplorerRulesViewModel(DataService dataService, MainWindowViewModel shell, string processName)
    {
        _dataService = dataService;
        _shell = shell;
        ProcessName = processName;
        LoadPage();
    }

    public string ProcessName { get; }

    public static string[] Columns { get; } = ["Name", "DocName", "Domain"];
    public static string[] MatchTypes { get; } = ["Prefix", "Include", "Suffix"];

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Set Rule and Data Table

    // Displayed Rules
    [ObservableProperty]
    public partial ExplorerRule[] ExplorerRules { get; private set; } = [];

    [ObservableProperty]
    public partial ExplorerPreviewRow[] PreviewRows { get; private set; } = [];

    // Working copy of rules includes pending changes and is used for reordering
    private List<ExplorerRule> _workingRules = new();

    private void LoadPage()
    {
        // Load Tags for dropdown. KnownTags covers tags that only exist in an Explorer rule.
        AvailableTags = _dataService.KnownTags.ToArray();

        // Load existing rules for this process, as COPIES. Holding the DataService's own
        // instances meant RefreshRulesDisplay's Order reassignment wrote straight through to the
        // live rules, so reordering with the arrows and then pressing Cancel still reordered them.
        _workingRules = _dataService.ExplorerRules
            .Where(r => r.Process == ProcessName)
            .OrderBy(r => r.Order)
            .Select(r => new ExplorerRule(r))
            .ToList();

        RefreshRulesDisplay();
    }

    private void RefreshRulesDisplay()
    {
        // Reassign Order based on current list position
        for (int i = 0; i < _workingRules.Count; i++)
            _workingRules[i].Order = i + 1;

        ExplorerRules = _workingRules.ToArray();
        PreviewRows = BuildPreview().ToArray();
    }

    private List<ExplorerPreviewRow> BuildPreview()
    {
        List<AppsTagsDocumentsTable> data = DataService.ApplyExplorerRules(
            _dataService.CachedAppsTagsDocuments
                .Where(r => r.Process == ProcessName)
                .ToList(),
            _workingRules);

        // Collapse the intervals into one row per grouped task, keeping how long
        // and how recently it was used
        return data
            .GroupBy(r => (r.Process, r.Name, r.DocName, r.Domain, r.Tag))
            .Select(g =>
            {
                var totalTime = TimeSpan.FromSeconds(g.Sum(r => r.DurationSeconds));
                var lastUsed = g.Max(r => r.End);
                return new ExplorerPreviewRow
                {
                    Process = g.Key.Process,
                    Name = g.Key.Name,
                    DocName = g.Key.DocName,
                    Domain = g.Key.Domain,
                    Tag = g.Key.Tag,
                    TotalTime = totalTime.Days > 0
                            ? totalTime.ToString(@"d\d\ hh\:mm")
                            : totalTime.ToString(@"hh\:mm"),
                    LastUsed = lastUsed.ToString("yyyy-MM-dd HH:mm"),

                    TotalSeconds = totalTime.TotalSeconds,
                    LastUsedDate = lastUsed
                };
            })
            .OrderByDescending(r => r.LastUsedDate)
            .ToList();
    }

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Rule Creation

    // Name, DocName, or Domain Column
    [ObservableProperty]
    public partial string SelectedColumn { get; set; } = "Name";

    // Prefix/Include/Suffix
    [ObservableProperty]
    public partial string SelectedMatchType { get; set; } = "Prefix";

    // Pattern to match
    [ObservableProperty]
    public partial string Pattern { get; set; } = "";

    // Tag
    [ObservableProperty]
    public partial string[] AvailableTags { get; private set; } = [];

    [ObservableProperty]
    public partial string? SelectedTag { get; set; }

    [ObservableProperty]
    public partial string NewTag { get; set; } = "";

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Rule Changes
    [RelayCommand]
    private void AddRule()
    {
        var tagToUse = string.IsNullOrWhiteSpace(NewTag) ? SelectedTag : NewTag.Trim();

        if (string.IsNullOrWhiteSpace(Pattern) || string.IsNullOrWhiteSpace(tagToUse))
            return;

        _workingRules.Add(new ExplorerRule
        {
            Process = ProcessName,
            Column = SelectedColumn,
            MatchType = SelectedMatchType,
            Pattern = Pattern,
            Tag = tagToUse
        });

        if (!AvailableTags.Contains(tagToUse))
            AvailableTags = [.. AvailableTags, tagToUse];

        Pattern = "";
        NewTag = "";
        SelectedTag = null;
        RefreshRulesDisplay();
    }

    [RelayCommand]
    private void DeleteRule(ExplorerRule rule)
    {
        _workingRules.Remove(rule);
        RefreshRulesDisplay();
    }

    [RelayCommand]
    private void MoveUp(ExplorerRule rule)
    {
        int index = _workingRules.IndexOf(rule);
        if (index <= 0) return;

        (_workingRules[index], _workingRules[index - 1]) =
            (_workingRules[index - 1], _workingRules[index]);

        RefreshRulesDisplay();
    }

    [RelayCommand]
    private void MoveDown(ExplorerRule rule)
    {
        int index = _workingRules.IndexOf(rule);
        if (index < 0 || index >= _workingRules.Count - 1) return;

        (_workingRules[index], _workingRules[index + 1]) =
            (_workingRules[index + 1], _workingRules[index]);

        RefreshRulesDisplay();
    }

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Save/Discard Changes
    [RelayCommand]
    private async Task Confirm()
    {
        await _dataService.ReplaceExplorerRulesAsync(ProcessName, _workingRules);
        await _shell.GoBackAsync();
    }

    [RelayCommand]
    private Task Cancel() => _shell.GoBackAsync();
}


// %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
// Classes to Bind to the UI
// %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

public class ExplorerPreviewRow
{
    public required string Process { get; init; }
    public required string Name { get; init; }
    public required string DocName { get; init; }
    public required string Domain { get; init; }
    public required string Tag { get; init; }
    public required string TotalTime { get; init; }
    public required string LastUsed { get; init; }
    // For Sorting
    public double TotalSeconds { get; init; }
    public DateTime LastUsedDate { get; init; }
}
