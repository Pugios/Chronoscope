using CsvHelper;
using System.Diagnostics;
using System.Globalization;

namespace TimeViewer;

public class DataService
{
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Reading Tables (Tags & Time)
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

    private List<AppsTable> _cachedApps = new();
    private List<TagTable> _cachedTags = new();
    private readonly SemaphoreSlim _dataGate = new(1, 1);
    private static readonly string TagsPath = Path.Combine(FileSystem.AppDataDirectory, "tags.csv");

    public List<TagTable> CachedTags => _cachedTags;


    // Create _cachedApps | 1. Read Tags, 2. Read Time Table from ManicTime, 3. Merge them by Process Name
    public async Task<List<AppsTable>> GetMergedDataAsync(bool forceReload)
    {
        await _dataGate.WaitAsync();
        try
        {
            if (!forceReload && _cachedApps.Any())
            {
                return _cachedApps;
            }

            var tags = await GetTagTable();
            var apps = await ExportTimeTableAsync();
            _cachedTags = tags;
            _cachedApps = MergeAppTags(apps, tags);
            return _cachedApps;
        }
        finally
        {
            _dataGate.Release();
        }
    }

    // 1. Read Tags
    public static async Task<List<TagTable>> GetTagTable()
    {
        Directory.CreateDirectory(FileSystem.AppDataDirectory);
        if (!File.Exists(TagsPath))
        {
            await File.WriteAllTextAsync(TagsPath, "Process,Tag" + Environment.NewLine);
        }
        Debug.WriteLine($"Tags file path: {TagsPath}");

        using var reader = new StreamReader(TagsPath);
        using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
        return csv.GetRecords<TagTable>().ToList();
    }

    // 2. Read Time Table from ManicTime
    private static async Task<List<AppsTable>> ExportTimeTableAsync()
    {
        // Running mtc to export CSV to tempCsvPath
        string tempCsvPath = Path.Combine(FileSystem.CacheDirectory, "manictime-export.csv");

        using Process process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                WindowStyle = ProcessWindowStyle.Hidden,
                FileName = @"C:\Program Files\ManicTime\mtc.exe",
                Arguments = $"export ManicTime/Applications \"{tempCsvPath}\"",
                CreateNoWindow = true
            }
        };

        process.Start();
        await process.WaitForExitAsync().ConfigureAwait(false);

        // Reading Exported Applications Table
        using var reader = new StreamReader(tempCsvPath);
        using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
        return csv.GetRecords<AppsTable>().ToList();
    }

    // 3. Merge Tags into TimeTable by Process Name
    private static List<AppsTable> MergeAppTags(List<AppsTable> apps, List<TagTable> tags)
    {
        // Merging Tags into Applications by Process Name
        var merged = from app in apps
                     join tag in tags on app.Process equals tag.Process into gj
                     from subgroup in gj.DefaultIfEmpty()
                     select new AppsTable
                     {
                         Name = app.Name,
                         Start = app.Start,
                         End = app.End,
                         Duration = app.Duration,
                         Process = app.Process,
                         Tag = subgroup?.Tag ?? "No Clue"
                     };

        return merged.ToList();
    }

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Allow User to Edit Tags and Save Back to CSV
    public async Task ApplyTagChangesAsync(List<TagTable> updates)
    {
        foreach (var update in updates)
        {
            var existing = _cachedTags.FirstOrDefault(t => t.Process == update.Process);
            if (existing is not null)
                existing.Tag = update.Tag;
            else
                _cachedTags.Add(new TagTable { Process = update.Process, Tag = update.Tag });
        }

        using var writer = new StreamWriter(TagsPath);
        using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture);
        await csv.WriteRecordsAsync(_cachedTags);
    }

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Util
    public static void PrintTable(List<AppsTable> table)
    {
        foreach (var row in table)
        {
            Debug.WriteLine(row.ToString());
        }
    }
}