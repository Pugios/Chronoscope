using System.ComponentModel;
using Avalonia.Input;
using Avalonia.Interactivity;
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

        // Tunnel, and handled events too: the thumb buttons must work wherever the pointer is,
        // including over buttons, grids and charts that would otherwise swallow the press
        AddHandler(PointerPressedEvent, OnThumbButtonPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
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
                "Tags" => AppSection.Tags,
                _ => AppSection.Day
            };

        await ViewModel.NavigateToSectionAsync(section);
    }

    // The mouse's thumb buttons: XButton1 is Back, XButton2 is Forward
    private async void OnThumbButtonPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ViewModel is null) return;

        switch (e.GetCurrentPoint(this).Properties.PointerUpdateKind)
        {
            case PointerUpdateKind.XButton1Pressed:
                e.Handled = true;
                await ViewModel.GoBackAsync();
                break;
            case PointerUpdateKind.XButton2Pressed:
                e.Handled = true;
                await ViewModel.GoForwardAsync();
                break;
        }
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
            AppSection.Tags => TagsItem,
            AppSection.Settings => NavView.SettingsItem,
            _ => DayItem
        };
    }
}
