using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using System.ComponentModel;
using System.Diagnostics;

namespace TimeViewer;
public partial class MainPage : ContentPage, INotifyPropertyChanged
{

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Parameters
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    int MaxRadialColumnWidth = 100;
    int RefreshTime = 5; // in minutes

    // Load Settings & DataService
    private readonly SettingsService _settingsService = new();
    private readonly DataService _dataService = new();

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    //  Loading Data & Refresh Logic
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

    private readonly IDispatcherTimer _refreshTimer;

    public MainPage()
    {
        InitializeComponent();
        BindingContext = this;

        _refreshTimer = Dispatcher.CreateTimer();
        _refreshTimer.Interval = TimeSpan.FromMinutes(RefreshTime);
        _refreshTimer.Tick += async (_, _) => await RefreshAsync(forceReload: true);
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _refreshTimer.Stop();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _settingsService.LoadAsync();
        await RefreshAsync(forceReload: true);
    }

    private bool _isRefreshing;
    private async Task RefreshAsync(bool forceReload = false)
    {
        if (_isRefreshing) return;
        _isRefreshing = true;
        try
        {
            var apps = await _dataService.GetMergedDataAsync(forceReload);
            await LoadDayNestedPieAsync(apps, _currentDay);
        }
        finally
        {
            _isRefreshing = false;
        }
    }
    
    private DateTime _currentDay = DateTime.Today;
    private async Task ChangeDayAsync(int deltaDays)
    {
        _currentDay = _currentDay.AddDays(deltaDays);
        await RefreshAsync();
    }

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Controll Buttons
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

    private async void OnRefreshClicked(object? sender, EventArgs e)
    {
        await RefreshAsync(forceReload: true);
    }

    private async void OnPrevDayClicked(object? sender, EventArgs e)
    {
        await ChangeDayAsync(-1);
    }

    private async void OnNextDayClicked(object? sender, EventArgs e)
    {
        await ChangeDayAsync(1);
    }

    private async void OnPrevWeekClicked(object? sender, EventArgs e)
    {
        await ChangeDayAsync(-7);
    }

    private async void OnNextWeekClicked(object? sender, EventArgs e)
    {
        await ChangeDayAsync(7);
    }


    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Graph
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

    private string _displayDay = DateTime.Today.ToString("ddd dd-MM-yyyy");
    public string DisplayDay
    {
        get => _displayDay;
        set
        {
            if (_displayDay != value)
            {
                _displayDay = value;
                OnPropertyChanged(nameof(DisplayDay));
            }
        }
    }

    private PieData[] _pieDataCollection = [];
    public PieData[] PieDataCollection
    {
        get => _pieDataCollection;
        set
        {
            _pieDataCollection = value;
            OnPropertyChanged(nameof(PieDataCollection));
        }
    }


    private LegendItem[] _legendItems = [];
    public LegendItem[] LegendItems
    {
        get => _legendItems;
        set
        {
            _legendItems = value;
            OnPropertyChanged(nameof(LegendItems));
        }
    }

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

    private Task LoadDayNestedPieAsync(List<AppsTable> apps, DateTime day)
    {
        _currentDay = day;
        DisplayDay = day.ToString("ddd dd-MM-yyyy");

        // If no data or no data for the day, clear the graph and legend
        if (!apps.Any() || !apps.Select(a => a.Start.Date).Contains(day))
        {
            PieDataCollection = [];
            LegendItems = [];
            return Task.CompletedTask;
        }

        // Build relevant Apps Table! Of the day, Grouped by Tag and by Process, Ordered by Duration
        var nested = apps
            .Where(a => a.Start.Date == day && TimeSpan.Parse(a.Duration).TotalSeconds > 30)
            .GroupBy(a => a.Tag)
            .Select(a => new
            {
                // TAGS
                Tag = a.Key,
                Seconds = a.Sum(b => TimeSpan.Parse(b.Duration).TotalSeconds),
                Processes = a
                .GroupBy(b => b.Process)
                .Select(b => new
                {
                    // PROCESSES
                    Process = b.Key,
                    Seconds = b.Sum(b => TimeSpan.Parse(b.Duration).TotalSeconds)
                })
                .OrderByDescending(b => b.Seconds)
                .ToList()
            })
            .OrderBy(a => a.Tag)
            .ToList();

        // Progressively building PieData and LegendItem List that includes an entry for each Tag and each Process
        var pieDataList = new List<PieData>();
        var legendItemList = new List<LegendItem>();

        double totalSeconds = 0;
        foreach (var tag in nested)
        {
            // TAG
            var tagColor = _settingsService.GetTagColor(tag.Tag);
            pieDataList.Add(new PieData {
                Name = tag.Tag,
                Values = [null, tag.Seconds],
                Fill = new SolidColorPaint(SKColor.Parse(tagColor))
            });

            legendItemList.Add(new LegendItem
            {
                Name = tag.Tag,
                Duration = TimeSpan.FromSeconds(tag.Seconds).ToString(@"hh\:mm"),
                Color = Color.FromArgb(tagColor)
            });

            int processCount = tag.Processes.Count;
            for (int i = 0; i < processCount; i++)
            {
                // PROCESS
                var proc = tag.Processes[i];

                float value = 20f + ((float)i + 1f) / processCount * 80f;
                var procColor = _settingsService.ChangeColor(tagColor, value);

                pieDataList.Add(new PieData
                {
                    Name = proc.Process,
                    Values = [proc.Seconds, null],
                    Fill = new SolidColorPaint(SKColor.Parse(procColor))
                });

                legendItemList.Add(new LegendItem
                {
                    Name = "\t" + proc.Process,
                    Duration = TimeSpan.FromSeconds(proc.Seconds).ToString(@"hh\:mm"),
                    Color = Color.FromArgb(procColor)
                });

                totalSeconds += proc.Seconds;
            }
        }

        // Add remaining time to reach 24h
        var remaining = Math.Max(0, TimeSpan.FromDays(1).TotalSeconds - totalSeconds);

        var remainColor = _settingsService.GetTagColor("Remaining");
        var remainPaint = new SolidColorPaint(SKColor.Parse(remainColor));

        pieDataList.Add(new PieData { Name = "Remaining", Values = [null, remaining], Fill = remainPaint });
        pieDataList.Add(new PieData { Name = "Remaining", Values = [remaining, null], Fill = remainPaint });

        // Transform pieDataList into an Array and binding to UI
        PieDataCollection = pieDataList.ToArray();
        LegendItems = legendItemList.ToArray();

        Debug.WriteLine($"Total series added: {pieDataList.Count}");

        return Task.CompletedTask;
    }
}


