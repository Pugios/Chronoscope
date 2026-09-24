using Avalonia;
using Avalonia.Controls;

namespace TimeViewer.Controls;

// A vertical stack laid out by each child's Order rather than by its position in Children.
//
// The Statistics page rearranges its tag cards through this: changing a card's Order moves it on
// screen while its container - and the charts inside - stay exactly where they are in the tree.
// Reordering the collection instead would unload and reload those charts, which disposes their
// series and redraws every graph from blank. Invisible children take no space, as in StackPanel.
public class OrderedStackPanel : Panel
{
    public static readonly AttachedProperty<int> OrderProperty =
        AvaloniaProperty.RegisterAttached<OrderedStackPanel, Control, int>("Order");

    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<OrderedStackPanel, double>(nameof(Spacing));

    public static int GetOrder(Control control) => control.GetValue(OrderProperty);
    public static void SetOrder(Control control, int value) => control.SetValue(OrderProperty, value);

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    static OrderedStackPanel()
    {
        AffectsParentMeasure<OrderedStackPanel>(OrderProperty);
        AffectsMeasure<OrderedStackPanel>(SpacingProperty);
    }

    private IEnumerable<Control> Stacked() =>
        Children.Where(c => c.IsVisible).OrderBy(GetOrder);

    protected override Size MeasureOverride(Size availableSize)
    {
        var childSize = availableSize.WithHeight(double.PositiveInfinity);
        double width = 0, height = 0;
        int count = 0;

        foreach (var child in Children)
            child.Measure(childSize);

        foreach (var child in Stacked())
        {
            width = Math.Max(width, child.DesiredSize.Width);
            height += child.DesiredSize.Height;
            count++;
        }

        return new Size(width, height + Math.Max(0, count - 1) * Spacing);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double y = 0;
        foreach (var child in Stacked())
        {
            child.Arrange(new Rect(0, y, finalSize.Width, child.DesiredSize.Height));
            y += child.DesiredSize.Height + Spacing;
        }
        return finalSize;
    }
}
