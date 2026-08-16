using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Harbor.Desktop.Abstractions.ViewModels;
namespace Harbor.App.Wpf.ViewModels;
/// <summary>
///     Token usage charts. Plots cumulative input/output tokens over the
///     last N turns using native WPF Shapes (no third-party chart library
///     required — keeps the build self-contained).
/// </summary>
public sealed partial class TokenUsageViewModel : ObservableObject
{

    /// <summary>Estimated total cost in USD.</summary>
    [ObservableProperty] private decimal _totalCost;

    /// <summary>Cumulative input tokens across the session.</summary>
    [ObservableProperty] private int _totalInputTokens;

    /// <summary>Cumulative output tokens across the session.</summary>
    [ObservableProperty] private int _totalOutputTokens;
    /// <summary>Construct a <see cref="TokenUsageViewModel" />.</summary>
    public TokenUsageViewModel()
    {
        Bars = new ObservableCollection<TokenBarViewModel>();
        TotalInputTokens = 2480;
        TotalOutputTokens = 1180;
        TotalCost = 0.0432m;
        SeedSampleBars();
    }

    /// <summary>Bar chart series.</summary>
    public ObservableCollection<TokenBarViewModel> Bars { get; }

    /// <summary>Formatted input token count for display.</summary>
    public string TotalInputTokensDisplay => TotalInputTokens.ToString("N0");

    /// <summary>Formatted output token count for display.</summary>
    public string TotalOutputTokensDisplay => TotalOutputTokens.ToString("N0");

    /// <summary>Formatted cost for display.</summary>
    public string TotalCostDisplay => $"${TotalCost:F4}";

    /// <summary>Reset the chart.</summary>
    [RelayCommand]
    private void Reset()
    {
        Bars.Clear();
        TotalInputTokens = 0;
        TotalOutputTokens = 0;
        TotalCost = 0m;
    }

    partial void OnTotalInputTokensChanged(int value) => this.OnPropertyChanged(nameof(TotalInputTokensDisplay));
    partial void OnTotalOutputTokensChanged(int value) => this.OnPropertyChanged(nameof(TotalOutputTokensDisplay));
    partial void OnTotalCostChanged(decimal value) => this.OnPropertyChanged(nameof(TotalCostDisplay));

    private void SeedSampleBars()
    {
        int[] input = new[] { 120, 480, 950, 1300, 1620, 1850, 2100, 2480 };
        int[] output = new[] { 80, 220, 380, 540, 720, 880, 1020, 1180 };
        int maxInput = input[^1];
        int maxOutput = output[^1];
        for (int i = 0; i < input.Length; i++)
        {
            Bars.Add(new TokenBarViewModel(
                $"T{i + 1}",
                80.0 * input[i] / maxInput,
                80.0 * output[i] / maxOutput,
                new SolidColorBrush(Color.FromRgb(0x89, 0xB4, 0xFA)),
                new SolidColorBrush(Color.FromRgb(0xA6, 0xE3, 0xA1))));
        }
    }
}

/// <summary>
///     One bar in the token usage chart. Platform extension of the shared
///     <see cref="Harbor.Desktop.Abstractions.ViewModels.TokenBarViewModel" /> record —
///     adds the WPF <see cref="Brush" />es for each bar (the canonical color keys
///     live on the shared record as <c>InputBrushKey</c> / <c>OutputBrushKey</c>).
///     <see cref="Label" />, <see cref="InputHeight" /> and <see cref="OutputHeight" />
///     are inherited from the shared record.
/// </summary>
public sealed partial class TokenBarViewModel : Harbor.Desktop.Abstractions.ViewModels.TokenBarViewModel
{
    /// <summary>Input-bar fill brush.</summary>
    public Brush InputBrush { get; set; } = Brushes.CornflowerBlue;

    /// <summary>Output-bar fill brush.</summary>
    public Brush OutputBrush { get; set; } = Brushes.MediumSeaGreen;

    /// <summary>Construct a <see cref="TokenBarViewModel" />.</summary>
    public TokenBarViewModel(string label, double inputHeight, double outputHeight, Brush inputBrush, Brush outputBrush)
        : base(label, inputHeight, outputHeight)
    {
        InputBrush = inputBrush;
        OutputBrush = outputBrush;
    }
}
