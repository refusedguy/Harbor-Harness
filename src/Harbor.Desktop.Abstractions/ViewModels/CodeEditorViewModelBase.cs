namespace Harbor.Desktop.Abstractions.ViewModels;
/// <summary>
///     Base for the code-editor view-model. Holds the open file path,
///     dirty flag, and document text. Platform VMs wire the actual editor
///     control (AvaloniaEdit / AvalonEdit / Monaco / MAUI editor) and the
///     <c>IFilePicker</c> calls.
/// </summary>
public abstract partial class CodeEditorViewModelBase : ViewModelBase
{

    /// <summary>Absolute path of the open file, or null for an unsaved buffer.</summary>
    [ObservableProperty]
    private string? _filePath;

    /// <summary>True while a file is loading or saving.</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>True if the buffer has unsaved changes.</summary>
    [ObservableProperty]
    private bool _isDirty;

    /// <summary>Language id for syntax highlighting (e.g. "csharp", "typescript").</summary>
    [ObservableProperty]
    private string _language = "plaintext";

    /// <summary>Document text.</summary>
    [ObservableProperty]
    private string _text = string.Empty;
    /// <summary>Construct a <see cref="CodeEditorViewModelBase" />.</summary>
    protected CodeEditorViewModelBase(ILogger logger) : base(logger)
    {
    }

    /// <summary>Display name (file name only, or "Untitled" if no path).</summary>
    public string DisplayName => string.IsNullOrEmpty(FilePath)
        ? "Untitled"
        : Path.GetFileName(FilePath);

    /// <summary>Open a file. Implemented by the platform VM via <c>IFilePicker</c>.</summary>
    protected abstract Task OpenAsync(CancellationToken cancellationToken);

    /// <summary>Save the buffer to <see cref="FilePath" /> (or Save-As if null). Implemented by the platform VM.</summary>
    protected abstract Task SaveAsync(CancellationToken cancellationToken);

    /// <summary>Mark the VM dirty when the text changes.</summary>
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(Text) || e.PropertyName == nameof(FilePath))
        {
            IsDirty = true;
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(DisplayName)));
        }
    }
}
