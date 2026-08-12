using Harbor.Ui.Framework.Services;
namespace Harbor.Desktop.Abstractions.ViewModels;

/// <summary>
///     Base for the multi-tab code-editor view-model. Holds the tab list and
///     the active tab; platform VMs wire the actual editor control
///     (AvaloniaEdit / AvalonEdit / Monaco / MAUI editor) and the
///     <c>IFilePicker</c> calls.
/// </summary>
public abstract partial class CodeEditorViewModelBase : ViewModelBase
{
    /// <summary>The currently-focused tab (null when no tabs are open).</summary>
    [ObservableProperty]
    private EditorTabViewModelBase? _activeTab;

    /// <summary>True while a file is loading or saving.</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Construct a <see cref="CodeEditorViewModelBase" />.</summary>
    protected CodeEditorViewModelBase(ILogger logger) : base(logger)
    {
    }

    /// <summary>Open editor tabs.</summary>
    public ObservableCollection<EditorTabViewModelBase> Tabs { get; } = new();

    /// <summary>Close the given tab and activate the last remaining one when the active tab closes.</summary>
    /// <param name="tab">Tab to close (no-op when null).</param>
    protected void CloseTabCore(EditorTabViewModelBase? tab)
    {
        if (tab is null)
        {
            return;
        }
        Tabs.Remove(tab);
        if (ActiveTab == tab)
        {
            ActiveTab = Tabs.LastOrDefault();
        }
    }
}

/// <summary>
///     One editor tab — file path, name, extension, content, dirty flag.
///     Framework-agnostic; the Avalonia-specific syntax-highlighting name
///     (<c>SyntaxName</c>) lives on the platform subclass.
/// </summary>
public abstract partial class EditorTabViewModelBase : ViewModelBase
{
    [ObservableProperty]
    private string _content;

    [ObservableProperty]
    private bool _isDirty;

    /// <summary>Absolute file path on disk.</summary>
    [ObservableProperty]
    private string _filePath;

    /// <summary>Short file name (no directory).</summary>
    [ObservableProperty]
    private string _fileName;

    /// <summary>Construct an editor tab.</summary>
    protected EditorTabViewModelBase(
        string filePath,
        string fileName,
        string extension,
        string content,
        ILogger logger)
        : base(logger)
    {
        _filePath = filePath;
        _fileName = fileName;
        Extension = extension;
        _content = content;
    }

    /// <summary>File extension (no leading dot) — drives syntax highlighting.</summary>
    public string Extension { get; }

    /// <summary>Partial-patch setter: updates content + marks the tab dirty.</summary>
    partial void OnContentChanged(string value) => IsDirty = true;
}
