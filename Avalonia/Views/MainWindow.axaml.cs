using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
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
        AddHandler(PointerPressedEvent, OnTitleBarPressed, RoutingStrategies.Tunnel);
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

    // Moving the window by its title bar.
    //
    // On Windows FAAppWindow draws its own title bar inside the window, and Avalonia 12 only lets
    // Windows drag where the element under the pointer is marked as title bar. When that native
    // path does not take the press, it arrives here as an ordinary click on the strip above the
    // page instead, and the window could not be moved at all. So a left press on that strip -
    // anywhere outside the page content - starts the move explicitly, and a double click
    // maximizes or restores, as a native title bar would.
    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!OperatingSystem.IsWindows() || TitleBar.ExtendsContentIntoTitleBar) return;

        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed || point.Position.Y >= TitleBar.Height) return;

        // The page itself (navigation pane included) keeps its clicks, and so do the caption
        // buttons (minimize, maximize, close) that share the strip
        if (e.Source is Visual source && (source == NavView || NavView.IsVisualAncestorOf(source)
                                          || IsCaptionControl(source)))
            return;

        e.Handled = true;
        if (e.ClickCount == 2)
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else
            BeginMoveDrag(e);
    }

    private static bool IsCaptionControl(Visual visual)
    {
        for (Visual? v = visual; v is not null; v = v.GetVisualParent())
        {
            if (v is Button) return true;

            // Caption buttons and the resize edges have roles of their own; leave them be
            var role = Avalonia.Controls.Chrome.WindowDecorationProperties.GetElementRole(v);
            if (role is not (WindowDecorationsElementRole.None
                or WindowDecorationsElementRole.DecorationsElement
                or WindowDecorationsElementRole.User
                or WindowDecorationsElementRole.TitleBar))
                return true;
        }
        return false;
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
