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
    //
    // Cached: this is a property, so it reads as free at the call site, but it was running a LINQ
    // pipeline and allocating a list on every access - and SettingsPage.OnAppearing touches it
    // three times. Cleared wherever _cachedTags or _explorerRules are replaced.
    private IReadOnlyList<string>? _knownTags;
    public IReadOnlyList<string> KnownTags => _knownTags ??= BuildKnownTags();

    private IReadOnlyList<string> BuildKnownTags() =>
        _cachedTags.Select(t => t.Tag)
            .Concat(_explorerRules.Select(r => r.Tag))
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct()
            .OrderBy(t => t)
            .ToArray();

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Freshness
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // When the pipeline last completed. MainPage used to force a full reload on every OnAppearing,
    // so returning from Settings or Statistics relaunched mtc.exe twice and re-parsed the whole
    // export - for data that was usually seconds old. Edits do not need it either: both mutators
    // invalidate the cache themselves. Only genuinely stale data does, which is what maxAge is for.
    private DateTime _loadedAtUtc = DateTime.MinValue;

    public TimeSpan DataAge =>
        _loadedAtUtc == DateTime.MinValue ? TimeSpan.MaxValue : DateTime.UtcNow - _loadedAtUtc;


    // Create _cachedAppsTags.
    // forceReload always re-runs the pipeline; maxAge re-runs it only if the cache is older than
    // that. Both null/false serves whatever is cached, which is what a day change wants.
    public async Task<IReadOnlyList<AppsTagsTable>> GetMergedDataAsync(
        bool forceReload = false, TimeSpan? maxAge = null)
    {
        await _dataGate.WaitAsync();
        try
        {
            bool reload =
                _cachedAppsTags.Count == 0
                || forceReload
                || (maxAge is not null && DateTime.UtcNow - _loadedAtUtc > maxAge.Value);

            if (!reload)
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

            // Apply Rules for explorer Apps and reduce down to AppsTagsTable, in one pass
            List<AppsTagsTable> reduced = ApplyRulesAndReduce(appsTagsDocuments, explorerRules);

            _cachedTags = tags;
            _cachedAppsTagsDocuments = appsTagsDocuments;
            _explorerRules = explorerRules;
            _cachedAppsTags = reduced;
            _loadedAtUtc = DateTime.UtcNow;
            _knownTags = null;

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
        // A dictionary rather than a LINQ GroupJoin. Same left-join result, one lookup per row,
        // and the output list can be presized instead of doubling its way to ~86k.
        //
        // It also removes a fan-out: GroupJoin emits one row PER match, so a duplicated Process in
        // a hand-edited tags.csv silently duplicated every one of that process's rows and double
        // counted its time. First entry wins here, matching ApplyTagChangesAsync, which updates
        // the first match.
        var tagByProcess = new Dictionary<string, string>(tags.Count);
        foreach (var tag in tags)
            tagByProcess.TryAdd(tag.Process, tag.Tag);

        var merged = new List<AppsTagsTable>(apps.Count);
        foreach (var app in apps)
        {
            merged.Add(new AppsTagsTable
            {
                Name = app.Name,
                Start = app.Start,
                End = app.End,
                Duration = app.Duration,
                Process = app.Process,
                OriginalProcess = app.Process,
                Tag = tagByProcess.TryGetValue(app.Process, out var t) ? t : "No Clue"
            });
        }

        return merged;
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
        _loadedAtUtc = DateTime.MinValue;
        _knownTags = null;
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
        // Same treatment as MergeAppTags: a keyed lookup instead of a GroupJoin, so the result can
        // be presized and two documents sharing one interval cannot fan a row out into two and
        // double count it. First document wins.
        var byInterval = new Dictionary<(DateTime Start, DateTime End), DocumentsTable>(documents.Count);
        foreach (var document in documents)
            byInterval.TryAdd((document.Start, document.End), document);

        var merged = new List<AppsTagsDocumentsTable>(datas.Count);
        foreach (var data in datas)
        {
            byInterval.TryGetValue((data.Start, data.End), out var document);
            merged.Add(new AppsTagsDocumentsTable
            {
                Name = data.Name,
                DocName = document?.Name ?? "",
                Domain = document?.Domain ?? "",
                Start = data.Start,
                End = data.End,
                Duration = data.Duration,
                Process = data.Process,
                OriginalProcess = data.OriginalProcess,
                Tag = data.Tag
            });
        }

        return merged;
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

    // 4. Apply Explorer Process Rules to rename Process and assign Tag.
    // Public because ExplorerSettingsPage previews unsaved rules with it; it keeps the document
    // columns, which is what that grid displays. The pipeline uses ApplyRulesAndReduce instead.
    public static List<AppsTagsDocumentsTable> ApplyExplorerRules(
        IReadOnlyList<AppsTagsDocumentsTable> data, IEnumerable<ExplorerRule> rules)
    {
        var index = IndexRules(rules);
        var result = new List<AppsTagsDocumentsTable>(data.Count);

        foreach (var row in data)
        {
            var rule = MatchRule(row, index);
            result.Add(new AppsTagsDocumentsTable
            {
                Name = row.Name,
                Start = row.Start,
                End = row.End,
                Duration = row.Duration,
                // If a rule matched, rename Process to "Process - Tag"
                Process = rule is not null ? $"{row.Process} - {rule.Tag}" : row.Process,
                OriginalProcess = row.OriginalProcess,
                // If a rule matched, use the rule's tag
                Tag = rule is not null ? rule.Tag : row.Tag,
                DocName = row.DocName,
                Domain = row.Domain
            });
        }

        return result;
    }

    // 5. The pipeline's variant: applies the rules and drops the document columns in ONE pass.
    // Doing it as ReduceTable(ApplyExplorerRules(...)) built a full intermediate list of ~86k
    // AppsTagsDocumentsTable that nothing ever read - a whole extra copy of the dataset, and a
    // whole extra GC generation's worth of garbage, on every load.
    private static List<AppsTagsTable> ApplyRulesAndReduce(
        List<AppsTagsDocumentsTable> data, List<ExplorerRule> rules)
    {
        var index = IndexRules(rules);
        var result = new List<AppsTagsTable>(data.Count);

        foreach (var row in data)
        {
            var rule = MatchRule(row, index);
            result.Add(new AppsTagsTable
            {
                Name = row.Name,
                Start = row.Start,
                End = row.End,
                Duration = row.Duration,
                Process = rule is not null ? $"{row.Process} - {rule.Tag}" : row.Process,
                OriginalProcess = row.OriginalProcess,
                Tag = rule is not null ? rule.Tag : row.Tag
            });
        }

        return result;
    }

    // Rules grouped by process and pre-sorted by Order, built once per call. Before this, the
    // lookup filtered and sorted the entire rule list inside the per-row projection.
    private static Dictionary<string, ExplorerRule[]> IndexRules(IEnumerable<ExplorerRule> rules) =>
        rules
            .GroupBy(r => r.Process)
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.Order).ToArray());

    // The first rule for this row's process, in Order, whose pattern matches
    private static ExplorerRule? MatchRule(
        AppsTagsDocumentsTable row, Dictionary<string, ExplorerRule[]> index) =>
        index.TryGetValue(row.Process, out var candidates)
            ? Array.Find(candidates, r => Matches(row, r))
            : null;

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

}
