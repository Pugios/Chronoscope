namespace Chronoscope;

// The numbers behind the Trend page: where each tag's weekly hours are heading if the recent
// trend simply carries on.
//
// The model is deliberately simple - a moving average plus a slope - so every number on the page
// can be explained in one sentence:
//
//   level  = the average of the last LevelWeeks complete weeks       ("where you are now")
//   slope  = (average of the newer half - average of the older half)
//            / the distance between the two halves' centres          ("how fast that is moving")
//   F(x)   = level + slope * (x - centre of the level window), never below zero
//
// x counts weeks: week 0 is the oldest complete week of the history, week Weeks-1 the newest,
// week Weeks the one running today. A point is that week's middle (Thursday), so a day in the
// future sits at a fractional x and the forecast changes smoothly from day to day.
//
// Weeks rather than days, because a day swings with the weekday (a free Sunday is not a trend);
// a whole Monday-Sunday week contains each weekday exactly once. The current week is NOT part of
// the history: it is incomplete, and counting it would read every Monday as a collapse.
//
// Seconds come in, hours go out: every figure here is shown to a person, and nobody reads a
// weekly trend in seconds.
public static class TrendForecaster
{
    // Below this many complete weeks there is no "older half" and "newer half" worth comparing
    public const int MinWeeks = 4;

    // How many of the newest weeks make up "now". Four weeks smooths out a single odd week
    // without lagging a month behind; with a short history it shrinks to half of it.
    public const int LevelWeeks = 4;

    public static TrendResult Forecast(IEnumerable<AppsTagsTable> data, DateTime today, int historyWeeks)
    {
        today = today.Date;
        var currentMonday = MondayOf(today);
        var rows = data as IReadOnlyCollection<AppsTagsTable> ?? data.ToList();

        // Weeks before tracking began are not "zero hours", they are "unknown": counting them
        // would make every tag look like it is shooting up. So the history starts at the first
        // complete week that was tracked from its Monday on, whatever was asked for.
        var tracked = rows
            .Where(a => a.DurationSeconds > HeatmapAggregator.MinTrackedSeconds && !string.IsNullOrWhiteSpace(a.Tag))
            .Select(a => a.Start.Date)
            .DefaultIfEmpty(today)
            .Min();
        var firstFullMonday = tracked == MondayOf(tracked) ? tracked : MondayOf(tracked).AddDays(7);
        int availableWeeks = Math.Max(0, (currentMonday - firstFullMonday).Days / 7);
        int weeks = Math.Min(historyWeeks, availableWeeks);

        var firstWeek = currentMonday.AddDays(-7 * weeks);
        var yearStart = new DateTime(today.Year, 1, 1);
        var last30From = today.AddDays(-29);

        var result = new TrendResult
        {
            Today = today,
            FirstWeek = firstWeek,
            Weeks = weeks,
            RequestedWeeks = historyWeeks,
            AvailableWeeks = availableWeeks
        };
        if (weeks < MinWeeks) return result;

        // One pass over the data covers all three spans the page needs: the weekly history,
        // the last 30 days and the year so far. Same floor and midnight rule as Statistics.
        var from = new[] { firstWeek, yearStart, last30From }.Min();
        var perTag = HeatmapAggregator.AggregateTagDays(rows, from, today);

        foreach (var tag in perTag)
        {
            var weekly = new double[weeks];
            double last30 = 0, yearToDate = 0;

            foreach (var (day, seconds) in tag.Days)
            {
                double hours = seconds / 3600;
                if (day >= firstWeek && day < currentMonday) weekly[(day - firstWeek).Days / 7] += hours;
                if (day >= last30From) last30 += hours;
                if (day >= yearStart) yearToDate += hours;
            }

            // A tag with nothing in the history window has no trend to speak of
            if (weekly.All(h => h == 0)) continue;

            var forecast = Fit(tag.Tag, weekly);
            forecast.Last30Hours = last30;
            forecast.YearToDateHours = yearToDate;
            forecast.Next30Hours = SumDays(forecast, result, today.AddDays(1), today.AddDays(30));
            forecast.YearEndHours = yearToDate + SumDays(forecast, result, today.AddDays(1), new DateTime(today.Year, 12, 31));
            forecast.Days = tag.Days;
            result.Tags.Add(forecast);
        }

        // Biggest first by where they are heading, not where they were
        result.Tags.Sort((a, b) => b.Level.CompareTo(a.Level));
        return result;
    }

    // The moving average and the slope
    public static TagForecast Fit(string tag, double[] weekly)
    {
        int n = weekly.Length;
        int k = Math.Min(LevelWeeks, n / 2);
        int half = n / 2;

        // An odd history leaves its middle week out of both halves, so the halves stay equal
        double older = weekly.Take(half).Average();
        double newer = weekly.Skip(n - half).Average();
        int distance = n - half;   // centre of the older half to centre of the newer half, in weeks

        return new TagForecast
        {
            Tag = tag,
            WeeklyHours = weekly,
            Level = weekly.Skip(n - k).Average(),
            LevelCentre = n - 1 - ((k - 1) / 2.0),
            Slope = (newer - older) / distance
        };
    }

    // Forecast hours over a span of days, each day a seventh of its week's forecast
    private static double SumDays(TagForecast forecast, TrendResult result, DateTime from, DateTime to)
    {
        double total = 0;
        for (var day = from; day <= to; day = day.AddDays(1))
            total += forecast.WeeklyAt(result.XOf(day)) / 7;
        return total;
    }

    public static DateTime MondayOf(DateTime day) => day.Date.AddDays(-(((int)day.DayOfWeek + 6) % 7));
}

// Everything the Trend page shows, for one history window
public sealed class TrendResult
{
    public DateTime Today { get; init; }
    public DateTime FirstWeek { get; init; }    // Monday of week 0
    public int Weeks { get; init; }             // complete weeks actually used
    public int RequestedWeeks { get; init; }
    public int AvailableWeeks { get; init; }    // complete weeks tracked so far
    public List<TagForecast> Tags { get; } = new();

    public bool HasEnoughHistory => Weeks >= TrendForecaster.MinWeeks;

    // The same weeks with only some of the forecasts - the tags without their groups, for the
    // charts that add tags up, where a group (the sum of its tags) would count its hours twice
    public TrendResult With(IEnumerable<TagForecast> tags)
    {
        var copy = new TrendResult
        {
            Today = Today,
            FirstWeek = FirstWeek,
            Weeks = Weeks,
            RequestedWeeks = RequestedWeeks,
            AvailableWeeks = AvailableWeeks
        };
        copy.Tags.AddRange(tags);
        return copy;
    }

    // A day's place on the week axis: its week's index, shifted so Thursday is the week's middle
    public double XOf(DateTime day) => ((day.Date - FirstWeek).Days - 3) / 7.0;

    public DateTime WeekStart(double x) => FirstWeek.AddDays(7 * (int)Math.Round(x));
}

// One tag's history and where it is heading. Weekly figures are hours per week.
public sealed class TagForecast
{
    public string Tag { get; init; } = "";
    public double[] WeeklyHours { get; init; } = [];
    public Dictionary<DateTime, double> Days { get; set; } = new(); // seconds, as aggregated

    public double Level { get; init; }          // average of the newest LevelWeeks weeks
    public double LevelCentre { get; init; }    // the x that average stands for
    public double Slope { get; init; }          // hours per week, gained (or lost) each week

    public double Last30Hours { get; set; }
    public double Next30Hours { get; set; }
    public double YearToDateHours { get; set; }
    public double YearEndHours { get; set; }

    // Expected hours in the week centred on x. A falling tag bottoms out at zero rather than
    // promising negative time.
    public double WeeklyAt(double x) => Math.Max(0, Level + (Slope * (x - LevelCentre)));

    // How much the weekly hours change over the next four weeks, relative to where they are now.
    // A tag at half an hour a week gaining ten minutes is not "surging", hence the floor.
    public double FourWeekChange => Slope * 4 / Math.Max(Level, 0.5);
}
