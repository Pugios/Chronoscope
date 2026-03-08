using Maui.ColorPicker;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using Syncfusion.Maui.Data;
using System.ComponentModel;

namespace TimeViewer;

public partial class SettingsPage : ContentPage, INotifyPropertyChanged
{
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Loading Settings
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    private readonly SettingsService _settingsService;
    private readonly DataService _dataService;

    public SettingsPage(SettingsService settingsService, DataService dataService)
    {
        InitializeComponent();
        BindingContext = this;
        _settingsService = settingsService;
        _dataService = dataService;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        LoadColors();
        await LoadProcessesAsync();
        LoadAvailableTags();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        CleanupColors();
    }

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Tag Colors
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

    private TagColorRow[] _tagColors = [];
    public TagColorRow[] TagColors
    {
        get => _tagColors;
        set
        {
            _tagColors = value;
            OnPropertyChanged(nameof(TagColors));
        }
    }

    private void LoadColors()
    {
        TagColors = _settingsService.TagColors.Select(a =>
        {
            var color = SKColor.Parse(a.Value).ToMauiColor();
            return new TagColorRow
            {
                Tag = a.Key,
                Color = color
            };
        }).ToArray();
    }

    private void OnColorTapped(object? sender, TappedEventArgs e)
    {
        if (sender is VisualElement ve && ve.BindingContext is TagColorRow row)
        {
            row.IsPickerVisible = !row.IsPickerVisible;
        }
    }

    private void ColorPicker_PickedColorChanged(object sender, PickedColorChangedEventArgs e)
    {
        if (sender is VisualElement ve && ve.BindingContext is TagColorRow row)
        {
            // ignore the initial Color Change to the center of the picker
            if (row.IgnoreFirstPickerChange)
            {
                row.IgnoreFirstPickerChange = false;
                return;
            }

            row.Color = e.NewPickedColorValue.ToSKColor().ToMauiColor();
            row.IsPickerVisible = false;
        }
    }

    private void CleanupColors()
    {
        string[] exceptions = ["Remaining", "No Clue"];
        var usedTags = _dataService.CachedTags.Select(t => t.Tag).Distinct();

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

    private ProcessRow[] _processRows = [];
    public ProcessRow[] ProcessRows
    {
        get => _processRows;
        set
        {
            _processRows = value;
            OnPropertyChanged(nameof(ProcessRows));
        }
    }

    private async Task LoadProcessesAsync()
    {
        var apps = await _dataService.GetMergedDataAsync(forceReload: false);

        ProcessRows = apps
            .GroupBy(a => a.Process)
            .Select(g =>
            {
                var totalTime = TimeSpan.FromSeconds(g.Sum(a => TimeSpan.Parse(a.Duration).TotalSeconds));
                return new ProcessRow
                {
                    Process = g.Key,
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
    }

    private string[] _availableTags = [];
    public string[] AvailableTags
    {
        get => _availableTags;
        set
        {
            _availableTags = value;
            OnPropertyChanged(nameof(AvailableTags));
        }
    }
    private void LoadAvailableTags()
    {
        AvailableTags = _dataService.CachedTags
            .Select(t => t.Tag)
            .Distinct()
            .OrderBy(t => t)
            .ToArray();
    }

    // Assign Tag to Selected Processes
    private string _selectedAssignTag;
    public string SelectedAssignTag
    {
        get => _selectedAssignTag;
        set
        {
            _selectedAssignTag = value;
            OnPropertyChanged(nameof(SelectedAssignTag));
        }
    }
    private readonly List<TagTable> _pendingTagChanges = new();

    private void OnAssignTagClicked(object? sender, EventArgs e)
    {
        if (string.IsNullOrEmpty(SelectedAssignTag)) return;

        foreach (var selectedRow in ProcessDataGrid.SelectedRows)
        {
            if (selectedRow is ProcessRow row)
            {
                row.Tag = SelectedAssignTag;
                _pendingTagChanges.Add(new TagTable { Process = row.Process, Tag = SelectedAssignTag });
            }
        }
    }

    // Assign new Tag to Selected Processes
    private void OnCreateAndAssignClicked(object? sender, EventArgs e)
    {
        var newTag = NewTagEntry.Text?.Trim();
        if (string.IsNullOrEmpty(newTag)) return;

        // Assign to selected rows same as before
        foreach (var selectedRow in ProcessDataGrid.SelectedRows)
        {
            if (selectedRow is ProcessRow row)
            {
                row.Tag = newTag;
                _pendingTagChanges.Add(new TagTable { Process = row.Process, Tag = newTag });
            }
        }

        NewTagEntry.Text = string.Empty;

        // Update Available Tags List
        AvailableTags = AvailableTags.Append(newTag).ToArray();

        // Update Color Collection
        string newColor= _settingsService.GetTagColor(newTag);
        Color newMAUIColor = SKColor.Parse(newColor).ToMauiColor();
        TagColors = TagColors.Append(new TagColorRow {Color = newMAUIColor, Tag = newTag}).ToArray();
    }

    // Save
    private async void OnSaveClicked(object? sender, EventArgs e)
    {
        // Save Color Changes
        foreach (var row in TagColors)
        {
            _settingsService.SetTagColor(row.Tag, row.Color.ToHex());
        }
        await _settingsService.SaveAsync();

        // Save Tag Changes
        if (_pendingTagChanges.Any())
        {
            await _dataService.ApplyTagChangesAsync(_pendingTagChanges);
        }
        await Shell.Current.GoToAsync("..");
    }
}


// %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
// Classes to Bind to the UI
// %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

// TagColorRow
// ====================================================
public class TagColorRow : INotifyPropertyChanged
{
    public string Tag { get; set; }

    private Color _color;
    public Color Color
    {
        get => _color;
        set
        {
            if (_color == value) return;
            _color = value;
            OnPropertyChanged(nameof(Color));
        }
    }

    private bool _isPickerVisible;
    public bool IsPickerVisible
    {
        get => _isPickerVisible;
        set
        {
            if (_isPickerVisible == value) return;
            _isPickerVisible = value;
            OnPropertyChanged(nameof(IsPickerVisible));
        }
    }

    public bool IgnoreFirstPickerChange { get; set; } = true;


    public event PropertyChangedEventHandler PropertyChanged;
    protected void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

// ProcessRow
// ====================================================
public class ProcessRow : INotifyPropertyChanged
{
    public string Process { get; set; }
    private string _tag;
    public string Tag
    {
        get => _tag;
        set
        {
            if (_tag == value) return;
            _tag = value;
            OnPropertyChanged(nameof(Tag));
        }
    }
    public string TotalTime { get; set; }
    public string LastUsed { get; set; }
    // For Sorting
    public double TotalSeconds { get; set; }
    public DateTime LastUsedDate { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

// %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
// Custom Comparer for Sorting
// %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

public class DateSortComparer : IComparer<object>, ISortDirection
{
    public ListSortDirection SortDirection { get; set; }

    public int Compare(object x, object y)
    {
        var dateX = ((ProcessRow)x).LastUsedDate;
        var dateY = ((ProcessRow)y).LastUsedDate;
        int result = dateX.CompareTo(dateY);
        return SortDirection == ListSortDirection.Ascending ? result : -result;
    }
}

public class TotalSecondsSortComparer : IComparer<object>, ISortDirection
{
    public ListSortDirection SortDirection { get; set; }

    public int Compare(object x, object y)
    {
        var secX = ((ProcessRow)x).TotalSeconds;
        var secY = ((ProcessRow)y).TotalSeconds;
        int result = secX.CompareTo(secY);
        return SortDirection == ListSortDirection.Ascending ? result : -result;
    }
}