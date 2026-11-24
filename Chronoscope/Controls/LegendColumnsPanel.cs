using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;

namespace Chronoscope.Controls;

// The day legend's layout: columns filled top to bottom, growing to the right, like a vertical
// WrapPanel - except that it breaks between BLOCKS rather than anywhere.
//
// Two levels of block, see LegendItem:
//  - a top-level block is a tag group (its name, its tags, their processes) or a tag standing on
//    its own (the tag and its processes). One moves to the next column whole when it would not
//    fit beneath what is there, so a group stays together in one column whenever it can.
//  - a group too tall for any column has to split, and then it splits between its tags: a tag
//    moves on whole with its processes where it fits, as a lone tag always did.
// Whatever cannot be kept together still never leaves a heading alone at the bottom of a column:
// a group's name keeps its first tag, a tag keeps its first process.
public class LegendColumnsPanel : Panel
{
    private readonly List<Rect> _slots = new();

    // The item itself, from its container: Content is set as soon as the container is realized,
    // DataContext only once it is attached, which can be after the first measure
    private static LegendItem? ItemOf(Control child) =>
        child is ContentPresenter { Content: LegendItem item } ? item : child.DataContext as LegendItem;

    private static bool StartsBlock(Control child) => ItemOf(child) is { StartsBlock: true };
    private static bool StartsTag(Control child) => ItemOf(child) is { StartsTag: true };

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children)
            child.Measure(Size.Infinity);

        Layout(availableSize.Height);

        double width = 0, height = 0;
        foreach (var slot in _slots)
        {
            width = Math.Max(width, slot.Right);
            height = Math.Max(height, slot.Bottom);
        }
        return new Size(width, double.IsInfinity(availableSize.Height) ? height : availableSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Layout(finalSize.Height);
        for (int i = 0; i < Children.Count; i++)
            Children[i].Arrange(_slots[i]);
        return finalSize;
    }

    private void Layout(double columnHeight)
    {
        _slots.Clear();
        var children = Children;
        if (children.Count == 0) return;

        double columnWidth = children.Max(c => c.DesiredSize.Width);
        if (double.IsInfinity(columnHeight) || columnHeight <= 0) columnHeight = double.MaxValue;

        double x = 0, y = 0;
        void NextColumn() { x += columnWidth; y = 0; }

        // The rows from i up to (not including) the next one that matches `ends`
        double HeightUntil(int i, Func<Control, bool> ends)
        {
            double total = children[i].DesiredSize.Height;
            for (int j = i + 1; j < children.Count && !ends(children[j]); j++)
                total += children[j].DesiredSize.Height;
            return total;
        }

        // The row and the one after it - the least a heading may be left with
        double WithNext(int i) =>
            children[i].DesiredSize.Height
            + (i + 1 < children.Count && !StartsBlock(children[i + 1]) ? children[i + 1].DesiredSize.Height : 0);

        for (int i = 0; i < children.Count; i++)
        {
            var child = children[i];
            double h = child.DesiredSize.Height;

            if (y > 0 && StartsBlock(child))
            {
                // A group or a lone tag, whole
                double block = HeightUntil(i, StartsBlock);
                bool fitsHere = y + block <= columnHeight;
                bool fitsFreshColumn = block <= columnHeight;

                if (!fitsHere && (fitsFreshColumn || y + WithNext(i) > columnHeight))
                    NextColumn();
            }
            else if (y > 0 && StartsTag(child))
            {
                // A tag inside a group that has to split: the tag with its processes, whole
                double tag = HeightUntil(i, StartsTag);
                bool fitsHere = y + tag <= columnHeight;
                bool fitsFreshColumn = tag <= columnHeight;

                if (!fitsHere && (fitsFreshColumn || y + WithNext(i) > columnHeight))
                    NextColumn();
            }
            else if (y > 0 && y + h > columnHeight)
            {
                NextColumn();
            }

            _slots.Add(new Rect(x, y, columnWidth, h));
            y += h;
        }
    }
}
