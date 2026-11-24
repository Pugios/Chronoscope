using System.Globalization;
using Chronoscope.Tracker.Storage;
using CsvHelper;

namespace Chronoscope.Tracker.Reporting;

// Answers "does the tracker see what ManicTime sees?" for one day, against a ManicTime
// Applications export (the file Chronoscope already caches).
//
// Both timelines are laid onto a per-second grid, one app per second, and compared second by
// second. Only the stretch the tracker actually covered is compared: it cannot be blamed for
// the hours before it was started.
public static class ManicTimeComparison
{
    private sealed class ManicRow
    {
        public string Name { get; set; } = "";
        public DateTime Start { get; set; }
        public DateTime End { get; set; }
        public string Process { get; set; } = "";
    }

    public static void Run(ActivityStore store, string manicTimeCsv, DateOnly day, TextWriter output)
    {
        DateTimeOffset dayStart = new(day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local));
        DateTimeOffset dayEnd = new(day.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Local));

        List<ActivitySpan> tracked = store.Read(dayStart, dayEnd);
        if (tracked.Count == 0)
        {
            output.WriteLine($"The tracker has no data for {day:yyyy-MM-dd}.");
            return;
        }

        DateTimeOffset coverStart = Max(tracked.Min(s => s.Start), dayStart);
        DateTimeOffset coverEnd = Min(tracked.Max(s => s.End), dayEnd);
        int length = (int)(coverEnd - coverStart).TotalSeconds;
        if (length <= 0)
        {
            output.WriteLine("The tracker's data for that day is shorter than a second.");
            return;
        }

        var apps = new AppIds();
        int[] mine = new int[length];      // 0 = no app (idle, or not recorded)
        int[] theirs = new int[length];

        double idleSeconds = 0;
        foreach (ActivitySpan span in tracked)
        {
            if (span.Kind == SpanKind.Idle)
            {
                idleSeconds += (Min(span.End, coverEnd) - Max(span.Start, coverStart)).TotalSeconds;
                continue;
            }
            Paint(mine, coverStart, span.Start, span.End, apps.Id(span.AppName ?? span.Process ?? "unknown"));
        }

        foreach (ManicRow row in ReadManicTime(manicTimeCsv, coverStart, coverEnd))
        {
            Paint(theirs, coverStart, new DateTimeOffset(row.Start), new DateTimeOffset(row.End), apps.Id(row.Process));
        }

        Report(output, day, coverStart, coverEnd, idleSeconds, mine, theirs, apps);
    }

    private static IEnumerable<ManicRow> ReadManicTime(string path, DateTimeOffset from, DateTimeOffset to)
    {
        using var reader = new StreamReader(path);
        using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
        foreach (ManicRow row in csv.GetRecords<ManicRow>())
        {
            if (new DateTimeOffset(row.End) > from && new DateTimeOffset(row.Start) < to)
            {
                yield return row;
            }
        }
    }

    private static void Paint(int[] grid, DateTimeOffset origin, DateTimeOffset start, DateTimeOffset end, int app)
    {
        int from = Math.Clamp((int)Math.Round((start - origin).TotalSeconds), 0, grid.Length);
        int to = Math.Clamp((int)Math.Round((end - origin).TotalSeconds), 0, grid.Length);
        for (int i = from; i < to; i++)
        {
            grid[i] = app;
        }
    }

    private static void Report(TextWriter output, DateOnly day, DateTimeOffset coverStart, DateTimeOffset coverEnd,
        double idleSeconds, int[] mine, int[] theirs, AppIds apps)
    {
        int agree = 0, differ = 0, onlyMine = 0, onlyTheirs = 0, neither = 0;
        var mineTotals = new double[apps.Count];
        var theirTotals = new double[apps.Count];

        for (int i = 0; i < mine.Length; i++)
        {
            int a = mine[i], b = theirs[i];
            mineTotals[a]++;
            theirTotals[b]++;

            if (a == 0 && b == 0) neither++;
            else if (b == 0) onlyMine++;
            else if (a == 0) onlyTheirs++;
            else if (a == b) agree++;
            else differ++;
        }

        int total = mine.Length;
        string Pct(int n) => $"{100.0 * n / total,5:0.0}%";

        output.WriteLine($"Compared {day:yyyy-MM-dd}, {coverStart.ToLocalTime():HH:mm:ss} to {coverEnd.ToLocalTime():HH:mm:ss} ({Fmt(total)} covered by the tracker)");
        output.WriteLine();
        output.WriteLine($"  same app in both          {Fmt(agree),9}  {Pct(agree)}");
        output.WriteLine($"  different app             {Fmt(differ),9}  {Pct(differ)}");
        output.WriteLine($"  only the tracker saw      {Fmt(onlyMine),9}  {Pct(onlyMine)}");
        output.WriteLine($"  only ManicTime saw        {Fmt(onlyTheirs),9}  {Pct(onlyTheirs)}");
        output.WriteLine($"  neither (idle / away)     {Fmt(neither),9}  {Pct(neither)}");
        output.WriteLine($"  (tracker idle spans       {Fmt((int)idleSeconds),9})");
        output.WriteLine();
        output.WriteLine($"  {"App",-34}{"Tracker",10}{"ManicTime",11}{"Diff",10}");

        IEnumerable<int> byTime = Enumerable.Range(1, apps.Count - 1)
            .OrderByDescending(id => Math.Max(mineTotals[id], theirTotals[id]))
            .Take(15);
        foreach (int id in byTime)
        {
            string name = apps.Name(id);
            if (name.Length > 33) name = name[..32] + "…";
            output.WriteLine($"  {name,-34}{Fmt((int)mineTotals[id]),10}{Fmt((int)theirTotals[id]),11}{Signed((int)(mineTotals[id] - theirTotals[id])),10}");
        }
    }

    private static string Fmt(int seconds) => TimeSpan.FromSeconds(seconds).ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture);

    private static string Signed(int seconds) =>
        (seconds < 0 ? "-" : "+") + TimeSpan.FromSeconds(Math.Abs(seconds)).ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture);

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    // App name <-> small int, with 0 reserved for "no app". Names are compared case-insensitively
    // because the two tools take them from different places.
    private sealed class AppIds
    {
        private readonly List<string> _names = [""];
        private readonly Dictionary<string, int> _ids = new(StringComparer.OrdinalIgnoreCase);

        public int Count => _names.Count;
        public string Name(int id) => _names[id];

        public int Id(string name)
        {
            if (!_ids.TryGetValue(name, out int id))
            {
                id = _names.Count;
                _names.Add(name);
                _ids[name] = id;
            }
            return id;
        }
    }
}
