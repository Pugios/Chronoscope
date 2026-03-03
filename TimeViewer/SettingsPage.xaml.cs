using SkiaSharp;
using SkiaSharp.Views.Maui;
using System.ComponentModel;
using System.Diagnostics;

namespace TimeViewer.Platforms;

public partial class SettingsPage : ContentPage, INotifyPropertyChanged
{
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Loading Settings
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
	private readonly SettingsService _settingsService;

    public SettingsPage(SettingsService settingsService)
	{
        InitializeComponent();
        BindingContext = this;
        _settingsService = settingsService;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        LoadTags();
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
            var skColor = SKColor.Parse(a.Value);
            skColor.ToHsv(out float h, out float s, out float v);
            return new TagColorRow
            {
                Tag = a.Key,
                Hue = h
            };
        }).ToArray();
    }

    // Save

    private async void OnSaveClicked(object? sender, EventArgs e)
    {
        foreach (var row in TagColors)
        {
            var color = SKColor.FromHsv(row.Hue, 100f, 100f).ToString();
            _settingsService.SetTagColor(row.Tag, color);
        }
        await _settingsService.SaveAsync();
        await Shell.Current.GoToAsync("..");
    }
}

// TagColorRow binds to UI setting Tag and Hue, automatically determining Color from Hue
public class TagColorRow : INotifyPropertyChanged
{
    public string Tag { get; set; }
    public Color Color { get; private set; }

    private float _hue;
    public float Hue
    {
        get => _hue;
        set
        {
            _hue = value;
            Color = Color.FromHsv(_hue / 360f, 1f, 1f);
            OnPropertyChanged(nameof(Hue));
            OnPropertyChanged(nameof(Color));
        }
    }


    public event PropertyChangedEventHandler PropertyChanged;
    protected void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
