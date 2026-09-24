using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TimeViewer;

// Writes the heatmap's daily totals into the user's Obsidian vault, so dataviewjs + the
// "Heatmap Calendar" community plugin can render the same data the Statistics page shows.
//
// The two apps never talk: this only produces a file. Obsidian's Dataview watches the folder and
// re-renders on its own. The file MUST live inside the vault, because dv.io.load() resolves
// vault-relative paths only.
public class VaultExportService
{
    public const string FileName = "timeviewer-heatmap.json";

    private readonly SettingsService _settingsService;

    public VaultExportService(SettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public bool IsConfigured =>
        _settingsService.ObsidianExportEnabled &&
        !string.IsNullOrWhiteSpace(_settingsService.ObsidianExportPath);

    // Returns the path written, or null when the export is switched off or unconfigured.
    // Throws only on a real IO problem, which the caller decides whether to surface.
    public async Task<string?> ExportAsync(IEnumerable<AppsTagsTable> data)
    {
        if (!IsConfigured) return null;

        string folder = _settingsService.ObsidianExportPath;

        // Deliberately not CreateDirectory: a stale path (vault moved, drive unplugged) should be
        // reported, not silently recreated somewhere the user is not looking.
        if (!Directory.Exists(folder))
            throw new DirectoryNotFoundException($"Export folder not found:{Environment.NewLine}{folder}");

        // Every year, not just the one on screen - otherwise the vault could only ever render
        // the current year.
        var perTag = HeatmapAggregator.AggregateTagDays(data, year: null);

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
                        // TimeViewer's current theme, and Obsidian themes itself.
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
        await File.WriteAllTextAsync(tempPath, json);
        File.Move(tempPath, finalPath, overwrite: true);

        return finalPath;
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
