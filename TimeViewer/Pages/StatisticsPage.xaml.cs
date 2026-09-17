using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.Drawing;
using LiveChartsCore.Kernel;
using LiveChartsCore.Kernel.Sketches;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using SkiaSharp;
using System.Diagnostics;

namespace TimeViewer;
public partial class StatisticsPage : ContentPage
{
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Parameters
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    double CellSize = 14;           // one day, drawn square
    double CellPadding = 2;         // gap between two day cells
    int ShadeLevels = 4;            // GitHub's four non-empty shades; the empty one is index 0
    double MinTrackedSeconds = 30;  // the same floor the pie and the timeline apply
    double WeekdayGutter = 28;      // room for the M/W/F labels left of the grid
    double MonthGutter = 20;        // room for the month labels above the grid
    double ChartEdge = 4;           // breathing room on the other two sides

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Loading Data & Refresh Logic
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

    private readonly SettingsService _settingsService;
    private readonly DataService _dataService;

    public StatisticsPage(SettingsService settingsService, DataService dataService)
    {
        InitializeComponent();
        BindingContext = this;

        _settingsService = settingsService;
        _dataService = dataService;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await RefreshAsync();
    }

    private bool _isRefreshing;
    private async Task RefreshAsync(bool forceReload = false)
    {
        if (_isRefreshing) return;
        _isRefreshing = true;
        try
        {
            // MainPage has already primed the shared DataService, so the default path costs no
            // ManicTime export - only the refresh button pays for one.
            var apps = await _dataService.GetMergedDataAsync(forceReload);
            LoadYearHeatmaps(apps, _year);
        }
        catch (InvalidOperationException ex)
        {
            await DisplayAlertAsync("ManicTime Error", ex.Message, "OK");
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Control Buttons
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

    private int _year = DateTime.Today.Year;
    private async Task ChangeYearAsync(int deltaYears)
    {
        // Nothing can be tracked in a year that has not started yet
        int next = _year + deltaYears;
        if (next > DateTime.Today.Year) return;

        _year = next;
        await RefreshAsync();
    }

    private async void OnPrevYearClicked(object? sender, EventArgs e) => await ChangeYearAsync(-1);

    private async void OnNextYearClicked(object? sender, EventArgs e) => await ChangeYearAsync(1);

    private async void OnRefreshClicked(object? sender, EventArgs e) => await RefreshAsync(forceReload: true);

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Bound State
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

    private string _displayYear = DateTime.Today.Year.ToString();
    public string DisplayYear
    {
        get => _displayYear;
        set
        {
            if (_displayYear == value) return;
            _displayYear = value;
            OnPropertyChanged(nameof(DisplayYear));
        }
    }

    private TagYearHeatmap[] _tagHeatmaps = [];
    public TagYearHeatmap[] TagHeatmaps
    {
        get => _tagHeatmaps;
        set
        {
            _tagHeatmaps = value;
            OnPropertyChanged(nameof(TagHeatmaps));
        }
    }

    private string _emptyMessage = "";
    public string EmptyMessage
    {
        get => _emptyMessage;
        set
        {
            if (_emptyMessage == value) return;
            _emptyMessage = value;
            OnPropertyChanged(nameof(EmptyMessage));
        }
    }

    private bool _isEmpty;
    public bool IsEmpty
    {
        get => _isEmpty;
        set
        {
            if (_isEmpty == value) return;
            _isEmpty = value;
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Year Heatmaps
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

    private void LoadYearHeatmaps(List<AppsTagsTable> data, int year)
    {
        DisplayYear = year.ToString();

        // The grid's origin is the Monday of the week containing Jan 1, so every column is a whole
        // Mon-Sun week. The (+6 % 7) shift turns DayOfWeek's Sunday-first numbering into Monday = 0,
        // which is the row order the Y axis labels assume.
        var jan1 = new DateTime(year, 1, 1);
        var dec31 = new DateTime(year, 12, 31);
        var firstCell = jan1.AddDays(-(((int)jan1.DayOfWeek + 6) % 7));
        int weekCount = ((dec31 - firstCell).Days / 7) + 1;

        // Daily totals per tag, biggest tag first. Two deliberate agreements with MainPage: the
        // >30s floor is the one the pie and the timeline use, and an activity crossing midnight
        // counts wholly toward its Start day, which is how the pie groups a day.
        var perTag = data
            .Where(a => TimeSpan.Parse(a.Duration).TotalSeconds > MinTrackedSeconds)
            .Where(a => a.Start.Year == year)
            .GroupBy(a => a.Tag)
            .Select(g => new
            {
                Tag = g.Key,
                Days = g
                    .GroupBy(a => a.Start.Date)
                    .ToDictionary(d => d.Key, d => d.Sum(a => TimeSpan.Parse(a.Duration).TotalSeconds)),
                Seconds = g.Sum(a => TimeSpan.Parse(a.Duration).TotalSeconds)
            })
            .OrderByDescending(t => t.Seconds)
            .ToList();

        if (perTag.Count == 0)
        {
            TagHeatmaps = [];
            EmptyMessage = $"No time tracked in {year}.";
            IsEmpty = true;
            return;
        }

        IsEmpty = false;

        var heatmaps = new List<TagYearHeatmap>();
        foreach (var tag in perTag)
        {
            string tagColor = _settingsService.GetTagColor(tag.Tag);
            string[] ramp = _settingsService.BuildTagRamp(tagColor, ShadeLevels);
            double[] thresholds = BucketThresholds(tag.Days.Values);

            // One point per day of the year, zero days included so they draw in the empty shade.
            // Days of the leading and trailing partial weeks that fall outside the year get no
            // point at all, so those corner cells stay blank - the same as GitHub.
            var cells = new List<WeightedPoint>(366);
            for (var day = jan1; day <= dec31; day = day.AddDays(1))
            {
                int offset = (day - firstCell).Days;
                tag.Days.TryGetValue(day, out double seconds);
                cells.Add(new WeightedPoint(offset / 7, offset % 7, Bucket(seconds, thresholds)));
            }

            // Captured by the tooltip below, which reports the real duration rather than the
            // bucket the cell's color came from
            var days = tag.Days;

            var series = new HeatSeries<WeightedPoint>
            {
                Name = tag.Tag,
                Values = cells,
                HeatMap = ramp.Select(hex => SKColor.Parse(hex).AsLvcColor()).ToArray(),
                // The weight IS the bucket, and the stops are evenly spaced over 0..ShadeLevels,
                // so weight k lands exactly on stop k: five discrete shades, not a gradient.
                MinValue = 0,
                MaxValue = ShadeLevels,
                PointPadding = new Padding(CellPadding),
                XToolTipLabelFormatter = point => DateFor(firstCell, point).ToString("ddd dd MMM yyyy"),
                YToolTipLabelFormatter = point =>
                    days.TryGetValue(DateFor(firstCell, point), out double seconds)
                        ? TimeSpan.FromSeconds(seconds).ToString(@"h\:mm")
                        : "nothing tracked"
            };

            heatmaps.Add(new TagYearHeatmap
            {
                Tag = tag.Tag,
                TagColor = Color.Parse(tagColor),
                TotalLabel = $"{TimeSpan.FromSeconds(tag.Seconds).TotalHours:F0}h over {tag.Days.Count} days",
                Series = [series],
                XAxes = [BuildMonthAxis(firstCell, weekCount, year)],
                YAxes = [BuildWeekdayAxis()],
                DrawMargin = new Margin((float)WeekdayGutter, (float)MonthGutter, (float)ChartEdge, (float)ChartEdge),
                ChartWidth = WeekdayGutter + (weekCount * CellSize) + ChartEdge,
                ChartHeight = MonthGutter + (7 * CellSize) + ChartEdge,
                ScaleSwatches = ramp.Select(Color.Parse).ToArray()
            });
        }

        TagHeatmaps = heatmaps.ToArray();

        Debug.WriteLine($"Year heatmaps built: {heatmaps.Count} tags, {weekCount} week columns");
    }

    // The date a hovered cell stands for: X is its week column, Y its weekday within that week
    private static DateTime DateFor(DateTime firstCell, ChartPoint point) =>
        firstCell.AddDays((point.Coordinate.SecondaryValue * 7) + point.Coordinate.PrimaryValue);

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Shade Buckets
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Each tag is scaled against its own days, so a light-usage tag still shows a full light-to-dark
    // range instead of staying uniformly pale. Quartiles rather than a linear split of the maximum,
    // so one exceptional day cannot wash out the whole year.

    private static double[] BucketThresholds(IEnumerable<double> dailySeconds)
    {
        var sorted = dailySeconds.Where(s => s > 0).OrderBy(s => s).ToArray();
        if (sorted.Length == 0) return [];

        return [Percentile(sorted, 0.25), Percentile(sorted, 0.50), Percentile(sorted, 0.75)];
    }

    private static double Percentile(double[] sorted, double fraction)
    {
        int index = (int)Math.Ceiling(fraction * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    // 0 for a day with nothing tracked, otherwise the first threshold the day fits under.
    // Testing them in order keeps the buckets monotone even when the percentiles tie - a tag with
    // one or two active days, or a run of identical days, needs no special case.
    private int Bucket(double seconds, double[] thresholds)
    {
        if (seconds <= 0) return 0;

        for (int i = 0; i < thresholds.Length; i++)
        {
            if (seconds <= thresholds[i]) return i + 1;
        }

        return ShadeLevels;
    }

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Axes
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Both axes are pinned half a cell outside the data so the outermost cells are drawn whole.
    // -0.5 also sidesteps the quirk the timeline documents in MainPage: assigning an axis's own
    // default (0) raises no change notification, and the axis then auto-scales instead.

    private static Axis BuildMonthAxis(DateTime firstCell, int weekCount, int year)
    {
        // A label on the first week column each month reaches into. A column is named after the
        // month its Sunday falls in, so a week split across two months labels the one it mostly
        // belongs to and January never lands on the previous year's stub column. The final column
        // can reach into next January, which would print a second "Jan" at the far right, so a
        // column whose Sunday has already left the year gets no label.
        var separators = new List<double>();
        int lastMonth = 0;
        for (int week = 0; week < weekCount; week++)
        {
            var sunday = firstCell.AddDays((week * 7) + 6);
            if (sunday.Year != year || sunday.Month == lastMonth) continue;

            separators.Add(week);
            lastMonth = sunday.Month;
        }

        return new Axis
        {
            // End is the top of a cartesian chart's X axis, where GitHub puts the months
            Position = AxisPosition.End,
            Labeler = week => firstCell.AddDays((week * 7) + 6).ToString("MMM"),
            CustomSeparators = separators,
            ShowSeparatorLines = false,
            TextSize = 11,
            MinLimit = -0.5,
            MaxLimit = weekCount - 0.5
        };
    }

    private static Axis BuildWeekdayAxis() => new Axis
    {
        // Monday belongs on top, and GitHub labels only three of the seven rows
        IsInverted = true,
        Labeler = weekday => (int)Math.Round(weekday) switch
        {
            0 => "M",
            2 => "W",
            4 => "F",
            _ => ""
        },
        CustomSeparators = [0, 2, 4],
        ShowSeparatorLines = false,
        TextSize = 11,
        MinLimit = -0.5,
        MaxLimit = 6.5
    };
}
