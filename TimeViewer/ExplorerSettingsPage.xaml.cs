namespace TimeViewer;

[QueryProperty(nameof(ProcessName), "process")]
public partial class ExplorerSettingsPage : ContentPage
{
    private readonly DataService _dataService;
    public ExplorerSettingsPage(DataService dataService)
    {
        InitializeComponent();
        BindingContext = this;
        _dataService = dataService;
    }

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Set Rule and Data Table
    private string _processName = "";
    public string ProcessName
    {
        get => _processName;
        set
        {
            _processName = Uri.UnescapeDataString(value);
            OnPropertyChanged(nameof(ProcessName));
            LoadPage();
        }
    }

    // Displayed Rules
    private ExplorerRule[] _explorerRules = [];
    public ExplorerRule[] ExplorerRules
    {
        get => _explorerRules;
        set { _explorerRules = value; OnPropertyChanged(nameof(ExplorerRules)); }
    }

    // Working copy of rules includes pending changes and is used for reordering
    private List<ExplorerRule> _workingRules = new();
    
    private void LoadPage()
    {
        PageTitle.Text = _processName;

        // Load Tags for dropdown
        AvailableTags = _dataService.CachedTags
            .Select(t => t.Tag)
            .Distinct()
            .OrderBy(t => t)
            .ToArray();


        // Load existing rules for this process
        _workingRules = _dataService.ExplorerRules
            .Where(r => r.Process == _processName)
            .OrderBy(r => r.Order)
            .ToList();

        RefreshRulesDisplay();
    }

    private void RefreshRulesDisplay()
    {
        // Reassign Order based on current list position
        for (int i = 0; i < _workingRules.Count; i++)
            _workingRules[i].Order = i + 1;

        ExplorerRules = _workingRules.ToArray();
        ExplorerDataGrid.ItemsSource = BuildPreview();
    }
    private List<AppsTagsDocumentsTable> BuildPreview()
    {
        return DataService.ApplyExplorerRules(
            _dataService.CachedAppsTagsDocuments
                .Where(r => r.Process == _processName)
                .ToList(),
            _workingRules);
    }

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Rule Creation
    // Name, DocName, or Domain Column
    private string _selectedColumn = "Name";
    public string SelectedColumn
    {
        get => _selectedColumn;
        set { 
            _selectedColumn = value; 
            OnPropertyChanged(nameof(SelectedColumn)); 
        }
    }
    private void OnColumnCheckedChanged(object? sender, CheckedChangedEventArgs e)
    {
        if (e.Value && sender is RadioButton rb)
            SelectedColumn = rb.Value.ToString()!;
    }

    // Prefix/Suffix
    private string _selectedMatchType = "Prefix";
    public string SelectedMatchType
    {
        get => _selectedMatchType;
        set { 
            _selectedMatchType = value; 
            OnPropertyChanged(nameof(SelectedMatchType)); 
        }
    }

    // Pattern to match
    private string _pattern = "";
    public string Pattern
    {
        get => _pattern;
        set { _pattern = value; OnPropertyChanged(nameof(Pattern)); }
    }
    private void OnMatchTypeCheckedChanged(object? sender, CheckedChangedEventArgs e)
    {
        if (e.Value && sender is RadioButton rb)
            SelectedMatchType = rb.Value.ToString()!;
    }

    // Tag
    private string[] _availableTags = [];
    public string[] AvailableTags
    {
        get => _availableTags;
        set { 
            _availableTags = value; 
            OnPropertyChanged(nameof(AvailableTags)); 
        }
    }

    private string _selectedTag = "";
    public string SelectedTag
    {
        get => _selectedTag;
        set { _selectedTag = value; OnPropertyChanged(nameof(SelectedTag)); }
    }

    private string _newTag = "";
    public string NewTag
    {
        get => _newTag;
        set { _newTag = value; OnPropertyChanged(nameof(NewTag)); }
    }

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Rule Changes
    private void OnAddRuleClicked(object? sender, EventArgs e)
    {
        var tagToUse = string.IsNullOrWhiteSpace(_newTag) ? _selectedTag : _newTag;
        
        if (string.IsNullOrWhiteSpace(_pattern) || string.IsNullOrWhiteSpace(tagToUse))
            return;


        _workingRules.Add(new ExplorerRule
        {
            Process = _processName,
            Column = _selectedColumn,
            MatchType = _selectedMatchType,
            Pattern = _pattern,
            Tag = tagToUse
        });

        if (!AvailableTags.Contains(tagToUse))
            AvailableTags = [.. AvailableTags, tagToUse];

        Pattern = "";
        NewTag = "";
        SelectedTag = "";
        RefreshRulesDisplay();
    }

    private void OnDeleteRuleClicked(object? sender, EventArgs e)
    {
        if (sender is Button btn && btn.BindingContext is ExplorerRule rule)
        {
            _workingRules.Remove(rule);
            RefreshRulesDisplay();
        }
    }

    private void OnUpClicked(object? sender, EventArgs e)
    {
        if (sender is Button btn && btn.BindingContext is ExplorerRule rule)
        {
            int index = _workingRules.IndexOf(rule);
            if (index <= 0) return;

            (_workingRules[index], _workingRules[index - 1]) =
                (_workingRules[index - 1], _workingRules[index]);

            RefreshRulesDisplay();
        }
    }

    private void OnDownClicked(object? sender, EventArgs e)
    {
        if (sender is Button btn && btn.BindingContext is ExplorerRule rule)
        {
            int index = _workingRules.IndexOf(rule);
            if (index < 0 || index >= _workingRules.Count - 1) return;

            (_workingRules[index], _workingRules[index + 1]) =
                (_workingRules[index + 1], _workingRules[index]);

            RefreshRulesDisplay();
        }
    }


    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Save/Discard Changes
    private async void OnConfirmClicked(object? sender, EventArgs e)
    {
        await _dataService.ReplaceExplorerRulesAsync(_processName, _workingRules);
        await Shell.Current.GoToAsync("..");
    }

    private async void OnCancelClicked(object? sender, EventArgs e)
    {
        await Shell.Current.GoToAsync("..");
    }
}