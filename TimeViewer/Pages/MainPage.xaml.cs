using LiveChartsCore.Drawing;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using System.ComponentModel;
using System.Diagnostics;

namespace TimeViewer;
public partial class MainPage : ContentPage
{

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Parameters
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    const int RefreshTime = 5; // in minutes
    const double GapAbsorbSeconds = 60; // timeline: same-process segments closer than this merge (sub-pixel anyway)
    const int SeparatorHours = 2; // timeline: clock-hour spacing of the X axis labels
    const int LabelEdgeClearanceMinutes = 20; // timeline: drop a label this close to either end of the axis

    // Pie diameter, which doubles as the legend's height cap so the two can never drift apart
    public double PieSize { get; } = 500;
    // One legend row and one legend column - the item template is sized from these, so the
    // column arithmetic in UpdateLegendLayout matches what actually gets drawn.
    // Rows can be this compact only because the constructor lifts WinUI's 40px minimum off the
    // legend's item containers - without that, anything below 40 clips the bottom of every row.
    public double LegendItemHeight { get; } = 24;
    public double LegendColumnWidth { get; } = 230;
    const double LegendMargin = 10; // matches the CollectionView's Margin in the XAML
    // Kept free either side of the centered block, so extra legend columns never grow under the
    // tool buttons overlaid at the top right
    const double LegendSideGutter = 70;
    const double LegendScrollBarHeight = 14; // room for the horizontal scrollbar when columns overflow
    const double ReservedPageHeight = 200; // timeline row plus the day controls beneath the pie
    // Gap either side of the timeline bar. It is applied to the chart itself, so both edges keep
    // the same gap at any window width.
    public Thickness TimelineMargin { get; } = new Thickness(70, 0, 70, 0);

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    //  Loading Data & Settings & Refresh Logic
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

    private readonly SettingsService _settingsService;
    private readonly DataService _dataService;
    private readonly IDispatcherTimer _refreshTimer;

    public MainPage(SettingsService settingsService, DataService dataService)
    {
        InitializeComponent();
        BindingContext = this;

        _settingsService = settingsService;
        _dataService = dataService;

#if WINDOWS
        // MAUI renders the legend as a WinUI GridView, and its GridViewItem containers carry a 44px
        // minimum height from the theme. The wrap grid still gives them a LegendItemHeight cell, so
        // a container drawn at 44 gets clipped to the cell and the bottom of every row disappears.
        // The containers resolve these theme resources through their parent chain, so overriding
        // them on the legend's own list keeps the change here and leaves every other list alone.
        LegendView.HandlerChanged += (_, _) =>
        {
            if (LegendView.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.ListViewBase list)
            {
                list.Resources["GridViewItemMinHeight"] = 0.0;
                list.Resources["GridViewItemMinWidth"] = 0.0;
            }
        };
#endif

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
        _refreshTimer.Start();
    }

    private bool _isRefreshing;
    private bool _refreshQueued;
    private bool _queuedForceReload;

    private async Task RefreshAsync(bool forceReload = false)
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
                var apps = await _dataService.GetMergedDataAsync(reload);
                LoadDayNestedPie(apps, day);
                LoadDayTimeline(apps, day);
            }
            while (_refreshQueued);
        }
        catch (InvalidOperationException ex)
        {
            await DisplayAlertAsync("ManicTime Error", ex.Message, "OK");
        }
        catch (Exception ex)
        {
            // OnAppearing is async void, so anything unhandled here takes the whole app down.
            // A bad colour hex in settings.json reaching SKColor.Parse is the realistic one.
            await DisplayAlertAsync("Could not draw the day", ex.Message, "OK");
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

    private bool _isPinned = false;
    private void OnPinClicked(object? sender, EventArgs e)
    {
        _isPinned = !_isPinned;
        #if WINDOWS
        new TimeViewer.Platforms.Windows.WindowService().SetAlwaysOnTop(_isPinned);
        #endif
    }

    private async void OnSettingsClicked(object? sender, EventArgs e)
    {
        await Shell.Current.GoToAsync(nameof(SettingsPage));
    }

    private async void OnStatisticsClicked(object? sender, EventArgs e)
    {
        await Shell.Current.GoToAsync(nameof(StatisticsPage));
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


    private TimelineData[] _timelineDataCollection = [];
    public TimelineData[] TimelineDataCollection
    {
        get => _timelineDataCollection;
        set
        {
            _timelineDataCollection = value;
            OnPropertyChanged(nameof(TimelineDataCollection));
        }
    }


    private LegendItem[] _legendItems = [];
    public LegendItem[] LegendItems
    {
        get => _legendItems;
        set
        {
            // Assigning a new array tears down and re-animates every row, so an unchanged
            // refresh is dropped here rather than restarting the entrance animation for nothing
            if (SameLegend(_legendItems, value)) return;

            _legendItems = value;
            OnPropertyChanged(nameof(LegendItems));
            UpdateLegendLayout();
        }
    }

    private double _legendWidth;
    public double LegendWidth
    {
        get => _legendWidth;
        set
        {
            if (_legendWidth == value) return;
            _legendWidth = value;
            OnPropertyChanged(nameof(LegendWidth));
        }
    }

    private double _legendHeight;
    public double LegendHeight
    {
        get => _legendHeight;
        set
        {
            if (_legendHeight == value) return;
            _legendHeight = value;
            OnPropertyChanged(nameof(LegendHeight));
        }
    }

    private static bool SameLegend(LegendItem[] current, LegendItem[] incoming)
    {
        if (current.Length != incoming.Length) return false;

        for (int i = 0; i < current.Length; i++)
        {
            if (current[i].Name != incoming[i].Name
                || current[i].Duration != incoming[i].Duration
                || current[i].Color != incoming[i].Color
                || current[i].Indent != incoming[i].Indent)
                return false;
        }

        return true;
    }

    // The legend is a fixed-height block of columns that fill top-to-bottom and grow rightwards.
    // Height is capped at the pie's diameter (less on a short window); width is whatever the
    // window can spare next to the pie. Columns that do not fit are left to the scrollbar.
    private void UpdateLegendLayout()
    {
        if (LegendLayout is null) return; // called before InitializeComponent finished

        int count = LegendItems.Length;
        if (count == 0)
        {
            LegendWidth = 0;
            LegendHeight = 0;
            return;
        }

        // Height cap: the pie, unless the window is too short to show that much
        double maxHeight = PieSize;
        if (Height > 0)
            maxHeight = Math.Clamp(Height - ReservedPageHeight, LegendItemHeight * 4, PieSize);

        int rows = Math.Min(count, Math.Max(1, (int)(maxHeight / LegendItemHeight)));
        int columnsNeeded = (int)Math.Ceiling(count / (double)rows);

        // Width the legend may claim: the window minus the pie, its own margins and the gutters
        double available = Width > 0
            ? Width - PieSize - (2 * LegendMargin) - (2 * LegendSideGutter)
            : LegendColumnWidth;
        int columns = Math.Min(columnsNeeded, Math.Max(1, (int)(available / LegendColumnWidth)));

        LegendLayout.Span = rows;
        LegendWidth = columns * LegendColumnWidth;
        LegendHeight = (rows * LegendItemHeight) + (columnsNeeded > columns ? LegendScrollBarHeight : 0);
    }

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        UpdateLegendLayout();

    }

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

    // Colors the pie assigned to each process, reused by the timeline so both agree.
    // Keyed by process, which assumes one Tag per Process (true for AppsTagsTable) - last write wins.
    private readonly Dictionary<string, string> _processColors = new();

    private void LoadDayNestedPie(IReadOnlyList<AppsTagsTable> data, DateTime day)
    {
        DisplayDay = day.ToString("ddd dd-MM-yyyy");
        _processColors.Clear();

        // One pass over the dataset instead of two: this used to scan the whole thing just to
        // decide whether to bail, then scan it again to group.
        var dayRows = data.Where(a => a.Start.Date == day).ToList();

        // If no data for the day, clear the graph and legend. The timeline is left alone -
        // LoadDayTimeline runs straight after this and owns that collection.
        if (dayRows.Count == 0)
        {
            PieDataCollection = [];
            LegendItems = [];
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

        // Progressively building PieData and LegendItem List that includes an entry for each Tag and each Process
        var pieDataList = new List<PieData>();
        var legendItemList = new List<LegendItem>();

        double totalSeconds = 0;
        foreach (var tag in nested)
        {
            // TAG
            string tagColor = _settingsService.GetTagColor(tag.Tag);
            pieDataList.Add(new PieData {
                Name = tag.Tag,
                Values = [tag.Seconds, null],
                Fill = new SolidColorPaint(SKColor.Parse(tagColor))
            });

            legendItemList.Add(new LegendItem
            {
                Name = tag.Tag,
                Duration = TimeSpan.FromSeconds(tag.Seconds).ToString(@"hh\:mm"),
                Color = Color.Parse(tagColor),
                Indent = new Thickness(0, 0, 0, 0)
            });

            int processCount = tag.Processes.Count;
            for (int i = 0; i < processCount; i++)
            {
                // PROCESS
                var proc = tag.Processes[i];

                //float value = 20f + ((float)i + 1f) / processCount * 80f;
                // Longest Process = Brightest Color
                float value = 20f + (((processCount - (float)i) / processCount) * 80f);
                string procColor = _settingsService.VaryColor(tagColor, value);
                _processColors[proc.Process] = procColor;

                pieDataList.Add(new PieData
                {
                    Name = proc.Process,
                    Values = [null, proc.Seconds],
                    Fill = new SolidColorPaint(SKColor.Parse(procColor))
                });

                legendItemList.Add(new LegendItem
                {
                    Name = proc.Process,
                    Duration = TimeSpan.FromSeconds(proc.Seconds).ToString(@"hh\:mm"),
                    Color = Color.Parse(procColor),
                    Indent = new Thickness(20, 0, 0, 0)
                });

                totalSeconds += proc.Seconds;
            }
        }

        // Add remaining time to reach 24h
        var remaining = Math.Max(0, TimeSpan.FromDays(1).TotalSeconds - totalSeconds);

        var remainColor = _settingsService.GetTagColor("Remaining");
        var remainPaint = new SolidColorPaint(SKColor.Parse(remainColor));

        pieDataList.Add(new PieData { Name = "Remaining", Values = [remaining, null], Fill = remainPaint });
        pieDataList.Add(new PieData { Name = "Remaining", Values = [null, remaining], Fill = remainPaint });

        // Transform pieDataList into an Array and binding to UI
        PieDataCollection = pieDataList.ToArray();
        LegendItems = legendItemList.ToArray();

        Debug.WriteLine($"Total series added: {pieDataList.Count}");
    }

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Timeline Bar
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

    private void LoadDayTimeline(IReadOnlyList<AppsTagsTable> data, DateTime day)
    {
        // Today shows the rolling last 24h ending right now, any other day shows that day
        // 00:00-24:00. Both windows are exactly 86400s, so the axis is always 0..86400.
        // The end is deliberately not rounded: snapping it up to the next whole hour used to put
        // the window up to an hour into the future and start it an hour late, so "the last 24h"
        // at 22:35 actually began at 23:00 yesterday. The separators are placed on clock times
        // below instead, which is what the rounding was for.
        // Truncated to a whole second: the axis labels a separator by its offset in seconds, and
        // a fractional offset gets floored, which would print every round hour as HH:59.
        bool isToday = day.Date == DateTime.Today;
        var now = DateTime.Now;
        DateTime windowEnd = isToday
            ? now.AddTicks(-(now.Ticks % TimeSpan.TicksPerSecond))
            : day.Date.AddDays(1);
        DateTime windowStart = windowEnd.AddDays(-1);
        _timelineWindowStart = windowStart;

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
                a.End > windowEnd ? windowEnd : a.End))
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

        _timelineSlices = merged;

        // Walk the window emitting a transparent spacer for each gap, then the segment itself.
        // The cursor clamp is where the sub-minute overlaps get absorbed instead of shifting
        // everything to the right.
        var timelineList = new List<TimelineData>();
        var cursor = windowStart;
        foreach (var slice in merged)
        {
            var gap = (slice.Start - cursor).TotalSeconds;
            if (gap > 0.5)
            {
                timelineList.Add(new TimelineData
                {
                    Values = [gap],
                    Fill = new SolidColorPaint(SKColors.Black)
                });
            }

            var from = slice.Start > cursor ? slice.Start : cursor;
            var length = (slice.End - from).TotalSeconds;
            if (length <= 0) continue;

            timelineList.Add(new TimelineData
            {
                Name = slice.Process,
                Values = [length],
                Fill = new SolidColorPaint(SKColor.Parse(ColorForProcess(slice.Process, slice.Tag)))
            });

            cursor = slice.End > cursor ? slice.End : cursor;
        }

        // Spacers are only emitted ahead of a segment, so without this the bar stops at the last
        // recorded activity instead of at the end of the window, and the plot ends short of the
        // axis by however long ago that was.
        var tail = (windowEnd - cursor).TotalSeconds;
        if (tail > 0.5)
        {
            timelineList.Add(new TimelineData
            {
                Values = [tail],
                Fill = new SolidColorPaint(SKColors.Black)
            });
        }

        // Label the axis in clock time. The window can now start at any minute, so the separators
        // are walked from the first round clock hour inside it rather than taken as offsets from
        // its start. Ticks too close to either edge are dropped, otherwise their label is half cut
        // off by the edge of the plot area.
        TimelineXAxis.Labeler = seconds => windowStart.AddSeconds(seconds).ToString("HH:mm");

        var separators = new List<double>();
        var clearance = TimeSpan.FromMinutes(LabelEdgeClearanceMinutes);
        var firstTick = windowStart.Date.AddHours((windowStart.Hour / SeparatorHours + 1) * SeparatorHours);
        for (var tick = firstTick; tick < windowEnd; tick = tick.AddHours(SeparatorHours))
        {
            if (tick - windowStart < clearance || windowEnd - tick < clearance) continue;
            separators.Add((tick - windowStart).TotalSeconds);
        }
        TimelineXAxis.CustomSeparators = separators.ToArray();

        // Both ends of the axis are pinned here rather than in the XAML. A minimum of 0 is the
        // axis's own default, so assigning it raises no change notification and never reaches the
        // underlying axis, which then auto-scales and pads the start of the bar by a few percent
        // of the width - the bar floats off the left edge while the right sits flush. Writing a
        // different value first makes the 0 a real change, so it lands.
        TimelineXAxis.MinLimit = -1;
        TimelineXAxis.MinLimit = 0;
        TimelineXAxis.MaxLimit = 86400;

        // Pin the plot area to the full width of the control. Without this the hidden Y axis
        // still reserves space on the left, which pushes the bar right of the pie's left edge.
        TimelineChart.DrawMargin = new Margin(0, 4, 0, 26);

        TimelineDataCollection = timelineList.ToArray();

        Debug.WriteLine($"Timeline segments: {timelineList.Count}");
    }

    // A process the pie never colored (only possible in the previous-day part of the rolling
    // window) falls back to its tag's base color, the same color the pie gives that tag's ring.
    private string ColorForProcess(string process, string tag) =>
        _processColors.TryGetValue(process, out var hex)
            ? hex
            : _settingsService.GetTagColor(tag);

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Timeline Hover
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // LiveCharts cannot hit test this chart: every segment is its own stacked series, and a
    // stacked point's hover area is a sliver at the segment's trailing edge rather than the
    // drawn bar, so ExactMatch finds nothing and CompareAll only finds the slivers. Instead the
    // pointer is scaled back to a time and matched against the slices the bar was built from.

    private List<TimelineSlice> _timelineSlices = new();
    private DateTime _timelineWindowStart;

    private bool _timelineHoverVisible;
    public bool TimelineHoverVisible
    {
        get => _timelineHoverVisible;
        set
        {
            _timelineHoverVisible = value;
            OnPropertyChanged(nameof(TimelineHoverVisible));
        }
    }

    private string _timelineHoverName = "";
    public string TimelineHoverName
    {
        get => _timelineHoverName;
        set
        {
            _timelineHoverName = value;
            OnPropertyChanged(nameof(TimelineHoverName));
        }
    }

    private string _timelineHoverRange = "";
    public string TimelineHoverRange
    {
        get => _timelineHoverRange;
        set
        {
            _timelineHoverRange = value;
            OnPropertyChanged(nameof(TimelineHoverRange));
        }
    }

    private void OnTimelinePointerMoved(object? sender, PointerEventArgs e)
    {
        var position = e.GetPosition(TimelineChart);
        if (position is null)
        {
            TimelineHoverVisible = false;
            return;
        }

        // Pixels -> seconds into the window -> the clock time under the pointer
        var scaled = TimelineChart.ScalePixelsToData(new LvcPointD(position.Value.X, position.Value.Y), 0, 0);
        var time = _timelineWindowStart.AddSeconds(scaled.X);

        var hovered = _timelineSlices.FirstOrDefault(s => time >= s.Start && time < s.End);
        if (hovered is null)
        {
            // over a gap or outside the plot area
            TimelineHoverVisible = false;
            return;
        }

        TimelineHoverName = hovered.Process;
        TimelineHoverRange = $"{hovered.Start:HH:mm} - {hovered.End:HH:mm}  ({(hovered.End - hovered.Start).ToString(@"h\:mm")})";
        TimelineHoverVisible = true;

        // Follow the pointer, but keep the panel inside the chart
        double panelWidth = TimelineTooltip.Width > 0 ? TimelineTooltip.Width : 170;
        double x = position.Value.X + 12;
        if (x + panelWidth > TimelineChart.Width) x = TimelineChart.Width - panelWidth;

        TimelineTooltip.TranslationX = Math.Max(0, x);
        TimelineTooltip.TranslationY = Math.Max(0, position.Value.Y - 46);
    }

    private void OnTimelinePointerExited(object? sender, PointerEventArgs e) =>
        TimelineHoverVisible = false;
}


