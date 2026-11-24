using System.Diagnostics;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chronoscope;

// Writes the heatmap's daily totals into the user's Obsidian vault, so dataviewjs + the
// "Heatmap Calendar" community plugin can render the same data the Statistics page shows.
//
// The two apps never talk: this only produces a file. Obsidian's Dataview watches the folder and
// re-renders on its own. The file MUST live inside the vault, because dv.io.load() resolves
// vault-relative paths only.
//
// The file holds every tag under "tags" and every tag group under "groups", each entry the same
// shape - so a note that draws a tag draws a group by reading data.groups[name] instead of
// data.tags[name]. Two sections rather than one with marked names: a group may share its name
// with a tag. A group also lists its member tags, as they were at the time of the export.
public class VaultExportService
{
    public const string FileName = "chronoscope-heatmap.json";

    private readonly SettingsService _settingsService;
    private readonly DataService _dataService;

    // No page asks for an export: it follows the data. Every reload (either page's Reload button,
    // the day view's timer, the reload after a tag edit), every colour change (tag or group) and
    // every change of groups rewrites the file.
    public VaultExportService(SettingsService settingsService, DataService dataService)
    {
        _settingsService = settingsService;
        _dataService = dataService;

        _dataService.DataReloaded += RequestExport;
        _dataService.GroupsChanged += RequestExport;
        _settingsService.TagColorsChanged += RequestExport;
    }

    public bool IsConfigured =>
        _settingsService.ObsidianExportEnabled &&
        !string.IsNullOrWhiteSpace(_settingsService.ObsidianExportPath);

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Status
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Nothing awaits an export any more, so its outcome is kept here for Settings to show.

    // When the file in the vault was last written - read from the file itself, so it survives a
    // restart and cannot claim a write that never landed. Null when there is no such file.
    public DateTime? LastWrittenAt
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_settingsService.ObsidianExportPath)) return null;
            try
            {
                var file = new FileInfo(Path.Combine(_settingsService.ObsidianExportPath, FileName));
                return file.Exists ? file.LastWriteTime : null;
            }
            catch (Exception)
            {
                return null;   // a malformed path is reported by the export itself
            }
        }
    }

    // Why the latest attempt failed, and when. Cleared by the next export that succeeds.
    public string? LastError { get; private set; }
    public DateTime? LastErrorAt { get; private set; }

    // Raised after every attempt, successful or not
    public event Action? StatusChanged;

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Scheduling
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // The same shape as SettingsService's saves: Tags saves one colour per tag in a loop, and a
    // reload can land while an export is still writing. Both would otherwise race on one .tmp
    // file. So a request only marks the export dirty, one writer runs at a time, and it keeps
    // going until nothing new was asked for - the file always ends up matching the latest state.
    private readonly SemaphoreSlim _exportGate = new(1, 1);
    private int _exportPending;

    public void RequestExport()
    {
        if (Interlocked.Exchange(ref _exportPending, 1) == 1) return;
        _ = DrainExportsAsync();
    }

    private async Task DrainExportsAsync()
    {
        // Let the caller's synchronous burst (a colour per tag) finish first, so it becomes one
        // export instead of one now and a second for everything after the first colour
        await Task.Yield();

        await _exportGate.WaitAsync();
        try
        {
            while (Interlocked.Exchange(ref _exportPending, 0) == 1)
                await ExportOnceAsync();
        }
        finally
        {
            _exportGate.Release();
        }
    }

    private async Task ExportOnceAsync()
    {
        // Never loaded, or just invalidated: nothing worth writing. The reload that follows
        // raises DataReloaded and comes back here with real data.
        var data = _dataService.CachedAppsTags;
        if (!IsConfigured || data.Count == 0) return;

        try
        {
            await ExportAsync(data);
            LastError = null;
            LastErrorAt = null;
        }
        catch (Exception ex)
        {
            // Nothing is awaiting this, so an escaping exception would be an unobserved crash.
            // A vault on an unplugged drive must not take the app with it; Settings shows why.
            Debug.WriteLine($"Vault export failed: {ex.Message}");
            LastError = ex.Message;
            LastErrorAt = DateTime.Now;
        }

        StatusChanged?.Invoke();
    }

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Writing the File
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Only ever called with _exportGate held. Throws on a real IO problem.
    private async Task ExportAsync(IReadOnlyList<AppsTagsTable> data)
    {
        string folder = _settingsService.ObsidianExportPath;

        // Deliberately not CreateDirectory: a stale path (vault moved, drive unplugged) should be
        // reported, not silently recreated somewhere the user is not looking.
        if (!Directory.Exists(folder))
            throw new DirectoryNotFoundException($"Export folder not found:{Environment.NewLine}{folder}");

        // Every year, not just the one on screen - otherwise the vault could only ever render
        // the current year.
        // Off the UI thread: it walks every year of the dataset, and this runs on every reload.
        // The data is never mutated once loaded, so reading it from here is safe.
        //
        // Tags and groups in one pass: the grouped rows go in a second time under their group's
        // key (the same trick the Statistics page uses), and the result is split by that key.
        var groupByTag = _dataService.GroupByTag;
        var totals = await Task.Run(() =>
            HeatmapAggregator.AggregateTagDays(TagGrouping.WithGroupRows(data, groupByTag), year: null));
        var perTag = totals.Where(t => !TagGrouping.IsGroupKey(t.Tag)).ToList();
        var perGroup = totals.Where(t => TagGrouping.IsGroupKey(t.Tag)).ToList();

        var periods = Periods(totals);

        var payload = new HeatmapExport
        {
            GeneratedAt = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture),
            Unit = "seconds",
            Periods = periods.ToDictionary(p => p.Key, p => new PeriodExport { From = Iso(p.From), To = Iso(p.To) }),
            Tags = perTag.ToDictionary(
                t => t.Tag,
                t => Fill(new TagExport(), t, _settingsService.GetTagColor(t.Tag), periods)),

            // Shaded against their own days, as the Statistics page's group cards are: a dark
            // cell means a heavy day for the group, not a fixed number of hours
            Groups = perGroup.ToDictionary(
                g => TagGrouping.NameOf(g.Tag),
                g =>
                {
                    string name = TagGrouping.NameOf(g.Tag);
                    var entry = Fill(new GroupExport(), g, _settingsService.GetGroupColor(name), periods);
                    entry.Tags = groupByTag
                        .Where(kv => kv.Value == name)
                        .Select(kv => kv.Key)
                        .Order(StringComparer.CurrentCultureIgnoreCase)
                        .ToArray();
                    return entry;
                })
        };

        // The relaxed encoder only matters for readability: the default escapes the timezone's
        // "+" as + and any umlaut in a tag name as \uXXXX. This file is consumed by
        // JSON.parse in a note, never injected into HTML, so the stricter escaping buys nothing
        // and only makes the file unpleasant to open.
        string json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });

        // Dataview watches this folder and can read mid-write, so write beside the target and
        // swap it in: a reader sees either the old file or the new one, never a truncated one.
        string finalPath = Path.Combine(folder, FileName);
        string tempPath = finalPath + ".tmp";
        await Task.Run(() => File.WriteAllText(tempPath, json));
        File.Move(tempPath, finalPath, overwrite: true);
    }

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Periods
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // A period is a span of days a note can draw, the same spans the Statistics page steps
    // through: "pastYear" - its default "1 Year", the 365 days up to the export - and every
    // calendar year with anything tracked, by its number ("2025"). Every entry carries a scale
    // for every period, so a note picks a period by one name in both places:
    //   data.periods.pastYear   and   data.tags.Main.scales.pastYear
    //
    // The note takes the span from the file rather than its own clock: the cut points were worked
    // out for exactly that span, and a note opened days after the export would otherwise draw a
    // window the legend does not describe.

    private sealed record Period(string Key, DateTime From, DateTime To);

    private const string PastYearKey = "pastYear";

    private static List<Period> Periods(List<TagDailyTotals> totals)
    {
        var (pastFrom, pastTo) = HeatmapAggregator.PastYear(DateTime.Today);
        var periods = new List<Period> { new(PastYearKey, pastFrom, pastTo) };

        var years = totals.SelectMany(t => t.Days.Keys).Select(d => d.Year).Distinct().OrderDescending();
        foreach (var year in years)
            periods.Add(new Period(year.ToString(CultureInfo.InvariantCulture), new DateTime(year, 1, 1), new DateTime(year, 12, 31)));

        return periods;
    }

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Entries
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

    // One entry of the file, a tag's or a group's: its colour and shade ramp, its seconds per
    // day, and its scale for every period
    private T Fill<T>(T entry, TagDailyTotals totals, string color, List<Period> periods)
        where T : TagExport
    {
        entry.Color = ToRgbHex(color);
        // Skip(1) drops the ramp's "nothing tracked" slot: it is picked from Chronoscope's current
        // theme, and Obsidian themes itself.
        entry.Ramp = _settingsService.BuildTagRamp(color, HeatmapAggregator.ShadeLevels)
            .Skip(1)
            .Select(ToRgbHex)
            .ToArray();
        entry.Days = totals.Days
            .OrderBy(d => d.Key)
            .ToDictionary(
                d => Iso(d.Key),
                // ManicTime records whole seconds, so rounding the sum loses nothing and keeps
                // floating point artefacts out of the file.
                d => (long)Math.Round(d.Value));
        entry.Scales = periods.ToDictionary(p => p.Key, p => ScaleFor(totals.Days, p));
        return entry;
    }

    // How a period's days are shaded, and what each shade means - the Statistics page's own
    // numbers. Per PERIOD because the page buckets against the span on screen, so all-time
    // quartiles would colour the same day differently in Obsidian than in the app. Per entry
    // because that is how the app scales: a shade means "a heavy day for this tag".
    //
    // The labels are the legend text itself, so a note draws the legend as given rather than
    // rebuilding it - the two can then never disagree.
    private static ScaleExport ScaleFor(Dictionary<DateTime, double> days, Period period)
    {
        double[] thresholds = HeatmapAggregator.BucketThresholds(days
            .Where(d => d.Key >= period.From && d.Key <= period.To)
            .Select(d => d.Value));

        return new ScaleExport
        {
            Thresholds = thresholds,
            Labels = HeatmapAggregator.BucketLabels(thresholds)
        };
    }

    private static string Iso(DateTime day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // BuildTagRamp hands back SKColor.ToString() and GetTagColor may carry an alpha channel, so
    // both can arrive as #AARRGGBB. CSS and the plugin want #RRGGBB.
    private static string ToRgbHex(string hex)
    {
        string h = hex.TrimStart('#');

        return h.Length switch
        {
            8 => "#" + h.Substring(2),                          // AARRGGBB -> RRGGBB
            3 => $"#{h[0]}{h[0]}{h[1]}{h[1]}{h[2]}{h[2]}",      // RGB -> RRGGBB
            _ => "#" + h
        };
    }

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Shape of the File
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Private to the export: nothing else in the app binds to it.
    //
    //   generatedAt, unit
    //   periods:  { "pastYear": { from, to }, "2026": { from, to }, ... }
    //   tags:     { "<tag>":   { color, ramp[4], days{ "yyyy-MM-dd": seconds },
    //                            scales{ "<period>": { thresholds[<=3], labels[5] } } } }
    //   groups:   { "<group>": { ...the same..., tags[] } }
    //
    // A shade is picked as the app picks it: 0 ("none") for a day with nothing tracked, else the
    // first threshold the day's seconds fit under (1-based), else 4. ramp[shade - 1] is its colour
    // and labels[shade] its legend text.

    private class HeatmapExport
    {
        [JsonPropertyName("generatedAt")] public string GeneratedAt { get; init; } = "";
        [JsonPropertyName("unit")] public string Unit { get; init; } = "";
        [JsonPropertyName("periods")] public Dictionary<string, PeriodExport> Periods { get; init; } = new();
        [JsonPropertyName("tags")] public Dictionary<string, TagExport> Tags { get; init; } = new();
        [JsonPropertyName("groups")] public Dictionary<string, GroupExport> Groups { get; init; } = new();
    }

    // An inclusive span of days, as yyyy-MM-dd
    private class PeriodExport
    {
        [JsonPropertyName("from")] public string From { get; init; } = "";
        [JsonPropertyName("to")] public string To { get; init; } = "";
    }

    private class TagExport
    {
        [JsonPropertyName("color")] public string Color { get; set; } = "";
        [JsonPropertyName("ramp")] public string[] Ramp { get; set; } = [];
        [JsonPropertyName("days")] public Dictionary<string, long> Days { get; set; } = new();
        [JsonPropertyName("scales")] public Dictionary<string, ScaleExport> Scales { get; set; } = new();
    }

    // A group's entry: everything a tag's has, and which tags it is made of
    private class GroupExport : TagExport
    {
        [JsonPropertyName("tags")] public string[] Tags { get; set; } = [];
    }

    // One period's shading of one entry: its cut points in seconds, and the legend text of each
    // shade, "none" first
    private class ScaleExport
    {
        [JsonPropertyName("thresholds")] public double[] Thresholds { get; init; } = [];
        [JsonPropertyName("labels")] public string[] Labels { get; init; } = [];
    }
}
