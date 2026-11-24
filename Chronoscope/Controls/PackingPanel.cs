using Avalonia;
using Avalonia.Controls;

namespace Chronoscope.Controls;

// Lays its children out like a WrapPanel, except that a tall child does not make its whole line
// tall: the children after it fill the space beside it in as many rows as fit there.
//
// The Tags page needs this for its board: a group box is several chips high, and in a WrapPanel
// the loose tags beside it got a single row and left the rest of that height empty.
//
// Each child, in order, goes to the highest spot - then the leftmost - where it fits within the
// width without overlapping anything placed before it. Children keep their order wherever there
// is room, and a small child may slot into a gap left beside an earlier, taller one.
public class PackingPanel : Panel
{
    public static readonly StyledProperty<double> ItemSpacingProperty =
        AvaloniaProperty.Register<PackingPanel, double>(nameof(ItemSpacing));

    public static readonly StyledProperty<double> LineSpacingProperty =
        AvaloniaProperty.Register<PackingPanel, double>(nameof(LineSpacing));

    public double ItemSpacing
    {
        get => GetValue(ItemSpacingProperty);
        set => SetValue(ItemSpacingProperty, value);
    }

    public double LineSpacing
    {
        get => GetValue(LineSpacingProperty);
        set => SetValue(LineSpacingProperty, value);
    }

    static PackingPanel()
    {
        AffectsMeasure<PackingPanel>(ItemSpacingProperty, LineSpacingProperty);
    }

    private IEnumerable<Control> Placed() => Children.Where(c => c.IsVisible);

    protected override Size MeasureOverride(Size availableSize)
    {
        var childSize = new Size(availableSize.Width, double.PositiveInfinity);
        foreach (var child in Placed())
            child.Measure(childSize);

        var rects = Pack(availableSize.Width, Placed().Select(c => c.DesiredSize).ToList());
        return Extent(rects);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = Placed().ToList();
        var rects = Pack(finalSize.Width, children.Select(c => c.DesiredSize).ToList());

        for (int i = 0; i < children.Count; i++)
            children[i].Arrange(rects[i]);

        return finalSize;
    }

    private static Size Extent(List<Rect> rects) =>
        rects.Count == 0
            ? default
            : new Size(rects.Max(r => r.Right), rects.Max(r => r.Bottom));

    // Every child's place, in child order. The spots tried are the corners the children placed so
    // far leave free: the panel's own top-left, the right edge of each placed child, and the line
    // below each - so a child can only ever land flush against the panel or a neighbour.
    private List<Rect> Pack(double width, List<Size> sizes)
    {
        double gapX = ItemSpacing, gapY = LineSpacing;
        // Unbounded (measured inside something that scrolls sideways): one long row
        if (double.IsInfinity(width)) width = sizes.Sum(s => s.Width + gapX);

        var placed = new List<Rect>(sizes.Count);
        foreach (var size in sizes)
        {
            var xs = new SortedSet<double> { 0 };
            var ys = new SortedSet<double> { 0 };
            foreach (var r in placed)
            {
                xs.Add(r.Right + gapX);
                ys.Add(r.Bottom + gapY);
            }

            Rect? spot = null;
            foreach (var y in ys)
            {
                foreach (var x in xs)
                {
                    // Too wide for what is left of the line - unless it is the line's start,
                    // where an over-wide child still has to go somewhere
                    if (x > 0 && x + size.Width > width) break;

                    var candidate = new Rect(x, y, size.Width, size.Height);
                    if (placed.All(r => !Overlaps(r, candidate, gapX, gapY)))
                    {
                        spot = candidate;
                        break;
                    }
                }
                if (spot is not null) break;
            }

            // Nothing free beside anything: start a fresh line below everything
            spot ??= new Rect(0, placed.Count == 0 ? 0 : placed.Max(r => r.Bottom) + gapY, size.Width, size.Height);
            placed.Add(spot.Value);
        }
        return placed;
    }

    // Overlap, counting the spacing as part of each child so neighbours keep their gap
    private static bool Overlaps(Rect a, Rect b, double gapX, double gapY) =>
        a.X < b.Right + gapX && b.X < a.Right + gapX &&
        a.Y < b.Bottom + gapY && b.Y < a.Bottom + gapY;
}
