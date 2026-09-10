using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Harbor.App.Avalonia.Services;
using Harbor.Abstractions.Lsp;
using Harbor.Desktop.Abstractions.ViewModels;
using Harbor.Ui.Framework.Services;
using Microsoft.Extensions.Logging;
namespace Harbor.App.Avalonia.ViewModels;
/// <summary>
///     Multi-tab code editor view-model. Uses AvaloniaEdit under the hood (the
///     <c>CodeEditorView</c> hosts the <c>TextEditor</c>); this VM owns the tab list,
///     the active tab, and file open/save orchestration. When an
///     <see cref="ILspService" /> is available, opened files are pushed to the
///     matching builtin language server and its published diagnostics surface
///     for the active tab.
/// </summary>
public sealed partial class CodeEditorViewModel : ObservableObject
{
    private readonly IDispatcherAdapter _dispatcher;
    private readonly ILogger<CodeEditorViewModel> _logger;
    private readonly AvaloniaFilePicker _picker;
    private readonly IToastService _toasts;
    private readonly ILspService? _lsp;

    /// <summary>Files already announced to the language server (didOpen sent).</summary>
    private readonly HashSet<string> _lspOpened = new(StringComparer.OrdinalIgnoreCase);

    [ObservableProperty]
    private EditorTabViewModel? _activeTab;

    [ObservableProperty]
    private IReadOnlyList<LspDiagnostic> _activeDiagnostics = [];

    [ObservableProperty]
    private string? _inlineEditPrompt;

    [ObservableProperty]
    private string? _inlineEditDiff;

    [ObservableProperty]
    private bool _inlineEditVisible;

    [ObservableProperty]
    private bool _isInlineEditLoading;

    private string _inlineEditSelectedText = string.Empty;
    private int _inlineEditSelectionStart;
    private int _inlineEditSelectionEnd;

    /// <summary>Construct the code editor view-model.</summary>
    public CodeEditorViewModel(
        AvaloniaFilePicker picker,
        ILogger<CodeEditorViewModel> logger,
        IToastService toasts,
        IDispatcherAdapter dispatcher,
        ILspService? lspService = null)
    {
        _picker = picker;
        _logger = logger;
        _toasts = toasts;
        _dispatcher = dispatcher;
        _lsp = lspService;
        if (_lsp is not null)
        {
            _lsp.DiagnosticsChanged += OnLspDiagnosticsChanged;
        }
    }

    public ObservableCollection<EditorTabViewModel> Tabs { get; } = new();

    /// <summary>LSP auto-spawn hook: when the active tab changes, announce it to the language server and refresh diagnostics.</summary>
    partial void OnActiveTabChanged(EditorTabViewModel? oldValue, EditorTabViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.PropertyChanged -= OnTabPropertyChanged;
        }

        if (newValue is not null)
        {
            newValue.PropertyChanged += OnTabPropertyChanged;
        }

        RefreshDiagnostics(newValue);
        if (newValue is not null && _lsp is not null && !_lspOpened.Contains(newValue.FilePath))
        {
            OpenWithLsp(newValue.FilePath, newValue.Content);
        }
    }

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
        if (_lsp is not null && _lspOpened.Remove(tab.FilePath))
        {
            _ = CloseWithLspAsync(tab.FilePath);
        }
    }

    // ── LSP bridge ─────────────────────────────────────────────────────────

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EditorTabViewModel.Content)
            && sender is EditorTabViewModel { } tab
            && ActiveTab == tab
            && _lsp is not null
            && _lspOpened.Contains(tab.FilePath))
        {
            _ = NotifyLspChangeAsync(tab.FilePath, tab.Content);
        }
    }

    /// <summary>didOpen in the background — spawn + handshake must never block file loading.</summary>
    private void OpenWithLsp(string filePath, string content)
    {
        if (_lsp is null || !_lsp.SupportsFile(filePath)) return;
        _lspOpened.Add(filePath);
        _ = Task.Run(async () =>
        {
            try
            {
                await _lsp.OpenFileAsync(filePath, content).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "LSP open failed for {Path}", filePath);
            }
        });
    }

    private async Task NotifyLspChangeAsync(string filePath, string content)
    {
        if (_lsp is null) return;
        try
        {
            await _lsp.NotifyChangeAsync(filePath, content).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LSP change notification failed for {Path}", filePath);
        }
    }

    private async Task CloseWithLspAsync(string filePath)
    {
        try
        {
            await _lsp!.CloseFileAsync(filePath).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LSP close failed for {Path}", filePath);
        }
    }

    private void OnLspDiagnosticsChanged(object? sender, LspDiagnosticsChangedEventArgs args)
    {
        RefreshDiagnostics(ActiveTab);
    }

    private void RefreshDiagnostics(EditorTabViewModel? tab)
    {
        if (_lsp is null || tab is null || !_lsp.SupportsFile(tab.FilePath))
        {
            SetDiagnostics([]);
            return;
        }

        _ = ReadDiagnosticsAsync(tab.FilePath);
    }

    private void SetDiagnostics(IReadOnlyList<LspDiagnostic> diagnostics)
        => _dispatcher.Post(() => ActiveDiagnostics = diagnostics);

    private async Task ReadDiagnosticsAsync(string filePath)
    {
        try
        {
            IReadOnlyList<LspDiagnostic> diagnostics = await _lsp!.GetDiagnosticsAsync(filePath).ConfigureAwait(false);
            _dispatcher.Post(() =>
            {
                if (ActiveTab?.FilePath == filePath)
                {
                    ActiveDiagnostics = diagnostics;
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LSP diagnostics read failed for {Path}", filePath);
        }
    }

    // ── Inline Edit (Cmd+K / Ctrl+K) ────────────────────────────────────

    /// <summary>
    ///     Opens the inline edit overlay for the current selection.
    ///     Called from <see cref="CodeEditorView"/> when Cmd+K / Ctrl+K is pressed.
    /// </summary>
    public void OpenInlineEdit(string selectedText, int selectionStart, int selectionEnd, double caretPixelTop)
    {
        if (string.IsNullOrEmpty(selectedText)) return;
        _inlineEditSelectedText = selectedText;
        _inlineEditSelectionStart = selectionStart;
        _inlineEditSelectionEnd = selectionEnd;
        InlineEditPrompt = string.Empty;
        InlineEditDiff = null;
        InlineEditVisible = true;
        _logger.LogDebug("Inline edit opened: {Start}-{End} ({Length} chars)", selectionStart, selectionEnd, selectedText.Length);
    }

    /// <summary>
    ///     Cancels the inline edit overlay without applying changes.
    /// </summary>
    [RelayCommand]
    private void RejectInlineEdit()
    {
        InlineEditVisible = false;
        InlineEditPrompt = string.Empty;
        InlineEditDiff = null;
        _inlineEditSelectedText = string.Empty;
        _logger.LogDebug("Inline edit rejected");
    }

    /// <summary>
    ///     Accepts the proposed diff: replaces the original selected text with
    ///     <see cref="InlineEditDiff"/> in the active tab's content.
    ///     Called after the orchestrator wires up the agent call and populates
    ///     <see cref="InlineEditDiff"/>.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAcceptInlineEdit))]
    private void AcceptInlineEdit()
    {
        if (ActiveTab is null || InlineEditDiff is null) return;

        var content = ActiveTab.Content;
        if (_inlineEditSelectionStart >= 0
            && _inlineEditSelectionEnd <= content.Length
            && _inlineEditSelectionStart < _inlineEditSelectionEnd)
        {
            var newContent = string.Concat(
                content.AsSpan(0, _inlineEditSelectionStart),
                InlineEditDiff.AsSpan(),
                content.AsSpan(_inlineEditSelectionEnd));
            ActiveTab.Content = newContent;
            _logger.LogInformation("Inline edit accepted: replaced {OldLen} chars with {NewLen} chars at {Start}-{End}",
                _inlineEditSelectionEnd - _inlineEditSelectionStart,
                InlineEditDiff.Length,
                _inlineEditSelectionStart,
                _inlineEditSelectionEnd);
        }

        InlineEditVisible = false;
        InlineEditPrompt = string.Empty;
        InlineEditDiff = null;
        _inlineEditSelectedText = string.Empty;
    }

    private bool CanAcceptInlineEdit()
        => InlineEditVisible && !IsInlineEditLoading && InlineEditDiff is not null;

    /// <summary>
    ///     Called by the orchestrator-wired agent callback once the edit diff
    ///     is ready. Runs on the dispatcher to update observable state safely.
    /// </summary>
    public void SetInlineEditResult(string diff)
    {
        _dispatcher.Post(() =>
        {
            InlineEditDiff = diff;
            IsInlineEditLoading = false;
        });
    }

    /// <summary>
    ///     Called by the orchestrator-wired agent callback on error.
    /// </summary>
    public void SetInlineEditError(string error)
    {
        _dispatcher.Post(() =>
        {
            IsInlineEditLoading = false;
            InlineEditDiff = null;
            _toasts.Show($"Inline edit failed: {error}", ToastKind.Error);
            InlineEditVisible = false;
        });
    }

    /// <summary>
    ///     Sets the loading state while the agent is processing the edit request.
    /// </summary>
    public void SetInlineEditLoading(bool loading)
    {
        _dispatcher.Post(() => IsInlineEditLoading = loading);
    }

    /// <summary>
    ///     Returns the currently selected text range for the inline edit overlay.
    /// </summary>
    public (string Text, int Start, int End) GetInlineEditSelection()
        => (_inlineEditSelectedText, _inlineEditSelectionStart, _inlineEditSelectionEnd);
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
