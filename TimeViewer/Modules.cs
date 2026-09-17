using CsvHelper.Configuration.Attributes;
using LiveChartsCore.Kernel;
using LiveChartsCore.SkiaSharpView.Painting;
using Syncfusion.Maui.Data;
using System.ComponentModel;

namespace TimeViewer;

// Settings
public class AppSettings
{
    public Dictionary<string, string> TagColors { get; set; } = new();
    public string MtcExePath { get; set; } = @"C:\Program Files\ManicTime\mtc.exe";
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
