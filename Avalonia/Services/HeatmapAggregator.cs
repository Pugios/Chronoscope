namespace TimeViewer;

// The per-tag / per-day totals behind every heatmap view.
//
// This lives outside StatisticsPage because two consumers need the exact same numbers: the
// on-screen chart and the Obsidian vault export. If each computed its own, the app and the vault
// could drift apart silently - the export would look right and simply disagree.
//
// Seconds are the unit throughout, matching how Duration is summed everywhere else in the app.
// Bucketing into shades is deliberately NOT done here: that is a display decision owned by
// StatisticsPage, and it must not reach the exported file.
public static class HeatmapAggregator
{
    // The floor the pie and the timeline apply too. They reference this constant rather than
    // repeating the number, so the three cannot drift apart.
    public const double MinTrackedSeconds = 30;

    // GitHub's four non-empty shades; the empty one is index 0.
    public const int ShadeLevels = 4;

    // The two axes of the Active Hours grid. Monday = 0, matching the Monday-first shift the
    // year grid's rows already use.
    public const int WeekdayCount = 7;
    public const int HourCount = 24;

    // Daily totals per tag, biggest tag first. Pass a year to restrict to it, or null for every
    // year in the data (what the export wants, so the vault is not limited to one year).
    //
    // Two deliberate agreements with MainPage: the >30s floor above, and an activity crossing
    // midnight counting wholly toward its Start day, which is how the pie groups a day.
    public static List<TagDailyTotals> AggregateTagDays(IEnumerable<AppsTagsTable> data, int? year = null)
    {
        return data
            .Where(a => a.DurationSeconds > MinTrackedSeconds)
            .Where(a => year is null || a.Start.Year == year)
            .Where(a => !string.IsNullOrWhiteSpace(a.Tag))
            .GroupBy(a => a.Tag)
            .Select(g => new TagDailyTotals
            {
                Tag = g.Key,
                Days = g
                    .GroupBy(a => a.Start.Date)
                    .ToDictionary(d => d.Key, d => d.Sum(a => a.DurationSeconds)),
                TotalSeconds = g.Sum(a => a.DurationSeconds)
            })
            .OrderByDescending(t => t.TotalSeconds)
            .ToList();
    }

    // Seconds per weekday and hour of day, per tag, biggest tag first - the Active Hours grid.
    // Same shape and ordering as AggregateTagDays, so the two line up tag for tag.
    //
    // Row selection is deliberately identical to AggregateTagDays - the >30s floor, and the year
    // taken from Start - so both grids on the Statistics page describe the same set of activities.
    //
    // The one rule that does NOT carry over is "an activity belongs wholly to its Start day".
    // Here an activity is SPLIT across every hour it actually covers. At day resolution that rule
    // is invisible; at hour resolution it would drop a four hour session entirely into its first
    // hour and invent a spike that never happened. The consequence is that an activity crossing
    // midnight now also spills into the following weekday's row, which is the honest answer to
    // "when was I active".
    public static List<TagWeekHourTotals> AggregateTagWeekHours(IEnumerable<AppsTagsTable> data, int? year = null)
    {
        return data
            .Where(a => a.DurationSeconds > MinTrackedSeconds)
            .Where(a => year is null || a.Start.Year == year)
            .Where(a => !string.IsNullOrWhiteSpace(a.Tag))
            .GroupBy(a => a.Tag)
            .Select(g =>
            {
                var cells = new double[WeekdayCount, HourCount];
                foreach (var a in g) AddHourSlices(cells, a.Start, a.End);

                return new TagWeekHourTotals
                {
                    Tag = g.Key,
                    Cells = cells,
                    TotalSeconds = g.Sum(a => a.DurationSeconds)
                };
            })
            .OrderByDescending(t => t.TotalSeconds)
            .ToList();
    }

    // Walks the activity hour boundary by hour boundary, adding each slice to the cell for the
    // weekday and hour that slice actually falls in. Rolling past hour 23 lands on the next day's
    // 00:00 on its own, so midnight needs no special case. Splitting conserves the total, which is
    // what lets the 168 cells sum back to the tag's TotalSeconds.
    private static void AddHourSlices(double[,] cells, DateTime start, DateTime end)
    {
        for (var cursor = start; cursor < end; )
        {
            var nextHour = cursor.Date.AddHours(cursor.Hour + 1);
            var sliceEnd = nextHour < end ? nextHour : end;

            int weekday = ((int)cursor.DayOfWeek + 6) % 7;   // Monday = 0, as the Y axis assumes
            cells[weekday, cursor.Hour] += (sliceEnd - cursor).TotalSeconds;

            cursor = sliceEnd;
        }
    }

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Shade Buckets
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Each tag is scaled against its own days, so a light-usage tag still shows a full light-to-dark
    // range instead of staying uniformly pale. Quartiles rather than a linear split of the maximum,
    // so one exceptional day cannot wash out the whole year.
    //
    // These used to be private to StatisticsPage. They live here now because the exported file has
    // to carry the same cut points: a legend that disagreed with the grid it labels would be worse
    // than no legend at all.

    public static double[] BucketThresholds(IEnumerable<double> dailySeconds)
    {
        var sorted = dailySeconds.Where(s => s > 0).OrderBy(s => s).ToArray();
        if (sorted.Length == 0) return [];

        return [Percentile(sorted, 0.25), Percentile(sorted, 0.50), Percentile(sorted, 0.75)];
    }

    private static double Percentile(double[] sorted, double fraction)
    {
        int index = (int)Math.Ceiling(fraction * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    // 0 for a day with nothing tracked, otherwise the first threshold the day fits under.
    // Testing them in order keeps the buckets monotone even when the percentiles tie - a tag with
    // one or two active days, or a run of identical days, needs no special case.
    public static int Bucket(double seconds, double[] thresholds)
    {
        if (seconds <= 0) return 0;

        for (int i = 0; i < thresholds.Length; i++)
        {
            if (seconds <= thresholds[i]) return i + 1;
        }

        return ShadeLevels;
    }

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Legend
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

    // What each shade means, as text: one label per ramp entry, index 0 first.
    // The bounds mirror Bucket exactly - step i covers up to thresholds[i-1], and the last step is
    // everything past the final threshold - so a legend drawn from these cannot contradict the grid.
    public static string[] BucketLabels(double[] thresholds)
    {
        var labels = new string[ShadeLevels + 1];
        labels[0] = "none";

        for (int i = 1; i <= ShadeLevels; i++)
        {
            // A tag with too few active days yields no thresholds at all, in which case Bucket
            // sends every tracked day to the top shade and "any" is the honest label. Tied
            // percentiles can also leave a step covering nothing; that needs no special case,
            // the label simply repeats.
            labels[i] = thresholds.Length == 0
                ? (i == ShadeLevels ? "any" : "")
                : i - 1 < thresholds.Length
                    ? "≤" + FormatDuration(thresholds[i - 1])
                    : ">" + FormatDuration(thresholds[^1]);
        }

        return labels;
    }

    // Compact enough to sit under a 10px swatch: "45m", "3h", "1h20m".
    public static string FormatDuration(double seconds)
    {
        var span = TimeSpan.FromSeconds(seconds);
        int hours = (int)span.TotalHours;
        int minutes = span.Minutes;

        if (hours == 0) return $"{Math.Max(minutes, 1)}m";
        return minutes == 0 ? $"{hours}h" : $"{hours}h{minutes}m";
    }
}
