using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
// `Result` / `Result<T>` come from CSharpFunctionalExtensions, which the app does
// not import globally — #934 made this the first file in the desktop shell to name
// the type rather than only use an extension method on it.
using CSharpFunctionalExtensions;
using Harbor.App.Avalonia.Services;
using Harbor.Abstractions.Filesystem;
using Harbor.Abstractions.Lsp;
using Harbor.Abstractions.Tools;
using Harbor.Desktop.ViewModels;
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

    /// <summary>
    ///     #934: the file I/O, behind a port. The PATH always came from
    ///     <c>IFilePicker</c> — which returns paths and does no I/O — and the CONTENT
    ///     came from <c>File.*</c> calls in this class, so "a view-model knows about
    ///     the filesystem" was true with no seam behind it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The port is async for the reason the contract says it is:
    ///         <c>File.Exists</c> was a SYNCHRONOUS <c>stat</c> on the UI thread,
    ///         reached from <c>ActivityRailView.FileTreeView_SelectionChanged</c> and
    ///         from the toolbar button, in front of two calls that were already
    ///         properly async.
    ///     </para>
    ///     <para>
    ///         What stays here is the PATH STRING: <c>Path.GetFileName</c> and
    ///         <c>Path.GetExtension</c> are how a tab is labelled, they are pure
    ///         string handling, and
    ///         <c>tests/Harbor.Architecture.Tests/AvaloniaTextFileIoRules.cs</c>
    ///         allows them on purpose — as it allows the port call itself.
    ///     </para>
    /// </remarks>
    private readonly ITextFileStore _files;

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
        ITextFileStore files,
        ILspService? lspService = null)
    {
        _picker = picker;
        _logger = logger;
        _toasts = toasts;
        _dispatcher = dispatcher;
        _files = files;
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
            // #934: two round trips through the port instead of `File.Exists` plus
            // `File.ReadAllTextAsync`. The existence probe is async for the same
            // reason the read was — it was the one blocking syscall on the UI
            // thread — and an unreadable path is a FAILED result rather than a
            // `false` that would read as "no such file".
            Result<bool> present = await _files.ExistsAsync(path).ConfigureAwait(false);
            if (present.IsFailure)
            {
                CodeEditorViewModelLog.CannotProbe(_logger, path, present.Error);
                _toasts.Show($"Failed to open {path}: {present.Error}", ToastKind.Error);
                return;
            }

            if (!present.Value)
            {
                _toasts.Show($"File not found: {path}", ToastKind.Error);
                return;
            }

            Result<string> read = await _files.ReadAsync(path).ConfigureAwait(false);
            if (read.IsFailure)
            {
                CodeEditorViewModelLog.CannotRead(_logger, path, read.Error);
                _toasts.Show($"Failed to open {path}: {read.Error}", ToastKind.Error);
                return;
            }

            string content = read.Value;
            string name = Path.GetFileName(path);
            string ext = Path.GetExtension(path).TrimStart('.');
            _dispatcher.Post(() =>
            {
                var tab = new EditorTabViewModel(path, name, ext, content);
                Tabs.Add(tab);
                ActiveTab = tab;
            });
            CodeEditorViewModelLog.OpenedFile(_logger, path, content.Length);
        }
        catch (Exception ex)
        {
            CodeEditorViewModelLog.OpenFailed(_logger, ex, path);
            _toasts.Show($"Failed to open {path}: {ex.Message}", ToastKind.Error);
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (ActiveTab is null) return;

        // #934: the tab is captured before the await. `ConfigureAwait(false)` means
        // the continuation below is NOT on the UI thread, so re-reading `ActiveTab`
        // after the write would clear the dirty flag of whatever tab the user
        // switched to while the file was being written.
        EditorTabViewModel tab = ActiveTab;
        try
        {
            Result written = await _files.WriteAsync(tab.FilePath, tab.Content).ConfigureAwait(false);
            if (written.IsFailure)
            {
                CodeEditorViewModelLog.SaveFailed(_logger, tab.FilePath, written.Error);
                _dispatcher.Post(() => _toasts.Show($"Save failed: {written.Error}", ToastKind.Error));
                return;
            }

            // Everything below mutates BOUND state, so it goes through the
            // dispatcher like every other mutation in this class. It did not before:
            // `IsDirty = false` and the toast were assigned straight after a
            // `ConfigureAwait(false)` await, raising INPC and firing ToastAdded from
            // a pool thread.
            _dispatcher.Post(() =>
            {
                if (ActiveTab == tab)
                {
                    tab.IsDirty = false;
                }

                _toasts.Show($"Saved: {tab.FileName}", ToastKind.Success);
            });
            CodeEditorViewModelLog.Saved(_logger, tab.FilePath);
        }
        catch (Exception ex)
        {
            CodeEditorViewModelLog.SaveThrew(_logger, ex);
            _dispatcher.Post(() => _toasts.Show($"Save failed: {ex.Message}", ToastKind.Error));
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
            // #569: [RelayCommand] on a void method is synchronous by contract.
            // The LSP close used to be discarded bare; CloseWithLspAsync has an
            // internal catch, but a fault escaping it was unobservable.
            TaskFireAndForget.Forget(
                CloseWithLspAsync(tab.FilePath),
                ex => CodeEditorViewModelLog.LspCloseDropped(_logger, ex, tab.FilePath));
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
            // #569: PropertyChanged is a synchronous callback — cannot await.
            // Same reasoning as the close above.
            TaskFireAndForget.Forget(
                NotifyLspChangeAsync(tab.FilePath, tab.Content),
                ex => CodeEditorViewModelLog.LspChangeDropped(_logger, ex, tab.FilePath));
        }
    }

    /// <summary>didOpen in the background — spawn + handshake must never block file loading.</summary>
    private void OpenWithLsp(string filePath, string content)
    {
        if (_lsp is null || !_lsp.SupportsFile(filePath)) return;
        _lspOpened.Add(filePath);

        // #569: the inner catch-all already handles LSP failures, but the
        // Task.Run handle itself was discarded bare — so a fault raised outside
        // that try (task scheduling itself, the continuation's own throw) was
        // lost at finalization. TaskFireAndForget closes that gap; the internal
        // catch stays, because it downgrades an expected LSP hiccup to a warning
        // rather than an error.
        TaskFireAndForget.Forget(
            Task.Run(async () =>
            {
                try
                {
                    await _lsp.OpenFileAsync(filePath, content).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    CodeEditorViewModelLog.LspOpenFailed(_logger, ex, filePath);
                }
            }),
            ex => CodeEditorViewModelLog.LspOpenTaskFaulted(_logger, ex, filePath));
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
            CodeEditorViewModelLog.LspChangeFailed(_logger, ex, filePath);
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
            CodeEditorViewModelLog.LspCloseFailed(_logger, ex, filePath);
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

        // #569: called from an LSP event handler, which is synchronous by
        // contract. ReadDiagnosticsAsync catches internally and leaves the
        // previous diagnostics in place; the fault sink here covers anything
        // raised outside that catch.
        TaskFireAndForget.Forget(
            ReadDiagnosticsAsync(tab.FilePath),
            ex => CodeEditorViewModelLog.LspDiagnosticsReadFaulted(_logger, ex, tab.FilePath));
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
            CodeEditorViewModelLog.LspDiagnosticsReadFailed(_logger, ex, filePath);
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
        CodeEditorViewModelLog.InlineEditOpened(_logger, selectionStart, selectionEnd, selectedText.Length);
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
        CodeEditorViewModelLog.InlineEditRejected(_logger);
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
            CodeEditorViewModelLog.InlineEditAccepted(_logger,
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
public sealed partial class EditorTabViewModel : Harbor.Desktop.ViewModels.EditorTabViewModel
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

/// <summary>
///     SG14: BCL <c>[LoggerMessage]</c> delegates for <see cref="CodeEditorViewModel" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class CodeEditorViewModelLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Cannot probe {Path}: {Error}")]
    public static partial void CannotProbe(ILogger logger, string path, string error);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Cannot read {Path}: {Error}")]
    public static partial void CannotRead(ILogger logger, string path, string error);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Opened file: {Path} ({Size} chars)")]
    public static partial void OpenedFile(ILogger logger, string path, int size);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error, Message = "Failed to open {Path}")]
    public static partial void OpenFailed(ILogger logger, Exception ex, string path);

    [LoggerMessage(EventId = 5, Level = LogLevel.Error, Message = "Save failed for {Path}: {Error}")]
    public static partial void SaveFailed(ILogger logger, string path, string error);

    [LoggerMessage(EventId = 6, Level = LogLevel.Information, Message = "Saved {Path}")]
    public static partial void Saved(ILogger logger, string path);

    [LoggerMessage(EventId = 7, Level = LogLevel.Error, Message = "Save failed")]
    public static partial void SaveThrew(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 8, Level = LogLevel.Warning, Message = "LSP close dropped for {Path}")]
    public static partial void LspCloseDropped(ILogger logger, Exception ex, string path);

    [LoggerMessage(EventId = 9, Level = LogLevel.Warning, Message = "LSP change notification dropped for {Path}")]
    public static partial void LspChangeDropped(ILogger logger, Exception ex, string path);

    [LoggerMessage(EventId = 10, Level = LogLevel.Warning, Message = "LSP open failed for {Path}")]
    public static partial void LspOpenFailed(ILogger logger, Exception ex, string path);

    [LoggerMessage(EventId = 11, Level = LogLevel.Warning, Message = "LSP open task faulted for {Path}")]
    public static partial void LspOpenTaskFaulted(ILogger logger, Exception ex, string path);

    [LoggerMessage(EventId = 12, Level = LogLevel.Warning, Message = "LSP change notification failed for {Path}")]
    public static partial void LspChangeFailed(ILogger logger, Exception ex, string path);

    [LoggerMessage(EventId = 13, Level = LogLevel.Warning, Message = "LSP close failed for {Path}")]
    public static partial void LspCloseFailed(ILogger logger, Exception ex, string path);

    [LoggerMessage(EventId = 14, Level = LogLevel.Warning, Message = "LSP diagnostics read faulted for {Path}")]
    public static partial void LspDiagnosticsReadFaulted(ILogger logger, Exception ex, string path);

    [LoggerMessage(EventId = 15, Level = LogLevel.Warning, Message = "LSP diagnostics read failed for {Path}")]
    public static partial void LspDiagnosticsReadFailed(ILogger logger, Exception ex, string path);

    [LoggerMessage(EventId = 16, Level = LogLevel.Debug, Message = "Inline edit opened: {Start}-{End} ({Length} chars)")]
    public static partial void InlineEditOpened(ILogger logger, int start, int end, int length);

    [LoggerMessage(EventId = 17, Level = LogLevel.Debug, Message = "Inline edit rejected")]
    public static partial void InlineEditRejected(ILogger logger);

    [LoggerMessage(EventId = 18, Level = LogLevel.Information, Message = "Inline edit accepted: replaced {OldLen} chars with {NewLen} chars at {Start}-{End}")]
    public static partial void InlineEditAccepted(ILogger logger, int oldLen, int newLen, int start, int end);
}
