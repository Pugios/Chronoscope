using Syncfusion.Maui.Data;
using System.ComponentModel;

namespace TimeViewer;

// Grid rows that show Total Time and Last Used as formatted strings,
// keeping the raw values so the columns can be sorted by value
public interface IUsageStats
{
    double TotalSeconds { get; }
    DateTime LastUsedDate { get; }
}

// %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
// Custom Comparer for Sorting
// %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
// Shared by SettingsPage's process grid and ExplorerSettingsPage's preview grid, which is why
// they live here rather than beside either page.

public class DateSortComparer : IComparer<object>, ISortDirection
{
    public ListSortDirection SortDirection { get; set; }

    public int Compare(object? x, object? y)
    {
        var dateX = ((IUsageStats)x!).LastUsedDate;
        var dateY = ((IUsageStats)y!).LastUsedDate;
        int result = dateX.CompareTo(dateY);
        return SortDirection == ListSortDirection.Ascending ? result : -result;
    }
}

public class TotalSecondsSortComparer : IComparer<object>, ISortDirection
{
    public ListSortDirection SortDirection { get; set; }

    public int Compare(object? x, object? y)
    {
        var secX = ((IUsageStats)x!).TotalSeconds;
        var secY = ((IUsageStats)y!).TotalSeconds;
        int result = secX.CompareTo(secY);
        return SortDirection == ListSortDirection.Ascending ? result : -result;
    }
}
