using CsvHelper;
using System.Diagnostics;
using System.Globalization;

namespace TimeViewer;

public class DataService
{
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Reading Tables (Tags & Time)
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // One gate around every read AND every mutation. The refresh timer can fire while a settings
    // page is saving, so the mutators have to hold it too - otherwise they rewrite a CSV and
    // mutate _cachedTags / _explorerRules underneath a reload that is halfway through using them.
    private readonly SemaphoreSlim _dataGate = new(1, 1);

    // How long mtc.exe gets before it is treated as hung. A full export takes a few seconds;
    // anything past this is a stuck process, and without a bound the await here would never
    // return and the page would sit blank forever.
    private static readonly TimeSpan MtcTimeout = TimeSpan.FromMinutes(2);

    private static readonly string TagsPath = Path.Combine(FileSystem.AppDataDirectory, "tags.csv");
    private static readonly string ExplorerPath = Path.Combine(FileSystem.AppDataDirectory, "explorer-processes.csv");
    private readonly SettingsService _settingsService;

    public DataService(SettingsService settingsService)
    {
        _settingsService = settingsService;
    }


    private List<TagsTable> _cachedTags = new();
    public IReadOnlyList<TagsTable> CachedTags => _cachedTags;

    private List<AppsTagsTable> _cachedAppsTags = new();
    public IReadOnlyList<AppsTagsTable> CachedAppsTags => _cachedAppsTags;

    private List<AppsTagsDocumentsTable> _cachedAppsTagsDocuments = new();
    public IReadOnlyList<AppsTagsDocumentsTable> CachedAppsTagsDocuments => _cachedAppsTagsDocuments;

    private List<ExplorerRule> _explorerRules = new();
    public IReadOnlyList<ExplorerRule> ExplorerRules => _explorerRules;

    // Every tag the app knows about, from both places one can be created. tags.csv alone is not
    // enough: a tag invented inside an Explorer rule lives only in explorer-processes.csv, and
    // used to get a colour but never appear in any of the tag pickers.
    public IReadOnlyList<string> KnownTags =>
        _cachedTags.Select(t => t.Tag)
            .Concat(_explorerRules.Select(r => r.Tag))
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct()
            .OrderBy(t => t)
            .ToList();


    // Create _cachedAppsTags
    public async Task<IReadOnlyList<AppsTagsTable>> GetMergedDataAsync(bool forceReload)
    {
        await _dataGate.WaitAsync();
        try
        {
            if (!forceReload && _cachedAppsTags.Count > 0)
            {
                return _cachedAppsTags;
            }

            // Everything below builds into locals and only lands in the cache fields once the
            // whole pipeline has succeeded. Assigning as it went meant a failure partway through
            // (mtc.exe dying between the two exports) left _cachedAppsTags holding the
            // intermediate table - tagged, but with no Explorer rules applied. Being non-empty,
            // it then satisfied the check above and was served as finished data from then on.

            // Read Tags
            List<TagsTable> tags = await GetTagTableAsync();

            // Read Time Table from ManicTime
            List<AppsTable> apps = await ExportTimeTableAsync();

            // Merge them by Process Name
            List<AppsTagsTable> appsTags = MergeAppTags(apps, tags);

            // Read Documents Table from ManicTime
            List<DocumentsTable> documents = await ExportDocumentsTableAsync();
            // Merge by Start and End Time
            List<AppsTagsDocumentsTable> appsTagsDocuments = MergeAppsTagsDocuments(appsTags, documents);

            // Read Explorer Process Rules
            List<ExplorerRule> explorerRules = await GetExplorerAsync();

            // Apply Rules for explorer Apps and reduce down to AppsTagsTable
            List<AppsTagsTable> reduced = ReduceTable(ApplyExplorerRules(appsTagsDocuments, explorerRules));

            _cachedTags = tags;
            _cachedAppsTagsDocuments = appsTagsDocuments;
            _explorerRules = explorerRules;
            _cachedAppsTags = reduced;

            return _cachedAppsTags;
        }
        finally
        {
            _dataGate.Release();
        }
    }

    // 1. Read Tags
    private static async Task<List<TagsTable>> GetTagTableAsync()
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
    private Task<List<AppsTable>> ExportTimeTableAsync() =>
        RunMtcExportAsync<AppsTable>("ManicTime/Applications", "manictime-export.csv", "data");

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Run one mtc.exe export and read back what it wrote.
    //
    // The two exports differ only in the timeline they ask for, so the failure handling lives
    // here once. Three things it has to get right:
    //  - Delete the target first. mtc.exe writes into the cache directory, so a failed run used
    //    to leave the previous run's file in place and the reader happily returned stale data as
    //    if it were fresh.
    //  - Check the exit code. A bad path throws on Start and is caught below, but mtc.exe
    //    refusing the request exits non-zero and says so only on stderr, which was discarded.
    //  - Bound the wait, and kill the process if it overruns.
    private async Task<List<T>> RunMtcExportAsync<T>(string timeline, string cacheFileName, string what)
        where T : class
    {
        Directory.CreateDirectory(FileSystem.CacheDirectory);
        string tempCsvPath = Path.Combine(FileSystem.CacheDirectory, cacheFileName);

        try
        {
            if (File.Exists(tempCsvPath)) File.Delete(tempCsvPath);

            using Process process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    WindowStyle = ProcessWindowStyle.Hidden,
                    FileName = _settingsService.MtcExePath,
                    Arguments = $"export {timeline} \"{tempCsvPath}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardError = true
                }
            };

            process.Start();

            // Read stderr while it runs: a full pipe would block mtc.exe and deadlock the wait
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();

            using var timeout = new CancellationTokenSource(MtcTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw new TimeoutException(
                    $"mtc.exe did not finish within {MtcTimeout.TotalMinutes:0} minutes.");
            }

            if (process.ExitCode != 0)
            {
                string stderr = (await stderrTask.ConfigureAwait(false)).Trim();
                throw new InvalidOperationException(
                    $"mtc.exe exited with code {process.ExitCode}."
                    + (stderr.Length > 0 ? Environment.NewLine + stderr : ""));
            }

            if (!File.Exists(tempCsvPath))
            {
                throw new FileNotFoundException(
                    $"mtc.exe reported success but wrote no file to:{Environment.NewLine}{tempCsvPath}");
            }

            using var reader = new StreamReader(tempCsvPath);
            using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
            return csv.GetRecords<T>().ToList();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not export {what} from ManicTime.{Environment.NewLine}"
                + $"Check the mtc.exe path in Settings:{Environment.NewLine}{_settingsService.MtcExePath}"
                + $"{Environment.NewLine}{Environment.NewLine}{ex.Message}", ex);
        }
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
        await _dataGate.WaitAsync();
        try
        {
            foreach (var update in updates)
            {
                var existing = _cachedTags.FirstOrDefault(t => t.Process == update.Process);
                if (existing is not null)
                    existing.Tag = update.Tag;
                else
                    _cachedTags.Add(new TagsTable { Process = update.Process, Tag = update.Tag });
            }

            using (var writer = new StreamWriter(TagsPath))
            using (var csv = new CsvWriter(writer, CultureInfo.InvariantCulture))
            {
                await csv.WriteRecordsAsync(_cachedTags);
            }

            InvalidateDerived();
        }
        finally
        {
            _dataGate.Release();
        }
    }

    // Replace Explorer Rules
    public async Task ReplaceExplorerRulesAsync(string process, List<ExplorerRule> newRules)
    {
        await _dataGate.WaitAsync();
        try
        {
            _explorerRules.RemoveAll(r => r.Process == process);
            _explorerRules.AddRange(newRules);

            using (var writer = new StreamWriter(ExplorerPath))
            using (var csv = new CsvWriter(writer, CultureInfo.InvariantCulture))
            {
                await csv.WriteRecordsAsync(_explorerRules);
            }

            InvalidateDerived();
        }
        finally
        {
            _dataGate.Release();
        }
    }

    // Both cached tables are built FROM tags.csv and explorer-processes.csv, so editing either
    // file makes both stale. Every mutator routes through here rather than remembering to clear
    // the right pair - the Explorer rules used to skip this, which left the Settings grid and the
    // Statistics page showing rows built with the rules the user had just replaced.
    private void InvalidateDerived()
    {
        _cachedAppsTags = new();
        _cachedAppsTagsDocuments = new();
    }

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Explorer Processes
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // 1. Read Documents Table from ManicTime
    private Task<List<DocumentsTable>> ExportDocumentsTableAsync() =>
        RunMtcExportAsync<DocumentsTable>("ManicTime/Documents", "manictime-documents-export.csv", "documents");

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
                         DocName = subgroup?.Name ?? "",
                         Domain = subgroup?.Domain ?? "",
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
    private static async Task<List<ExplorerRule>> GetExplorerAsync()
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
    public static List<AppsTagsDocumentsTable> ApplyExplorerRules(
        IReadOnlyList<AppsTagsDocumentsTable> data, IEnumerable<ExplorerRule> rules)
    {
        // Indexed once instead of per row. This used to filter and sort the whole rule list inside
        // the Select, so a load ran that scan once per row - ~86k times; now each row is one
        // dictionary lookup into an already ordered array.
        var rulesByProcess = rules
            .GroupBy(r => r.Process)
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.Order).ToArray());

        return data.Select(row =>
        {
            // Find the first matching rule for this row
            ExplorerRule? matchingRule = rulesByProcess.TryGetValue(row.Process, out var candidates)
                ? Array.Find(candidates, r => Matches(row, r))
                : null;

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

    // Column: which of the row's text fields the rule looks at. MatchType: how.
    private static bool Matches(AppsTagsDocumentsTable row, ExplorerRule rule)
    {
        var value = rule.Column switch
        {
            "Name" => row.Name,
            "DocName" => row.DocName,
            "Domain" => row.Domain,
            _ => ""
        };

        return rule.MatchType switch
        {
            "Prefix" => value.StartsWith(rule.Pattern, StringComparison.OrdinalIgnoreCase),
            "Suffix" => value.EndsWith(rule.Pattern, StringComparison.OrdinalIgnoreCase),
            "Include" => value.Contains(rule.Pattern, StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    // 5. Reduce AppsTagsDocumentsTable down to just AppsTagsTable to be used by PieGraph and such
    private static List<AppsTagsTable> ReduceTable(List<AppsTagsDocumentsTable> data)
    {
        return data.Select(row => new AppsTagsTable
        {
            Name = row.Name,
            Start = row.Start,
            End = row.End,
            Duration = row.Duration,
            Process = row.Process,
            OriginalProcess = row.OriginalProcess,
            Tag = row.Tag
        }).ToList();
    }
}
