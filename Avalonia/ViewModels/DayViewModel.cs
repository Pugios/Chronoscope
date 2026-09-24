using System.Diagnostics;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;

namespace TimeViewer.ViewModels;

// The home page: one day as a nested pie (tags inside, their processes outside), its legend,
// and the 24h timeline bar beneath. Ported from the MAUI MainPage.
public partial class DayViewModel : ViewModelBase
{
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Parameters
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    const int RefreshTime = 5; // in minutes
    const double GapAbsorbSeconds = 60; // timeline: same-process segments closer than this merge (sub-pixel anyway)

    private readonly SettingsService _settingsService;
    private readonly DataService _dataService;
    private readonly DialogService _dialogs;
    private readonly DispatcherTimer _refreshTimer;

    public DayViewModel(SettingsService settingsService, DataService dataService, DialogService dialogs)
    {
        _settingsService = settingsService;
        _dataService = dataService;
        _dialogs = dialogs;

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(RefreshTime) };
        _refreshTimer.Tick += async (_, _) => await RefreshAsync(forceReload: true);
    }

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    //  Loading Data & Settings & Refresh Logic
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

    public override async Task OnNavigatedToAsync()
    {
        await _settingsService.LoadAsync();

        // Reload only if the data has gone stale, rather than unconditionally. Returning from
        // Settings or Statistics used to relaunch mtc.exe twice and re-parse the entire export for
        // data that was usually seconds old. Tag and rule edits do not rely on this: both mutators
        // invalidate the cache, so the next call reloads regardless of age.
        await RefreshAsync(maxAge: TimeSpan.FromMinutes(RefreshTime));
        _refreshTimer.Start();
    }

    public override void OnNavigatedFrom()
    {
        _refreshTimer.Stop();

        // A chart disposes the paints of the series it drew when it unloads, so series must never
        // outlive their chart: the next visit builds a new view, and handing it these would draw
        // with disposed Skia objects. OnNavigatedToAsync always redraws, so nothing is lost.
        PieSeries = [];
    }

    private bool _isRefreshing;
    private bool _refreshQueued;
    private bool _queuedForceReload;

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    private async Task RefreshAsync(bool forceReload = false, TimeSpan? maxAge = null)
    {
        // A refresh already in flight used to make this return outright - but ChangeDayAsync has
        // already moved _currentDay by then, so a second click on ">" advanced the day while
        // nothing redrew, leaving the label and the pie a day behind the actual state.
        // The request is queued instead and picked up by the loop below.
        if (_isRefreshing)
        {
            _refreshQueued = true;
            _queuedForceReload |= forceReload;
            return;
        }

        _isRefreshing = true;
        IsBusy = true;
        try
        {
            do
            {
                _refreshQueued = false;
                bool reload = forceReload || _queuedForceReload;
                forceReload = false;
                _queuedForceReload = false;

                // Re-read every pass: a queued request is usually a day change
                var day = _currentDay;
                var apps = await _dataService.GetMergedDataAsync(reload, maxAge);
                // Only the first pass weighs staleness; a queued pass is a day change and
                // redraws whatever that first pass just loaded.
                maxAge = null;
                LoadDayNestedPie(apps, day);
                LoadDayTimeline(apps, day);
            }
            while (_refreshQueued);
        }
        catch (InvalidOperationException ex)
        {
            await _dialogs.AlertAsync("ManicTime Error", ex.Message);
        }
        catch (Exception ex)
        {
            // Called from async void handlers, so anything unhandled here takes the whole app
            // down. A bad colour hex in settings.json reaching SKColor.Parse is the realistic one.
            await _dialogs.AlertAsync("Could not draw the day", ex.Message);
        }
        finally
        {
            _isRefreshing = false;
            IsBusy = false;
        }
    }

    private DateTime _currentDay = DateTime.Today;
    private async Task ChangeDayAsync(int deltaDays)
    {
        _currentDay = _currentDay.AddDays(deltaDays);
        UpdateDayLabels(_currentDay);
        await RefreshAsync();
    }

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Control Buttons
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

    [RelayCommand] private Task Refresh() => RefreshAsync(forceReload: true);
    [RelayCommand] private Task PrevDay() => ChangeDayAsync(-1);
    [RelayCommand] private Task NextDay() => ChangeDayAsync(1);
    [RelayCommand] private Task PrevWeek() => ChangeDayAsync(-7);
    [RelayCommand] private Task NextWeek() => ChangeDayAsync(7);
    [RelayCommand] private Task Today() => ChangeDayAsync((DateTime.Today - _currentDay).Days);

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Header
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

    [ObservableProperty]
    public partial string DisplayWeekday { get; private set; } = DateTime.Today.ToString("dddd");

    [ObservableProperty]
    public partial string DisplayDay { get; private set; } = DateTime.Today.ToString("dd-MM-yyyy");

    [ObservableProperty]
    public partial bool IsToday { get; private set; } = true;

    // Tracked time, shown in the hole of the pie. Everything but "Remaining".
    [ObservableProperty]
    public partial string TrackedTotal { get; private set; } = "0h 00m";

    [ObservableProperty]
    public partial bool HasData { get; private set; }

    private void UpdateDayLabels(DateTime day)
    {
        DisplayWeekday = day.ToString("dddd");
        DisplayDay = day.ToString("dd-MM-yyyy");
        IsToday = day.Date == DateTime.Today;
    }

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Graph
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

    [ObservableProperty]
    public partial ISeries[] PieSeries { get; private set; } = [];

    [ObservableProperty]
    public partial LegendItem[] LegendItems { get; private set; } = [];

    // Colors the pie assigned to each process, reused by the timeline so both agree.
    // Keyed by process, which assumes one Tag per Process (true for AppsTagsTable) - last write wins.
    private readonly Dictionary<string, string> _processColors = new();

    private void LoadDayNestedPie(IReadOnlyList<AppsTagsTable> data, DateTime day)
    {
        UpdateDayLabels(day);
        _processColors.Clear();

        // One pass over the dataset instead of two: this used to scan the whole thing just to
        // decide whether to bail, then scan it again to group.
        var dayRows = data.Where(a => a.Start.Date == day).ToList();

        // If no data for the day, clear the graph and legend. The timeline is left alone -
        // LoadDayTimeline runs straight after this and owns that collection.
        if (dayRows.Count == 0)
        {
            PieSeries = [];
            LegendItems = [];
            TrackedTotal = "0h 00m";
            HasData = false;
            return;
        }

        // Build relevant Apps Table! Of the day, Grouped by Tag and by Process, both ordered by
        // time spent descending, so the pie and the legend lead with the biggest tag.
        var nested = dayRows
            .Where(a => a.DurationSeconds > HeatmapAggregator.MinTrackedSeconds)
            .GroupBy(a => a.Tag)
            .Select(a => new
            {
                // TAGS
                Tag = a.Key,
                Seconds = a.Sum(b => b.DurationSeconds),
                Processes = a
                .GroupBy(b => b.Process)
                .Select(b => new
                {
                    // PROCESSES
                    Process = b.Key,
                    Seconds = b.Sum(b => b.DurationSeconds)
                })
                .OrderByDescending(b => b.Seconds)
                .ToList()
            })
            .OrderByDescending(a => a.Seconds)
            .ToList();

        // Progressively building the series and legend: an entry for each Tag and each Process.
        // Tags sit on ring 0 ([seconds, null]), processes on ring 1 ([null, seconds]).
        var series = new List<ISeries>();
        var legendItemList = new List<LegendItem>();

        double totalSeconds = 0;
        foreach (var tag in nested)
        {
            // TAG
            string tagColor = _settingsService.GetTagColor(tag.Tag);
            series.Add(Slice(tag.Tag, [tag.Seconds, null], tagColor));

            legendItemList.Add(new LegendItem
            {
                Name = tag.Tag,
                Duration = TimeSpan.FromSeconds(tag.Seconds).ToString(@"hh\:mm"),
                Color = Color.Parse(tagColor),
                IsTag = true
            });

            int processCount = tag.Processes.Count;
            for (int i = 0; i < processCount; i++)
            {
                // PROCESS
                var proc = tag.Processes[i];

                // Longest Process = Brightest Color
                float value = 20f + (((processCount - (float)i) / processCount) * 80f);
                string procColor = _settingsService.VaryColor(tagColor, value);
                _processColors[proc.Process] = procColor;

                series.Add(Slice(proc.Process, [null, proc.Seconds], procColor));

                legendItemList.Add(new LegendItem
                {
                    Name = proc.Process,
                    Duration = TimeSpan.FromSeconds(proc.Seconds).ToString(@"hh\:mm"),
                    Color = Color.Parse(procColor),
                    IsTag = false
                });

                totalSeconds += proc.Seconds;
            }
        }

        // Add remaining time to reach 24h
        var remaining = Math.Max(0, TimeSpan.FromDays(1).TotalSeconds - totalSeconds);
        var remainColor = _settingsService.GetTagColor("Remaining");
        series.Add(Slice("Remaining", [remaining, null], remainColor));
        series.Add(Slice("Remaining", [null, remaining], remainColor));

        PieSeries = series.ToArray();
        LegendItems = legendItemList.ToArray();
        TrackedTotal = FormatTotal(totalSeconds);
        HasData = true;

        Debug.WriteLine($"Total series added: {series.Count}");
    }

    // The hole in the middle holds the tracked total; the thin stroke in the card's own colour
    // parts neighbouring slices, which otherwise run together when two shades are close.
    const double PieHole = 72;

    private static PieSeries<double?> Slice(string name, double?[] values, string hex) => new()
    {
        Name = name,
        Values = values,
        Fill = new SolidColorPaint(SKColor.Parse(hex)),
        Stroke = new SolidColorPaint(SliceGapColor(), 1.5f),
        InnerRadius = PieHole,
        HoverPushout = 4,
        ToolTipLabelFormatter = point => TimeSpan.FromSeconds(point.Coordinate.PrimaryValue).ToString(@"hh\:mm")
    };

    private static SKColor SliceGapColor() =>
        Avalonia.Application.Current?.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark
            ? new SKColor(0x2B, 0x2B, 0x2B)
            : new SKColor(0xFB, 0xFB, 0xFB);

    private static string FormatTotal(double seconds)
    {
        var span = TimeSpan.FromSeconds(seconds);
        return $"{(int)span.TotalHours}h {span.Minutes:00}m";
    }

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Timeline Bar
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

    [ObservableProperty]
    public partial IReadOnlyList<TimelineSlice> TimelineSlices { get; private set; } = [];

    [ObservableProperty]
    public partial DateTime TimelineWindowStart { get; private set; } = DateTime.Today;

    // The untracked stretches of the bar, in the same "Remaining" colour the pie gives the
    // untracked rest of the day, so a change to it in Tags shows up in both
    [ObservableProperty]
    public partial Color TimelineGapColor { get; private set; } = Colors.Black;

    private void LoadDayTimeline(IReadOnlyList<AppsTagsTable> data, DateTime day)
    {
        // Today shows the rolling last 24h ending right now, any other day shows that day
        // 00:00-24:00. Both windows are exactly 86400s.
        // The end is deliberately not rounded: snapping it up to the next whole hour used to put
        // the window up to an hour into the future and start it an hour late, so "the last 24h"
        // at 22:35 actually began at 23:00 yesterday. The separators are placed on clock times
        // instead (see TimelineBar), which is what the rounding was for.
        bool isToday = day.Date == DateTime.Today;
        var now = DateTime.Now;
        DateTime windowEnd = isToday
            ? now.AddTicks(-(now.Ticks % TimeSpan.TicksPerSecond))
            : day.Date.AddDays(1);
        DateTime windowStart = windowEnd.AddDays(-1);

        // Everything overlapping the window, clamped to it. The floor is tested against the
        // unclamped row, and against the same HeatmapAggregator.MinTrackedSeconds the pie and the
        // heatmap use, so all three agree on which activities exist.
        var slices = data
            .Where(a => a.End > windowStart && a.Start < windowEnd)
            .Where(a => a.DurationSeconds > HeatmapAggregator.MinTrackedSeconds)
            .Select(a => new TimelineSlice(
                a.Process,
                a.Tag,
                a.Start < windowStart ? windowStart : a.Start,
                a.End > windowEnd ? windowEnd : a.End,
                Color.Parse(ColorForProcess(a.Process, a.Tag))))
            .Where(s => s.End > s.Start)
            .OrderBy(s => s.Start)
            .ToList();

        // Merge adjacent same-process slices. ManicTime splits a sitting on every window title
        // change, so this collapses one session back into one segment. Applied AFTER the filter,
        // so the set of activities shown stays the same as the pie's.
        var merged = new List<TimelineSlice>();
        foreach (var slice in slices)
        {
            if (merged.Count > 0)
            {
                var last = merged[^1];
                if (last.Process == slice.Process && (slice.Start - last.End).TotalSeconds <= GapAbsorbSeconds)
                {
                    merged[^1] = last with { End = slice.End > last.End ? slice.End : last.End };
                    continue;
                }
            }
            merged.Add(slice);
        }

        TimelineWindowStart = windowStart;
        TimelineGapColor = Color.Parse(_settingsService.GetTagColor("Remaining"));
        TimelineSlices = merged;

        Debug.WriteLine($"Timeline segments: {merged.Count}");
    }

    // A process the pie never colored (only possible in the previous-day part of the rolling
    // window) falls back to its tag's base color, the same color the pie gives that tag's ring.
    private string ColorForProcess(string process, string tag) =>
        _processColors.TryGetValue(process, out var hex)
            ? hex
            : _settingsService.GetTagColor(tag);
}
