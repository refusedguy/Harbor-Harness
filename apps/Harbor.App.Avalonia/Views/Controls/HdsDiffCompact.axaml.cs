using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
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
        if (string.IsNullOrEmpty(DiffText))
            return;

        var text = DiffText;
        var max = MaxLines;
        var lines = text.Split('\n');
        int count = 0;

        foreach (var raw in lines)
        {
            if (count >= max)
            {
                Lines.Add(new DiffLine("… diff truncated", TryGetBrush("TextTertiaryBrush") ?? new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80))));
                break;
            }

            string line = raw;
            if (line.EndsWith("\r", StringComparison.Ordinal))
                line = line[..^1];

            IBrush brush = line.Length > 0 ? line[0] switch
            {
                '+' => TryGetBrush("ChatToolResultBrush") ?? TryGetBrush("TextSecondaryBrush") ?? new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80)),
                '-' => TryGetBrush("ChatErrorBrush") ?? TryGetBrush("TextSecondaryBrush") ?? new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80)),
                '@' => TryGetBrush("AccentBrush") ?? TryGetBrush("TextSecondaryBrush") ?? new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80)),
                _ => TryGetBrush("TextSecondaryBrush") ?? new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80))
            } : TryGetBrush("TextSecondaryBrush") ?? new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80));

            Lines.Add(new DiffLine(line, brush));
            count++;
        }
    }

    private IBrush? TryGetBrush(string key)
    {
        return Application.Current?.Resources[key] as IBrush;
    }

    private void RootBorder_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            ExpandRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }
}

public sealed record DiffLine(string Text, IBrush Brush);
