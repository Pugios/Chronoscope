using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using TimeViewer.ViewModels;
using TimeViewer.Views;

namespace TimeViewer;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // The composition root: the singletons MauiProgram registered, built by hand
            var settingsService = new SettingsService();
            var dataService = new DataService(settingsService);
            var vaultExportService = new VaultExportService(settingsService);
            var dialogs = new DialogService();

            var shell = new MainWindowViewModel(settingsService, dataService, vaultExportService, dialogs);
            var window = new MainWindow { DataContext = shell };
            dialogs.Attach(window);

            desktop.MainWindow = window;
            window.Opened += async (_, _) => await shell.StartAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
