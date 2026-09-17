using SkiaSharp;
using SkiaSharp.Views.Maui;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace TimeViewer;

public class SettingsService
{
    private readonly string _filePath = Path.Combine(FileSystem.AppDataDirectory, "settings.json");
    private Random random = new Random();
    private AppSettings _settings = new();

    public async Task LoadAsync()
    {
        Debug.WriteLine($"FileSystem.AppDataDirectory: {FileSystem.AppDataDirectory}");
        if (!File.Exists(_filePath))
        {
            _settings = new AppSettings();
            return;
        }
        var json = await File.ReadAllTextAsync(_filePath);
        _settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
    }

    public async Task SaveAsync()
    {
        var json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(_filePath, json);
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
            _ = SaveAsync();
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
        if (_settings.TagColors.TryGetValue(tag, out string color))
            return color;

        // Auto-assign a random color and save it
        color = Color.FromRgb(
            (byte) random.Next(0, 255),
            (byte) random.Next(0, 255),
            (byte) random.Next(0, 255)).ToHex();
        _settings.TagColors[tag] = color;
        _ = SaveAsync();
        return color;
    }

    // Change Value of a color to a specified amount
    public string VaryColor(string hex, float value)
    {
        var color = SKColor.Parse(hex);
        color.ToHsv(out float h, out float s, out float v);
        v = Math.Min(100f, value);
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
        ramp[0] = Application.Current?.RequestedTheme == AppTheme.Dark ? "#2D333B" : "#EBEDF0";

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
        _ = SaveAsync();
    }

    public void DeleteTagColor(string tag)
    {
        _settings.TagColors.Remove(tag);
        _ = SaveAsync();
    }
}
