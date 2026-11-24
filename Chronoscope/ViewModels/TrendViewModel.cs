using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.Painting.Effects;
using SkiaSharp;

namespace Chronoscope.ViewModels;

// Where each tag is heading if the recent trend carries on. TrendForecaster does the maths; this
// turns it into a summary table and four kinds of chart:
//  - Next 30 vs last 30 days, per tag: what is growing and what is shrinking, at a glance
//  - Weekly hours by tag, stacked: how the week splits up, then and projected
//  - The year so far, per tag, and where it ends up by 31 December
//  - Every tag on one large chart - weekly hours, trend line and forecast - with a
//    list beside it that switches each tag on or off, so any set of tags can be compared directly
public partial class TrendViewModel : ViewModelBase, IKeepAlive
{
    private readonly SettingsService _settingsService;
    private readonly DataService _dataService;
    private readonly DialogService _dialogs;

    public TrendViewModel(SettingsService settingsService, DataService dataService, DialogService dialogs)
    {
        _settingsService = settingsService;
        _dataService = dataService;
        _dialogs = dialogs;
        SelectedHistory = HistoryOptions[2];
        SelectedHorizon = HorizonOptions[1];
    }

    // The defaults above are set through the same properties the drop-downs use; they must not
    // start a refresh before the page has even been opened
    private bool _ready;

    public override Task OnNavigatedToAsync()
    {
        _ready = true;
        return RefreshAsync();
    }

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Options
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // A short history follows recent changes quickly but believes every odd week; a long one is
    // steady but slow to notice that something changed. Neither is "right", so it is a choice.

    public TrendOption[] HistoryOptions { get; } =
    [
        new(8, "Last 8 weeks"),
        new(13, "Last 3 months"),
        new(26, "Last 6 months"),
        new(52, "Last year")
    ];

    public TrendOption[] HorizonOptions { get; } =
    [
        new(4, "4 weeks ahead"),
        new(12, "3 months ahead"),
        new(26, "6 months ahead")
    ];

    [ObservableProperty]
    public partial TrendOption SelectedHistory { get; set; }

    [ObservableProperty]
    public partial TrendOption SelectedHorizon { get; set; }

    partial void OnSelectedHistoryChanged(TrendOption value) { if (_ready) _ = RefreshAsync(); }
    partial void OnSelectedHorizonChanged(TrendOption value) { if (_ready) _ = RefreshAsync(); }

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Bound State
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

    [ObservableProperty] public partial bool IsBusy { get; private set; }
    [ObservableProperty] public partial bool IsEmpty { get; private set; }
    [ObservableProperty] public partial string EmptyMessage { get; private set; } = "";
    [ObservableProperty] public partial string BasisLabel { get; private set; } = "";
    [ObservableProperty] public partial string YearEndCaption { get; private set; } = "";

    [ObservableProperty] public partial TrendTagCard[] Cards { get; private set; } = [];
    [ObservableProperty] public partial TrendChart? MonthChart { get; private set; }
    [ObservableProperty] public partial TrendChart? ShareChart { get; private set; }
    [ObservableProperty] public partial TrendChart? YearChart { get; private set; }
    [ObservableProperty] public partial TrendChart? TagsChart { get; private set; }

    // The two columns of the 30 day chart, also drawn as its legend in the view
    public Color LastColor { get; } = Color.FromRgb(0x9A, 0x9A, 0x9A);
    public Color NextColor { get; } = Color.FromRgb(0x3B, 0x82, 0xF6);

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Loading
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

    // What is on screen: same data, options, day and colours means there is nothing to redo
    private (IReadOnlyList<AppsTagsTable>? Apps, int History, int Horizon, DateTime Today, int Colors, int Groups) _shown;

    private bool _isRefreshing;
    private bool _refreshAgain;

    [RelayCommand] private Task Refresh() => RefreshAsync(forceReload: true);

    private async Task RefreshAsync(bool forceReload = false)
    {
        // A drop-down changed while a refresh was running: run once more when it is done, so the
        // page ends up showing the latest choice rather than the one it started with
        if (_isRefreshing)
        {
            _refreshAgain = true;
            return;
        }

        _isRefreshing = true;
        IsBusy = true;
        try
        {
            do
            {
                _refreshAgain = false;

                var apps = await _dataService.GetMergedDataAsync(forceReload);
                forceReload = false;

                int history = SelectedHistory.Weeks, horizon = SelectedHorizon.Weeks;
                var groups = _dataService.GroupByTag;
                var shown = (apps, history, horizon, DateTime.Today, _settingsService.TagColorsVersion, _dataService.GroupsVersion);
                if (shown == _shown) continue;

                var result = await Task.Run(() => TrendForecaster.Forecast(
                    TagGrouping.WithGroupRows(apps, groups), DateTime.Today, history));
                Build(result, horizon);
                _shown = (apps, history, horizon, DateTime.Today, _settingsService.TagColorsVersion, _dataService.GroupsVersion);
            }
            while (_refreshAgain);
        }
        catch (InvalidOperationException ex)
        {
            await _dialogs.AlertAsync("ManicTime Error", ex.Message);
        }
        catch (Exception ex)
        {
            await _dialogs.AlertAsync("Could not draw the trends", ex.Message);
        }
        finally
        {
            _isRefreshing = false;
            IsBusy = false;
        }
    }

    // Every tag AND every tag group is forecast: the grouped rows go in a second time under their
    // group's key (TagGrouping.WithGroupRows), so a group is one more forecast beside its tags.
    // Groups take part where they add something - the per tag chart, its list and the table -
    // and stay out of the charts that add tags up, where they would count their hours twice.
    private void Build(TrendResult result, int horizon)
    {
        foreach (var old in Cards) old.PropertyChanged -= OnCardChanged;
        _tagSeries.Clear();

        if (!result.HasEnoughHistory || result.Tags.Count == 0)
        {
            Cards = [];
            MonthChart = ShareChart = YearChart = TagsChart = null;
            BasisLabel = "";
            EmptyMessage = !result.HasEnoughHistory
                ? $"A trend needs at least {TrendForecaster.MinWeeks} complete tracked weeks - so far there " +
                  $"{(result.AvailableWeeks == 1 ? "is 1" : $"are {result.AvailableWeeks}")}."
                : "Nothing was tracked in the chosen weeks.";
            IsEmpty = true;
            return;
        }

        IsEmpty = false;

        var lastWeekEnd = result.FirstWeek.AddDays((7 * result.Weeks) - 1);
        BasisLabel = $"Based on the {result.Weeks} complete weeks from {result.FirstWeek:dd MMM yyyy} to {lastWeekEnd:dd MMM yyyy}"
                     + (result.Weeks < result.RequestedWeeks ? " - all that has been tracked so far" : "")
                     + ".";
        YearEndCaption = $"The year so far, and where it ends up by 31 Dec {result.Today.Year}";

        var colors = result.Tags.ToDictionary(t => t.Tag, t => ColorOf(t.Tag));
        var tagsOnly = result.With(result.Tags.Where(t => !TagGrouping.IsGroupKey(t.Tag)));

        Cards = result.Tags.Select(t => BuildCard(t, colors[t.Tag])).ToArray();
        MonthChart = BuildMonthChart(tagsOnly);
        ShareChart = BuildShareChart(tagsOnly, colors, horizon);
        YearChart = BuildYearChart(tagsOnly, colors);
        TagsChart = BuildTagsChart(result, colors, horizon);

        // Which entries start switched on: the ones the user had on before this rebuild (a new
        // history window or a reload must not reset the selection), else the three biggest
        _chosenTags ??= result.Tags.Take(DefaultShownTags).Select(t => t.Tag).ToHashSet();
        foreach (var card in Cards)
        {
            card.IsShown = _chosenTags.Contains(card.Tag);
            ApplyVisibility(card);
            card.PropertyChanged += OnCardChanged;
        }
    }

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Summary Table
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

    // Within ±10 % over four weeks counts as steady: a trend smaller than that is well inside
    // what a normal week scatters by, and calling it "rising" would be reading tea leaves
    private const double SteadyBand = 0.10;

    private static TrendTagCard BuildCard(TagForecast t, string hex)
    {
        var direction = t.FourWeekChange > SteadyBand ? TrendDirection.Rising
            : t.FourWeekChange < -SteadyBand ? TrendDirection.Falling
            : TrendDirection.Steady;

        return new TrendTagCard
        {
            Tag = t.Tag,
            Title = TagGrouping.NameOf(t.Tag),
            IsGroup = TagGrouping.IsGroupKey(t.Tag),
            TagColor = Color.Parse(hex),
            NowLabel = $"{Hours(t.Level)} / week",
            Direction = direction,
            TrendLabel = $"{(t.Slope >= 0 ? "+" : "−")}{Hours(Math.Abs(t.Slope))} / week each week",
            Last30Label = Hours(t.Last30Hours),
            Next30Label = "≈ " + Hours(t.Next30Hours),
            YearToDateLabel = Hours(t.YearToDateHours),
            YearEndLabel = "≈ " + Hours(t.YearEndHours)
        };
    }

    // Hours with one decimal below ten, whole hours above: "0.4h", "6.5h", "28h", "412h"
    private static string Hours(double hours) =>
        hours < 10 ? $"{hours:0.#}h" : $"{hours:0}h";

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Next 30 vs Last 30 Days
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

    private TrendChart BuildMonthChart(TrendResult result)
    {
        var tags = result.Tags;
        string[] names = tags.Select(t => t.Tag).ToArray();

        ColumnSeries<double> Columns(string name, Color color, IEnumerable<double> values, string prefix) => new()
        {
            Name = name,
            Values = values.ToArray(),
            Fill = new SolidColorPaint(ToSk(color)),
            Stroke = null,
            MaxBarWidth = 22,
            Padding = 2,
            XToolTipLabelFormatter = p => names[(int)Math.Round(p.Coordinate.SecondaryValue)],
            YToolTipLabelFormatter = p => $"{name}: {prefix}{Hours(p.Coordinate.PrimaryValue)}"
        };

        return new TrendChart
        {
            Series =
            [
                Columns("Last 30 days", LastColor, tags.Select(t => t.Last30Hours), ""),
                Columns("Next 30 days", NextColor, tags.Select(t => t.Next30Hours), "≈ ")
            ],
            XAxes =
            [
                new Axis
                {
                    Labels = names,
                    LabelsPaint = LabelPaint(),
                    TextSize = 11,
                    LabelsRotation = names.Length > 6 ? 30 : 0,
                    ShowSeparatorLines = false,
                    MinStep = 1,
                    ForceStepToMin = true
                }
            ],
            YAxes = [HoursAxis()]
        };
    }

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Weekly Hours by Tag (stacked)
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Every tag's weeks stacked on top of each other: the height is the whole tracked week, each
    // band one tag's share of it. Left of the shaded section is what happened, right of it the
    // projection - so a tag swelling or thinning out reads as its band changing width.

    private TrendChart BuildShareChart(TrendResult result, Dictionary<string, string> colors, int horizon)
    {
        int lastX = result.Weeks - 1 + horizon;

        var series = result.Tags.Select(t =>
        {
            var points = new List<ObservablePoint>(lastX + 1);
            for (int x = 0; x <= lastX; x++)
                points.Add(new ObservablePoint(x, x < result.Weeks ? t.WeeklyHours[x] : t.WeeklyAt(x)));

            int weeks = result.Weeks;
            return (ISeries)new StackedAreaSeries<ObservablePoint>
            {
                Name = t.Tag,
                Values = points,
                Fill = new SolidColorPaint(ToSk(colors[t.Tag]).WithAlpha(0xC0)),
                Stroke = null,
                GeometrySize = 0,
                GeometryFill = null,
                GeometryStroke = null,
                LineSmoothness = 0.3,
                XToolTipLabelFormatter = p => WeekLabel(result, p.Coordinate.SecondaryValue),
                YToolTipLabelFormatter = p => $"{t.Tag}: {(p.Coordinate.SecondaryValue >= weeks ? "≈ " : "")}{Hours(p.Coordinate.PrimaryValue)}"
            };
        }).ToArray();

        return new TrendChart
        {
            Series = series,
            XAxes = [WeekAxis(result, lastX)],
            YAxes = [HoursAxis()],
            Sections = [ForecastSection(result.Weeks - 0.5, lastX + 0.5)]
        };
    }

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Year-End Projection
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Hours summed from 1 January: solid up to today, dashed from there to 31 December. X is the
    // day of the year, so the chart always spans the whole calendar year.

    private TrendChart BuildYearChart(TrendResult result, Dictionary<string, string> colors)
    {
        var today = result.Today;
        var yearStart = new DateTime(today.Year, 1, 1);
        var yearEnd = new DateTime(today.Year, 12, 31);
        int todayX = (today - yearStart).Days;
        int lastX = (yearEnd - yearStart).Days;

        var series = new List<ISeries>();
        foreach (var t in result.Tags)
        {
            var color = ToSk(colors[t.Tag]);

            var actual = new List<ObservablePoint>(todayX + 1);
            double sum = 0;
            for (int x = 0; x <= todayX; x++)
            {
                t.Days.TryGetValue(yearStart.AddDays(x), out double seconds);
                sum += seconds / 3600;
                actual.Add(new ObservablePoint(x, sum));
            }

            // Starts on today's point so the two lines meet
            var projected = new List<ObservablePoint>(lastX - todayX + 1) { new(todayX, sum) };
            for (int x = todayX + 1; x <= lastX; x++)
            {
                sum += t.WeeklyAt(result.XOf(yearStart.AddDays(x))) / 7;
                projected.Add(new ObservablePoint(x, sum));
            }

            series.Add(new LineSeries<ObservablePoint>
            {
                Name = t.Tag,
                Values = actual,
                Stroke = new SolidColorPaint(color, 2),
                Fill = null,
                GeometrySize = 0,
                GeometryFill = null,
                GeometryStroke = null,
                LineSmoothness = 0,
                XToolTipLabelFormatter = p => yearStart.AddDays(p.Coordinate.SecondaryValue).ToString("ddd dd MMM"),
                YToolTipLabelFormatter = p => $"{t.Tag}: {Hours(p.Coordinate.PrimaryValue)} so far"
            });
            series.Add(new LineSeries<ObservablePoint>
            {
                Name = t.Tag + " (projected)",
                Values = projected,
                Stroke = new SolidColorPaint(color, 2) { PathEffect = new DashEffect([6, 4]) },
                Fill = null,
                GeometrySize = 0,
                GeometryFill = null,
                GeometryStroke = null,
                LineSmoothness = 0,
                XToolTipLabelFormatter = p => yearStart.AddDays(p.Coordinate.SecondaryValue).ToString("ddd dd MMM"),
                YToolTipLabelFormatter = p => $"{t.Tag}: ≈ {Hours(p.Coordinate.PrimaryValue)}"
            });
        }

        // A label at the start of every month
        var monthStarts = Enumerable.Range(1, 12)
            .Select(m => (double)(new DateTime(today.Year, m, 1) - yearStart).Days)
            .ToArray();

        return new TrendChart
        {
            Series = series.ToArray(),
            XAxes =
            [
                new Axis
                {
                    LabelsPaint = LabelPaint(),
                    TextSize = 11,
                    Labeler = x => yearStart.AddDays(Math.Round(x)).ToString("MMM"),
                    CustomSeparators = monthStarts,
                    SeparatorsPaint = GridPaint(),
                    MinLimit = 0,
                    MaxLimit = lastX
                }
            ],
            YAxes = [HoursAxis()],
            Sections = [ForecastSection(todayX, lastX)]
        };
    }

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Tags Together
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // One chart holding every tag, each as three layers, back to front:
    //  - the trend line drawn back over the history, faint and dashed, so the slope can be checked
    //    against the weeks it was taken from
    //  - the weeks that happened, solid with a dot per week
    //  - the forecast, dashed, carrying on from the trend line
    // Every tag's series are built once; the list beside the chart only switches them visible or
    // not. Nothing is rebuilt on a toggle, so it is instant and the chart never blinks.

    private const int DefaultShownTags = 3;

    // The entries switched on, by key, kept across rebuilds. null until the page is first built.
    private HashSet<string>? _chosenTags;

    // A group's lines are drawn a little heavier than a tag's, so the sum reads apart from its parts
    private static float LineWidth(TagForecast t) => TagGrouping.IsGroupKey(t.Tag) ? 3f : 2f;

    // A group forecast takes its group's colour, a tag forecast its tag's
    private string ColorOf(string key) =>
        TagGrouping.IsGroupKey(key)
            ? _settingsService.GetGroupColor(TagGrouping.NameOf(key))
            : _settingsService.GetTagColor(key);

    private sealed record TagSeries(ISeries[] Lines);
    private readonly Dictionary<string, TagSeries> _tagSeries = new();

    private TrendChart BuildTagsChart(TrendResult result, Dictionary<string, string> colors, int horizon)
    {
        int weeks = result.Weeks;
        int lastX = weeks - 1 + horizon;
        var lines = new List<ISeries>();

        foreach (var t in result.Tags)
        {
            var color = ToSk(colors[t.Tag]);

            var ahead = new List<ObservablePoint>();
            for (int x = weeks - 1; x <= lastX; x++)
                ahead.Add(new ObservablePoint(x, t.WeeklyAt(x)));

            var fitted = Enumerable.Range(0, weeks).Select(x => new ObservablePoint(x, t.WeeklyAt(x))).ToList();
            var history = t.WeeklyHours.Select((h, x) => new ObservablePoint(x, h)).ToList();

            var trendSeries = new LineSeries<ObservablePoint>
            {
                Values = fitted,
                Stroke = new SolidColorPaint(color.WithAlpha(0x80), 1.5f) { PathEffect = new DashEffect([3, 3]) },
                Fill = null,
                GeometrySize = 0,
                GeometryFill = null,
                GeometryStroke = null,
                LineSmoothness = 0,
                IsHoverable = false
            };

            var historySeries = new LineSeries<ObservablePoint>
            {
                Name = TagGrouping.NameOf(t.Tag),
                Values = history,
                Stroke = new SolidColorPaint(color, LineWidth(t)),
                Fill = null,
                GeometrySize = 6,
                GeometryFill = new SolidColorPaint(color),
                GeometryStroke = null,
                LineSmoothness = 0,
                XToolTipLabelFormatter = p => WeekLabel(result, p.Coordinate.SecondaryValue),
                YToolTipLabelFormatter = p => Hours(p.Coordinate.PrimaryValue)
            };

            // Starts on the last complete week so it carries on from the history without a gap
            var forecastSeries = new LineSeries<ObservablePoint>
            {
                Name = TagGrouping.NameOf(t.Tag) + " (forecast)",
                Values = ahead,
                Stroke = new SolidColorPaint(color, LineWidth(t)) { PathEffect = new DashEffect([6, 4]) },
                Fill = null,
                GeometrySize = 0,
                GeometryFill = null,
                GeometryStroke = null,
                LineSmoothness = 0,
                XToolTipLabelFormatter = p => WeekLabel(result, p.Coordinate.SecondaryValue),
                YToolTipLabelFormatter = p => "≈ " + Hours(p.Coordinate.PrimaryValue)
            };

            lines.AddRange([trendSeries, historySeries, forecastSeries]);
            _tagSeries[t.Tag] = new TagSeries([trendSeries, historySeries, forecastSeries]);
        }

        return new TrendChart
        {
            Series = lines.ToArray(),
            XAxes = [WeekAxis(result, lastX)],
            YAxes = [HoursAxis()],
            Sections = [ForecastSection(weeks - 0.5, lastX + 0.5)]
        };
    }

    private void OnCardChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TrendTagCard.IsShown) || sender is not TrendTagCard card) return;

        if (card.IsShown) _chosenTags?.Add(card.Tag);
        else _chosenTags?.Remove(card.Tag);
        ApplyVisibility(card);
    }

    private void ApplyVisibility(TrendTagCard card)
    {
        if (!_tagSeries.TryGetValue(card.Tag, out var series)) return;

        foreach (var line in series.Lines) line.IsVisible = card.IsShown;
    }

    [RelayCommand]
    private void ShowAllTags()
    {
        foreach (var card in Cards) card.IsShown = true;
    }

    [RelayCommand]
    private void HideAllTags()
    {
        foreach (var card in Cards) card.IsShown = false;
    }

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Axes & Paints
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Every paint is NEW per chart on purpose: a chart disposes its paints when it unloads, so one
    // shared between two charts would be disposed under the other (see ChartPaints).

    private static string WeekLabel(TrendResult result, double x) =>
        "Week of " + result.WeekStart(x).ToString("dd MMM");

    // Weeks along the bottom, labelled with their Monday. Around six labels whatever the span:
    // at one per week a year of history would print its dates on top of each other.
    private static Axis WeekAxis(TrendResult result, int lastX)
    {
        int step = Math.Max(1, (int)Math.Ceiling((lastX + 1) / 6.0));
        var separators = new List<double>();
        for (int x = 0; x <= lastX; x += step) separators.Add(x);

        return new Axis
        {
            LabelsPaint = LabelPaint(),
            TextSize = 11,
            Labeler = x => result.WeekStart(x).ToString("dd MMM"),
            CustomSeparators = separators,
            SeparatorsPaint = GridPaint(),
            MinLimit = -0.5,
            MaxLimit = lastX + 0.5
        };
    }

    private static Axis HoursAxis() => new()
    {
        LabelsPaint = LabelPaint(),
        TextSize = 11,
        Labeler = h => $"{h:0.#}h",
        SeparatorsPaint = GridPaint(),
        MinLimit = 0
    };

    // The forecast half of a chart, shaded so it never passes for something that happened
    private static RectangularSection ForecastSection(double from, double to) => new()
    {
        Xi = from,
        Xj = to,
        Fill = new SolidColorPaint(IsDark ? new SKColor(0xFF, 0xFF, 0xFF, 0x0C) : new SKColor(0x00, 0x00, 0x00, 0x08))
    };

    private static bool IsDark => Application.Current?.ActualThemeVariant == ThemeVariant.Dark;

    private static SolidColorPaint LabelPaint() =>
        new(IsDark ? new SKColor(0xC5, 0xC5, 0xC5) : new SKColor(0x5C, 0x5C, 0x5C));

    private static SolidColorPaint GridPaint() =>
        new(IsDark ? new SKColor(0xFF, 0xFF, 0xFF, 0x18) : new SKColor(0x00, 0x00, 0x00, 0x14), 1);

    private static SKColor ToSk(string hex) => SKColor.Parse(hex);
    private static SKColor ToSk(Color c) => new(c.R, c.G, c.B, c.A);
}
