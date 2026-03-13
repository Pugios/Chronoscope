using CsvHelper;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace TimeViewer;

public class DataService
{
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Reading Tables (Tags & Time)
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    private readonly SemaphoreSlim _dataGate = new(1, 1);
    private static readonly string TagsPath = Path.Combine(FileSystem.AppDataDirectory, "tags.csv");
    private static readonly string ExplorerPath = Path.Combine(FileSystem.AppDataDirectory, "explorer-processes.csv");


    private List<TagsTable> _cachedTags = new();
    public IReadOnlyList<TagsTable> CachedTags => _cachedTags;

    private List<AppsTagsTable> _cachedAppsTags = new();
    public IReadOnlyList<AppsTagsTable> CachedAppsTags => _cachedAppsTags;

    private List<AppsTagsDocumentsTable> _cachedAppsTagsDocuments = new();
    public IReadOnlyList<AppsTagsDocumentsTable> CachedAppsTagsDocuments => _cachedAppsTagsDocuments;

    private List<ExplorerRule> _explorerRules = new();
    public IReadOnlyList<ExplorerRule> ExplorerRules => _explorerRules;


    // Create _cachedAppsTags
    public async Task<List<AppsTagsTable>> GetMergedDataAsync(bool forceReload)
    {
        await _dataGate.WaitAsync();
        try
        {
            if (!forceReload && _cachedAppsTags.Any())
            {
                return _cachedAppsTags;
            }

            // Read Tags
            List<TagsTable> tags = await GetTagTableAsync();
            _cachedTags = tags;

            // Read Time Table from ManicTime
            List<AppsTable> apps = await ExportTimeTableAsync();

            // Merge them by Process Name
            _cachedAppsTags = MergeAppTags(apps, tags);

            // Read Documents Table from ManicTime
            List<DocumentsTable> documents = await ExportDocumentsTableAsync();
            // Merge by Start and End Time
            _cachedAppsTagsDocuments = MergeAppsTagsDocuments(_cachedAppsTags, documents);

            // Read Explorer Process Rules
            _explorerRules = await GetExplorerAsync();

            // Apply Rules for explorer Apps and reduce down to AppsTagsTable
            _cachedAppsTags = ReduceTable(ApplyExplorerRules(_cachedAppsTagsDocuments, _explorerRules));

            return _cachedAppsTags;
        }
        finally
        {
            _dataGate.Release();
        }
    }

    // 1. Read Tags
    public static async Task<List<TagsTable>> GetTagTableAsync()
    {
        Directory.CreateDirectory(FileSystem.AppDataDirectory);
        if (!File.Exists(TagsPath))
        {
            await File.WriteAllTextAsync(TagsPath, "Process,Tag" + Environment.NewLine);
        }

        using var reader = new StreamReader(TagsPath);
        using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
        return csv.GetRecords<TagsTable>().ToList();
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
    private static List<AppsTagsTable> MergeAppTags(List<AppsTable> apps, List<TagsTable> tags)
    {
        // Merging Tags into Applications by Process Name
        var merged = from app in apps
                     join tag in tags on app.Process equals tag.Process into gj
                     from subgroup in gj.DefaultIfEmpty()
                     select new AppsTagsTable
                     {
                         Name = app.Name,
                         Start = app.Start,
                         End = app.End,
                         Duration = app.Duration,
                         Process = app.Process,
                         OriginalProcess = app.Process,
                         Tag = subgroup?.Tag ?? "No Clue"
                     };

        return merged.ToList();
    }

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Allow User to Edit Tags and Save Back to CSV
    public async Task ApplyTagChangesAsync(List<TagsTable> updates)
    {
        foreach (var update in updates)
        {
            var existing = _cachedTags.FirstOrDefault(t => t.Process == update.Process);
            if (existing is not null)
                existing.Tag = update.Tag;
            else
                _cachedTags.Add(new TagsTable { Process = update.Process, Tag = update.Tag });
        }

        using var writer = new StreamWriter(TagsPath);
        using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture);
        await csv.WriteRecordsAsync(_cachedTags);
    }

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Explorer Processes
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // 1. Read Documents Table from ManicTime
    public static async Task<List<DocumentsTable>> ExportDocumentsTableAsync()
    {
        // Running mtc to export CSV to tempCsvPath
        string tempCsvPath = Path.Combine(FileSystem.CacheDirectory, "manictime-documents-export.csv");

        using Process process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                WindowStyle = ProcessWindowStyle.Hidden,
                FileName = @"C:\Program Files\ManicTime\mtc.exe",
                Arguments = $"export ManicTime/Documents \"{tempCsvPath}\"",
                CreateNoWindow = true
            }
        };

        process.Start();
        await process.WaitForExitAsync().ConfigureAwait(false);

        // Reading Exported Applications Table
        using var reader = new StreamReader(tempCsvPath);
        using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
        return csv.GetRecords<DocumentsTable>().ToList();
    }

    // 2. Merge Documents into Applications/Tags by Start and End Time
    private static List<AppsTagsDocumentsTable> MergeAppsTagsDocuments(List<AppsTagsTable> datas, List<DocumentsTable> documents)
    {
        // Merging Documents into Applications/Tags by Start and End Time
        var merged = from data in datas
                     join document in documents
                     on new { data.Start, data.End } equals new { document.Start, document.End } into gj
                     from subgroup in gj.DefaultIfEmpty()
                     select new AppsTagsDocumentsTable
                     {
                         Name = data.Name,
                         DocName = subgroup?.Name ?? "No Clue",
                         Domain = subgroup?.Domain ?? "No Clue",
                         Start = data.Start,
                         End = data.End,
                         Duration = data.Duration,
                         Process = data.Process,
                         OriginalProcess = data.OriginalProcess,
                         Tag = data.Tag
                     };

        return merged.ToList();
    }

    // 3. Read Explorer Process Rules
    public static async Task<List<ExplorerRule>> GetExplorerAsync()
    {
        Directory.CreateDirectory(FileSystem.AppDataDirectory);
        if (!File.Exists(ExplorerPath))
        {
            await File.WriteAllTextAsync(ExplorerPath, "Process,Tag,Column,MatchType,Pattern,Order" + Environment.NewLine);
        }

        using var reader = new StreamReader(ExplorerPath);
        using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
        return csv.GetRecords<ExplorerRule>().ToList();
    }

    // 4. Apply Explorer Process Rules to rename Process and assign Tag
    public static List<AppsTagsDocumentsTable> ApplyExplorerRules(List<AppsTagsDocumentsTable> data, List<ExplorerRule> rules)
    {
        return data.Select(row =>
        {
            // Find the first matching rule for this row
            var matchingRule = rules
                .Where(r => r.Process == row.Process)
                .OrderBy(r => r.Order)
                .FirstOrDefault(r =>
                {
                    var value = r.Column switch
                    {
                        "Name" => row.Name,
                        "DocName" => row.DocName,
                        "Domain" => row.Domain,
                        _ => ""
                    };
                    return r.MatchType switch
                    {
                        "Prefix" => value.StartsWith(r.Pattern, StringComparison.OrdinalIgnoreCase),
                        "Suffix" => value.EndsWith(r.Pattern, StringComparison.OrdinalIgnoreCase),
                        "Include" => value.Contains(r.Pattern, StringComparison.OrdinalIgnoreCase),
                        _ => false
                    };
                });

            return new AppsTagsDocumentsTable
            {
                Name = row.Name,
                Start = row.Start,
                End = row.End,
                Duration = row.Duration,
                // If a rule matched, rename Process to "Process - Tag"
                Process = matchingRule is not null
                    ? $"{row.Process} - {matchingRule.Tag}"
                    : row.Process,
                OriginalProcess = row.OriginalProcess,
                // If a rule matched, use the rule's tag
                Tag = matchingRule is not null
                    ? matchingRule.Tag
                    : row.Tag,
                DocName = row.DocName,
                Domain = row.Domain
            };
        }).ToList();
    }
    // 5. Reduce AppsTagsDocumentsTable down to just AppsTagsTable to be used by PieGraph and such
    private static List<AppsTagsTable> ReduceTable(List<AppsTagsDocumentsTable> data)
    {
        return data.Select(row =>
        {
            return new AppsTagsTable
            {
                Name = row.Name,
                Start = row.Start,
                End = row.End,
                Duration = row.Duration,
                Process = row.Process,
                OriginalProcess = row.OriginalProcess,
                Tag = row.Tag
            };
        }).ToList();
    }

    // Replace Explorer Rules
    public async Task ReplaceExplorerRulesAsync(string process, List<ExplorerRule> newRules)
    {
        _explorerRules.RemoveAll(r => r.Process == process);
        _explorerRules.AddRange(newRules);

        using var writer = new StreamWriter(ExplorerPath);
        using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture);
        await csv.WriteRecordsAsync(_explorerRules);
    }
}