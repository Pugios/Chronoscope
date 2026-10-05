using Avalonia;
using Avalonia.Controls;

namespace Chronoscope.Controls;

// Shows one line of text (its child, usually a TextBlock) at most as wide as it is allowed to
// be, and when the line is longer than that, scrolls it back and forth for as long as it is on
// screen: a pause at the start, scroll to the end, a pause, scroll back, repeat.
//
// The child is measured at its full width and slid left inside a clipped box, so the text is
// never trimmed. The offset is a function of the time since the scroll started rather than
// being stepped per frame, so a dropped frame cannot make it drift.
public class Marquee : Decorator
{
    const double PixelsPerSecond = 45;
    const double PauseSeconds = 1.2;

    private TimeSpan? _startedAt; // frame time the current scroll began; null = not yet
    private double _offset;
    private bool _running;

    public Marquee()
    {
        ClipToBounds = true;
    }

    // Start over from the beginning, e.g. because the text changed
    public void Restart()
    {
        _startedAt = null;
        _offset = 0;
        InvalidateArrange();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Child is null) return default;

        Child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
        var full = Child.DesiredSize;
        return new Size(Math.Min(full.Width, availableSize.Width), full.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Child is null) return finalSize;

        double overflow = Child.DesiredSize.Width - finalSize.Width;
        if (overflow > 0.5)
        {
            StartFrames();
        }
        else
        {
            _running = false;
            _startedAt = null;
            _offset = 0;
        }

        Child.Arrange(new Rect(-_offset, 0, Child.DesiredSize.Width, finalSize.Height));
        return finalSize;
    }

    private void StartFrames()
    {
        if (_running || TopLevel.GetTopLevel(this) is not { } topLevel) return;
        _running = true;
        topLevel.RequestAnimationFrame(OnFrame);
    }

    private void OnFrame(TimeSpan now)
    {
        if (!_running) return;

        // Hidden or removed: stop asking for frames, and start from the beginning next time
        // it is shown. Showing it again arranges it, which starts the frames again.
        if (!IsEffectivelyVisible || Child is null || TopLevel.GetTopLevel(this) is not { } topLevel)
        {
            _running = false;
            _startedAt = null;
            _offset = 0;
            return;
        }

        double overflow = Child.DesiredSize.Width - Bounds.Width;
        _startedAt ??= now;
        _offset = OffsetAt((now - _startedAt.Value).TotalSeconds, Math.Max(0, overflow));
        InvalidateArrange();

        topLevel.RequestAnimationFrame(OnFrame);
    }

    // Pause, forward, pause, back - one cycle, repeating
    private static double OffsetAt(double seconds, double overflow)
    {
        double travel = overflow / PixelsPerSecond;
        double t = seconds % (2 * (PauseSeconds + travel));

        if (t < PauseSeconds) return 0;
        t -= PauseSeconds;
        if (t < travel) return t * PixelsPerSecond;
        t -= travel;
        if (t < PauseSeconds) return overflow;
        t -= PauseSeconds;
        return overflow - (t * PixelsPerSecond);
    }
}
