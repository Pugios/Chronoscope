using Maui.ColorPicker;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace TimeViewer.Platforms;

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
        LoadTags();
        await LoadProcessesAsync();
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

    private void LoadTags()
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

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Process Tag Table
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

    private ProcessRow[] _processRows = [];
    public ProcessRow[] ProcessRows
    {
        get => _processRows;
        set { 
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
                    LastUsed = g.Max(a => a.End).ToString("yyyy-MM-dd HH:mm")
                };
            })
            .OrderBy(r => r.Process)
            .ToArray();
    }

    // Save
    private async void OnSaveClicked(object? sender, EventArgs e)
    {
        foreach (var row in TagColors)
        {
            _settingsService.SetTagColor(row.Tag, row.Color.ToHex());
        }
        await _settingsService.SaveAsync();
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
public class ProcessRow
{
    public string Process { get; set; }
    public string Tag { get; set; }
    public string TotalTime { get; set; }
    public string LastUsed { get; set; }
}