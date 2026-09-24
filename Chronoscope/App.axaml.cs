using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Chronoscope.ViewModels;
using Chronoscope.Views;

namespace Chronoscope;

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
            var vaultExportService = new VaultExportService(settingsService, dataService);
            var startupService = new StartupService();
            var dialogs = new DialogService();

            var shell = new MainWindowViewModel(settingsService, dataService, vaultExportService,
                startupService, dialogs);
            var window = new MainWindow { DataContext = shell };

            // Started with Windows and asked to stay out of the way: straight to the taskbar. The
            // app still loads as usual, so the first refresh (and vault export) happens right away.
            if (desktop.Args?.Contains(StartupService.MinimizedArg, StringComparer.OrdinalIgnoreCase) == true)
                window.WindowState = WindowState.Minimized;
            dialogs.Attach(window);

            desktop.MainWindow = window;
            window.Opened += async (_, _) => await shell.StartAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
