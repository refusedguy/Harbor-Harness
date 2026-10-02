using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Media;
using Harbor.Desktop.Abstractions.Models;

namespace Harbor.App.Avalonia.Views.Components;

[PseudoClasses(":idle", ":running", ":thinking", ":queued", ":done", ":error")]
public sealed partial class StatusDot : UserControl
{
    public static readonly StyledProperty<SessionDotState> StateProperty =
        AvaloniaProperty.Register<StatusDot, SessionDotState>(
            nameof(State), SessionDotState.Idle);

    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<StatusDot, double>(
            nameof(Size), 8, coerce: (_, v) => Math.Max(4, v));

    public SessionDotState State
    {
        get => GetValue(StateProperty);
        set
        {
            SetValue(StateProperty, value);
            UpdateState();
        }
    }

    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    private static readonly (string pseudoClass, string brushKey, bool pulse)[] _states =
    {
        (":idle",    "StateIdleBrush",    false),
        (":running", "StateRunningBrush", true),
        (":thinking","StateInfoBrush",    true),
        (":queued",  "StatePendingBrush", false),
        (":done",    "StatusSuccessBrush",false),
        (":error", "StatusErrorBrush", false),
    };

    public StatusDot()
    {
        if (global::Avalonia.Application.Current is not null)
        {
            // `Dot` is wired by the generated InitializeComponent overload, which
            // also takes the optional loadXaml flag. It used to be shadowed by a
            // hand-written copy that only called AvaloniaXamlLoader.Load: different
            // signature, so the tree compiled, but the parameterless call bound to
            // the copy and `Dot` stayed null. #973.
            InitializeComponent();
            UpdateState();
        }
    }

    private void UpdateState()
    {
        for (int i = 0; i < _states.Length; i++)
            PseudoClasses.Set(_states[i].pseudoClass, i == (int)State);

        // Null until `InitializeComponent` has run, which it has not when the
        // control is built without an Avalonia Application — the branch above
        // skips it — and the `State` setter calls this from a binding. The same
        // guard TypewriterStreamingText uses for `BlinkCursor`.
        if (Dot is not { } dot)
            return;

        var state = _states[(int)State];
        dot.Classes.Set("running", state.pulse);

        if (ThemeBrushResolver.Resolve(state.brushKey) is SolidColorBrush brush)
            dot.Fill = brush;
    }
}
