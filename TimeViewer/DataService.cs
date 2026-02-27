using CsvHelper;
using System;
using System.Diagnostics;
using System.Globalization;
using static TimeViewer.MainPage;

namespace TimeViewer;

public class DataService
{
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Reading Tables (Tags & Time)
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

    private List<AppsTable> _cachedApps = new();
    private List<TagTable> _cachedTags = new();
    private readonly SemaphoreSlim _dataGate = new(1, 1);

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

    private static readonly string TagsPath = Path.Combine(FileSystem.AppDataDirectory, "tags.csv");
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

    public static void PrintTable(List<AppsTable> table)
    {
        foreach (var row in table)
        {
            Debug.WriteLine(row.ToString());
        }
    }
}