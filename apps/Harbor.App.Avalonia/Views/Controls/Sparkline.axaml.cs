using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
namespace Harbor.App.Avalonia.Views.Controls;
/// <summary>
///     Compact inline sparkline chart with kinetic animation. Renders a polyline
///     from <see cref="Values" /> auto-scaled to the visible min/max range.
///     Used in the status bar to surface per-turn token-usage history at a
///     glance — ports the sparkline pattern from Opencode's
///     <c>progress-circle-v2.tsx</c> and Kilocode's console header.
/// </summary>
/// <remarks>
///     <para>
///         Kinetic mode: when values change, the line smoothly interpolates
///         from the previous state to the new state over ~250ms using a
///         <see cref="DispatcherTimer" /> at ~60fps. The endpoint dot pulses
///         gently to reinforce the "live" feel.
///     </para>
///     <para>
///         Gradient stroke: when <see cref="StrokeBrush" /> is a
///         <see cref="SolidColorBrush" />, the control automatically builds a
///         <see cref="LinearGradientBrush" /> that fades from the accent color
///         (left) to transparent (right), giving the line a "trailing off"
///         temporal feel.
///     </para>
/// </remarks>
public partial class Sparkline : UserControl
{
    private readonly DispatcherTimer _animationTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private List<double>? _previousValues;
    private List<double>? _targetValues;
    private List<double>? _displayValues;
    private double _animationProgress;
    private bool _isAnimating;
    private int _pulsePhase;

    public static readonly StyledProperty<IEnumerable<double>?> ValuesProperty =
        AvaloniaProperty.Register<Sparkline, IEnumerable<double>?>(nameof(Values));

    public static readonly StyledProperty<IBrush?> StrokeBrushProperty =
        AvaloniaProperty.Register<Sparkline, IBrush?>(nameof(StrokeBrush));

    static Sparkline()
    {
        AffectsRender<Sparkline>(ValuesProperty);
        AffectsRender<Sparkline>(StrokeBrushProperty);
    }

    public Sparkline()
    {
        InitializeComponent();
        _animationTimer.Tick += OnAnimationTick;
    }

    public IEnumerable<double>? Values
    {
        get => this.GetValue(ValuesProperty);
        set => this.SetValue(ValuesProperty, value);
    }

    public IBrush? StrokeBrush
    {
        get => this.GetValue(StrokeBrushProperty);
        set => this.SetValue(StrokeBrushProperty, value);
    }

    private void OnValuesChanged(IEnumerable<double>? newValues)
    {
        if (newValues is null) return;
        var list = newValues.ToList();
        if (list.Count < 2) return;

        _previousValues = _displayValues?.Count == list.Count
            ? new List<double>(_displayValues)
            : new List<double>(list);
        _targetValues = list;
        _animationProgress = 0;
        _isAnimating = true;
        _animationTimer.Start();
        InvalidateVisual();
    }

    private void OnAnimationTick(object? sender, EventArgs e)
    {
        if (!_isAnimating)
        {
            _animationTimer.Stop();
            return;
        }

        _animationProgress += 0.18;
        if (_animationProgress >= 1.0)
        {
            _animationProgress = 1.0;
            _isAnimating = false;
            _animationTimer.Stop();
        }

        _displayValues = new List<double>(_targetValues!.Count);
        for (int i = 0; i < _targetValues.Count; i++)
        {
            double prev = _previousValues is { } pv && _previousValues!.Count > i ? pv[i] : _targetValues[i];
            double target = _targetValues[i];
            _displayValues.Add(prev + (target - prev) * EaseOutCubic(_animationProgress));
        }

        _pulsePhase = (_pulsePhase + 1) % 60;
        InvalidateVisual();
    }

    private static double EaseOutCubic(double t) => 1 - Math.Pow(1 - t, 3);

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var values = _displayValues ?? Values?.ToList();
        if (values is null || values.Count < 2)
        {
            return;
        }

        double max = values.Max();
        double min = values.Min();
        double range = max - min;
        if (range < 0.0001)
        {
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

        var baseBrush = StrokeBrush
                    ?? (global::Avalonia.Application.Current?.TryFindResource("StateWarningBrush", out object? r) == true
                        ? r as IBrush
                        : null)
                    ?? Brushes.OrangeRed;

        IBrush brush = baseBrush;
        if (baseBrush is SolidColorBrush solid)
        {
            brush = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(solid.Color, 0),
                    new GradientStop(Color.FromArgb(0, solid.Color.R, solid.Color.G, solid.Color.B), 1)
                }
            };
        }

        var pen = new Pen(brush, 1.3)
        {
            LineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            double y0 = h - (values[0] - min) / range * h;
            ctx.BeginFigure(new Point(0, y0), false);
            for (int i = 1; i < values.Count; i++)
            {
                double y = h - (values[i] - min) / range * h;
                ctx.LineTo(new Point(i * stepX, y));
            }
            ctx.EndFigure(isClosed: false);
        }
        context.DrawGeometry(null, pen, geometry);

        // Pulsing endpoint dot.
        double lastY = h - (values[^1] - min) / range * h;
        double pulse = 0.5 + 0.5 * Math.Sin(_pulsePhase * Math.PI / 30.0);
        double dotRadius = 1.5 + pulse * 1.0;
        context.DrawEllipse(brush, null, new Rect(w - dotRadius, lastY - dotRadius, dotRadius * 2, dotRadius * 2));
    }
}
