using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Harbor.Desktop.Abstractions.ViewModels;

/// <summary>
///     Shared, framework-agnostic view-model for a line-level diff viewer.
///     Holds the before/after text and the computed diff. Platform renderers
///     (Avalonia, WPF, MAUI, Blazor) bind to it and supply the actual
///     clipboard mechanism via the <see cref="CopyAsync" /> delegate — the VM
///     itself never references Blazor, JSInterop, or any clipboard API, so it
///     can be reused across every desktop/web target.
/// </summary>
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
    ///     <see cref="After" /> and store the unified-style result in
    ///     <see cref="DiffText" />. Lines present on both sides are prefixed
    ///     with <c>"  "</c>; removals with <c>"- "</c>; additions with
    ///     <c>"+ "</c>.
    /// </summary>
    public void ComputeDiff()
    {
        var beforeLines = Before.Split('\n');
        var afterLines = After.Split('\n');
        var sb = new StringBuilder();
        var max = Math.Max(beforeLines.Length, afterLines.Length);
        for (var i = 0; i < max; i++)
        {
            var b = i < beforeLines.Length ? beforeLines[i] : string.Empty;
            var a = i < afterLines.Length ? afterLines[i] : string.Empty;
            if (b == a)
            {
                sb.AppendLine("  " + a);
            }
            else
            {
                if (i < beforeLines.Length) sb.AppendLine("- " + b);
                if (i < afterLines.Length) sb.AppendLine("+ " + a);
            }
        }

        DiffText = sb.ToString();
    }

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
