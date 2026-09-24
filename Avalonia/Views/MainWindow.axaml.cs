using System.ComponentModel;
using FluentAvalonia.UI.Controls;
using FluentAvalonia.UI.Windowing;
using TimeViewer.ViewModels;

namespace TimeViewer.Views;

public partial class MainWindow : FAAppWindow
{
    public MainWindow()
    {
        InitializeComponent();
        NavView.SelectedItem = DayItem;
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
                vm.PropertyChanged += OnViewModelPropertyChanged;
        };
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private async void OnItemInvoked(object? sender, FANavigationViewItemInvokedEventArgs e)
    {
        if (ViewModel is null) return;

        var section = e.IsSettingsInvoked
            ? AppSection.Settings
            : (e.InvokedItemContainer?.Tag as string) switch
            {
                "Statistics" => AppSection.Statistics,
                _ => AppSection.Day
            };

        await ViewModel.NavigateToSectionAsync(section);
    }

    private async void OnBackRequested(object? sender, FANavigationViewBackRequestedEventArgs e)
    {
        if (ViewModel is not null) await ViewModel.GoBackAsync();
    }

    // Pages can leave their section themselves (Save returns to the day view), so the pane's
    // highlight follows the view model rather than only the clicks
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainWindowViewModel.Section) || ViewModel is null) return;

        NavView.SelectedItem = ViewModel.Section switch
        {
            AppSection.Statistics => StatisticsItem,
            AppSection.Settings => NavView.SettingsItem,
            _ => DayItem
        };
    }
}
