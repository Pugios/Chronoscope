using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;

namespace Chronoscope.Controls;

// The day legend's layout: columns filled top to bottom, growing to the right, like a vertical
// WrapPanel - except that it breaks between TAGS rather than anywhere. A tag moves to the next
// column whole when its processes would not fit beneath it, so no column ends on a tag header
// cut off from its processes. A tag too long for any column cannot be kept together, so that one
// simply flows on into the next column (but never leaves its header alone at the bottom).
public class LegendColumnsPanel : Panel
{
    private readonly List<Rect> _slots = new();

    // The item itself, from its container: Content is set as soon as the container is realized,
    // DataContext only once it is attached, which can be after the first measure
    private static bool IsTag(Control child) =>
        (child is ContentPresenter { Content: LegendItem item } ? item : child.DataContext as LegendItem)
            is { IsTag: true };

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

        for (int i = 0; i < children.Count; i++)
        {
            var child = children[i];
            double h = child.DesiredSize.Height;

            if (IsTag(child) && y > 0)
            {
                // The whole group: this tag and every process up to the next tag
                double group = h;
                for (int j = i + 1; j < children.Count && !IsTag(children[j]); j++)
                    group += children[j].DesiredSize.Height;

                // Header plus its first process, the least that may stay together
                double head = h + (i + 1 < children.Count && !IsTag(children[i + 1])
                    ? children[i + 1].DesiredSize.Height
                    : 0);

                bool fitsHere = y + group <= columnHeight;
                bool fitsFreshColumn = group <= columnHeight;

                if (!fitsHere && (fitsFreshColumn || y + head > columnHeight))
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
