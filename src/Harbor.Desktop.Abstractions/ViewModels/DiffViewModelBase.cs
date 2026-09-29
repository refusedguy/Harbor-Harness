using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Harbor.Ui.Framework.Services;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;
using Harbor.Ui.Framework.Rendering.Widgets;
using Harbor.Ui.Framework.ViewModels;
using Microsoft.Extensions.Logging;

namespace Harbor.Desktop.Abstractions.ViewModels;

/// <summary>
///     Base for the diff view-model. Holds the original and modified text,
///     the file path, and the language. Platform VMs render the actual diff
///     (Avalonia <c>AvaloniaEdit</c> diff, WPF <c>AvalonEdit</c> diff, Blazor
///     Monaco diff editor).
/// </summary>
public abstract partial class DiffViewModelBase : StoreSubscriberViewModel
{
    /// <summary>File path being diffed (display only).</summary>
    [ObservableProperty]
    private string? _filePath;

    /// <summary>True if the diff is read-only; false if the user can edit the right side.</summary>
    [ObservableProperty]
    private bool _isReadOnly = true;

    /// <summary>Language id for syntax highlighting.</summary>
    [ObservableProperty]
    private string _language = "plaintext";

    /// <summary>Modified (right) text.</summary>
    [ObservableProperty]
    private string _modifiedText = string.Empty;

    /// <summary>Original (left) text.</summary>
    [ObservableProperty]
    private string _originalText = string.Empty;

    /// <summary>Construct a <see cref="DiffViewModelBase" />.</summary>
    /// <param name="dispatcher">UI-thread marshaller / store binder.</param>
    /// <param name="logger">Logger.</param>
    protected DiffViewModelBase(IDispatcherAdapter dispatcher, ILogger logger)
        : base(dispatcher, logger)
    {
        Select(ExtractDiffFilePath, v => FilePath = v);
        Select(ExtractDiffText, v => ModifiedText = v);
    }

    private static string? ExtractDiffFilePath(UiState state)
    {
        foreach (var line in state.Chat.Lines)
        {
            if (line.ToolCallId is null) continue;
            if (UnifiedDiffParser.TryReadFilePath(line.Text) is { } path) return path;
        }
        return null;
    }

    /// <summary>
    ///     The tool output that carries a diff, recognised structurally: a
    ///     context-diff block (a run of <c>"  "</c>/<c>"- "</c>/<c>"+ "</c>
    ///     rows) or a real unified diff. Both readings come from the core's own
    ///     parsers.
    /// </summary>
    /// <remarks>
    ///     This used to keep any tool line CONTAINING <c>"diff"</c>,
    ///     <c>"---"</c>, <c>"+++"</c> or <c>"@@"</c>, which is not what any of
    ///     those mean: prose saying "the result is different", a markdown
    ///     <c>---</c> rule and a <c>// --- section ---</c> comment all read as a
    ///     diff, and the pane was then filled with the other tool's output
    ///     (#679). A row is a row because of how it starts, and a block is a run
    ///     of such rows — that decision belongs to the format's parser, not to a
    ///     set of literals copied into a view-model.
    /// </remarks>
    private static string ExtractDiffText(UiState state)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var line in state.Chat.Lines)
        {
            if (line.ToolCallId is null) continue;
            if (LineDiff.TryParseContextBlock(line.Text, out _)
                || UnifiedDiffParser.LooksLikeDiff(line.Text))
                sb.AppendLine(line.Text);
        }
        return sb.ToString();
    }

    /// <summary>
    ///     Apply declared selectors against the new state snapshot.
    /// </summary>
    /// <param name="state">The current <see cref="UiState" /> snapshot.</param>
    protected override void OnStoreChanged(UiState state)
    {
        ApplySelectors(state);
    }
}
