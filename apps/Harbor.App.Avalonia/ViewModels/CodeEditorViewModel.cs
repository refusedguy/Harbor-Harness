using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Harbor.App.Avalonia.Services;
using Microsoft.Extensions.Logging;

namespace Harbor.App.Avalonia.ViewModels;

/// <summary>
///     Multi-tab code editor view-model. Uses AvaloniaEdit under the hood (the
///     <c>CodeEditorView</c> hosts the <c>TextEditor</c>); this VM owns the tab list,
///     the active tab, and file open/save orchestration.
/// </summary>
public sealed partial class CodeEditorViewModel : ObservableObject
{
    private readonly AvaloniaFilePicker _picker;
    private readonly ILogger<CodeEditorViewModel> _logger;
    private readonly ToastService _toasts;

    /// <summary>Construct the code editor view-model.</summary>
    public CodeEditorViewModel(AvaloniaFilePicker picker, ILogger<CodeEditorViewModel> logger, ToastService toasts)
    {
        _picker = picker;
        _logger = logger;
        _toasts = toasts;
    }

    /// <summary>Open editor tabs.</summary>
    public ObservableCollection<EditorTabViewModel> Tabs { get; } = new();

    [ObservableProperty]
    private EditorTabViewModel? _activeTab;

    /// <summary>Open a file picker and load the chosen file into a new tab.</summary>
    [RelayCommand]
    private async Task OpenFileAsync()
    {
        var paths = await _picker.PickFilesAsync("Open file").ConfigureAwait(false);
        if (paths is null || paths.Count == 0) return;
        foreach (var path in paths)
        {
            await LoadFileAsync(path).ConfigureAwait(false);
        }
    }

    /// <summary>Load a specific file path into a new tab.</summary>
    /// <param name="path">File path to load.</param>
    public async Task LoadFileAsync(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                _toasts.Show($"File not found: {path}", ToastKind.Error);
                return;
            }
            var content = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            var name = Path.GetFileName(path);
            var ext = Path.GetExtension(path).TrimStart('.');
            Dispatcher.UIThread.Post(() =>
            {
                var tab = new EditorTabViewModel(path, name, ext, content);
                Tabs.Add(tab);
                ActiveTab = tab;
            });
            _logger.LogInformation("Opened file: {Path} ({Size} chars)", path, content.Length);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open {Path}", path);
            _toasts.Show($"Failed to open {path}: {ex.Message}", ToastKind.Error);
        }
    }

    /// <summary>Save the active tab to disk.</summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        if (ActiveTab is null) return;
        try
        {
            await File.WriteAllTextAsync(ActiveTab.FilePath, ActiveTab.Content).ConfigureAwait(false);
            ActiveTab.IsDirty = false;
            _toasts.Show($"Saved: {ActiveTab.FileName}", ToastKind.Success);
            _logger.LogInformation("Saved {Path}", ActiveTab.FilePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Save failed");
            _toasts.Show($"Save failed: {ex.Message}", ToastKind.Error);
        }
    }

    /// <summary>Save the active tab under a new file name.</summary>
    [RelayCommand]
    private async Task SaveAsAsync()
    {
        if (ActiveTab is null) return;
        var path = await _picker.PickSaveFileAsync("Save as", ActiveTab.FileName).ConfigureAwait(false);
        if (path is null) return;
        ActiveTab.FilePath = path;
        ActiveTab.FileName = Path.GetFileName(path);
        await SaveAsync();
    }

    /// <summary>Close the active tab.</summary>
    [RelayCommand]
    private void CloseTab(EditorTabViewModel? tab)
    {
        if (tab is null) return;
        Tabs.Remove(tab);
        if (ActiveTab == tab)
        {
            ActiveTab = Tabs.LastOrDefault();
        }
    }
}

/// <summary>One editor tab — file path, name, extension, content, dirty flag.</summary>
public sealed partial class EditorTabViewModel : ObservableObject
{
    /// <summary>Construct an editor tab.</summary>
    public EditorTabViewModel(string filePath, string fileName, string extension, string content)
    {
        FilePath = filePath;
        FileName = fileName;
        Extension = extension;
        _content = content;
    }

    /// <summary>Absolute file path on disk.</summary>
    public string FilePath { get; set; }

    /// <summary>Short file name (no directory).</summary>
    public string FileName { get; set; }

    /// <summary>File extension (no leading dot) — drives syntax highlighting.</summary>
    public string Extension { get; }

    [ObservableProperty]
    private string _content;

    [ObservableProperty]
    private bool _isDirty;

    /// <summary>Partial-patch setter: updates content + marks the tab dirty.</summary>
    partial void OnContentChanged(string value)
    {
        IsDirty = true;
    }

    /// <summary>The AvaloniaEdit syntax-highlighting definition name.</summary>
    public string SyntaxName => Extension.ToLowerInvariant() switch
    {
        "cs" => "C#",
        "ts" or "tsx" or "js" or "jsx" => "JavaScript",
        "json" => "Json",
        "md" => "Markdown",
        "py" => "Python",
        "go" => "Go",
        "rs" => "Rust",
        "java" => "Java",
        "cpp" or "cc" or "cxx" or "h" or "hpp" => "C++",
        "xml" or "axaml" or "xaml" => "XML",
        "html" or "htm" => "HTML",
        "css" => "CSS",
        "sql" => "SQL",
        "sh" or "bash" => "Bash",
        _ => "C#"
    };
}
