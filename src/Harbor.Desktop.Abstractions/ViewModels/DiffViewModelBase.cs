namespace Harbor.Desktop.Abstractions.ViewModels;
/// <summary>
///     Base for the diff view-model. Holds the original and modified text,
///     the file path, and the language. Platform VMs render the actual diff
///     (Avalonia <c>AvaloniaEdit</c> diff, WPF <c>AvalonEdit</c> diff, Blazor
///     Monaco diff editor).
/// </summary>
public abstract partial class DiffViewModelBase : ViewModelBase
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
    protected DiffViewModelBase(ILogger logger) : base(logger)
    {
    }
}
