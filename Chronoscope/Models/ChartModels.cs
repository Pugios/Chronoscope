using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using LiveChartsCore;
using LiveChartsCore.Kernel.Sketches;
using LiveChartsCore.Measure;

namespace Chronoscope;

// Everything the views hand to the charts, plus the intermediate shapes the aggregation
// produces on the way there.

// A single activity block on the timeline, after clamping to the window and merging.
// Color is the one the pie gave the process, so the bar and the pie agree.
public sealed record TimelineSlice(string Process, string Tag, DateTime Start, DateTime End, Color Color);

// One row of the day legend: a tag (IsTag) or one of its processes, indented beneath it
public class LegendItem
{
    public string Name { get; init; } = "";
    public string Duration { get; init; } = "";
    public Color Color { get; init; }
    public bool IsTag { get; init; }
    public Thickness Indent => IsTag ? new Thickness(0) : new Thickness(18, 0, 0, 0);
}

// One tag's daily totals in seconds, straight out of HeatmapAggregator and before any display
// decision is made. Shared by the Statistics chart and the Obsidian vault export.
public class TagDailyTotals
{
    public string Tag { get; init; } = "";
    public Dictionary<DateTime, double> Days { get; init; } = new(); // midnight-keyed, seconds
    public double TotalSeconds { get; init; }
}

// One tag's seconds broken down by weekday and hour of day, straight out of HeatmapAggregator.
// [weekday, hour] with Monday = 0 - the same row order the year grid's inverted Y axis assumes.
// Unlike TagDailyTotals these cells SPLIT an activity across the hours it covers; see
// HeatmapAggregator.AggregateTagWeekHours for why, and for what that does to midnight.
public class TagWeekHourTotals
{
    public string Tag { get; init; } = "";
    public double[,] Cells { get; init; } =
        new double[HeatmapAggregator.WeekdayCount, HeatmapAggregator.HourCount];
    public double TotalSeconds { get; init; }
}

// One drawn grid: its caption, the ready-made series and axes, its size, and its own legend strip.
// The series and axes are built in StatisticsViewModel rather than in the DataTemplate, because
// their labelers and tooltips close over the period they describe.
public class HeatmapPanel
{
    public string Caption { get; init; } = "";    // "Year Overview" / "Active Hours"
    public ISeries[] Series { get; init; } = [];  // exactly ONE heat series
    public ICartesianAxis[] XAxes { get; init; } = [];
    public ICartesianAxis[] YAxes { get; init; } = [];
    // Pinned so the plot area is exactly the grid and the cells cannot go rectangular on a resize
    public Margin DrawMargin { get; init; } = new(0);
    public double ChartWidth { get; init; }
    public double ChartHeight { get; init; }
    public HeatmapScaleStep[] ScaleSteps { get; init; } = []; // the legend strip, the ramp itself
}

// One tag's row on the Statistics page: the title, then its grids drawn left to right.
// Each panel carries its own scale strip, because a Year Overview cell is one day while an
// Active Hours cell is ~52 of that hour summed - the two sets of quartiles are not comparable.
//
// A card is built once per year shown and then only rearranged: its place, whether it is hidden
// and which arrows apply all change in place. Rebuilding it would mean new charts, and a new chart
// draws empty for a moment - every graph on the page would blink on each move.
public partial class TagStatistics : ObservableObject
{
    public string Tag { get; init; } = "";
    public Color TagColor { get; init; } = Colors.Transparent; // the dot beside the title
    public string TotalLabel { get; init; } = "";              // "412h over 231 days"

    // Exactly two, in draw order - once built. They start empty and are filled a card at a time
    // after the page is up (and only when the card is shown), because creating ~50 charts in one
    // go froze the window for a second or more on a full year.
    [ObservableProperty] public partial HeatmapPanel[] Panels { get; set; } = [];

    [ObservableProperty] public partial int DisplayIndex { get; set; }  // place on the page
    [ObservableProperty] public partial bool IsHidden { get; set; }
    [ObservableProperty] public partial bool CanMoveUp { get; set; }    // not already first / last
    [ObservableProperty] public partial bool CanMoveDown { get; set; }
}

// A tag hidden from the Statistics page, listed at the bottom so it can be brought back
public class HiddenTag
{
    public string Tag { get; init; } = "";
    public Color TagColor { get; init; } = Colors.Transparent;
    public string TotalLabel { get; init; } = "";
}

// One step of a heatmap's legend: the shade, and the span of tracked time it stands for.
// The label is built from that tag's own quartiles, so it reads differently per tag and per year -
// which is the point: a shade means "a heavy day for THIS tag", not a fixed number of hours.
public class HeatmapScaleStep
{
    public Color Color { get; init; } = Colors.Transparent;
    public string Label { get; init; } = ""; // "none", "≤45m", ">3h"
}
