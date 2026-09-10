using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Harbor.Ui.Framework.Services;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;
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
        foreach (var line in state.Lines)
        {
            if (line.ToolCallId is null) continue;
            var text = line.Text;
            if (text.Length >= 6 && text[..4] == "+++ " && text.Contains('/'))
                return text[4..].Trim();
            if (text.Length >= 6 && text[..4] == "--- " && text.Contains('/'))
                return text[4..].Trim();
        }
        return null;
    }

    private static string ExtractDiffText(UiState state)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var line in state.Lines)
        {
            if (line.ToolCallId is null) continue;
            if (line.Text.Contains("diff") || line.Text.Contains("---") || line.Text.Contains("+++") || line.Text.Contains("@@"))
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
