using Avalonia.Media;
using Avalonia.Media.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using LiveChartsCore;
using LiveChartsCore.Kernel.Sketches;
using LiveChartsCore.SkiaSharpView;

namespace Chronoscope;

// What the Trend page hands to its charts and its table. The numbers themselves come from
// TrendForecaster; these only carry them, already formatted, to the view.

// One ready-made cartesian chart: series, axes, and the shaded "this part is a forecast" section.
// Built in TrendViewModel because the labelers and tooltips close over the weeks they describe.
public class TrendChart
{
    public ISeries[] Series { get; init; } = [];
    public ICartesianAxis[] XAxes { get; init; } = [];
    public ICartesianAxis[] YAxes { get; init; } = [];
    public RectangularSection[] Sections { get; init; } = [];
}

// One tag: its row in the summary table and its entry in the list beside the per tag chart
public partial class TrendTagCard : ObservableObject
{
    // The forecast's key: a tag's name, or a group's TagGrouping.GroupKey. Title is what is shown.
    public string Tag { get; init; } = "";
    public string Title { get; init; } = "";
    public bool IsGroup { get; init; }
    public Color TagColor { get; init; } = Colors.Transparent;

    public string NowLabel { get; init; } = "";        // "6.5h / week"
    // Drawn as the icon set's right arrow, turned up for rising and down for falling. Not as a
    // character: the app's font has → but no diagonal arrows, so ↗ and ↘ came out as empty boxes.
    public TrendDirection Direction { get; init; }
    public ITransform ArrowTurn => new RotateTransform(Direction switch
    {
        TrendDirection.Rising => -45,
        TrendDirection.Falling => 45,
        _ => 0
    });
    public string DirectionLabel => Direction switch
    {
        TrendDirection.Rising => "Rising",
        TrendDirection.Falling => "Falling",
        _ => "Steady"
    };
    public string TrendLabel { get; init; } = "";      // "+0.4h / week each week"
    public string Last30Label { get; init; } = "";     // "25h"
    public string Next30Label { get; init; } = "";     // "≈ 28h"
    public string YearToDateLabel { get; init; } = ""; // "412h"
    public string YearEndLabel { get; init; } = "";    // "≈ 820h"

    // Whether this tag is drawn on the combined per tag chart; switched in the list beside it
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChipBackground))]
    public partial bool IsShown { get; set; }

    // The list's chip: filled with the line's own colour while the line is drawn, see-through
    // (the card's own background) while it is not
    public IBrush ChipBackground => IsShown ? new ImmutableSolidColorBrush(TagColor) : Brushes.Transparent;

    // Dark text on a light colour, light text on a dark one - by perceived brightness, which
    // weighs green far above blue (a yellow and a blue of equal RGB sum are nowhere near alike)
    public bool DarkText => (0.299 * TagColor.R) + (0.587 * TagColor.G) + (0.114 * TagColor.B) > 150;
}

// Which way a forecast is heading over the next four weeks; within ±10 % counts as steady
public enum TrendDirection { Steady, Rising, Falling }

// A choice in one of the header's drop-downs: how much history to fit, how far ahead to look
public sealed record TrendOption(int Weeks, string Label)
{
    public override string ToString() => Label;
}
