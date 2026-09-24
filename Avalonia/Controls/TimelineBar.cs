using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace TimeViewer.Controls;

// The 24h activity bar under the pie: one coloured block per (merged) activity, clock labels
// beneath, and a hover card naming the block under the pointer.
//
// The MAUI build drew this with LiveCharts as one stacked row series per block, padded with
// black spacer series for the gaps, and then had to hit test by scaling the pointer back to a
// time, because LiveCharts could not hit test a stacked segment's drawn shape. Drawing the blocks
// directly makes both the gaps and the hit test trivial: x is simply time.
public class TimelineBar : Control
{
    const int SeparatorHours = 2;             // clock-hour spacing of the axis labels
    const int LabelEdgeClearanceMinutes = 20; // drop a label this close to either end of the axis
    const double BarHeight = 56;
    const double LabelGap = 8;
    const double LabelFontSize = 11;
    const double BarRadius = 6;

    public static readonly StyledProperty<IReadOnlyList<TimelineSlice>?> SlicesProperty =
        AvaloniaProperty.Register<TimelineBar, IReadOnlyList<TimelineSlice>?>(nameof(Slices));

    public static readonly StyledProperty<DateTime> WindowStartProperty =
        AvaloniaProperty.Register<TimelineBar, DateTime>(nameof(WindowStart));

    // The empty track between activities
    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<TimelineBar, IBrush?>(nameof(TrackBrush));

    public static readonly StyledProperty<IBrush?> LabelBrushProperty =
        AvaloniaProperty.Register<TimelineBar, IBrush?>(nameof(LabelBrush));

    public static readonly StyledProperty<IBrush?> HighlightBrushProperty =
        AvaloniaProperty.Register<TimelineBar, IBrush?>(nameof(HighlightBrush));

    public static readonly DirectProperty<TimelineBar, TimelineSlice?> HoveredSliceProperty =
        AvaloniaProperty.RegisterDirect<TimelineBar, TimelineSlice?>(nameof(HoveredSlice), o => o.HoveredSlice);

    public IReadOnlyList<TimelineSlice>? Slices
    {
        get => GetValue(SlicesProperty);
        set => SetValue(SlicesProperty, value);
    }

    public DateTime WindowStart
    {
        get => GetValue(WindowStartProperty);
        set => SetValue(WindowStartProperty, value);
    }

    public IBrush? TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public IBrush? LabelBrush
    {
        get => GetValue(LabelBrushProperty);
        set => SetValue(LabelBrushProperty, value);
    }

    public IBrush? HighlightBrush
    {
        get => GetValue(HighlightBrushProperty);
        set => SetValue(HighlightBrushProperty, value);
    }

    private TimelineSlice? _hoveredSlice;
    public TimelineSlice? HoveredSlice
    {
        get => _hoveredSlice;
        private set => SetAndRaise(HoveredSliceProperty, ref _hoveredSlice, value);
    }

    static TimelineBar()
    {
        AffectsRender<TimelineBar>(SlicesProperty, WindowStartProperty, TrackBrushProperty,
            LabelBrushProperty, HighlightBrushProperty, HoveredSliceProperty);
        AffectsMeasure<TimelineBar>(SlicesProperty);
    }

    private DateTime WindowEnd => WindowStart.AddDays(1);

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 400 : availableSize.Width,
            BarHeight + LabelGap + LabelFontSize + 6);

    private double XFor(DateTime time, double width) =>
        (time - WindowStart).TotalSeconds / TimeSpan.FromDays(1).TotalSeconds * width;

    public override void Render(DrawingContext context)
    {
        double width = Bounds.Width;
        if (width <= 0) return;

        var barRect = new Rect(0, 0, width, BarHeight);

        // Clip the blocks to the rounded track, so the first and last block get round corners too
        using (context.PushClip(new RoundedRect(barRect, BarRadius)))
        {
            context.FillRectangle(TrackBrush ?? Brushes.Black, barRect);

            if (Slices is { } slices)
            {
                foreach (var slice in slices)
                {
                    double x0 = XFor(slice.Start, width);
                    double x1 = XFor(slice.End, width);
                    // Keep a very short block visible rather than dropping it below a pixel
                    if (x1 - x0 < 1) x1 = x0 + 1;

                    context.FillRectangle(new ImmutableSolidColorBrush(slice.Color),
                        new Rect(x0, 0, x1 - x0, BarHeight));
                }
            }

            if (HoveredSlice is { } hovered && HighlightBrush is { } highlight)
            {
                double x0 = XFor(hovered.Start, width);
                double x1 = Math.Max(XFor(hovered.End, width), x0 + 1);
                context.DrawRectangle(null, new Pen(highlight, 2),
                    new Rect(x0, 1, x1 - x0, BarHeight - 2).Deflate(0.5));
            }
        }

        DrawClockLabels(context, width);
    }

    // Label the axis in clock time. The window can start at any minute, so the ticks are walked
    // from the first round clock hour inside it rather than taken as offsets from its start.
    // Ticks too close to either edge are dropped, otherwise their label is half cut off.
    private void DrawClockLabels(DrawingContext context, double width)
    {
        var labelBrush = LabelBrush ?? Brushes.Gray;
        var tickPen = new Pen(labelBrush, 1) { DashStyle = null };
        var clearance = TimeSpan.FromMinutes(LabelEdgeClearanceMinutes);
        var windowStart = WindowStart;
        var windowEnd = WindowEnd;

        var firstTick = windowStart.Date.AddHours((windowStart.Hour / SeparatorHours + 1) * SeparatorHours);
        for (var tick = firstTick; tick < windowEnd; tick = tick.AddHours(SeparatorHours))
        {
            if (tick - windowStart < clearance || windowEnd - tick < clearance) continue;

            double x = Math.Round(XFor(tick, width)) + 0.5;
            context.DrawLine(tickPen, new Point(x, BarHeight + 1), new Point(x, BarHeight + 4));

            var text = new FormattedText(
                tick.ToString("HH:mm", CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                Typeface.Default,
                LabelFontSize,
                labelBrush);

            context.DrawText(text, new Point(x - (text.Width / 2), BarHeight + LabelGap - 2));
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        var position = e.GetPosition(this);

        if (position.Y < 0 || position.Y > BarHeight || Bounds.Width <= 0 || Slices is null)
        {
            HoveredSlice = null;
            return;
        }

        // Pixels -> seconds into the window -> the clock time under the pointer
        var time = WindowStart.AddSeconds(position.X / Bounds.Width * TimeSpan.FromDays(1).TotalSeconds);
        HoveredSlice = Slices.FirstOrDefault(s => time >= s.Start && time < s.End);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        HoveredSlice = null;
    }
}
