using Avalonia;
using Avalonia.Controls;

namespace Chronoscope.Controls;

// A list read in columns: children run down the first column until it is ColumnHeight tall,
// then continue at the top of the next. One column when everything fits in one; a second only
// for what overflows the first, so the second may well be the shorter one.
//
// Past MaxColumns full columns there is no room to the side any more: the children are then
// shared out evenly over MaxColumns columns, still read top to bottom, and the list grows
// taller - put it in a ScrollViewer for that case.
//
// Every child gets the same slot, as tall as the tallest and as wide as the widest, so the
// columns line up row for row. Made for the Trend page's tag chips beside its chart.
public class ColumnFlowPanel : Panel
{
    public static readonly StyledProperty<double> ColumnHeightProperty =
        AvaloniaProperty.Register<ColumnFlowPanel, double>(nameof(ColumnHeight), double.PositiveInfinity);

    public static readonly StyledProperty<int> MaxColumnsProperty =
        AvaloniaProperty.Register<ColumnFlowPanel, int>(nameof(MaxColumns), 2);

    public static readonly StyledProperty<double> RowSpacingProperty =
        AvaloniaProperty.Register<ColumnFlowPanel, double>(nameof(RowSpacing));

    public static readonly StyledProperty<double> ColumnSpacingProperty =
        AvaloniaProperty.Register<ColumnFlowPanel, double>(nameof(ColumnSpacing));

    public double ColumnHeight
    {
        get => GetValue(ColumnHeightProperty);
        set => SetValue(ColumnHeightProperty, value);
    }

    public int MaxColumns
    {
        get => GetValue(MaxColumnsProperty);
        set => SetValue(MaxColumnsProperty, value);
    }

    public double RowSpacing
    {
        get => GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    public double ColumnSpacing
    {
        get => GetValue(ColumnSpacingProperty);
        set => SetValue(ColumnSpacingProperty, value);
    }

    static ColumnFlowPanel()
    {
        AffectsMeasure<ColumnFlowPanel>(ColumnHeightProperty, MaxColumnsProperty, RowSpacingProperty, ColumnSpacingProperty);
    }

    private List<Control> Shown() => Children.Where(c => c.IsVisible).ToList();

    protected override Size MeasureOverride(Size availableSize)
    {
        var children = Shown();
        foreach (var child in children)
            child.Measure(Size.Infinity);

        var (slot, rows, columns) = Plan(children);
        if (children.Count == 0) return default;

        return new Size(
            (columns * slot.Width) + ((columns - 1) * ColumnSpacing),
            (rows * slot.Height) + ((rows - 1) * RowSpacing));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = Shown();
        var (slot, rows, _) = Plan(children);

        for (int i = 0; i < children.Count; i++)
        {
            int column = i / rows, row = i % rows;
            children[i].Arrange(new Rect(
                column * (slot.Width + ColumnSpacing),
                row * (slot.Height + RowSpacing),
                slot.Width, slot.Height));
        }
        return finalSize;
    }

    // The slot every child gets, and how many rows and columns the children need
    private (Size Slot, int Rows, int Columns) Plan(List<Control> children)
    {
        int count = children.Count;
        if (count == 0) return (default, 1, 0);

        var slot = new Size(children.Max(c => c.DesiredSize.Width), children.Max(c => c.DesiredSize.Height));

        // How many fit down one column: n slots and n - 1 gaps within ColumnHeight
        int perColumn = double.IsInfinity(ColumnHeight)
            ? count
            : Math.Max(1, (int)Math.Floor((ColumnHeight + RowSpacing) / (slot.Height + RowSpacing)));
        int maxColumns = Math.Max(1, MaxColumns);

        if (count <= perColumn)
            return (slot, count, 1);                                                    // one column
        if (count <= perColumn * maxColumns)
            return (slot, perColumn, (int)Math.Ceiling(count / (double)perColumn));    // full first column, the rest beside it

        int rows = (int)Math.Ceiling(count / (double)maxColumns);                      // too many: share out, grow taller
        return (slot, rows, maxColumns);
    }
}
