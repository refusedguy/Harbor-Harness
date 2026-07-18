using Microsoft.AspNetCore.Components;

namespace Harbor.App.Blazor.Services;

/// <summary>
///     Catppuccin theme identifier. The UI ships with Mocha (dark),
///     Latte (light), and Macchiato.
/// </summary>
public enum HarborTheme
{
    /// <summary>Dark — the default. Background #1e1e2e.</summary>
    Mocha,
    /// <summary>Medium-dark. Background #181825.</summary>
    Macchiato,
    /// <summary>Light. Background #eff1f5.</summary>
    Latte
}

/// <summary>
///     Event args for <see cref="ThemeService.ThemeChanged"/>. Carries the
///     newly selected theme so subscribers don't need to re-read the service.
/// </summary>
public sealed class ThemeChangedEventArgs : EventArgs
{
    /// <summary>Construct the args.</summary>
    /// <param name="theme">The newly selected theme.</param>
    public ThemeChangedEventArgs(HarborTheme theme)
    {
        Theme = theme;
    }

    /// <summary>The newly selected theme.</summary>
    public HarborTheme Theme { get; }
}

/// <summary>
///     Singleton theme service. Holds the current theme and raises
///     <see cref="ThemeChanged"/> when it changes so the layout can update
///     the <c>data-theme</c> attribute on the <c>&lt;html&gt;</c> element.
/// </summary>
public sealed class ThemeService
{
    private HarborTheme _current = HarborTheme.Mocha;
    private readonly BlazorDispatcherAdapter _dispatcher;
    private readonly object _gate = new();

    /// <summary>Construct the theme service.</summary>
    /// <param name="dispatcher">Render-thread marshal adapter.</param>
    public ThemeService(BlazorDispatcherAdapter dispatcher)
    {
        _dispatcher = dispatcher;
    }

    /// <summary>The currently selected theme.</summary>
    public HarborTheme Current
    {
        get { lock (_gate) return _current; }
    }

    /// <summary>Raised on the render thread after the theme changes.</summary>
    public event EventHandler<ThemeChangedEventArgs>? ThemeChanged;

    /// <summary>Switch to a new theme. Marshals the change event to the render thread.</summary>
    /// <param name="theme">The new theme.</param>
    public async Task SetThemeAsync(HarborTheme theme)
    {
        lock (_gate)
        {
            _current = theme;
        }
        var args = new ThemeChangedEventArgs(theme);
        await _dispatcher.InvokeAsync(() => ThemeChanged?.Invoke(this, args)).ConfigureAwait(false);
    }

    /// <summary>Return the CSS data-theme attribute value for the current theme.</summary>
    public string DataThemeValue => Current.ToString().ToLowerInvariant();
}

/// <summary>
///     Dialog service — opens modal dialogs (confirm, prompt, file picker) over
///     the current page. Components subscribe to <see cref="Changed"/>
///     to render the actual modal HTML.
/// </summary>
public sealed class DialogService
{
    private readonly BlazorDispatcherAdapter _dispatcher;
    private DialogRequest? _current;

    /// <summary>Construct the dialog service.</summary>
    /// <param name="dispatcher">Render-thread marshal adapter.</param>
    public DialogService(BlazorDispatcherAdapter dispatcher)
    {
        _dispatcher = dispatcher;
    }

    /// <summary>Currently open dialog request, or <see langword="null"/> if none.</summary>
    public DialogRequest? Current => _current;

    /// <summary>Raised when a dialog is opened or closed.</summary>
    public event EventHandler? Changed;

    /// <summary>Open a confirmation dialog and await the user's response.</summary>
    /// <param name="title">Dialog title.</param>
    /// <param name="message">Dialog body text.</param>
    /// <param name="okLabel">Label for the confirm button (default "OK").</param>
    /// <param name="cancelLabel">Label for the cancel button (default "Cancel").</param>
    /// <returns><see langword="true"/> if the user confirmed; otherwise <see langword="false"/>.</returns>
    public async Task<bool> ConfirmAsync(string title, string message, string okLabel = "OK", string cancelLabel = "Cancel")
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var req = new DialogRequest(
            Title: title,
            Message: message,
            OkLabel: okLabel,
            CancelLabel: cancelLabel,
            OnOk: () => tcs.TrySetResult(true),
            OnCancel: () => tcs.TrySetResult(false));
        await OpenAsync(req).ConfigureAwait(false);
        return await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>Open a dialog request and notify subscribers.</summary>
    public async Task OpenAsync(DialogRequest request)
    {
        _current = request;
        await _dispatcher.InvokeAsync(() => Changed?.Invoke(this, EventArgs.Empty)).ConfigureAwait(false);
    }

    /// <summary>Close the current dialog (no action).</summary>
    public async Task CloseAsync()
    {
        _current = null;
        await _dispatcher.InvokeAsync(() => Changed?.Invoke(this, EventArgs.Empty)).ConfigureAwait(false);
    }
}

/// <summary>Immutable dialog request.</summary>
public sealed record DialogRequest(
    string Title,
    string Message,
    string OkLabel,
    string CancelLabel,
    Action OnOk,
    Action OnCancel);
/// <summary>
///     Toast notification service. Pushes transient messages to a
///     <see cref="ToastContainer"/> component. Toasts auto-dismiss after
///     4 seconds by default.
/// </summary>
public sealed class ToastService
{
    private readonly BlazorDispatcherAdapter _dispatcher;
    private readonly List<Toast> _toasts = new();
    private int _nextId = 1;

    /// <summary>Construct the toast service.</summary>
    /// <param name="dispatcher">Render-thread marshal adapter.</param>
    public ToastService(BlazorDispatcherAdapter dispatcher)
    {
        _dispatcher = dispatcher;
    }

    /// <summary>Current visible toasts (snapshot).</summary>
    public IReadOnlyList<Toast> Current
    {
        get { lock (_toasts) return _toasts.ToArray(); }
    }

    /// <summary>Raised when a toast is added or removed.</summary>
    public event EventHandler? Changed;

    /// <summary>Push an informational toast.</summary>
    public Task InfoAsync(string message, string? title = null) => PushAsync(ToastLevel.Info, message, title);

    /// <summary>Push a success toast.</summary>
    public Task SuccessAsync(string message, string? title = null) => PushAsync(ToastLevel.Success, message, title);

    /// <summary>Push a warning toast.</summary>
    public Task WarnAsync(string message, string? title = null) => PushAsync(ToastLevel.Warn, message, title);

    /// <summary>Push an error toast (no auto-dismiss).</summary>
    public Task ErrorAsync(string message, string? title = null) => PushAsync(ToastLevel.Error, message, title, autoDismiss: false);

    /// <summary>Dismiss a toast by id.</summary>
    public async Task DismissAsync(int id)
    {
        lock (_toasts)
        {
            int idx = _toasts.FindIndex(t => t.Id == id);
            if (idx >= 0) _toasts.RemoveAt(idx);
        }
        await _dispatcher.InvokeAsync(() => Changed?.Invoke(this, EventArgs.Empty)).ConfigureAwait(false);
    }

    private async Task PushAsync(ToastLevel level, string message, string? title, bool autoDismiss = true)
    {
        int id;
        lock (_toasts)
        {
            id = _nextId++;
            _toasts.Add(new Toast(id, level, title ?? level.ToString(), message, DateTimeOffset.UtcNow));
        }
        await _dispatcher.InvokeAsync(() => Changed?.Invoke(this, EventArgs.Empty)).ConfigureAwait(false);

        if (autoDismiss)
        {
            _ = Task.Delay(TimeSpan.FromSeconds(4)).ContinueWith(_ => DismissAsync(id));
        }
    }
}

/// <summary>Toast severity level.</summary>
public enum ToastLevel
{
    Info,
    Success,
    Warn,
    Error
}

/// <summary>Immutable toast record.</summary>
public sealed record Toast(int Id, ToastLevel Level, string Title, string Message, DateTimeOffset At);

/// <summary>
///     Command palette service (Ctrl+P fuzzy finder). Holds the list of
///     available commands, opens/closes the palette overlay, and tracks the
///     current query + selected index. Use <see cref="Register"/>
///     on app start to seed the default navigation commands; pages register
///     their own commands in <c>OnInitialized</c>.
/// </summary>
public sealed class CommandPaletteService
{
    private readonly BlazorDispatcherAdapter _dispatcher;
    private bool _isOpen;
    private string _query = string.Empty;
    private int _selectedIndex;
    private readonly List<CommandEntry> _commands = new();

    /// <summary>Construct the service.</summary>
    /// <param name="dispatcher">Render-thread marshal adapter.</param>
    public CommandPaletteService(BlazorDispatcherAdapter dispatcher)
    {
        _dispatcher = dispatcher;
        SeedDefaultCommands();
    }

    /// <summary>Whether the palette overlay is currently open.</summary>
    public bool IsOpen => _isOpen;

    /// <summary>Current search query.</summary>
    public string Query => _query;

    /// <summary>Index of the highlighted command in the filtered list.</summary>
    public int SelectedIndex => _selectedIndex;

    /// <summary>Raised when the palette state changes (open/close/query/selection).</summary>
    public event EventHandler? Changed;

    /// <summary>All registered commands.</summary>
    public IReadOnlyList<CommandEntry> Commands => _commands;

    /// <summary>Filtered list based on the current query (case-insensitive substring match).</summary>
    public IReadOnlyList<CommandEntry> Filtered
    {
        get
        {
            string q = _query.Trim();
            if (q.Length == 0) return _commands;
            var matches = new List<CommandEntry>();
            foreach (var c in _commands)
            {
                if (c.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    c.Hint?.Contains(q, StringComparison.OrdinalIgnoreCase) == true)
                {
                    matches.Add(c);
                }
            }
            return matches;
        }
    }

    /// <summary>Register a new command. Idempotent by <see cref="CommandEntry.Id"/>.</summary>
    public void Register(CommandEntry entry)
    {
        if (_commands.Exists(c => c.Id == entry.Id)) return;
        _commands.Add(entry);
    }

    /// <summary>Open the palette and reset the query.</summary>
    public async Task OpenAsync()
    {
        _isOpen = true;
        _query = string.Empty;
        _selectedIndex = 0;
        await _dispatcher.InvokeAsync(() => Changed?.Invoke(this, EventArgs.Empty)).ConfigureAwait(false);
    }

    /// <summary>Close the palette.</summary>
    public async Task CloseAsync()
    {
        _isOpen = false;
        await _dispatcher.InvokeAsync(() => Changed?.Invoke(this, EventArgs.Empty)).ConfigureAwait(false);
    }

    /// <summary>Toggle open/closed.</summary>
    public async Task ToggleAsync()
    {
        if (_isOpen) await CloseAsync().ConfigureAwait(false);
        else await OpenAsync().ConfigureAwait(false);
    }

    /// <summary>Update the query and reset the selection.</summary>
    public async Task SetQueryAsync(string query)
    {
        _query = query;
        _selectedIndex = 0;
        await _dispatcher.InvokeAsync(() => Changed?.Invoke(this, EventArgs.Empty)).ConfigureAwait(false);
    }

    /// <summary>Move the selection up/down by the supplied delta (wraps around).</summary>
    public async Task MoveSelectionAsync(int delta)
    {
        var filtered = Filtered;
        if (filtered.Count == 0) return;
        _selectedIndex = (_selectedIndex + delta) % filtered.Count;
        if (_selectedIndex < 0) _selectedIndex += filtered.Count;
        await _dispatcher.InvokeAsync(() => Changed?.Invoke(this, EventArgs.Empty)).ConfigureAwait(false);
    }

    /// <summary>Execute the currently selected command and close the palette.</summary>
    public async Task ExecuteSelectedAsync()
    {
        var filtered = Filtered;
        if (_selectedIndex < 0 || _selectedIndex >= filtered.Count) return;
        await filtered[_selectedIndex].ExecuteAsync().ConfigureAwait(false);
        await CloseAsync().ConfigureAwait(false);
    }

    private void SeedDefaultCommands()
    {
        // Navigation commands are seeded with empty actions — the
        // CommandPalette.razor component rewrites their ExecuteAsync on
        // first render using the resolved NavigationManager (circuit-scoped).
        _commands.Add(new CommandEntry("nav.chat", "Go to Chat", "Open the chat page", () => { }));
        _commands.Add(new CommandEntry("nav.sessions", "Go to Sessions", "Browse saved sessions", () => { }));
        _commands.Add(new CommandEntry("nav.providers", "Go to Providers", "Browse configured LLM providers", () => { }));
        _commands.Add(new CommandEntry("nav.settings", "Go to Settings", "Edit app settings", () => { }));
        _commands.Add(new CommandEntry("nav.code", "Go to Code Editor", "Open the Monaco editor", () => { }));
        _commands.Add(new CommandEntry("nav.diff", "Go to Diff Viewer", "Compare two text snippets", () => { }));
        _commands.Add(new CommandEntry("nav.tokens", "Go to Token Usage", "View token/cost chart", () => { }));
        _commands.Add(new CommandEntry("theme.toggle", "Toggle Theme", "Switch dark/light theme", () => { }));
    }
}

/// <summary>One command in the palette.</summary>
public sealed record CommandEntry
{
    /// <summary>Stable id used for deduplication and keyboard assignment.</summary>
    public string Id { get; init; }

    /// <summary>Display title shown in the palette list.</summary>
    public string Title { get; init; }

    /// <summary>Optional hint / subtitle shown muted under the title.</summary>
    public string? Hint { get; init; }

    /// <summary>Optional navigation URI. If set, ExecuteAsync navigates here.</summary>
    public string? NavigateUri { get; init; }

    /// <summary>Optional custom action. Runs after navigation (if any).</summary>
    public Func<Task>? Action { get; init; }

    /// <summary>Execute the command (navigate + run action).</summary>
    public Task ExecuteAsync() => Action?.Invoke() ?? Task.CompletedTask;

    /// <summary>Create a navigation-only command.</summary>
    public CommandEntry(string id, string title, string? hint, string navigateUri)
    {
        Id = id;
        Title = title;
        Hint = hint;
        NavigateUri = navigateUri;
    }

    /// <summary>Create an action-only command.</summary>
    public CommandEntry(string id, string title, string? hint, Action action)
    {
        Id = id;
        Title = title;
        Hint = hint;
        Action = () => { action(); return Task.CompletedTask; };
    }

    /// <summary>Create a fully custom command.</summary>
    public CommandEntry(string id, string title, string? hint, Func<Task> action)
    {
        Id = id;
        Title = title;
        Hint = hint;
        Action = action;
    }
}
