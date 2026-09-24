using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.Drawing;
using LiveChartsCore.Kernel;
using LiveChartsCore.Kernel.Sketches;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using System.Diagnostics;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TimeViewer.ViewModels;

// A heatmap per tag for one year: the GitHub-style Year Overview and the weekday x hour Active
// Hours grid. Ported from the MAUI StatisticsPage.
public partial class StatisticsViewModel : ViewModelBase
{
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Parameters
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    const double CellSize = 14;           // one cell - a day, or one weekday-hour - drawn square
    const double CellPadding = 2;         // gap between two day cells
    const double WeekdayGutter = 38;      // room for the weekday labels left of either grid.
                                    // Sized for Active Hours' "Wed", not the year grid's "W":
                                    // at 28 the 00:00 column painted over the last letter.
    const double TopGutter = 20;          // room for the month or hour labels above the grid
    const double ChartEdge = 4;           // breathing room on the other two sides

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Loading Data & Refresh Logic
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

    private readonly SettingsService _settingsService;
    private readonly DataService _dataService;
    private readonly VaultExportService _vaultExportService;
    private readonly DialogService _dialogs;

    public StatisticsViewModel(SettingsService settingsService, DataService dataService,
        VaultExportService vaultExportService, DialogService dialogs)
    {
        _settingsService = settingsService;
        _dataService = dataService;
        _vaultExportService = vaultExportService;
        _dialogs = dialogs;
    }

    public override Task OnNavigatedToAsync() => RefreshAsync();

    // Same rule as the day view: the charts dispose their series' and axes' paints on unload, so
    // a later visit (Back / Forward) must get fresh ones rather than these. RefreshAsync rebuilds.
    public override void OnNavigatedFrom() => TagStats = [];

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    private bool _isRefreshing;
    private async Task RefreshAsync(bool forceReload = false)
    {
        if (_isRefreshing) return;
        _isRefreshing = true;
        IsBusy = true;
        try
        {
            // MainPage has already primed the shared DataService, so the default path costs no
            // ManicTime export - only the refresh button pays for one.
            var apps = await _dataService.GetMergedDataAsync(forceReload);
            LoadTagStatistics(apps, _year);

            // The chart is the point of this page; a vault that has moved or is on an unplugged
            // drive must not take it down with it. The Export button reports failures out loud.
            try
            {
                await _vaultExportService.ExportAsync(apps);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Vault export skipped: {ex.Message}");
            }
        }
        catch (InvalidOperationException ex)
        {
            await _dialogs.AlertAsync("ManicTime Error", ex.Message);
        }
        catch (Exception ex)
        {
            // OnAppearing is async void, so anything unhandled here takes the whole app down
            await _dialogs.AlertAsync("Could not draw the statistics", ex.Message);
        }
        finally
        {
            _isRefreshing = false;
            IsBusy = false;
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

    [RelayCommand] private Task PrevYear() => ChangeYearAsync(-1);

    [RelayCommand] private Task NextYear() => ChangeYearAsync(1);

    [RelayCommand] private Task Refresh() => RefreshAsync(forceReload: true);

    // The same export RefreshAsync runs silently, but here the outcome is the whole point, so
    // every branch says something - including "you have not switched it on yet".
    [RelayCommand]
    private async Task Export()
    {
        try
        {
            var apps = await _dataService.GetMergedDataAsync(forceReload: false);
            string? path = await _vaultExportService.ExportAsync(apps);

            if (path is null)
                await _dialogs.AlertAsync("Vault Export",
                    "No export folder is set. Choose one in Settings and enable the export.");
            else
                await _dialogs.AlertAsync("Vault Export", $"Written to:{Environment.NewLine}{path}");
        }
        catch (Exception ex)
        {
            await _dialogs.AlertAsync("Vault Export Failed", ex.Message);
        }
    }

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Bound State
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

    [ObservableProperty]
    public partial string DisplayYear { get; private set; } = DateTime.Today.Year.ToString();

    [ObservableProperty]
    public partial TagStatistics[] TagStats { get; private set; } = [];

    [ObservableProperty]
    public partial string EmptyMessage { get; private set; } = "";

    [ObservableProperty]
    public partial bool IsEmpty { get; private set; }

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Tag Statistics
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

    // One row per tag: the title, then two grids side by side. Year Overview answers how much and
    // on which days; Active Hours answers when during the day. Both cover the same year and are
    // built from the same set of activities, so they can be read against each other.
    private void LoadTagStatistics(IReadOnlyList<AppsTagsTable> data, int year)
    {
        DisplayYear = year.ToString();

        // The year grid's origin is the Monday of the week containing Jan 1, so every column is a
        // whole Mon-Sun week. The (+6 % 7) shift turns DayOfWeek's Sunday-first numbering into
        // Monday = 0, which is the row order both grids' Y axes assume.
        var jan1 = new DateTime(year, 1, 1);
        var firstCell = jan1.AddDays(-(((int)jan1.DayOfWeek + 6) % 7));
        int weekCount = ((new DateTime(year, 12, 31) - firstCell).Days / 7) + 1;

        // Daily totals per tag, biggest tag first. Shared with the Obsidian vault export so the
        // two can never disagree - see HeatmapAggregator for the floor and the midnight rule.
        var perTag = HeatmapAggregator.AggregateTagDays(data, year);

        // The second grid's numbers, looked up by tag rather than zipped: the loop below is driven
        // by the daily totals, so nothing can pair one tag's year with another tag's hours. Both
        // aggregations select rows identically, so every tag here has an entry there.
        var perTagHours = HeatmapAggregator.AggregateTagWeekHours(data, year)
            .ToDictionary(t => t.Tag);

        if (perTag.Count == 0)
        {
            TagStats = [];
            EmptyMessage = $"No time tracked in {year}.";
            IsEmpty = true;
            return;
        }

        IsEmpty = false;

        var stats = new List<TagStatistics>();
        foreach (var tag in perTag)
        {
            // One colour per tag, one ramp from it, shared by both of that tag's grids. A shade
            // stands for a different span in each, which is why each panel carries its own strip.
            string tagColor = _settingsService.GetTagColor(tag.Tag);
            string[] ramp = _settingsService.BuildTagRamp(tagColor, HeatmapAggregator.ShadeLevels);


            stats.Add(new TagStatistics
            {
                Tag = tag.Tag,
                TagColor = Color.Parse(tagColor),
                TotalLabel = $"{TimeSpan.FromSeconds(tag.TotalSeconds).TotalHours:F0}h over {tag.Days.Count} days",
                Panels =
                [
                    BuildYearPanel(tag, ramp, year, firstCell, weekCount),
                    BuildActiveHoursPanel(perTagHours[tag.Tag], ramp)
                ]
            });
        }

        TagStats = stats.ToArray();

        Debug.WriteLine($"Tag statistics built: {stats.Count} tags, {weekCount} week columns, "
            + $"{HeatmapAggregator.WeekdayCount}x{HeatmapAggregator.HourCount} active hour cells");
    }

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Year Overview
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

    // The GitHub contributions grid: X = week column, Y = weekday, one cell per day of the year.
    private HeatmapPanel BuildYearPanel(TagDailyTotals tag, string[] ramp,
        int year, DateTime firstCell, int weekCount)
    {
        double[] thresholds = HeatmapAggregator.BucketThresholds(tag.Days.Values);

        // One point per day of the year, zero days included so they draw in the empty shade.
        // Days of the leading and trailing partial weeks that fall outside the year get no
        // point at all, so those corner cells stay blank - the same as GitHub.
        var cells = new List<WeightedPoint>(366);
        for (var day = new DateTime(year, 1, 1); day.Year == year; day = day.AddDays(1))
        {
            int offset = (day - firstCell).Days;
            tag.Days.TryGetValue(day, out double seconds);
            cells.Add(new WeightedPoint(offset / 7, offset % 7, HeatmapAggregator.Bucket(seconds, thresholds)));
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
            MaxValue = HeatmapAggregator.ShadeLevels,
            PointPadding = new Padding(CellPadding),
            XToolTipLabelFormatter = point => DateFor(firstCell, point).ToString("ddd dd MMM yyyy"),
            YToolTipLabelFormatter = point =>
                days.TryGetValue(DateFor(firstCell, point), out double seconds)
                    ? TimeSpan.FromSeconds(seconds).ToString(@"h\:mm")
                    : "nothing tracked"
        };

        return new HeatmapPanel
        {
            Caption = "Year Overview",
            Series = [series],
            XAxes = [BuildMonthAxis(firstCell, weekCount, year)],
            YAxes = [BuildWeekdayAxis()],
            DrawMargin = new Margin((float)WeekdayGutter, (float)TopGutter, (float)ChartEdge, (float)ChartEdge),
            ChartWidth = WeekdayGutter + (weekCount * CellSize) + ChartEdge,
            ChartHeight = TopGutter + (HeatmapAggregator.WeekdayCount * CellSize) + ChartEdge,
            ScaleSteps = BuildScaleSteps(ramp, thresholds)
        };
    }

    // The date a hovered cell stands for: X is its week column, Y its weekday within that week
    private static DateTime DateFor(DateTime firstCell, ChartPoint point) =>
        firstCell.AddDays((point.Coordinate.SecondaryValue * 7) + point.Coordinate.PrimaryValue);

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Active Hours
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

    // Monday-Sunday down the side, 00-23 across the top: when during the day this tag happens.
    // Drawn at the same cell size and height as the year grid, so the two line up row for row.
    //
    // The quartiles are computed over these 168 cells rather than over the year's days, because a
    // cell here is one hour summed over every week of the year and is nowhere near the same
    // magnitude as a single day. That is exactly why this panel carries its own scale strip.
    private HeatmapPanel BuildActiveHoursPanel(TagWeekHourTotals tag, string[] ramp)
    {
        var cells = tag.Cells;
        double[] thresholds = HeatmapAggregator.BucketThresholds(cells.Cast<double>());

        // Every cell gets a point, including the empty ones: unlike the year grid there is no
        // such thing as an hour outside the period, so nothing here should be left blank.
        var points = new List<WeightedPoint>(HeatmapAggregator.WeekdayCount * HeatmapAggregator.HourCount);
        for (int weekday = 0; weekday < HeatmapAggregator.WeekdayCount; weekday++)
        {
            for (int hour = 0; hour < HeatmapAggregator.HourCount; hour++)
            {
                // X = hour, Y = weekday, mirroring the year grid's X = week, Y = weekday
                points.Add(new WeightedPoint(hour, weekday,
                    HeatmapAggregator.Bucket(cells[weekday, hour], thresholds)));
            }
        }

        var series = new HeatSeries<WeightedPoint>
        {
            Name = tag.Tag,
            Values = points,
            HeatMap = ramp.Select(hex => SKColor.Parse(hex).AsLvcColor()).ToArray(),
            MinValue = 0,
            MaxValue = HeatmapAggregator.ShadeLevels,
            PointPadding = new Padding(CellPadding),
            XToolTipLabelFormatter = point =>
            {
                var (weekday, hour) = CellFor(point);
                return $"{WeekdayNames[weekday]} {hour:00}:00-{(hour + 1) % HeatmapAggregator.HourCount:00}:00";
            },
            // FormatDuration rather than TimeSpan's "h:mm": a cell sums one hour over every week of
            // the year, so it routinely passes 24h - and "h" is hours WITHIN a day, which would
            // render 52h as "4:00" and silently drop two whole days.
            YToolTipLabelFormatter = point =>
            {
                var (weekday, hour) = CellFor(point);
                double seconds = cells[weekday, hour];
                return seconds > 0 ? HeatmapAggregator.FormatDuration(seconds) : "nothing tracked";
            }
        };

        return new HeatmapPanel
        {
            Caption = "Active Hours",
            Series = [series],
            XAxes = [BuildHourAxis()],
            YAxes = [BuildFullWeekdayAxis()],
            DrawMargin = new Margin((float)WeekdayGutter, (float)TopGutter, (float)ChartEdge, (float)ChartEdge),
            ChartWidth = WeekdayGutter + (HeatmapAggregator.HourCount * CellSize) + ChartEdge,
            ChartHeight = TopGutter + (HeatmapAggregator.WeekdayCount * CellSize) + ChartEdge,
            ScaleSteps = BuildScaleSteps(ramp, thresholds)
        };
    }

    // The cell a hovered point stands for: X is the hour, Y the weekday
    private static (int Weekday, int Hour) CellFor(ChartPoint point) =>
        ((int)Math.Round(point.Coordinate.PrimaryValue), (int)Math.Round(point.Coordinate.SecondaryValue));

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Legend
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

    // One swatch per shade, labelled with the span of tracked time it stands for, so a grid can be
    // read without hovering every cell. Built from the same ramp and thresholds the cells use, so
    // a strip cannot drift from what it describes - which is also why each panel builds its own.
    private static HeatmapScaleStep[] BuildScaleSteps(string[] ramp, double[] thresholds)
    {
        string[] labels = HeatmapAggregator.BucketLabels(thresholds);

        return ramp
            .Select((hex, i) => new HeatmapScaleStep { Color = Color.Parse(hex), Label = labels[i] })
            .ToArray();
    }


    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Axes
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Every axis is pinned half a cell outside the data so the outermost cells are drawn whole.
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
            LabelsPaint = LabelPaint(),
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
        LabelsPaint = LabelPaint(),
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
        MaxLimit = HeatmapAggregator.WeekdayCount - 0.5
    };

    // Every third hour, which is as dense as 14px cells carry at this text size
    private static Axis BuildHourAxis() => new Axis
    {
        LabelsPaint = LabelPaint(),
        // End puts them along the top, where the year grid's months are
        Position = AxisPosition.End,
        Labeler = hour => $"{(int)Math.Round(hour):00}",
        CustomSeparators = [0, 3, 6, 9, 12, 15, 18, 21],
        ShowSeparatorLines = false,
        TextSize = 11,
        MinLimit = -0.5,
        MaxLimit = HeatmapAggregator.HourCount - 0.5
    };

    // Axis text in the theme's secondary text colour; LiveCharts draws its own text and cannot
    // follow the theme resources the rest of the page uses. A NEW paint per axis on purpose:
    // each chart disposes its paints when it unloads, so one shared between charts would be
    // disposed under the other.
    private static SolidColorPaint LabelPaint() =>
        new(Application.Current?.ActualThemeVariant == ThemeVariant.Dark
            ? new SKColor(0xC5, 0xC5, 0xC5)
            : new SKColor(0x5C, 0x5C, 0x5C));

    private static readonly string[] WeekdayNames = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];

    // Monday on top like the year grid, but every row labelled. The year grid can get away with
    // three of seven because its rows are not its subject; here the weekday IS the subject, and
    // initials would leave two ambiguous pairs (T/T and S/S).
    private static Axis BuildFullWeekdayAxis() => new Axis
    {
        LabelsPaint = LabelPaint(),
        IsInverted = true,
        Labeler = weekday => WeekdayNames[Math.Clamp((int)Math.Round(weekday), 0, WeekdayNames.Length - 1)],
        CustomSeparators = [0, 1, 2, 3, 4, 5, 6],
        ShowSeparatorLines = false,
        TextSize = 11,
        MinLimit = -0.5,
        MaxLimit = HeatmapAggregator.WeekdayCount - 0.5
    };
}
