using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Harbor.App.Avalonia.Views.Controls;

/// <summary>
///     Compact inline sparkline chart (no axes). Renders a polyline from
///     <see cref="Values"/> auto-scaled to the visible min/max range.
///     Used in the status bar to surface per-turn token-usage history at
///     a glance — ports the sparkline pattern from Opencode's
///     <c>progress-circle-v2.tsx</c> and Kilocode's console header.
/// </summary>
/// <remarks>
///     The control is dependency-property driven so it can be bound
///     directly from XAML. Re-renders only when <see cref="Values"/>
///     changes (AffectsRender flag). Drawing is done in
///     <see cref="Render"/> using a <see cref="StreamGeometry"/> for
///     sub-pixel-smooth strokes.
/// </remarks>
public partial class Sparkline : UserControl
{
    /// <summary>
    ///     Styled property for the data series to render. Re-rendering is
    ///     triggered automatically by <c>AffectsRender&lt;T&gt;</c>.
    /// </summary>
    public static readonly StyledProperty<IEnumerable<double>?> ValuesProperty =
        AvaloniaProperty.Register<Sparkline, IEnumerable<double>?>(nameof(Values));

    static Sparkline()
    {
        // AffectsRender wires up the invalidation: when Values changes,
        // Avalonia schedules a Render pass automatically.
        AffectsRender<Sparkline>(ValuesProperty);
    }

    /// <summary>Construct the sparkline.</summary>
    public Sparkline()
    {
        InitializeComponent();
    }

    /// <summary>
    ///     The data series. Null or fewer-than-2 points renders nothing.
    /// </summary>
    public IEnumerable<double>? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    /// <summary>
    ///     Brush used for the sparkline stroke. Defaults to
    ///     <c>MochaPeach</c> (output-token color) when available.
    /// </summary>
    public static readonly StyledProperty<IBrush?> StrokeBrushProperty =
        AvaloniaProperty.Register<Sparkline, IBrush?>(nameof(StrokeBrush));

    public IBrush? StrokeBrush
    {
        get => GetValue(StrokeBrushProperty);
        set => SetValue(StrokeBrushProperty, value);
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var values = Values?.ToList();
        if (values is null || values.Count < 2)
        {
            return;
        }

        double max = values.Max();
        double min = values.Min();
        double range = max - min;
        if (range < 0.0001)
        {
            // Flat line — center it vertically.
            range = 1;
            min = max - 0.5;
        }

        double w = Bounds.Width;
        double h = Bounds.Height;
        if (w <= 0 || h <= 0)
        {
            return;
        }

        double stepX = w / (values.Count - 1);

        // Resolve stroke brush: explicit > MochaPeach > fallback orange.
        IBrush brush = StrokeBrush
            ?? (Application.Current?.TryFindResource("MochaPeach", out var r) == true
                ? r as IBrush
                : null)
            ?? Brushes.OrangeRed;

        var pen = new Pen(brush, 1.3)
        {
            LineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            double y0 = h - ((values[0] - min) / range) * h;
            ctx.BeginFigure(new Point(0, y0), isFilled: false);
            for (int i = 1; i < values.Count; i++)
            {
                double y = h - ((values[i] - min) / range) * h;
                ctx.LineTo(new Point(i * stepX, y));
            }
            ctx.EndFigure(isClosed: false);
        }
        context.DrawGeometry(null, pen, geometry);

        // Final-point dot — gives the "live" feel.
        double lastY = h - ((values[^1] - min) / range) * h;
        context.DrawEllipse(brush, null, new Rect(w - 3, lastY - 1.5, 3, 3));
    }
}
