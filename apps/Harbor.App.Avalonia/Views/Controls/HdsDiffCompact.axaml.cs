using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Harbor.App.Avalonia.ViewModels;
using System.Collections.ObjectModel;

namespace Harbor.App.Avalonia.Views.Controls;

public sealed partial class HdsDiffCompact : UserControl
{
    public static readonly StyledProperty<string> DiffTextProperty =
        AvaloniaProperty.Register<HdsDiffCompact, string>(nameof(DiffText), string.Empty);

    public static readonly StyledProperty<int> MaxLinesProperty =
        AvaloniaProperty.Register<HdsDiffCompact, int>(nameof(MaxLines), 6);

    public string DiffText
    {
        get => GetValue(DiffTextProperty);
        set => SetValue(DiffTextProperty, value);
    }

    public int MaxLines
    {
        get => GetValue(MaxLinesProperty);
        set => SetValue(MaxLinesProperty, value);
    }

    public ObservableCollection<DiffLine> Lines { get; } = new();

    public event EventHandler? ExpandRequested;

    public HdsDiffCompact()
    {
        InitializeComponent();
    }

    static HdsDiffCompact()
    {
        DiffTextProperty.Changed.AddClassHandler<HdsDiffCompact>((control, _) => control.UpdateLines());
        MaxLinesProperty.Changed.AddClassHandler<HdsDiffCompact>((control, _) => control.UpdateLines());
    }

    private void UpdateLines()
    {
        Lines.Clear();

        IBrush secondary = TryGetBrush("TextSecondaryBrush") ?? FallbackBrush;
        IBrush tertiary = TryGetBrush("TextTertiaryBrush") ?? FallbackBrush;

        foreach (WordDiffLine display in WordDiffHighlight.Build(DiffText, MaxLines))
        {
            if (string.Equals(display.RawText, WordDiffHighlight.TruncationText, StringComparison.Ordinal))
            {
                Lines.Add(new DiffLine(
                    display.RawText,
                    tertiary,
                    [new DiffRun(display.RawText, tertiary)]));
                continue;
            }

            IBrush lineBrush = LineBrush(display.RawText) ?? secondary;
            var runs = new List<DiffRun>(display.Runs.Count);
            foreach (WordDiffRun run in display.Runs)
            {
                // K13: a changed word keeps the line's colour; a shared word
                // (and the row prefix) drops back to the neutral one.
                runs.Add(new DiffRun(run.Text, run.Emphasized ? lineBrush : secondary));
            }

            Lines.Add(new DiffLine(display.RawText, lineBrush, runs));
        }
    }

    private IBrush? LineBrush(string line)
    {
        if (line.Length == 0)
        {
            return TryGetBrush("TextSecondaryBrush");
        }

        return line[0] switch
        {
            '+' => TryGetBrush("ChatToolResultBrush") ?? TryGetBrush("TextSecondaryBrush"),
            '-' => TryGetBrush("ChatErrorBrush") ?? TryGetBrush("TextSecondaryBrush"),
            '@' => TryGetBrush("AccentBrush") ?? TryGetBrush("TextSecondaryBrush"),
            _ => TryGetBrush("TextSecondaryBrush"),
        };
    }

    private static SolidColorBrush FallbackBrush { get; } =
        new(Color.FromRgb(0x80, 0x80, 0x80));

    private IBrush? TryGetBrush(string key) => ThemeBrushResolver.Resolve(key);

    private void RootBorder_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            ExpandRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }
}

/// <summary>One display row of the compact diff.</summary>
public sealed record DiffLine(string Text, IBrush Brush, IReadOnlyList<DiffRun> Runs);

/// <summary>One coloured run inside a <see cref="DiffLine" /> (K13).</summary>
public sealed record DiffRun(string Text, IBrush Brush);
