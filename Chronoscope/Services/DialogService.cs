using Avalonia.Controls;
using Avalonia.Platform.Storage;
using FluentAvalonia.UI.Controls;

namespace Chronoscope;

// Everything a view model needs from the window it is shown in: the MAUI build got alerts and
// pickers from the page itself (DisplayAlertAsync, FilePicker, FolderPicker), a view model has
// no page, so they are routed through here instead.
public class DialogService
{
    private TopLevel? _host;

    // A ContentDialog cannot open while another is showing, but alerts can come from anywhere at
    // once (the refresh timer failing while a page reports its own error), so they queue here
    // the way MAUI's alerts did.
    private readonly SemaphoreSlim _alertGate = new(1, 1);

    // Set once the main window exists; until then every call is a quiet no-op
    public void Attach(TopLevel host) => _host = host;

    public async Task AlertAsync(string title, string message)
    {
        if (_host is null) return;

        var dialog = new FAContentDialog
        {
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                MaxWidth = 460
            },
            CloseButtonText = "OK",
            DefaultButton = FAContentDialogButton.Close
        };

        await _alertGate.WaitAsync();
        try
        {
            await dialog.ShowAsync(_host);
        }
        finally
        {
            _alertGate.Release();
        }
    }

    public async Task<string?> PickFileAsync(string title, params string[] extensions)
    {
        if (_host is null) return null;

        var result = await _host.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(title) { Patterns = extensions.Select(e => "*" + e).ToArray() },
                FilePickerFileTypes.All
            ]
        });

        return result.Count > 0 ? result[0].TryGetLocalPath() : null;
    }

    // Shows a folder in the system file manager
    public async Task OpenFolderAsync(string path)
    {
        if (_host is null || !Directory.Exists(path)) return;
        await _host.Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(path));
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        if (_host is null) return null;

        var result = await _host.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        });

        return result.Count > 0 ? result[0].TryGetLocalPath() : null;
    }
}
