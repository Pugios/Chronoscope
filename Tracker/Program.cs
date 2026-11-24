using System.Globalization;
using Chronoscope.Tracker;
using Chronoscope.Tracker.Reporting;
using Chronoscope.Tracker.Sampling;
using Chronoscope.Tracker.Storage;

const string Usage = """
    chronoscope-tracker  -  records which window you are working in, into a local SQLite file

    Commands
      run                       Track until Ctrl+C (the default)
      export  [--out FILE]      Write active spans as a ManicTime-style CSV
      compare [--manictime CSV] Compare one day against a ManicTime Applications export
      help

    Options
      --db FILE          Database file          (default: <local app data>/Chronoscope/Tracker/activity.db)
      --interval SEC     Seconds between polls  (default: 1)
      --idle-after SEC   Idle threshold         (default: 300)
      --day yyyy-MM-dd   Day for export/compare (default: today; export without --day writes everything)
      --manictime FILE   ManicTime export       (default: Chronoscope's cached manictime-export.csv)
      --out FILE         Output CSV             (default: tracker-export.csv)
    """;

if (args.Any(a => a is "help" or "--help" or "-h"))
{
    Console.WriteLine(Usage);
    return 0;
}

string command = args.Length > 0 && !args[0].StartsWith("--") ? args[0].ToLowerInvariant() : "run";
Dictionary<string, string> options = ParseOptions(args);

string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
string dbPath = options.GetValueOrDefault("db") ?? Path.Combine(localData, "Chronoscope", "Tracker", "activity.db");

try
{
    switch (command)
    {
        case "run":
            return await RunAsync();
        case "export":
            return Export();
        case "compare":
            return Compare();
        default:
            Console.Error.WriteLine($"Unknown command '{command}'.{Environment.NewLine}{Environment.NewLine}{Usage}");
            return 2;
    }
}
catch (Exception ex) when (ex is PlatformNotSupportedException or IOException or ArgumentException or FormatException)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

async Task<int> RunAsync()
{
    TimeSpan interval = TimeSpan.FromSeconds(ParseDouble("interval", 1));
    TimeSpan idleAfter = TimeSpan.FromSeconds(ParseDouble("idle-after", 300));

    IActivitySource source = ActivitySourceFactory.Create();

    // Two trackers writing one file would interleave overlapping spans. The OS drops this lock
    // when the process dies, however it dies, so it can never be left stale.
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);
    FileStream instanceLock;
    try
    {
        instanceLock = new FileStream(dbPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    catch (IOException)
    {
        Console.Error.WriteLine($"Another tracker is already writing to {dbPath}.");
        return 1;
    }

    using (instanceLock)
    using (var store = new ActivityStore(dbPath))
    {
        // Anything slower than this between two polls means the machine was asleep or frozen
        var recorder = new SpanRecorder(store, idleAfter, maxGap: TimeSpan.FromSeconds(Math.Max(10, interval.TotalSeconds * 5)));

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;   // let the loop finish and write the last heartbeat
            cts.Cancel();
        };

        Console.WriteLine($"Tracking to {dbPath}");
        Console.WriteLine($"Poll every {interval.TotalSeconds:0.##}s, idle after {idleAfter.TotalSeconds:0}s. Ctrl+C to stop.");

        using var timer = new PeriodicTimer(interval);
        try
        {
            do
            {
                try
                {
                    recorder.Observe(source.Poll(), DateTimeOffset.UtcNow);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One bad poll (a window that vanished mid-read) must not end a day of tracking
                    Console.Error.WriteLine($"{DateTime.Now:HH:mm:ss} poll failed: {ex.Message}");
                }
            }
            while (await timer.WaitForNextTickAsync(cts.Token));
        }
        catch (OperationCanceledException)
        {
        }

        recorder.Stop(DateTimeOffset.UtcNow);
        Console.WriteLine("Stopped.");
    }
    return 0;
}

int Export()
{
    using var store = new ActivityStore(dbPath);

    (DateTimeOffset from, DateTimeOffset to) = options.ContainsKey("day")
        ? DayRange(ParseDay())
        : (DateTimeOffset.UnixEpoch, DateTimeOffset.MaxValue.AddYears(-1));

    string path = options.GetValueOrDefault("out") ?? "tracker-export.csv";
    int rows = CsvExport.Write(store, path, from, to);
    Console.WriteLine($"Wrote {rows} rows to {Path.GetFullPath(path)}");
    return 0;
}

int Compare()
{
    string csv = options.GetValueOrDefault("manictime")
        ?? Path.Combine(localData, "Chronoscope", "Cache", "manictime-export.csv");
    if (!File.Exists(csv))
    {
        Console.Error.WriteLine($"ManicTime export not found: {csv}{Environment.NewLine}Pass --manictime FILE.");
        return 1;
    }

    using var store = new ActivityStore(dbPath);
    ManicTimeComparison.Run(store, csv, ParseDay(), Console.Out);
    return 0;
}

DateOnly ParseDay() => options.TryGetValue("day", out string? text)
    ? DateOnly.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture)
    : DateOnly.FromDateTime(DateTime.Now);

(DateTimeOffset, DateTimeOffset) DayRange(DateOnly day) => (
    new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local)),
    new DateTimeOffset(day.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Local)));

double ParseDouble(string name, double fallback)
{
    if (!options.TryGetValue(name, out string? text))
    {
        return fallback;
    }

    if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || value <= 0)
    {
        throw new ArgumentException($"--{name} needs a positive number, got '{text}'.");
    }
    return value;
}

static Dictionary<string, string> ParseOptions(string[] args)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (int i = 0; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--"))
        {
            continue;
        }

        if (i + 1 >= args.Length)
        {
            throw new ArgumentException($"{args[i]} needs a value.");
        }
        result[args[i][2..]] = args[++i];
    }
    return result;
}
