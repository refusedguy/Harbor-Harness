using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Harbor.Ui.Framework.Rendering.Widgets;
using Microsoft.Extensions.Logging;
namespace Harbor.Ui.Framework.ViewModels;
/// <summary>
///     Side-by-side diff view-model. Accepts two text inputs (left = before,
///     right = after) and shows the difference between them.
/// </summary>
/// <remarks>
///     <para>
///         The diff itself is <b>computed by
///         <see cref="Harbor.Ui.Framework.Rendering.Widgets.LineDiff" />, the
///         headless diff engine in the renderer-agnostic layer</b>, and this
///         view-model only projects what it returns. It used to walk
///         <c>left[i]</c> against <c>right[i]</c> itself and call the mismatch
///         a modification, which reported the tail of the file as rewritten as
///         soon as a line was
///         inserted anywhere above it (#679). The business rule "what changed"
///         does not belong to a view-model; the view-model decides only what a
///         row is <i>painted</i> as, via <see cref="DiffRowViewModel.Kind" />.
///     </para>
///     <para>
///         <b>Why the name says "SideBySide" (#570).</b> This type was
///         <c>DiffViewModel</c>, and so was
///         <c>Harbor.Desktop.Abstractions.ViewModels.DiffViewModel</c>. Two live
///         types behind one name is what let #570's bug hide: the author of a fix
///         could not tell from the name which one they were editing, one copy
///         kept the old behaviour, and the end-to-end test stayed green because
///         it drove both. The contracts are genuinely different — this one
///         projects a row per line with a per-row <c>BrushKey</c> for Avalonia
///         <c>DiffView.axaml</c>, that one flattens to a single
///         <c>DiffText</c> string and owns the platform clipboard delegate — so
///         neither is a copy to delete, and the fix is to let each name state
///         which projection it is. The shared algorithm is
///         <see cref="Harbor.Ui.Framework.Rendering.Widgets.LineDiff" />, and
///         <c>DiffSurfaceNameCollisionRule</c> is what stops the pair regrowing.
///     </para>
///     <para>
///         <b>vm-dedup canon (audit 27-G):</b> canonical TEA-projection side-by-side
///         diff VM (<c>LeftText</c>/<c>RightText</c>/<c>Compute</c>/<c>Rows</c>), bound by
///         Avalonia <c>DiffView.axaml</c>. Not the same as the TUI
///         <c>DiffPreviewViewModel</c> (event-driven diff list) or the Desktop
///         <c>DiffViewModel</c> (Before/After unified text) — the two now share
///         one diff algorithm, but their view contracts still differ, so they
///         stay separate types.
///     </para>
/// </remarks>
public sealed partial class SideBySideDiffViewModel : ObservableObject
{
    private readonly ILogger<SideBySideDiffViewModel> _logger;

    [ObservableProperty]
    private string _leftText = string.Empty;

    [ObservableProperty]
    private string _leftTitle = "before";

    [ObservableProperty]
    private string _rightText = string.Empty;

    [ObservableProperty]
    private string _rightTitle = "after";

    /// <summary>Construct the diff view-model.</summary>
    public SideBySideDiffViewModel(ILogger<SideBySideDiffViewModel> logger)
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
        foreach (var row in LineDiff.ComputeSideBySide(LeftText, RightText))
        {
            Rows.Add(new DiffRowViewModel(
                row.LineNumber,
                row.OldText ?? string.Empty,
                row.NewText ?? string.Empty,
                WireKind(row.Kind)));
        }

        _logger.LogInformation("Diff computed: {Rows} rows", Rows.Count);
    }

    /// <summary>
    ///     The row's kind as the wire value <see cref="DiffRowViewModel.BrushKey" />
    ///     and the Avalonia view already speak. Presentation vocabulary, so it
    ///     stays here: the diff kind itself is a core enum.
    /// </summary>
    private static string WireKind(SideBySideRowKind kind) => kind switch
    {
        SideBySideRowKind.Added => "added",
        SideBySideRowKind.Removed => "removed",
        SideBySideRowKind.Modified => "modified",
        _ => "unchanged"
    };
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
