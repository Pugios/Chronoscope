using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Avalonia.Input;
using Avalonia.Interactivity;
using FluentAvalonia.UI.Controls;
using FluentAvalonia.UI.Windowing;
using Chronoscope.ViewModels;

namespace Chronoscope.Views;

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
        PageHost.AddHandler(PointerWheelChangedEvent, OnPageWheel, RoutingStrategies.Bubble);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.PropertyChanged += OnViewModelPropertyChanged;
                ShowPage(vm.CurrentPage);
            }
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
                "Trend" => AppSection.Trend,
                "Tags" => AppSection.Tags,
                _ => AppSection.Day
            };

        await ViewModel.NavigateToSectionAsync(section);

        // The pane highlights the clicked item at once. If leaving was cancelled (unsaved changes,
        // Cancel) the section never changed, so put the highlight back where the page still is.
        SyncSelection();
    }

    private void SyncSelection()
    {
        if (ViewModel is null) return;

        NavView.SelectedItem = ViewModel.Section switch
        {
            AppSection.Statistics => StatisticsItem,
            AppSection.Trend => TrendItem,
            AppSection.Tags => TagsItem,
            AppSection.Settings => NavView.SettingsItem,
            _ => DayItem
        };
    }

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Closing With Unsaved Changes
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Closing is held while the open page is asked (see MainWindowViewModel.ConfirmCloseAsync);
    // once it agrees, the window closes for real, this time without asking again.

    private bool _closeConfirmed;

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_closeConfirmed || e.Cancel || ViewModel is null) return;

        // The question is async and the close cannot wait for it: stop this close, ask, and
        // close again if the answer allows it
        e.Cancel = true;
        try
        {
            if (!await ViewModel.ConfirmCloseAsync()) return;
        }
        catch (Exception)
        {
            // A failed save must not trap the user in the app; the save reported nothing, so
            // keep the window open and let them try again
            return;
        }

        _closeConfirmed = true;
        Close();
    }

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Page Host
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // A kept page (IKeepAlive) is built once and stays in the host, hidden while another page is
    // shown: its charts are never unloaded, so there is nothing to rebuild on the way back and no
    // chart disposes paints a later one would still use. Any other page is built per visit and
    // removed when left, so it always starts fresh.

    private readonly Dictionary<ViewModelBase, Control> _keptViews = new();
    private readonly ViewLocator _viewLocator = new();

    private void ShowPage(ViewModelBase? page)
    {
        if (page is null) return;

        if (!_keptViews.TryGetValue(page, out var view))
        {
            view = _viewLocator.Build(page) ?? new TextBlock { Text = page.GetType().Name };
            view.DataContext = page;
            PageHost.Children.Add(view);
            if (page is IKeepAlive) _keptViews[page] = view;
        }

        foreach (var other in PageHost.Children.ToList())
        {
            if (other == view) continue;
            if (_keptViews.ContainsValue(other))
                other.IsVisible = false;
            else
                PageHost.Children.Remove(other);
        }

        view.IsVisible = true;
        FadeIn(view);
    }

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Scrolling from the Page Margins
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Every page keeps a margin around its content, and that margin lies outside the page's own
    // ScrollViewer - so the wheel did nothing between the navigation pane and the content, or
    // beside the header. Here, a wheel turn that reached the page host without anything having
    // scrolled scrolls the page instead. Bubbling and unhandled only: a table or a chart under the
    // pointer still scrolls itself first, as it should.

    // Pixels per wheel notch - what Avalonia's own ScrollViewer moves, so both feel the same
    const double WheelStep = 50;

    private void OnPageWheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.Handled) return;

        var page = PageHost.Children.FirstOrDefault(c => c.IsVisible);
        if (page is null || PageScroller(page) is not { } scroller) return;

        var offset = scroller.Offset;
        scroller.Offset = new Vector(offset.X - (e.Delta.X * WheelStep), offset.Y - (e.Delta.Y * WheelStep));
        e.Handled = true;
    }

    // The page's main scroller: the outermost visible ScrollViewer that has something to scroll.
    // Breadth first, so a table's or a chart's own scroller nested inside it never wins.
    private static ScrollViewer? PageScroller(Visual page)
    {
        var queue = new Queue<Visual>([page]);
        while (queue.Count > 0)
        {
            var visual = queue.Dequeue();
            if (visual is ScrollViewer { IsVisible: true } sv
                && (sv.Extent.Height > sv.Viewport.Height || sv.Extent.Width > sv.Viewport.Width))
                return sv;

            foreach (var child in visual.GetVisualChildren())
                queue.Enqueue(child);
        }
        return null;
    }

    private static readonly Animation PageFadeIn = new()
    {
        Duration = TimeSpan.FromMilliseconds(150),
        Easing = new CubicEaseOut(),
        Children =
        {
            new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, 0.0) } },
            new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, 1.0) } }
        }
    };

    private static void FadeIn(Control view) => _ = PageFadeIn.RunAsync(view);

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
        if (e.PropertyName == nameof(MainWindowViewModel.CurrentPage))
            ShowPage(ViewModel?.CurrentPage);

        if (e.PropertyName == nameof(MainWindowViewModel.Section))
            SyncSelection();
    }
}
