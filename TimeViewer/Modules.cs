using CsvHelper.Configuration.Attributes;
using LiveChartsCore;
using LiveChartsCore.Kernel;
using LiveChartsCore.Kernel.Sketches;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView.Painting;
using Syncfusion.Maui.Data;
using System.ComponentModel;

namespace TimeViewer;

// Settings
public class AppSettings
{
    public Dictionary<string, string> TagColors { get; set; } = new();
    public string MtcExePath { get; set; } = @"C:\Program Files\ManicTime\mtc.exe";

    // Where the heatmap JSON is written for Obsidian to pick up. Must be a folder INSIDE the
    // vault - dataviewjs' dv.io.load() resolves vault-relative paths only.
    public string ObsidianExportPath { get; set; } = "";
    public bool ObsidianExportEnabled { get; set; } = false;
}

// Data Services
public class TagsTable
{
    public string Process { get; set; }
    public string Tag { get; set; }
}

public class AppsTable
{
    public string Name { get; set; }
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public string Duration { get; set; }
    public string Process { get; set; }

    public override string ToString() => $"{Name} | {Start} | {End} | {Duration} | {Process}";
}

public class AppsTagsTable
{
    public string Name { get; set; }
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public string Duration { get; set; }
    public string Process { get; set; }
    public string OriginalProcess { get; set; }
    public string Tag { get; set; }

    public override string ToString() => $"{Name} | {Start} | {End} | {Duration} | {Process} | {OriginalProcess} | {Tag}";
}

public class DocumentsTable
{
    public string Name { get; set; }
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public string Duration { get; set; }
    public string Domain { get; set; }
}

public class AppsTagsDocumentsTable
{
    public string Name { get; set; }
    public string DocName { get; set; }
    public string Domain { get; set; }
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public string Duration { get; set; }
    public string Process { get; set; }
    public string OriginalProcess { get; set; }
    public string Tag { get; set; }

}

// Explorer Processes Rules
public class ExplorerRule
{
    public string Process { get; set; }
    public string Tag { get; set; }
    public string Column { get; set; }  // "Name", "DocName", or "Domain"
    public string MatchType { get; set; }  // "Prefix" or "Suffix"
    public string Pattern { get; set; } // "github.com", "C:/Users/Documents/ProjectName", etc.
    public int Order { get; set; } // Order of rule application
}

// Grid rows that show Total Time and Last Used as formatted strings,
// keeping the raw values so the columns can be sorted by value
public interface IUsageStats
{
    double TotalSeconds { get; }
    DateTime LastUsedDate { get; }
}


// Graph
public class PieData
{
    public string Name { get; set; }
    public double?[] Values { get; set; }
    public Func<ChartPoint, string> Formatter { get; } = point => TimeSpan.FromSeconds(point.Coordinate.PrimaryValue).ToString(@"hh\:mm");
    public SolidColorPaint Fill { get; set; }
}

// One segment of the day timeline bar. Each instance becomes one stacked row series
// holding a single value, so together they stack into a single horizontal bar.
// Carries no tooltip text: hovering is handled by MainPage against TimelineSlice instead,
// because LiveCharts cannot hit test a stacked segment's drawn shape (see OnTimelinePointerMoved).
public class TimelineData
{
    public string Name { get; init; } = "";          // process, or "" for a gap spacer
    public double?[] Values { get; init; } = [];     // exactly ONE element: seconds
    public SolidColorPaint Fill { get; init; }
}

// A single activity block on the timeline, after clamping to the window and merging
public sealed record TimelineSlice(string Process, string Tag, DateTime Start, DateTime End);

public class LegendItem
{
    public string Name { get; set; }
    public string Duration { get; set; }
    public Color Color { get; set; }
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

// %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
// Custom Comparer for Sorting
// %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

public class DateSortComparer : IComparer<object>, ISortDirection
{
    public ListSortDirection SortDirection { get; set; }

    public int Compare(object x, object y)
    {
        var dateX = ((IUsageStats)x).LastUsedDate;
        var dateY = ((IUsageStats)y).LastUsedDate;
        int result = dateX.CompareTo(dateY);
        return SortDirection == ListSortDirection.Ascending ? result : -result;
    }
}

public class TotalSecondsSortComparer : IComparer<object>, ISortDirection
{
    public ListSortDirection SortDirection { get; set; }

    public int Compare(object x, object y)
    {
        var secX = ((IUsageStats)x).TotalSeconds;
        var secY = ((IUsageStats)y).TotalSeconds;
        int result = secX.CompareTo(secY);
        return SortDirection == ListSortDirection.Ascending ? result : -result;
    }
}
