using CommunityToolkit.Mvvm.ComponentModel;
using Harbor.Ui.Framework.Rendering.Widgets;

namespace Harbor.Desktop.Abstractions.ViewModels;

/// <summary>
///     Shared, framework-agnostic view-model for a line-level diff viewer.
///     Holds the before/after text and the computed diff. Platform renderers
///     (Avalonia, WPF, MAUI, Blazor) bind to it and supply the actual
///     clipboard mechanism via the <see cref="CopyAsync" /> delegate — the VM
///     itself never references Blazor, JSInterop, or any clipboard API, so it
///     can be reused across every desktop/web target.
/// </summary>
/// <remarks>
///     <para>
///         <b>vm-dedup canon (audit 27-G):</b> desktop canon unified-diff VM
///         (<c>Before</c>/<c>After</c>/<c>ComputeDiff</c>/<c>CopyAsync</c>).
///         NOT bound by Avalonia <c>DiffView</c> — that view binds the Framework
///         side-by-side VM via <c>ContentHost.Diff</c> (issue #160). Distinct from
///         the Framework side-by-side compute VM and the TUI
///         <c>DiffPreviewViewModel</c>: the index-by-index diff that used to be
///         duplicated in both was replaced by the shared core
///         <see cref="LineDiff" /> (#679), but the three view contracts (unified
///         text, aligned rows, event-driven list) are still different, so they
///         remain separate types.
///     </para>
/// </remarks>
public sealed partial class DiffViewModel : ObservableObject
{
    /// <summary>The original (left) text.</summary>
    [ObservableProperty]
    private string _before = "Hello, world!\nThis is the original.";

    /// <summary>The modified (right) text.</summary>
    [ObservableProperty]
    private string _after = "Hello, Harbor!\nThis is the original.\nWith a new line.";

    /// <summary>The computed line-level diff (empty until <see cref="ComputeDiff" /> runs).</summary>
    [ObservableProperty]
    private string _diffText = string.Empty;

    /// <summary>Optional display file path (left side header).</summary>
    [ObservableProperty]
    private string? _filePath;

    /// <summary>Language id for syntax highlighting (optional).</summary>
    [ObservableProperty]
    private string _language = "plaintext";

    /// <summary>Construct a <see cref="DiffViewModel" /> with the default demo snippets.</summary>
    public DiffViewModel()
    {
    }

    /// <summary>
    ///     Compute a line-level diff between <see cref="Before" /> and
    ///     <see cref="After" /> and store the result in
    ///     <see cref="DiffText" />. Lines present on both sides are prefixed
    ///     with <c>"  "</c>; removals with <c>"- "</c>; additions with
    ///     <c>"+ "</c> — the context-diff block the core speaks, which
    ///     <see cref="LineDiff.TryParseContextBlock" /> reads back.
    /// </summary>
    /// <remarks>
    ///     The diff is computed by the headless core
    ///     (<see cref="LineDiff" />), not here. This method previously walked
    ///     <c>before[i]</c> against <c>after[i]</c>, so a line inserted
    ///     anywhere above rewrote the whole remainder of the file in the output
    ///     (#679). This view-model is display state: it holds the two texts and
    ///     hands the answer to the view.
    /// </remarks>
    public void ComputeDiff() => DiffText = LineDiff.ToUnifiedText(LineDiff.Compute(Before, After));

    /// <summary>
    ///     Copy the computed diff to the clipboard. The actual clipboard write
    ///     is supplied by the platform (e.g. Blazor <c>HarborJsInterop</c>);
    ///     this method only guards against copying an empty diff and hands the
    ///     text to the provided callback.
    /// </summary>
    /// <param name="copyToClipboard">Platform clipboard write (receives the diff text).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task CopyAsync(
        Func<string, Task> copyToClipboard,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(DiffText))
        {
            return;
        }

        if (copyToClipboard is null)
        {
            throw new ArgumentNullException(nameof(copyToClipboard));
        }

        await copyToClipboard(DiffText).ConfigureAwait(false);
    }
}
