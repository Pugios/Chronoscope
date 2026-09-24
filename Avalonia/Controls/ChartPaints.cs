using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using LiveChartsCore.SkiaSharpView.Avalonia;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;

namespace TimeViewer.Controls;

// Gives a chart its OWN tooltip paints: <lvc:PieChart controls:ChartPaints.OwnTooltip="True" />
//
// Left alone, every chart draws its tooltip with the LiveCharts theme's single, app-wide
// TooltipTextPaint - and every chart that unloads disposes the paints it drew with, that one
// included. Navigating away from one chart while another measures its tooltip then hands Skia a
// disposed SKPaint, which takes the process down with an ExecutionEngineException (seen when
// reloading the day after visiting another page). A paint per chart is disposed only with it.
public static class ChartPaints
{
    public static readonly AttachedProperty<bool> OwnTooltipProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("OwnTooltip", typeof(ChartPaints));

    public static bool GetOwnTooltip(Control control) => control.GetValue(OwnTooltipProperty);
    public static void SetOwnTooltip(Control control, bool value) => control.SetValue(OwnTooltipProperty, value);

    static ChartPaints()
    {
        OwnTooltipProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            if (e.NewValue is not true) return;

            Apply(control);
            // Follow a light/dark switch while the app runs
            control.ActualThemeVariantChanged += (_, _) => Apply(control);
        });
    }

    private static void Apply(Control control)
    {
        bool dark = control.ActualThemeVariant == ThemeVariant.Dark;
        var text = new SolidColorPaint(dark ? new SKColor(0xF2, 0xF2, 0xF2) : new SKColor(0x1C, 0x1C, 0x1C));
        var background = new SolidColorPaint(dark ? new SKColor(0x2C, 0x2C, 0x2C, 0xF2) : new SKColor(0xFB, 0xFB, 0xFB, 0xF2));

        switch (control)
        {
            case PieChart pie:
                pie.TooltipTextPaint = text;
                pie.TooltipBackgroundPaint = background;
                break;
            case CartesianChart cartesian:
                cartesian.TooltipTextPaint = text;
                cartesian.TooltipBackgroundPaint = background;
                break;
        }
    }
}
