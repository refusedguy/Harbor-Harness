using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
///     One bar in the token usage chart.
/// </summary>
public sealed partial class TokenBarViewModel : ObservableObject
{

    /// <summary>Input-bar fill brush.</summary>
    [ObservableProperty] private Brush _inputBrush = Brushes.CornflowerBlue;

    /// <summary>Input-bar height in pixels.</summary>
    [ObservableProperty] private double _inputHeight;
    /// <summary>X-axis label (turn number).</summary>
    [ObservableProperty] private string _label = string.Empty;

    /// <summary>Output-bar fill brush.</summary>
    [ObservableProperty] private Brush _outputBrush = Brushes.MediumSeaGreen;

    /// <summary>Output-bar height in pixels.</summary>
    [ObservableProperty] private double _outputHeight;

    /// <summary>Construct a <see cref="TokenBarViewModel" />.</summary>
    public TokenBarViewModel(string label, double inputHeight, double outputHeight, Brush inputBrush, Brush outputBrush)
    {
        _label = label;
        _inputHeight = inputHeight;
        _outputHeight = outputHeight;
        _inputBrush = inputBrush;
        _outputBrush = outputBrush;
    }
}
