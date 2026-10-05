using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Chronoscope.Controls;

namespace Chronoscope.Views;

public partial class DayView : UserControl
{
    const double MinPieSize = 220;
    const double MaxPieSize = 520;
    const double LegendScrollBarRoom = 16; // keeps the last row clear of the horizontal scrollbar

    public DayView()
    {
        InitializeComponent();

        // The pie is as tall as its card allows; the legend beside it borrows the same height,
        // so the two can never drift apart
        PieCard.SizeChanged += (_, e) =>
        {
            double inner = e.NewSize.Height - PieCard.Padding.Top - PieCard.Padding.Bottom - 2;
            double size = Math.Clamp(inner, MinPieSize, MaxPieSize);
            PieHost.Width = size;
            PieHost.Height = size;
            // From the card's real height, not the pie's: on a very short window the pie stops at
            // its minimum and overflows, and a legend that followed it would be clipped
            Legend.Height = Math.Max(Math.Min(size, inner) - LegendScrollBarRoom, 26);
        };

        Timeline.PointerMoved += OnTimelinePointerMoved;
        Timeline.PointerExited += (_, _) => TimelineTooltip.IsVisible = false;
    }

    // Follows the pointer, but keeps the card inside the bar
    private void OnTimelinePointerMoved(object? sender, PointerEventArgs e)
    {
        var hovered = Timeline.HoveredSlice;
        if (hovered is null)
        {
            // over a gap or below the bar
            TimelineTooltip.IsVisible = false;
            return;
        }

        var position = e.GetPosition(Timeline);

        // The document open at the pointer. Rows are in time order, so the last one started by
        // then is it; that also covers the short gaps the merge absorbed, which belong to the
        // row before them.
        var time = Timeline.TimeAt(position.X);
        var document = hovered.Documents.LastOrDefault(d => d.Start <= time);

        TooltipName.Text = hovered.Process;
        ShowLine(TooltipTitleLine, TooltipTitle, document?.Title);
        ShowLine(TooltipDocNameLine, TooltipDocName, document?.DocName);
        ShowLine(TooltipDomainLine, TooltipDomain, document?.Domain);
        TooltipRange.Text = $"{hovered.Start:HH:mm} - {hovered.End:HH:mm}  ({(hovered.End - hovered.Start):h\\:mm})";
        TooltipDot.Fill = new ImmutableSolidColorBrush(hovered.Color);
        TimelineTooltip.IsVisible = true;

        TimelineTooltip.Measure(Size.Infinity);
        var card = TimelineTooltip.DesiredSize;

        double x = position.X + 14;
        if (x + card.Width > Timeline.Bounds.Width) x = position.X - card.Width - 14;

        Canvas.SetLeft(TimelineTooltip, Math.Max(0, x));
        Canvas.SetTop(TimelineTooltip, Math.Max(-card.Height - 6, position.Y - card.Height - 10));
    }

    // One optional line of the hover card: hidden when empty, and scrolled from the start again
    // only when the text actually changes - every pointer move lands here, and restarting on
    // each one would keep a long title pinned at its beginning
    private static void ShowLine(Marquee line, TextBlock text, string? value)
    {
        line.IsVisible = !string.IsNullOrEmpty(value);
        if (text.Text == value) return;

        text.Text = value;
        line.Restart();
    }
}
