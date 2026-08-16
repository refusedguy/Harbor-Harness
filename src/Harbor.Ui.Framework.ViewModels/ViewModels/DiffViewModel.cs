using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
namespace Harbor.Ui.Framework.ViewModels;
/// <summary>
///     Side-by-side diff view-model. Accepts two text inputs (left = before, right = after)
///     and computes a simple line-by-line diff. Real production would use a proper diff
///     algorithm (Myers) — this implementation is intentionally simple: same lines are
///     "unchanged", differing lines are "modified", and length differences are "added"/"removed".
/// </summary>
public sealed partial class DiffViewModel : ObservableObject
{
    private readonly ILogger<DiffViewModel> _logger;

    [ObservableProperty]
    private string _leftText = string.Empty;

    [ObservableProperty]
    private string _leftTitle = "before";

    [ObservableProperty]
    private string _rightText = string.Empty;

    [ObservableProperty]
    private string _rightTitle = "after";

    /// <summary>Construct the diff view-model.</summary>
    public DiffViewModel(ILogger<DiffViewModel> logger)
    {
        _logger = logger;
    }

    /// <summary>The diff rows for the view.</summary>
    public ObservableCollection<DiffRowViewModel> Rows { get; } = new();

    /// <summary>Compute the diff between <see cref="LeftText" /> and <see cref="RightText" />.</summary>
    [RelayCommand]
    private void Compute()
    {
        Rows.Clear();
        string[] leftLines = LeftText.Replace("\r\n", "\n").Split('\n');
        string[] rightLines = RightText.Replace("\r\n", "\n").Split('\n');
        int max = Math.Max(leftLines.Length, rightLines.Length);
        for (int i = 0; i < max; i++)
        {
            string l = i < leftLines.Length ? leftLines[i] : string.Empty;
            string r = i < rightLines.Length ? rightLines[i] : string.Empty;
            string kind;
            if (i >= leftLines.Length) kind = "added";
            else if (i >= rightLines.Length) kind = "removed";
            else if (l == r) kind = "unchanged";
            else kind = "modified";
            Rows.Add(new DiffRowViewModel(i + 1, l, r, kind));
        }
        _logger.LogInformation("Diff computed: {Rows} rows", Rows.Count);
    }
}

/// <summary>One diff row.</summary>
public sealed record DiffRowViewModel(int LineNumber, string Left, string Right, string Kind)
{
    /// <summary>Brush key for the row (resolved by the view).</summary>
    public string BrushKey => Kind switch
    {
        "added" => "ChatToolResultBrush",
        "removed" => "ChatErrorBrush",
        "modified" => "ChatToolBrush",
        _ => "TextSubtleBrush"
    };
}
