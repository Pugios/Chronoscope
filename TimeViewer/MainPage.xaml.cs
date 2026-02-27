using CsvHelper;
using CsvHelper.Configuration.Attributes;
using LiveChartsCore;
using LiveChartsCore.Kernel;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace TimeViewer;
public partial class MainPage : ContentPage, INotifyPropertyChanged
{

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Parameters
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    int MaxRadialColumnWidth = 100;
    int RefreshTime = 5; // in minutes

    // Load Settings
    private readonly SettingsService _settingsService = new();

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    //  Startup & Refresh Logic
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

    private readonly IDispatcherTimer _refreshTimer;
    private readonly SemaphoreSlim _dataGate = new(1, 1);
    private bool _isRefreshing;

    public MainPage()
    {
        InitializeComponent();
        BindingContext = this;

        _ = _settingsService.LoadAsync();

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
        await RefreshAsync(forceReload: true);
    }

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Controlls
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

    private DateTime _currentDay = DateTime.Today;
    private string _displayDay = DateTime.Today.ToString("ddd dd-MM-yyyy");

    private async Task RefreshAsync(bool forceReload = false)
    {
        if (_isRefreshing) return;
        _isRefreshing = true;
        try
        {
            var apps = await GetMergedDataAsync(forceReload);
            await LoadDayNestedPieAsync(apps, _currentDay);
            //await LoadDayPieAsync(apps, _currentDay);
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private async Task ChangeDayAsync(int deltaDays)
    {
        _currentDay = _currentDay.AddDays(deltaDays);
        await RefreshAsync();
    }

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Reading Tables (Tags & Time)
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    public class TagTable
    {
        public string Process { get; set; }
        public string Tag { get; set; }
    }

    private static readonly string TagsPath = Path.Combine(FileSystem.AppDataDirectory, "tags.csv");
    public static async Task<List<TagTable>> GetTagTable()
    {
        Directory.CreateDirectory(FileSystem.AppDataDirectory);
        if (!File.Exists(TagsPath))
        {
            await File.WriteAllTextAsync(TagsPath, "Process,Tag" + Environment.NewLine);
        }
        Debug.WriteLine($"Tags file path: {TagsPath}");

        using var reader = new StreamReader(TagsPath);
        using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
        return csv.GetRecords<TagTable>().ToList();
    }

    public class AppsTable
    {
        public string Name { get; set; }
        public DateTime Start { get; set; }
        public DateTime End { get; set; }
        public string Duration { get; set; }
        public string Process { get; set; }

        [Ignore]
        public string Tag { get; set; }

        public static string ToString(AppsTable x)
        {
            string Message = $"{x.Name} | {x.Start} | {x.End} | {x.Duration} | {x.Process}";
            if (x.Tag is not null)
            {
                Message += $" | {x.Tag}";
            }
            return Message;
        }
    }
    private static async Task<List<AppsTable>> ExportTimeTableAsync()
    {
        // Running mtc to export CSV to tempCsvPath
        string tempCsvPath = Path.Combine(FileSystem.CacheDirectory, "manictime-export.csv");

        using Process process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                WindowStyle = ProcessWindowStyle.Hidden,
                FileName = @"C:\Program Files\ManicTime\mtc.exe",
                Arguments = $"export ManicTime/Applications \"{tempCsvPath}\"",
                CreateNoWindow = true
            }
        };

        process.Start();
        await process.WaitForExitAsync().ConfigureAwait(false);

        // Reading Exported Applications Table
        using var reader = new StreamReader(tempCsvPath);
        using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
        return csv.GetRecords<AppsTable>().ToList();
    }

    private static List<AppsTable> MergeAppTags(List<AppsTable> apps, List<TagTable> tags)
    {
        // Merging Tags into Applications by Process Name
        var merged = from app in apps
                        join tag in tags on app.Process equals tag.Process into gj
                        from subgroup in gj.DefaultIfEmpty()
                        select new AppsTable
                        {
                            Name = app.Name,
                            Start = app.Start,
                            End = app.End,
                            Duration = app.Duration,
                            Process = app.Process,
                            Tag = subgroup?.Tag ?? "No Clue"
                        };

        return merged.ToList();
    }

    private List<AppsTable> _cachedApps = new();
    private List<TagTable> _cachedTags = new();

    private async Task<List<AppsTable>> GetMergedDataAsync(bool forceReload)
    {
        await _dataGate.WaitAsync();
        try
        {
            if (!forceReload && _cachedApps.Any())
            {
                return _cachedApps;
            }

            var tags = await GetTagTable();
            var apps = await ExportTimeTableAsync();
            _cachedTags = tags;
            _cachedApps = MergeAppTags(apps, tags);
            return _cachedApps;
        }
        finally
        {
            _dataGate.Release();
        }
    }

    // Util
    public static void PrintTable(List<AppsTable> table)
    {
        foreach (var row in table)
        {
            Debug.WriteLine(row.ToString());
        }
    }

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Legend
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

    public class LegendItem
    {
        public string Name { get; set; }
        public string Duration { get; set; }
    }

    private ObservableCollection<LegendItem> _legendItems = new();
    public ObservableCollection<LegendItem> LegendItems
    {
        get => _legendItems;
        set
        {
            _legendItems = value;
            OnPropertyChanged(nameof(LegendItems));
        }
    }

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

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Graphs
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

    private ObservableCollection<ISeries> _daySeries = new();
    public ObservableCollection<ISeries> DaySeries
    {
        get => _daySeries;
        set
        {
            _daySeries = value;
            OnPropertyChanged(nameof(DaySeries));
        }
    }

    // Simple Pie Graph
    // ===================
    private Task LoadDayPieAsync(List<AppsTable> apps, DateTime day)
    {
        _currentDay = day;
        DisplayDay = day.ToString("ddd dd-MM-yyyy");

        if (!apps.Any())
        {
            DaySeries.Clear();
            LegendItems.Clear();
            return Task.CompletedTask;
        }

        double totalSeconds = 0;
        var grouped = apps
            .Where(a => a.Start.Date == day) // Data only of day
            .GroupBy(a => a.Process)    // Grouped by the Process
            .Select(a => new    // Parsing and Summing the Duration for each Process
            {
                Process = a.Key,
                Seconds = a.Sum(b => TimeSpan.Parse(b.Duration).TotalSeconds)
            })
            .Where(a => a.Seconds > 5)  // Remove anything less than 5 seconds
            .OrderBy(a => a.Seconds) // Order by Duration
            .Select(a =>    // Format into Series for PieChart and Legend
            {
                totalSeconds += a.Seconds;
                return (Series: new PieSeries<double>
                {
                    Name = a.Process,
                    Values = new[] { a.Seconds },
                    ToolTipLabelFormatter = (p) => TimeSpan.FromSeconds(p.Model).ToString(@"hh\:mm"),
                    MaxRadialColumnWidth = MaxRadialColumnWidth
                },
                Legend: new LegendItem
                {
                    Name = a.Process,
                    Duration = TimeSpan.FromSeconds(a.Seconds).ToString(@"hh\:mm")
                });
            })
            .ToList();

        // add remainder to reach 24h
        var remaining = Math.Max(0, TimeSpan.FromHours(24).TotalSeconds - totalSeconds);
        grouped.Add((Series: new PieSeries<double>
        {
            Name = "Remaining",
            Values = new[] { remaining },
            ToolTipLabelFormatter = p => TimeSpan.FromSeconds(p.Model).ToString(@"hh\:mm"),
            MaxRadialColumnWidth = MaxRadialColumnWidth
        }, Legend: new LegendItem
        {
            Name = "Remaining",
            Duration = TimeSpan.FromSeconds(remaining).ToString(@"hh\:mm")
        }));

        // Bind to UI
        DaySeries.Clear();
        LegendItems.Clear();
        foreach (var items in grouped)
        {
            DaySeries.Add(items.Series);
            LegendItems.Add(items.Legend);
        }

        return Task.CompletedTask;
    }

    // Nested Pie Graph
    // ===================
    public class TagUsage
    {
        public string Tag { get; set; }
        public double Seconds { get; set; }
        public List<ProcessUsage> Processes { get; set; }
    }

    public class ProcessUsage
    {
        public string Process { get; set; }
        public double Seconds { get; set; }
    }

    public class PieData
    {
        public string Name { get; set; }
        public double?[] Values { get; set; }
        public Func<ChartPoint, string> Formatter { get; } = point => TimeSpan.FromSeconds(point.Coordinate.PrimaryValue).ToString(@"hh\:mm");
        public SolidColorPaint Fill { get; set; }
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

    private Task LoadDayNestedPieAsync(List<AppsTable> apps, DateTime day)
    {
        _currentDay = day;
        DisplayDay = day.ToString("ddd dd-MM-yyyy");

        // If no data or no data for the day, clear the graph and legend
        if (!apps.Any() || !apps.Select(a => a.Start.Date).Contains(day))
        {
            PieDataCollection = [];
            LegendItems.Clear();
            return Task.CompletedTask;
        }

        // Apps of the day, Grouped by Tag and by Process, Ordered by Duration
        var nested = apps
            .Where(a => a.Start.Date == day)
            .GroupBy(a => a.Tag)
            .Select(a => new TagUsage
            {
                Tag = a.Key,
                Seconds = a.Sum(b => TimeSpan.Parse(b.Duration).TotalSeconds),
                Processes = a
                .GroupBy(b => b.Process)
                .Select(b => new ProcessUsage
                {
                    Process = b.Key,
                    Seconds = b.Sum(b => TimeSpan.Parse(b.Duration).TotalSeconds)
                })
                .Where(b => b.Seconds > 5)  // Remove anything less than 5 seconds
                .OrderBy(b => b.Seconds)
                .ToList()
            })
        .ToList();

        // Build PieData List
        var pieDataList = new List<PieData>();

        double totalSeconds = 0;
        foreach (var tag in nested)
        {
            var tagColor = _settingsService.GetTagColor(tag.Tag);
            pieDataList.Add(new PieData { 
                Name = tag.Tag, 
                Values = [null, tag.Seconds],
                Fill = new SolidColorPaint(SKColor.Parse(tagColor))
            });

            int processCount = tag.Processes.Count;
            for (int i = 0; i < processCount; i++)
            {
                var proc = tag.Processes[i];

                float value = ((float)i + 1f)/ processCount * 100f;
                var procColor = _settingsService.ChangeColor(tagColor, value);
                
                pieDataList.Add(new PieData
                {
                    Name = proc.Process,
                    Values = [proc.Seconds, null],
                    Fill = new SolidColorPaint(SKColor.Parse(procColor))
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

        // Bind to UI
        PieDataCollection = pieDataList.ToArray();

        Debug.WriteLine($"Total series added: {pieDataList.Count}");

        return Task.CompletedTask;
    }
}
