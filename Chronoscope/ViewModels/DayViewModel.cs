using System.Diagnostics;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;

namespace Chronoscope.ViewModels;

// The home page: one day as a nested pie (tags inside, their processes outside), its legend,
// and the 24h timeline bar beneath. Ported from the MAUI MainPage.
// Tags in groups add a level: groups innermost, then tags, then processes.
public partial class DayViewModel : ViewModelBase, IKeepAlive
{
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Parameters
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
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

        // The interval is set on every visit, from Settings: it may have changed while away
        _refreshTimer = new DispatcherTimer();
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
        TimeSpan interval = _settingsService.RefreshInterval;
        await RefreshAsync(maxAge: interval);
        _refreshTimer.Interval = interval;
        _refreshTimer.Start();
    }

    // The view is kept (IKeepAlive), so the pie stays loaded while another page is shown and its
    // series stay valid; nothing needs clearing here.
    public override void OnNavigatedFrom() => _refreshTimer.Stop();

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

                // Coming back to this page with nothing changed (same data, day and colours, and
                // the rolling window not yet a minute older) keeps what is on screen, rather than
                // rebuilding and re-animating the pie on every visit
                var drawn = (apps, day, _settingsService.TagColorsVersion, _dataService.GroupsVersion);
                if (!reload && drawn == _drawn && DateTime.Now - _drawnAt < TimeSpan.FromMinutes(1))
                    continue;

                LoadDayNestedPie(apps, day);
                LoadDayTimeline(apps, day);
                // drawing may colour new tags
                _drawn = (apps, day, _settingsService.TagColorsVersion, _dataService.GroupsVersion);
                _drawnAt = DateTime.Now;
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

    private (IReadOnlyList<AppsTagsTable>? Apps, DateTime Day, int Colors, int Groups) _drawn;
    private DateTime _drawnAt;

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

    // Colors the pie gave each process, reused by the timeline so both agree.
    // Keyed by process, which assumes one Tag per Process (true for AppsTagsTable) - last write wins.
    private readonly Dictionary<string, string> _processColors = new();

    // One tag of the day with its processes, and the top-level block it is drawn in: its group,
    // or the tag alone when it has none
    private sealed record DayTag(string Tag, double Seconds, List<(string Process, double Seconds)> Processes);
    private sealed record DayBlock(string? Group, double Seconds, List<DayTag> Tags);

    // The legend's tint for a group's rows - light enough for the text, strong enough to read as one block
    private static IBrush GroupTint(Color c) => new ImmutableSolidColorBrush(Color.FromArgb(0x33, c.R, c.G, c.B));

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

        // The day's tags, each with its processes, both ordered by time spent descending
        var tags = dayRows
            .Where(a => a.DurationSeconds > HeatmapAggregator.MinTrackedSeconds)
            .GroupBy(a => a.Tag)
            .Select(g => new DayTag(
                g.Key,
                g.Sum(a => a.DurationSeconds),
                g.GroupBy(a => a.Process)
                    .Select(p => (p.Key, p.Sum(a => a.DurationSeconds)))
                    .OrderByDescending(p => p.Item2)
                    .ToList()))
            .ToList();

        // ...gathered into blocks: one per tag group, and one per tag that has no group. Biggest
        // block first, and the biggest tag first within a group, so the pie and the legend lead
        // with where most of the day went.
        var groupByTag = _dataService.GroupByTag;
        var blocks = tags
            .GroupBy(t => groupByTag.TryGetValue(t.Tag, out var g) ? g : null)
            .SelectMany(g => g.Key is null
                ? g.Select(t => new DayBlock(null, t.Seconds, [t]))
                : [new DayBlock(g.Key, g.Sum(t => t.Seconds), g.OrderByDescending(t => t.Seconds).ToList())])
            .OrderByDescending(b => b.Seconds)
            .ToList();

        // The rings, inside out: tag groups, tags, processes. A day without a grouped tag has no
        // group ring at all and looks as it always did. On a day with one, a tag outside any
        // group fills its own spot on the group ring in its own colour, so the ring has no gaps.
        bool hasGroups = blocks.Any(b => b.Group is not null);
        int rings = hasGroups ? 3 : 2;
        int groupRing = 0, tagRing = hasGroups ? 1 : 0, processRing = hasGroups ? 2 : 1;

        double?[] On(int ring, double seconds)
        {
            var values = new double?[rings];
            values[ring] = seconds;
            return values;
        }

        var series = new List<ISeries>();
        var legend = new List<LegendItem>();
        double totalSeconds = 0;

        foreach (var block in blocks)
        {
            IBrush? tint = null;
            int firstRow = legend.Count;

            if (block.Group is { } group)
            {
                // GROUP
                string groupColor = _settingsService.GetGroupColor(group);
                series.Add(Slice(group, On(groupRing, block.Seconds), groupColor));

                tint = GroupTint(Color.Parse(groupColor));
                legend.Add(new LegendItem
                {
                    Name = group,
                    Duration = FormatDuration(block.Seconds),
                    Color = Color.Parse(groupColor),
                    Kind = LegendKind.Group,
                    InGroup = true,
                    Background = tint
                });
            }
            else if (hasGroups)
            {
                // A tag of its own: its spot on the group ring, in its own colour
                var lone = block.Tags[0];
                series.Add(Slice(lone.Tag, On(groupRing, lone.Seconds), _settingsService.GetTagColor(lone.Tag)));
            }

            foreach (var tag in block.Tags)
            {
                // TAG
                string tagColor = _settingsService.GetTagColor(tag.Tag);
                series.Add(Slice(tag.Tag, On(tagRing, tag.Seconds), tagColor));

                legend.Add(new LegendItem
                {
                    Name = tag.Tag,
                    Duration = FormatDuration(tag.Seconds),
                    Color = Color.Parse(tagColor),
                    Kind = LegendKind.Tag,
                    InGroup = tint is not null,
                    Background = tint
                });

                int processCount = tag.Processes.Count;
                for (int i = 0; i < processCount; i++)
                {
                    // PROCESS - longest process = brightest shade of its tag's colour
                    var (process, seconds) = tag.Processes[i];
                    float value = 20f + (((processCount - (float)i) / processCount) * 80f);
                    string procColor = _settingsService.VaryColor(tagColor, value);
                    _processColors[process] = procColor;

                    series.Add(Slice(process, On(processRing, seconds), procColor));

                    legend.Add(new LegendItem
                    {
                        Name = process,
                        Duration = FormatDuration(seconds),
                        Color = Color.Parse(procColor),
                        Kind = LegendKind.Process,
                        InGroup = tint is not null,
                        Background = tint
                    });

                    totalSeconds += seconds;
                }
            }

            // Round the group block's outer corners: top of its first row, bottom of its last
            if (tint is not null)
            {
                int lastRow = legend.Count - 1;
                for (int r = firstRow; r <= lastRow; r++)
                {
                    legend[r] = Rounded(legend[r], new CornerRadius(
                        r == firstRow ? 6 : 0, r == firstRow ? 6 : 0,
                        r == lastRow ? 6 : 0, r == lastRow ? 6 : 0));
                }
            }
        }

        // Add remaining time to reach 24h, on every ring
        var remaining = Math.Max(0, TimeSpan.FromDays(1).TotalSeconds - totalSeconds);
        var remainColor = _settingsService.GetTagColor("Remaining");
        for (int ring = 0; ring < rings; ring++)
            series.Add(Slice("Remaining", On(ring, remaining), remainColor));

        PieSeries = series.ToArray();
        LegendItems = legend.ToArray();
        TrackedTotal = FormatTotal(totalSeconds);
        HasData = true;

        Debug.WriteLine($"Total series added: {series.Count}");
    }

    private static string FormatDuration(double seconds) => TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm");

    private static LegendItem Rounded(LegendItem item, CornerRadius corners) => new()
    {
        Name = item.Name,
        Duration = item.Duration,
        Color = item.Color,
        Kind = item.Kind,
        InGroup = item.InGroup,
        Background = item.Background,
        Corners = corners
    };

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
        var rows = data
            .Where(a => a.End > windowStart && a.Start < windowEnd)
            .Where(a => a.DurationSeconds > HeatmapAggregator.MinTrackedSeconds)
            .Select(a => (
                a.Process,
                a.Tag,
                Start: a.Start < windowStart ? windowStart : a.Start,
                End: a.End > windowEnd ? windowEnd : a.End,
                Title: WindowTitleWithoutApp(a.Name, a.OriginalProcess),
                a.DocName,
                a.Domain))
            .Where(r => r.End > r.Start)
            .OrderBy(r => r.Start);

        // Merge adjacent same-process rows. ManicTime splits a sitting on every window title
        // change, so this collapses one session back into one segment. Applied AFTER the filter,
        // so the set of activities shown stays the same as the pie's. Each row is kept as one of
        // the block's documents; `with` copies the list reference, so appending to the list
        // grows the merged block's documents.
        var merged = new List<TimelineSlice>();
        List<TimelineDocument> documents = [];
        foreach (var row in rows)
        {
            var document = new TimelineDocument(row.Start, row.End, row.Title, row.DocName, row.Domain);
            if (merged.Count > 0)
            {
                var last = merged[^1];
                if (last.Process == row.Process && (row.Start - last.End).TotalSeconds <= GapAbsorbSeconds)
                {
                    merged[^1] = last with { End = row.End > last.End ? row.End : last.End };
                    documents.Add(document);
                    continue;
                }
            }
            documents = [document];
            merged.Add(new TimelineSlice(row.Process, row.Tag, row.Start, row.End,
                Color.Parse(ColorForProcess(row.Process, row.Tag)), documents));
        }

        TimelineWindowStart = windowStart;
        TimelineGapColor = Color.Parse(_settingsService.GetTagColor("Remaining"));
        TimelineSlices = merged;

        Debug.WriteLine($"Timeline segments: {merged.Count}");
    }

    private static readonly string[] TitleSeparators = [" — ", " – ", " - "];

    // Most apps end their window title with their own name ("Monkeytype — Mozilla Firefox",
    // "Inbox - Mozilla Thunderbird"), which the hover card already shows. Only that last part is
    // dropped, and only when it names the app, so a title that merely contains a dash survives.
    // A title that is nothing but the app's name (Firefox on a new tab) has nothing to add, so
    // it becomes empty.
    private static string WindowTitleWithoutApp(string title, string app)
    {
        if (string.IsNullOrEmpty(app)) return title;

        int cut = -1, separatorLength = 0;
        foreach (var separator in TitleSeparators)
        {
            int at = title.LastIndexOf(separator, StringComparison.Ordinal);
            if (at > cut) (cut, separatorLength) = (at, separator.Length);
        }

        if (cut < 0)
            return title.Contains(app, StringComparison.OrdinalIgnoreCase) ? "" : title;

        return title.AsSpan(cut + separatorLength).Contains(app, StringComparison.OrdinalIgnoreCase)
            ? title[..cut].TrimEnd()
            : title;
    }

    // A process the pie never colored (only possible in the previous-day part of the rolling
    // window) falls back to its tag's base color, the same color the pie gives that tag's ring.
    private string ColorForProcess(string process, string tag) =>
        _processColors.TryGetValue(process, out var hex)
            ? hex
            : _settingsService.GetTagColor(tag);
}
