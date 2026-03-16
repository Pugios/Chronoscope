using CsvHelper.Configuration.Attributes;
using LiveChartsCore.Kernel;
using LiveChartsCore.SkiaSharpView.Painting;

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


// Graph
public class PieData
{
    public string Name { get; set; }
    public double?[] Values { get; set; }
    public Func<ChartPoint, string> Formatter { get; } = point => TimeSpan.FromSeconds(point.Coordinate.PrimaryValue).ToString(@"hh\:mm");
    public SolidColorPaint Fill { get; set; }
}

public class LegendItem
{
    public string Name { get; set; }
    public string Duration { get; set; }
    public Color Color { get; set; }
    public Thickness Indent { get; set; }
}
