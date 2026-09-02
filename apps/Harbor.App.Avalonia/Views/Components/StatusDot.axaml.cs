using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Markup.Xaml;
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
            InitializeComponent();
            UpdateState();
        }
    }

    private void UpdateState()
    {
        for (int i = 0; i < _states.Length; i++)
            PseudoClasses.Set(_states[i].pseudoClass, i == (int)State);

        var state = _states[(int)State];
        Dot.Classes.Set("running", state.pulse);

        if (global::Avalonia.Application.Current?.Resources[state.brushKey] is SolidColorBrush brush)
            Dot.Fill = brush;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
