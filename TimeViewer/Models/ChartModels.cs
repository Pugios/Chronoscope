using LiveChartsCore;
using LiveChartsCore.Kernel;
using LiveChartsCore.Kernel.Sketches;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView.Painting;

namespace TimeViewer;

// Everything the pages hand to LiveCharts, plus the intermediate shapes the aggregation
// produces on the way there.

// One ring segment of MainPage's nested pie. Tags sit at ring 0 ([seconds, null]),
// processes at ring 1 ([null, seconds]).
public class PieData
{
    public string Name { get; set; } = "";
    public double?[] Values { get; set; } = [];
    public Func<ChartPoint, string> Formatter { get; } = point => TimeSpan.FromSeconds(point.Coordinate.PrimaryValue).ToString(@"hh\:mm");
    public required SolidColorPaint Fill { get; set; }
}

// One segment of the day timeline bar. Each instance becomes one stacked row series
// holding a single value, so together they stack into a single horizontal bar.
// Carries no tooltip text: hovering is handled by MainPage against TimelineSlice instead,
// because LiveCharts cannot hit test a stacked segment's drawn shape (see OnTimelinePointerMoved).
public class TimelineData
{
    public string Name { get; init; } = "";          // process, or "" for a gap spacer
    public double?[] Values { get; init; } = [];     // exactly ONE element: seconds
    public required SolidColorPaint Fill { get; init; }
}

// A single activity block on the timeline, after clamping to the window and merging
public sealed record TimelineSlice(string Process, string Tag, DateTime Start, DateTime End);

// One row of MainPage's legend: a tag (Indent 0) or one of its processes (Indent 20)
public class LegendItem
{
    public string Name { get; set; } = "";
    public string Duration { get; set; } = "";
    public required Color Color { get; set; }
    public Thickness Indent { get; set; }
}

// One tag's daily totals in seconds, straight out of HeatmapAggregator and before any display
// decision is made. Shared by the Statistics chart and the Obsidian vault export.
public class TagDailyTotals
{
    public string Tag { get; init; } = "";
    public Dictionary<DateTime, double> Days { get; init; } = new(); // midnight-keyed, seconds
    public double TotalSeconds { get; init; }
}

// One tag's year of daily totals, shaped as a GitHub-contributions grid:
// X = week column, Y = weekday (0 = Mon .. 6 = Sun, drawn top-down by an inverted Y axis).
// The series and axes are built ready-made in StatisticsPage rather than in the DataTemplate,
// because their labelers and the tooltip close over the year's calendar and this tag's own daily
// totals - and because binding them whole keeps x:TypeArguments generics out of the XAML.
public class TagYearHeatmap
{
    public string Tag { get; init; } = "";
    public Color TagColor { get; init; } = Colors.Transparent; // the dot beside the title
    public string TotalLabel { get; init; } = ""; // "412h over 231 days"
    public ISeries[] Series { get; init; } = [];  // exactly ONE heat series
    public ICartesianAxis[] XAxes { get; init; } = [];
    public ICartesianAxis[] YAxes { get; init; } = [];
    // Pinned so the plot area is exactly the grid and the cells cannot go rectangular on a resize
    public Margin DrawMargin { get; init; } = new(0);
    public double ChartWidth { get; init; }
    public double ChartHeight { get; init; }
    public HeatmapScaleStep[] ScaleSteps { get; init; } = []; // the legend strip, the ramp itself
}

// One step of a heatmap's legend: the shade, and the span of tracked time it stands for.
// The label is built from that tag's own quartiles, so it reads differently per tag and per year -
// which is the point: a shade means "a heavy day for THIS tag", not a fixed number of hours.
public class HeatmapScaleStep
{
    public Color Color { get; init; } = Colors.Transparent;
    public string Label { get; init; } = ""; // "none", "≤45m", ">3h"
}
