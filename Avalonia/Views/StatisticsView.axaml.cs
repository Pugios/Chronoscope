using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using TimeViewer.ViewModels;

namespace TimeViewer.Views;

// The tag cards' arrange buttons run through here rather than straight to the view model, so
// every change can be animated:
//  - Hide: the card drops a little and fades out, then goes; the cards below slide up.
//  - Move up / down, Show: the cards are rebuilt in the new order (each chart has to be, see
//    StatisticsViewModel), then every card starts drawn where it USED to be and slides to where it
//    is now ("FLIP"). A card that was not there before, one brought back, fades in instead.
// Only transforms and opacity are animated, never layout, so this costs next to nothing next to
// the chart rebuild the change needs anyway. Transform animations run on the card itself: Avalonia
// drives the card's RenderTransform from there, and refuses to animate a transform directly.
public partial class StatisticsView : UserControl
{
    static readonly TimeSpan HideDuration = TimeSpan.FromMilliseconds(180);
    static readonly TimeSpan MoveDuration = TimeSpan.FromMilliseconds(260);
    const double HideDrop = 24;

    private bool _animating;

    public StatisticsView()
    {
        InitializeComponent();
        TagCards.AddHandler(Button.ClickEvent, OnCardButtonClick);
        HiddenChips.AddHandler(Button.ClickEvent, OnShowClick);
    }

    private StatisticsViewModel? ViewModel => DataContext as StatisticsViewModel;

    private async void OnCardButtonClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm || e.Source is not Button button
            || button.DataContext is not TagStatistics card || _animating)
            return;

        e.Handled = true;
        _animating = true;
        try
        {
            var before = CardPositions();

            if (button.Classes.Contains("Hide"))
            {
                if (CardFor(card.Tag) is { } leaving)
                    await AnimateOut(leaving);
                vm.HideTagCommand.Execute(card);
            }
            else if (button.Classes.Contains("MoveUp"))
                vm.MoveTagUpCommand.Execute(card);
            else if (button.Classes.Contains("MoveDown"))
                vm.MoveTagDownCommand.Execute(card);
            else
                return;

            await SlideFrom(before);
        }
        finally
        {
            _animating = false;
        }
    }

    private async void OnShowClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm || e.Source is not Button { DataContext: HiddenTag hidden } || _animating)
            return;

        e.Handled = true;
        _animating = true;
        try
        {
            var before = CardPositions();
            vm.ShowTagCommand.Execute(hidden);
            await SlideFrom(before);
        }
        finally
        {
            _animating = false;
        }
    }

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Positions
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

    private IEnumerable<Control> Cards() => TagCards.GetRealizedContainers();

    private Control? CardFor(string tag) =>
        Cards().FirstOrDefault(c => c.DataContext is TagStatistics s && s.Tag == tag);

    private Dictionary<string, double> CardPositions() =>
        Cards()
            .Where(c => c.DataContext is TagStatistics)
            .ToDictionary(c => ((TagStatistics)c.DataContext!).Tag, c => c.Bounds.Y);

    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
    // Animations
    // %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

    private static Task AnimateOut(Control card)
    {
        var shift = new TranslateTransform();
        card.RenderTransform = shift;

        var drop = new Animation
        {
            Duration = HideDuration,
            Easing = new CubicEaseIn(),
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(TranslateTransform.YProperty, 0.0) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(TranslateTransform.YProperty, HideDrop) } }
            }
        };

        return Task.WhenAll(drop.RunAsync(card), Fade(card, 1, 0, HideDuration, new CubicEaseIn()));
    }

    // Waits for the rebuilt cards to be laid out, then plays each one from its old position.
    //
    // Three steps, because the rebuild is the expensive part: every card's charts are new, and
    // building and first-drawing them keeps the UI thread busy for longer than the slide lasts.
    // Started straight away, the animation would be over before the first frame showed it. So the
    // cards are first parked where they used to be (nothing visibly jumps), the charts are left to
    // settle, and only then does everything slide into place.
    private async Task SlideFrom(Dictionary<string, double> before)
    {
        await NextLayout();

        var moves = new List<(Control Card, double Offset)>();
        var arrivals = new List<Control>();
        foreach (var card in Cards())
        {
            if (card.DataContext is not TagStatistics stats) continue;

            if (!before.TryGetValue(stats.Tag, out double oldY))
            {
                // Newly shown: nothing to slide from, so it fades in where it lands
                card.Opacity = 0;
                arrivals.Add(card);
                continue;
            }

            double offset = oldY - card.Bounds.Y;
            if (Math.Abs(offset) < 0.5) continue;

            card.RenderTransform = new TranslateTransform(0, offset);
            moves.Add((card, offset));
        }

        if (moves.Count == 0 && arrivals.Count == 0) return;

        await Settle();

        var runs = new List<Task>();
        foreach (var (card, offset) in moves)
        {
            var slide = new Animation
            {
                Duration = MoveDuration,
                Easing = new CubicEaseOut(),
                FillMode = FillMode.Forward,
                Children =
                {
                    new KeyFrame { Cue = new Cue(0), Setters = { new Setter(TranslateTransform.YProperty, offset) } },
                    new KeyFrame { Cue = new Cue(1), Setters = { new Setter(TranslateTransform.YProperty, 0.0) } }
                }
            };
            runs.Add(slide.RunAsync(card));
        }
        foreach (var card in arrivals)
            runs.Add(Fade(card, 0, 1, MoveDuration, new CubicEaseOut()));

        await Task.WhenAll(runs);

        // Hand the cards back clean: no leftover transform or local opacity on them
        foreach (var (card, _) in moves) card.RenderTransform = null;
        foreach (var card in arrivals) card.ClearValue(OpacityProperty);
    }

    // Lets the rebuilt charts do their deferred first update (LiveCharts batches it behind a short
    // throttle), then waits for the UI thread to go idle and one more frame to be drawn
    private async Task Settle()
    {
        await Task.Delay(80);
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);

        var frame = new TaskCompletionSource();
        if (TopLevel.GetTopLevel(this) is { } top)
            top.RequestAnimationFrame(_ => frame.TrySetResult());
        else
            frame.TrySetResult();
        await frame.Task;
    }

    private static Task Fade(Control control, double from, double to, TimeSpan duration, Easing easing) =>
        new Animation
        {
            Duration = duration,
            Easing = easing,
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, from) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, to) } }
            }
        }.RunAsync(control);

    // The cards are replaced by new containers when the list changes; their positions exist only
    // once the next layout pass has run
    private Task NextLayout()
    {
        var done = new TaskCompletionSource();
        void OnLayout(object? s, EventArgs e)
        {
            TagCards.LayoutUpdated -= OnLayout;
            done.TrySetResult();
        }
        TagCards.LayoutUpdated += OnLayout;
        TagCards.InvalidateMeasure();
        return done.Task;
    }
}
