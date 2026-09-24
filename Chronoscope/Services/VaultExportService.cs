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
public class VaultExportService
{
    public const string FileName = "chronoscope-heatmap.json";

    private readonly SettingsService _settingsService;
    private readonly DataService _dataService;

    // No page asks for an export: it follows the data. Every reload (either page's Reload button,
    // the day view's timer, the reload after a tag edit) and every colour change rewrites the file.
    public VaultExportService(SettingsService settingsService, DataService dataService)
    {
        _settingsService = settingsService;
        _dataService = dataService;

        _dataService.DataReloaded += RequestExport;
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
        var perTag = await Task.Run(() => HeatmapAggregator.AggregateTagDays(data, year: null));

        var payload = new HeatmapExport
        {
            GeneratedAt = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture),
            Unit = "seconds",
            Tags = perTag.ToDictionary(
                t => t.Tag,
                t =>
                {
                    string color = _settingsService.GetTagColor(t.Tag);
                    return new TagExport
                    {
                        Color = ToRgbHex(color),
                        // Skip(1) drops the ramp's "nothing tracked" slot: it is picked from
                        // Chronoscope's current theme, and Obsidian themes itself.
                        Ramp = _settingsService.BuildTagRamp(color, HeatmapAggregator.ShadeLevels)
                            .Skip(1)
                            .Select(ToRgbHex)
                            .ToArray(),
                        Thresholds = ThresholdsByYear(t.Days),
                        Days = t.Days
                            .OrderBy(d => d.Key)
                            .ToDictionary(
                                d => d.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                                // ManicTime records whole seconds, so rounding the sum loses
                                // nothing and keeps floating point artefacts out of the file.
                                d => (long)Math.Round(d.Value))
                    };
                })
        };

        // The relaxed encoder only matters for readability: the default escapes the timezone's
        // "+" as \u002B and any umlaut in a tag name as \uXXXX. This file is consumed by
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

    // The cut points the Statistics page colours this tag by, per year, in seconds.
    //
    // Per YEAR because the chart buckets a tag against the year on screen, so all-time quartiles
    // would colour the same day differently in Obsidian than in the app. Per tag because that is
    // how the app scales: a shade means "a heavy day for this tag", not a fixed number of hours.
    // Exporting them is what lets a note draw a legend that agrees with the grid it labels.
    private static Dictionary<string, double[]> ThresholdsByYear(Dictionary<DateTime, double> days) =>
        days.GroupBy(d => d.Key.Year)
            .OrderBy(g => g.Key)
            .ToDictionary(
                g => g.Key.ToString(CultureInfo.InvariantCulture),
                g => HeatmapAggregator.BucketThresholds(g.Select(d => d.Value)));

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

    // Shape of the exported file. Private to the export: nothing else in the app binds to it.
    private class HeatmapExport
    {
        [JsonPropertyName("generatedAt")] public string GeneratedAt { get; init; } = "";
        [JsonPropertyName("unit")] public string Unit { get; init; } = "";
        [JsonPropertyName("tags")] public Dictionary<string, TagExport> Tags { get; init; } = new();
    }

    private class TagExport
    {
        [JsonPropertyName("color")] public string Color { get; init; } = "";
        [JsonPropertyName("ramp")] public string[] Ramp { get; init; } = [];
        [JsonPropertyName("thresholds")] public Dictionary<string, double[]> Thresholds { get; init; } = new();
        [JsonPropertyName("days")] public Dictionary<string, long> Days { get; init; } = new();
    }
}
