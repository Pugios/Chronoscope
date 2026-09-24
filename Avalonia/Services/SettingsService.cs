using Avalonia;
using Avalonia.Styling;
using SkiaSharp;
using System.Diagnostics;
using System.Text.Json;

namespace TimeViewer;

public class SettingsService
{
    private readonly string _filePath = Path.Combine(FileSystem.AppDataDirectory, "settings.json");
    private AppSettings _settings = new();

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Persistence
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Every mutator below wants to persist immediately, and several of them are called in a loop -
    // SettingsPage saves one colour per tag, CleanupColors deletes one per stale tag. Each used to
    // fire an unawaited SaveAsync, so N writers raced on one file: File.WriteAllTextAsync opens
    // with FileShare.Read, so the losers threw IOException. The unawaited ones swallowed it and
    // the awaited one threw out of an async void handler, taking the app down.
    //
    // So writes are serialised behind a gate and a burst is coalesced into a single write: a
    // mutator only marks the settings dirty, and whoever holds the gate writes whatever the
    // latest state is. SaveAsync stays public and awaitable for callers that need a hard flush
    // before navigating away.
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private int _savePending;

    private void RequestSave()
    {
        // Already queued: the in-flight writer will pick up this change too
        if (Interlocked.Exchange(ref _savePending, 1) == 1) return;
        _ = DrainSavesAsync();
    }

    private async Task DrainSavesAsync()
    {
        await _saveGate.WaitAsync();
        try
        {
            // Re-check inside the loop: anything marked dirty while we were writing gets its own
            // pass, so the file always ends up matching the final in-memory state.
            while (Interlocked.Exchange(ref _savePending, 0) == 1)
                await WriteAsync();
        }
        catch (Exception ex)
        {
            // Nothing is awaiting this, so an escaping exception would be an unobserved crash.
            // Losing a settings write is survivable; the next mutation writes the same state again.
            Debug.WriteLine($"Deferred settings save failed: {ex.Message}");
        }
        finally
        {
            _saveGate.Release();
        }
    }

    public async Task LoadAsync()
    {
        Debug.WriteLine($"FileSystem.AppDataDirectory: {FileSystem.AppDataDirectory}");

        // Taking the gate flushes any queued write first, so a reload cannot read a file that is
        // about to be overwritten and quietly roll back the change that queued it.
        await _saveGate.WaitAsync();
        try
        {
            if (!File.Exists(_filePath))
            {
                _settings = new AppSettings();
                return;
            }

            var json = await File.ReadAllTextAsync(_filePath);
            _settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            // A truncated or hand-edited settings.json must not stop the app from starting
            Debug.WriteLine($"Could not read settings, starting from defaults: {ex.Message}");
            _settings = new AppSettings();
        }
        finally
        {
            _saveGate.Release();
        }
    }

    public async Task SaveAsync()
    {
        Interlocked.Exchange(ref _savePending, 0);
        await _saveGate.WaitAsync();
        try
        {
            await WriteAsync();
        }
        finally
        {
            _saveGate.Release();
        }
    }

    // Only ever called with _saveGate held. Writes beside the target and swaps it in, so a crash
    // or a full disk mid-write leaves the previous settings intact rather than a truncated file.
    private async Task WriteAsync()
    {
        Directory.CreateDirectory(FileSystem.AppDataDirectory);

        var json = JsonSerializer.Serialize(_settings, JsonOptions);
        var tempPath = _filePath + ".tmp";

        await File.WriteAllTextAsync(tempPath, json);
        File.Move(tempPath, _filePath, overwrite: true);
    }

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Mtc.exe Path
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

    public string MtcExePath
    {
        get => _settings.MtcExePath;
        set
        {
            _settings.MtcExePath = value;
            RequestSave();
        }
    }

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Obsidian Vault Export
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

    public string ObsidianExportPath
    {
        get => _settings.ObsidianExportPath;
        set
        {
            _settings.ObsidianExportPath = value;
            RequestSave();
        }
    }

    public bool ObsidianExportEnabled
    {
        get => _settings.ObsidianExportEnabled;
        set
        {
            _settings.ObsidianExportEnabled = value;
            RequestSave();
        }
    }

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Tagging Files
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // settings.json itself cannot move - it is where these paths are remembered.

    public static readonly string DefaultTagsCsvPath = Path.Combine(FileSystem.AppDataDirectory, "tags.csv");
    public static readonly string DefaultExplorerRulesCsvPath = Path.Combine(FileSystem.AppDataDirectory, "explorer-processes.csv");

    public string TagsCsvPath
    {
        get => OrDefault(_settings.TagsCsvPath, DefaultTagsCsvPath);
        set
        {
            _settings.TagsCsvPath = StoredPath(value, DefaultTagsCsvPath);
            RequestSave();
        }
    }

    public string ExplorerRulesCsvPath
    {
        get => OrDefault(_settings.ExplorerRulesCsvPath, DefaultExplorerRulesCsvPath);
        set
        {
            _settings.ExplorerRulesCsvPath = StoredPath(value, DefaultExplorerRulesCsvPath);
            RequestSave();
        }
    }

    private static string OrDefault(string path, string fallback) =>
        string.IsNullOrWhiteSpace(path) ? fallback : path;

    // The default is stored as "", so it keeps following the app data folder rather than
    // being frozen to whatever that resolved to on the day it was saved
    private static string StoredPath(string path, string fallback) =>
        string.IsNullOrWhiteSpace(path) || PathsEqual(path, fallback) ? "" : path;

    public static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Statistics Arrangement
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Written as soon as they change: the Statistics page has no Save button to wait for.

    public IReadOnlyList<string> StatisticsTagOrder
    {
        get => _settings.StatisticsTagOrder;
        set
        {
            _settings.StatisticsTagOrder = value.ToList();
            RequestSave();
        }
    }

    public IReadOnlyList<string> StatisticsHiddenTags
    {
        get => _settings.StatisticsHiddenTags;
        set
        {
            _settings.StatisticsHiddenTags = value.ToList();
            RequestSave();
        }
    }

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Tag Colors
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

    // Expose all Tag Colors as ReadOnly
    public IReadOnlyDictionary<string, string> TagColors => _settings.TagColors;

    // Get or Create Color for a Tag
    public string GetTagColor(string tag)
    {
        if (_settings.TagColors.TryGetValue(tag, out string? color))
            return color;

        // Auto-assign a random color and save it
        color = $"#{Random.Shared.Next(256):X2}{Random.Shared.Next(256):X2}{Random.Shared.Next(256):X2}";
        _settings.TagColors[tag] = color;
        RequestSave();
        return color;
    }

    // Change Value of a color to a specified amount
    public string VaryColor(string hex, float value)
    {
        var color = SKColor.Parse(hex);
        color.ToHsv(out float h, out float s, out float v);
        v = Math.Clamp(value, 0f, 100f);
        return SKColor.FromHsv(h, s, v).ToString();
    }

    // GitHub-style shade ramp for one tag: index 0 is "nothing tracked" (a theme-neutral gray),
    // 1..steps run pale -> the tag's own color, which is the highest step exactly.
    // VaryColor cannot do this on its own: it only moves HSV value, so a pale step would come out
    // as a washed-out bright color rather than a tint. Saturation is lifted and value dropped
    // together here instead.
    public string[] BuildTagRamp(string hex, int steps)
    {
        var ramp = new string[steps + 1];
        ramp[0] = Application.Current?.ActualThemeVariant == ThemeVariant.Dark ? "#2D333B" : "#EBEDF0";

        var color = SKColor.Parse(hex);
        color.ToHsv(out float h, out float s, out float v);

        for (int i = 1; i <= steps; i++)
        {
            float t = i / (float)steps;
            float si = Math.Clamp(s * (0.25f + (0.75f * t)), 0f, 100f);
            float vi = Math.Clamp(v + ((100f - v) * (1f - t) * 0.75f), 0f, 100f);
            ramp[i] = SKColor.FromHsv(h, si, vi).ToString();
        }

        return ramp;
    }

    // Set a new Color
    public void SetTagColor(string tag, string color)
    {
        _settings.TagColors[tag] = color;
        RequestSave();
    }

    public void DeleteTagColor(string tag)
    {
        _settings.TagColors.Remove(tag);
        RequestSave();
    }
}
