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
        var random = new Random();
        color = Color.FromRgb(
            (byte) random.Next(0, 255),
            (byte) random.Next(0, 255),
            (byte) random.Next(0, 255)).ToHex();
        _settings.TagColors[tag] = color;
        _ = SaveAsync();
        return color;
    }

    // Change Value of a color to a specified amount
    public string ChangeColor(string hex, float value)
    {
        var color = SKColor.Parse(hex);
        color.ToHsv(out float h, out float s, out float v);
        v = Math.Min(100f, value);
        return SKColor.FromHsv(h, s, v).ToString();
    }

    // Set a new Color
    public void SetTagColor(string tag, string color)
    {
        _settings.TagColors[tag] = color;
    }
}
