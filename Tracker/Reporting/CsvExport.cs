using System.Globalization;
using System.Text;
using Chronoscope.Tracker.Storage;
using CsvHelper;
using CsvHelper.Configuration;

namespace Chronoscope.Tracker.Reporting;

// Writes the tracker's active spans in exactly the shape `mtc.exe export ManicTime/Applications`
// produces: Name, Start, End, Duration, Process. Chronoscope's existing pipeline can read the
// result unchanged, which makes this the migration seam: swapping ManicTime for the tracker
// later means replacing the mtc.exe call, not rewriting the pipeline behind it.
public static class CsvExport
{
    public static int Write(ActivityStore store, string path, DateTimeOffset from, DateTimeOffset to)
    {
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            ShouldQuote = _ => true,   // ManicTime quotes every field, so match it
        };

        int rows = 0;
        using var writer = new StreamWriter(path, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        using var csv = new CsvWriter(writer, config);

        foreach (string header in new[] { "Name", "Start", "End", "Duration", "Process" })
        {
            csv.WriteField(header);
        }
        csv.NextRecord();

        foreach (ActivitySpan span in store.Read(from, to))
        {
            if (span.Kind != SpanKind.Active)
            {
                continue;
            }

            // ManicTime works in whole seconds and local time
            DateTime start = Round(span.Start);
            DateTime end = Round(span.End);
            if (end <= start)
            {
                continue;
            }

            csv.WriteField(span.Title ?? "");
            csv.WriteField(start.ToString("yyyy-MM-ddTHH:mm:ss.0000000", CultureInfo.InvariantCulture));
            csv.WriteField(end.ToString("yyyy-MM-ddTHH:mm:ss.0000000", CultureInfo.InvariantCulture));
            csv.WriteField((end - start).ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture));
            csv.WriteField(span.AppName ?? span.Process ?? "");
            csv.NextRecord();
            rows++;
        }

        return rows;
    }

    private static DateTime Round(DateTimeOffset value)
    {
        DateTime local = value.ToLocalTime().DateTime;
        return new DateTime(local.Ticks - local.Ticks % TimeSpan.TicksPerSecond + (local.Millisecond >= 500 ? TimeSpan.TicksPerSecond : 0),
            DateTimeKind.Unspecified);
    }
}
