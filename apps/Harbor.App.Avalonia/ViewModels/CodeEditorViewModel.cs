using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Harbor.App.Avalonia.Services;
using Harbor.Desktop.Abstractions.ViewModels;
using Harbor.Ui.Framework.Services;
using Microsoft.Extensions.Logging;
namespace Harbor.App.Avalonia.ViewModels;
/// <summary>
///     Multi-tab code editor view-model. Uses AvaloniaEdit under the hood (the
///     <c>CodeEditorView</c> hosts the <c>TextEditor</c>); this VM owns the tab list,
///     the active tab, and file open/save orchestration.
/// </summary>
public sealed partial class CodeEditorViewModel : ObservableObject
{
    private readonly IDispatcherAdapter _dispatcher;
    private readonly ILogger<CodeEditorViewModel> _logger;
    private readonly AvaloniaFilePicker _picker;
    private readonly IToastService _toasts;

    [ObservableProperty]
    private EditorTabViewModel? _activeTab;

    /// <summary>Construct the code editor view-model.</summary>
    public CodeEditorViewModel(
        AvaloniaFilePicker picker,
        ILogger<CodeEditorViewModel> logger,
        IToastService toasts,
        IDispatcherAdapter dispatcher)
    {
        _picker = picker;
        _logger = logger;
        _toasts = toasts;
        _dispatcher = dispatcher;
    }

    public ObservableCollection<EditorTabViewModel> Tabs { get; } = new();

    [RelayCommand]
    private async Task OpenFileAsync()
    {
        var paths = await _picker.PickFilesAsync("Open file").ConfigureAwait(false);
        if (paths is null || paths.Count == 0) return;
        foreach (string path in paths)
        {
            await LoadFileAsync(path).ConfigureAwait(false);
        }
    }

    public async Task LoadFileAsync(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                _toasts.Show($"File not found: {path}", ToastKind.Error);
                return;
            }
            string content = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            string name = Path.GetFileName(path);
            string ext = Path.GetExtension(path).TrimStart('.');
            _dispatcher.Post(() =>
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

    [RelayCommand]
    private async Task SaveAsAsync()
    {
        if (ActiveTab is null) return;
        string? path = await _picker.PickSaveFileAsync("Save as", ActiveTab.FileName).ConfigureAwait(false);
        if (path is null) return;
        ActiveTab.FilePath = path;
        ActiveTab.FileName = Path.GetFileName(path);
        await SaveAsync();
    }

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

/// <summary>One editor tab — file path, name, extension, content, dirty flag. Inherits shared model; adds AvaloniaEdit-specific SyntaxName.</summary>
public sealed partial class EditorTabViewModel : Harbor.Desktop.Abstractions.ViewModels.EditorTabViewModel
{
    public EditorTabViewModel(string filePath, string fileName, string extension, string content)
        : base(filePath, fileName, extension, content)
    {
    }

    public string SyntaxName => (Extension ?? string.Empty).ToLowerInvariant() switch
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
