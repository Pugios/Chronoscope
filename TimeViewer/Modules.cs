using CsvHelper.Configuration.Attributes;
using LiveChartsCore.Kernel;
using LiveChartsCore.SkiaSharpView.Painting;
using System;
using System.Collections.Generic;
using System.Text;

namespace TimeViewer;

public class TagTable
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

    [Ignore]
    public string Tag { get; set; }

    public static string ToString(AppsTable x)
    {
        string Message = $"{x.Name} | {x.Start} | {x.End} | {x.Duration} | {x.Process}";
        if (x.Tag is not null)
        {
            Message += $" | {x.Tag}";
        }
        return Message;
    }
}

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
}
