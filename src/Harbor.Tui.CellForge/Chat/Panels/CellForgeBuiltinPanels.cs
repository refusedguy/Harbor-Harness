using System.Globalization;
using Harbor.Ui.Framework.Diagnostics;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;
using Microsoft.Extensions.Logging;

namespace Harbor.Tui.CellForge.Panels;

// Cell-native builtin panels for the CellForge renderer (CF-E-002, TOP-1 #27).
// <remarks>
//     Same 7 panel contracts as the SpectreTUI builtins (identical
//     Id / Title / DefaultPlacement / DefaultSize so Alt+1..9 slots and
//     EnsureSeeded defaults line up), but rendered as plain cell rows
//     (IReadOnlyList<string>) instead of Spectre widgets. There is
//     intentionally no reference to contrib/tui/Harbor.Tui.SpectreTui here
//     (the architecture matrix forbids it) — CellForgePanelAdapter already
//     flattens string / IReadOnlyList<string> widgets without a ToString
//     round-trip.
//     Purity: every Build reads only ctx.State (+ DI services for help/logs,
//     the filesystem for file-tree) and returns freshly allocated rows — safe
//     to call from the render thread. OnKey never mutates UiState; state
//     transitions go through UiStore.Dispatch resolved from ctx.Services. A
//     null service provider degrades gracefully: the key is still reported as
//     consumed, help/logs render fallback rows.
// </remarks>

// ── todo-list (Right/40, pure) ─────────────────────────────────────────────

/// <summary>
///     Cell-native todo-list panel: the freshest <c>[ ]</c> / <c>[~]</c> /
///     <c>[x]</c> block parsed from the transcript via
///     <see cref="PanelExtractors.ExtractTodos(UiState)"/>. Non-interactive.
/// </summary>
public sealed class CellForgeTodoListPanel : IPanelProvider
{
    /// <inheritdoc />
    public string Id => "todo-list";

    /// <inheritdoc />
    public string Title => "Todo List";

    /// <inheritdoc />
    public TuiPanelPlacement DefaultPlacement => TuiPanelPlacement.Right;

    /// <inheritdoc />
    public int DefaultSize => 40;

    /// <inheritdoc />
    public object? Build(PanelContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var todos = PanelExtractors.ExtractTodos(ctx.State);
        var rows = new List<string>(todos.Count + 4);
        rows.Add($"Todo List ({todos.Count} items)");
        rows.Add(CellPanelText.Separator);
        if (todos.Count == 0)
        {
            rows.Add("No todos yet.");
            rows.Add("Ask the agent to use the todo tool.");
        }
        else
        {
            int done = 0;
            int active = 0;
            int pending = 0;
            for (int i = 0; i < todos.Count; i++)
            {
                string marker = todos[i].Marker;
                switch (marker)
                {
                    case "[x]":
                    case "[X]":
                        done++;
                        break;
                    case "[~]":
                        active++;
                        break;
                    case "[ ]":
                        pending++;
                        break;
                }

                rows.Add($"{marker} {todos[i].Content}");
            }

            rows.Add(CellPanelText.Separator);
            rows.Add($"Done {done} · active {active} · pending {pending}");
        }

        return CellPanelText.Clip(rows, ctx.Width, ctx.Height);
    }

    /// <inheritdoc />
    public bool OnKey(UiKey key, PanelContext ctx) => false;
}

// ── diff-preview (Bottom/12, pure) ─────────────────────────────────────────

/// <summary>
///     Cell-native diff-preview panel: recent <c>edit</c> / <c>write</c> /
///     <c>read</c> / <c>patch</c> tool calls paired with their results via
///     <see cref="PanelExtractors.ExtractRecentChanges(UiState, int)"/>. One header
///     row per change (<c>tool-icon ok-icon path</c>) plus up to 4 diff body lines.
///     Non-interactive.
/// </summary>
public sealed class CellForgeDiffPreviewPanel : IPanelProvider
{
    /// <inheritdoc />
    public string Id => "diff-preview";

    /// <inheritdoc />
    public string Title => "Diff Preview";

    /// <inheritdoc />
    public TuiPanelPlacement DefaultPlacement => TuiPanelPlacement.Bottom;

    /// <inheritdoc />
    public int DefaultSize => 12;

    /// <inheritdoc />
    public object? Build(PanelContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var changes = PanelExtractors.ExtractRecentChanges(ctx.State, 8);
        var rows = new List<string>(changes.Count * 5 + 4);
        rows.Add($"Diff Preview ({changes.Count} recent change(s))");
        rows.Add(CellPanelText.Separator);
        if (changes.Count == 0)
        {
            rows.Add("No file edits yet.");
            rows.Add("Edits made by the agent will appear here.");
        }
        else
        {
            for (int i = 0; i < changes.Count; i++)
            {
                var change = changes[i];
                string icon = change.ToolName switch
                {
                    "edit" => "✎",
                    "write" => "✚",
                    "read" => "▸",
                    "patch" => "⌥",
                    _ => "·",
                };
                string ok = change.IsError ? "✗" : "✓";
                string path = CellPanelText.ShortenTail(change.FilePath, Math.Max(4, ctx.Width - 12));
                rows.Add($"{icon} {ok} {path}");
                if (!string.IsNullOrEmpty(change.DiffBody))
                {
                    string body = change.DiffBody;
                    int start = 0;
                    for (int shown = 0; shown < 4 && start < body.Length; shown++)
                    {
                        int nl = body.IndexOf('\n', start);
                        string line = nl < 0 ? body[start..] : body[start..nl];
                        start = nl < 0 ? body.Length : nl + 1;
                        rows.Add("  " + line.TrimEnd('\r'));
                    }
                }
            }
        }

        return CellPanelText.Clip(rows, ctx.Width, ctx.Height);
    }

    /// <inheritdoc />
    public bool OnKey(UiKey key, PanelContext ctx) => false;
}

// ── diagnostics (Bottom/10, pure — cursor navigation lands later) ──────────

/// <summary>
///     Cell-native diagnostics panel: transcript errors classified via
///     <see cref="PanelExtractors.CollectDiagnostics(UiState)"/>, one row per issue
///     (<c>✗ message</c> for errors, <c>▲ message</c> for warnings).
///     Deliberately cursor-free (pure <c>Build</c>, <c>OnKey</c> returns
///     <see langword="false"/>); j/k navigation lands in a follow-up.
/// </summary>
public sealed class CellForgeDiagnosticsPanel : IPanelProvider
{
    /// <inheritdoc />
    public string Id => "diagnostics";

    /// <inheritdoc />
    public string Title => "Diagnostics";

    /// <inheritdoc />
    public TuiPanelPlacement DefaultPlacement => TuiPanelPlacement.Bottom;

    /// <inheritdoc />
    public int DefaultSize => 10;

    /// <inheritdoc />
    public object? Build(PanelContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var diagnostics = PanelExtractors.CollectDiagnostics(ctx.State);
        var rows = new List<string>(diagnostics.Count + 4);
        rows.Add($"Diagnostics ({diagnostics.Count} issue(s))");
        rows.Add(CellPanelText.Separator);
        if (diagnostics.Count == 0)
        {
            rows.Add("No diagnostics detected.");
            rows.Add("Errors emitted by the `bash` tool will show up here.");
        }
        else
        {
            int maxVisible = Math.Max(2, ctx.Height - 4);
            int end = Math.Min(diagnostics.Count, maxVisible);
            for (int i = 0; i < end; i++)
            {
                var diagnostic = diagnostics[i];
                string icon = diagnostic.Severity == PanelDiagnosticSeverity.Warning ? "▲" : "✗";
                rows.Add($"{icon} {diagnostic.Message}");
            }

            rows.Add(CellPanelText.Separator);
            rows.Add("read-only · cursor navigation lands in a follow-up");
        }

        return CellPanelText.Clip(rows, ctx.Width, ctx.Height);
    }

    /// <inheritdoc />
    public bool OnKey(UiKey key, PanelContext ctx) => false;
}

// ── token-breakdown (Bottom/10, pure) ──────────────────────────────────────

/// <summary>
///     Cell-native token-breakdown panel: cumulative <see cref="UiState.Cost"/>
///     totals with <c>█</c> / <c>░</c> bars and K/M formatting.
///     Non-interactive.
/// </summary>
public sealed class CellForgeTokenBreakdownPanel : IPanelProvider
{
    /// <inheritdoc />
    public string Id => "token-breakdown";

    /// <inheritdoc />
    public string Title => "Token Breakdown";

    /// <inheritdoc />
    public TuiPanelPlacement DefaultPlacement => TuiPanelPlacement.Bottom;

    /// <inheritdoc />
    public int DefaultSize => 10;

    /// <inheritdoc />
    public object? Build(PanelContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        long input = ctx.State.Cost.TokensIn;
        long output = ctx.State.Cost.TokensOut;
        decimal cost = ctx.State.Cost.CostUsd;
        long scale = Math.Max(input, Math.Max(output, 1));
        int barWidth = Math.Max(0, ctx.Width - 24);
        var rows = new List<string>(7);
        rows.Add("Token Breakdown");
        rows.Add(CellPanelText.Separator);
        rows.Add($"in    {Format(input).PadLeft(12)}  {Bar(input, barWidth, scale)}".TrimEnd());
        rows.Add($"out   {Format(output).PadLeft(12)}  {Bar(output, barWidth, scale)}".TrimEnd());
        rows.Add(CellPanelText.Separator);
        rows.Add($"total {Format(input + output).PadLeft(12)}  ${cost.ToString("F4", CultureInfo.InvariantCulture)}");
        rows.Add("(cumulative session totals)");
        return CellPanelText.Clip(rows, ctx.Width, ctx.Height);
    }

    /// <inheritdoc />
    public bool OnKey(UiKey key, PanelContext ctx) => false;

    private static string Format(long n) =>
        n >= 1_000_000
            ? (n / 1_000_000.0).ToString("F2", CultureInfo.InvariantCulture) + "M"
            : n >= 1_000
                ? (n / 1_000.0).ToString("F1", CultureInfo.InvariantCulture) + "K"
                : n.ToString(CultureInfo.InvariantCulture);

    private static string Bar(long value, int width, long scale)
    {
        if (width <= 0)
        {
            return string.Empty;
        }

        int filled = (int)((double)value / scale * width);
        if (filled > width)
        {
            filled = width;
        }

        if (filled < 0)
        {
            filled = 0;
        }

        return new string('█', filled) + new string('░', width - filled);
    }
}

// ── help (Right/48, '?' toggles) ───────────────────────────────────────────

/// <summary>
///     Cell-native help panel: static hotkey text plus one row per registered panel
///     (from <see cref="IPanelRegistry"/> in <c>ctx.Services</c>) plus the slash
///     command list. <c>?</c> while focused dispatches
///     <c>UiMsg.TogglePanel("help")</c>.
/// </summary>
public sealed class CellForgeHelpPanel : IPanelProvider
{
    /// <inheritdoc />
    public string Id => "help";

    /// <inheritdoc />
    public string Title => "Help";

    /// <inheritdoc />
    public TuiPanelPlacement DefaultPlacement => TuiPanelPlacement.Right;

    /// <inheritdoc />
    public int DefaultSize => 48;

    /// <inheritdoc />
    public object? Build(PanelContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var rows = new List<string>(32);
        rows.Add("Harbor — keymap & panels");
        rows.Add(CellPanelText.Separator);
        rows.Add("Hotkeys");
        rows.Add("  Alt+1..9   toggle Nth panel");
        rows.Add("  Ctrl+Tab   cycle panel focus");
        rows.Add("  Ctrl+Up/Down  grow / shrink focused panel");
        rows.Add("  q / Esc    return focus to chat");
        rows.Add("  ?          toggle this help panel");
        rows.Add("  F2         toggle input/chat focus");
        rows.Add("  F12        toggle logs panel (live ILogger output)");
        rows.Add("  Ctrl+L     clear transcript");
        rows.Add("  Ctrl+C     abort running agent");
        rows.Add("  Esc        quit");
        rows.Add(string.Empty);
        rows.Add("Panels");
        var registry = ctx.Services?.GetService(typeof(IPanelRegistry)) as IPanelRegistry;
        if (registry is null || registry.All.Count == 0)
        {
            rows.Add("  (no panels)");
        }
        else
        {
            var all = registry.All;
            for (int i = 0; i < all.Count; i++)
            {
                var panel = all[i];
                bool isFocused = panel.Id == ctx.State.FocusedPanelId;
                var state = ctx.State.PanelStates.TryGetValue(panel.Id, out var ps) ? ps : TuiPanelState.Hidden;
                string stateText = isFocused ? "*focused*" : state == TuiPanelState.Hidden ? "hidden" : "visible";
                string slot = i < 9 ? $"Alt+{i + 1}" : "      ";
                rows.Add($"  {slot}  {panel.Id}  {panel.Title}  {stateText}");
            }
        }

        rows.Add(string.Empty);
        rows.Add("Slash commands");
        foreach (string cmd in ChatCommands.Slash)
        {
            rows.Add($"  {cmd}");
        }

        rows.Add(string.Empty);
        rows.Add("Press ? to close this panel.");
        return CellPanelText.Clip(rows, ctx.Width, ctx.Height);
    }

    /// <inheritdoc />
    public bool OnKey(UiKey key, PanelContext ctx)
    {
        if (key.Code == UiKeyCode.Char && key.Character == '?')
        {
            if (ctx.Services?.GetService(typeof(UiStore)) is UiStore store)
            {
                _ = store.Dispatch(new UiMsg.TogglePanel(Id));
            }

            return true;
        }

        return false;
    }
}

// ── logs (Bottom/10, F12 toggles) ──────────────────────────────────────────

/// <summary>
///     Cell-native logs panel: live <c>ILogger</c> output surfaced from
///     <see cref="IDiagnosticsPanel"/> in <c>ctx.Services</c>. <c>F12</c> while
///     focused dispatches <c>UiMsg.TogglePanel("logs")</c>.
/// </summary>
public sealed class CellForgeLogsPanel : IPanelProvider
{
    /// <inheritdoc />
    public string Id => "logs";

    /// <inheritdoc />
    public string Title => "Logs";

    /// <inheritdoc />
    public TuiPanelPlacement DefaultPlacement => TuiPanelPlacement.Bottom;

    /// <inheritdoc />
    public int DefaultSize => 10;

    /// <inheritdoc />
    public object? Build(PanelContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var rows = new List<string>(16);
        rows.Add("Logs (F12 to hide · live ILogger output · file at ~/.harbor/logs/)");
        rows.Add(CellPanelText.Separator);
        var panel = ctx.Services?.GetService(typeof(IDiagnosticsPanel)) as IDiagnosticsPanel;
        if (panel is null)
        {
            rows.Add("Diagnostics panel not registered.");
            rows.Add("HostBuilder registers IDiagnosticsPanel for interactive TUIs.");
            return CellPanelText.Clip(rows, ctx.Width, ctx.Height);
        }

        int maxVisible = Math.Max(2, ctx.Height - 4);
        int requested = Math.Min(maxVisible, 50);
        var entries = panel.GetRecent(requested);
        if (entries.Count == 0)
        {
            rows.Add("No log entries yet.");
            rows.Add("Logs from every ILogger will appear here in arrival order.");
            return CellPanelText.Clip(rows, ctx.Width, ctx.Height);
        }

        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            string levelTag = entry.Level switch
            {
                LogLevel.Trace => "TRAC",
                LogLevel.Debug => "DBUG",
                LogLevel.Information => "INFO",
                LogLevel.Warning => "WARN",
                LogLevel.Error => "ERRO",
                LogLevel.Critical => "CRIT",
                _ => "????",
            };
            string time = entry.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
            string category = ShortenCategory(entry.Category);
            int budget = ctx.Width - time.Length - levelTag.Length - category.Length - 7;
            string body = CellPanelText.SingleLine(entry.Message);
            if (budget <= 0)
            {
                body = string.Empty;
            }
            else if (body.Length > budget)
            {
                body = budget == 1 ? "…" : body[..(budget - 1)] + "…";
            }

            rows.Add($"{time} {levelTag} {category} {body}".TrimEnd());
        }

        rows.Add(CellPanelText.Separator);
        rows.Add("F12 toggle · Ctrl+L clear console (does not clear this buffer)");
        return CellPanelText.Clip(rows, ctx.Width, ctx.Height);
    }

    /// <inheritdoc />
    public bool OnKey(UiKey key, PanelContext ctx)
    {
        if (key.Code == UiKeyCode.F12)
        {
            if (ctx.Services?.GetService(typeof(UiStore)) is UiStore store)
            {
                _ = store.Dispatch(new UiMsg.TogglePanel(Id));
            }

            return true;
        }

        return false;
    }

    private static string ShortenCategory(string category)
    {
        if (string.IsNullOrEmpty(category))
        {
            return "-";
        }

        int lastDot = category.LastIndexOf('.');
        return lastDot >= 0 && lastDot < category.Length - 1 ? category[(lastDot + 1)..] : category;
    }
}

// ── file-tree (Left/32, j/k/h/r/Enter) ─────────────────────────────────────

/// <summary>
///     Cell-native file-tree panel: read-only listing of the working directory,
///     directories first. <c>j</c> / <c>k</c> move, <c>h</c> goes to the parent,
///     <c>r</c> refreshes, <c>Enter</c> descends into directories or dispatches
///     <c>UiMsg.KeyInput(Submit)</c> for files (the host slash handler routes it to
///     the <c>read</c> tool).
/// </summary>
/// <remarks>
///     TODO(principles)[FP-005, TEA]: cursor + directory cache are provider-local
///     mutable state (same compromise as the Spectre original) instead of living in
///     <see cref="UiState"/> keyed by panel id. Guarded by a small lock so
///     <c>Build</c> (render thread) and <c>OnKey</c> (input thread) stay thread-safe;
///     moving the cursor into the store is follow-up work.
/// </remarks>
public sealed class CellForgeFileTreePanel : IPanelProvider
{
    private readonly object _gate = new();
    private string _currentDir = string.Empty;
    private int _cursor;
    private string _displayDir = string.Empty;
    private List<Entry> _entries = new();

    /// <inheritdoc />
    public string Id => "file-tree";

    /// <inheritdoc />
    public string Title => "File Tree";

    /// <inheritdoc />
    public TuiPanelPlacement DefaultPlacement => TuiPanelPlacement.Left;

    /// <inheritdoc />
    public int DefaultSize => 32;

    /// <inheritdoc />
    public object? Build(PanelContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        EnsureEntries();
        List<Entry> snapshot;
        int cursor;
        string displayDir;
        lock (_gate)
        {
            snapshot = new List<Entry>(_entries);
            cursor = _cursor;
            displayDir = _displayDir;
        }

        var rows = new List<string>(snapshot.Count + 6);
        rows.Add("File Tree");
        rows.Add(CellPanelText.ShortenTail(displayDir, Math.Max(0, ctx.Width - 2)));
        rows.Add(CellPanelText.Separator);
        if (snapshot.Count == 0)
        {
            rows.Add("(empty directory)");
            return CellPanelText.Clip(rows, ctx.Width, ctx.Height);
        }

        int maxVisible = Math.Max(2, ctx.Height - 4);
        int start = Math.Max(0, cursor - maxVisible + 1);
        int end = Math.Min(snapshot.Count, start + maxVisible);
        if (start > 0)
        {
            rows.Add("  ↑ more above");
        }

        int nameBudget = Math.Max(1, ctx.Width - 6);
        for (int i = start; i < end; i++)
        {
            var entry = snapshot[i];
            string marker = i == cursor ? ">" : " ";
            string icon = entry.IsDirectory ? "▸" : entry.IsHidden ? "·" : " ";
            rows.Add($"{marker} {icon} {CellPanelText.Truncate(entry.Name, nameBudget)}");
        }

        if (end < snapshot.Count)
        {
            rows.Add("  ↓ more below");
        }

        rows.Add(CellPanelText.Separator);
        rows.Add("j/k move · Enter open · h parent · r refresh");
        return CellPanelText.Clip(rows, ctx.Width, ctx.Height);
    }

    /// <inheritdoc />
    public bool OnKey(UiKey key, PanelContext ctx)
    {
        if (key.Code == UiKeyCode.Enter
            || (key.Code == UiKeyCode.Char && (key.Character == '\r' || key.Character == '\n')))
        {
            Entry? current;
            lock (_gate)
            {
                current = _cursor >= 0 && _cursor < _entries.Count ? _entries[_cursor] : null;
            }

            if (current is not null)
            {
                if (current.IsDirectory)
                {
                    lock (_gate)
                    {
                        _currentDir = current.FullPath;
                        _displayDir = current.FullPath;
                        _entries = new List<Entry>();
                        _cursor = 0;
                    }
                }
                else if (ctx.Services?.GetService(typeof(UiStore)) is UiStore store)
                {
                    _ = store.Dispatch(new UiMsg.KeyInput(ChatAction.Submit, UiKey.ForChar('\r')));
                }
            }

            return true;
        }

        if (key.Code != UiKeyCode.Char || key.Character is null)
        {
            return false;
        }

        switch (key.Character)
        {
            case 'j':
            case 'J':
                lock (_gate)
                {
                    if (_entries.Count > 0)
                    {
                        _cursor = Math.Min(_entries.Count - 1, _cursor + 1);
                    }
                }

                return true;
            case 'k':
            case 'K':
                lock (_gate)
                {
                    if (_entries.Count > 0)
                    {
                        _cursor = Math.Max(0, _cursor - 1);
                    }
                }

                return true;
            case 'h':
            case 'H':
                string currentDir;
                lock (_gate)
                {
                    currentDir = string.IsNullOrEmpty(_currentDir) ? Environment.CurrentDirectory : _currentDir;
                }

                if (Directory.GetParent(currentDir) is { } parent)
                {
                    lock (_gate)
                    {
                        _currentDir = parent.FullName;
                        _displayDir = parent.FullName;
                        _entries = new List<Entry>();
                        _cursor = 0;
                    }
                }

                return true;
            case 'r':
            case 'R':
                lock (_gate)
                {
                    _entries = new List<Entry>();
                }

                return true;
            default:
                return false;
        }
    }

    private void EnsureEntries()
    {
        lock (_gate)
        {
            if (_entries.Count > 0)
            {
                return;
            }
        }

        string dir;
        lock (_gate)
        {
            dir = string.IsNullOrEmpty(_currentDir) ? Environment.CurrentDirectory : _currentDir;
        }

        var fresh = new List<Entry>(32);
        try
        {
            foreach (string d in Directory.EnumerateDirectories(dir))
            {
                var info = new DirectoryInfo(d);
                fresh.Add(new Entry(
                    info.Name + Path.DirectorySeparatorChar,
                    info.FullName,
                    true,
                    (info.Attributes & FileAttributes.Hidden) != 0));
            }

            foreach (string f in Directory.EnumerateFiles(dir))
            {
                var info = new FileInfo(f);
                fresh.Add(new Entry(
                    info.Name,
                    info.FullName,
                    false,
                    (info.Attributes & FileAttributes.Hidden) != 0));
            }
        }
        catch (IOException)
        {
            // Directory not readable — publish an empty listing below.
        }
        catch (UnauthorizedAccessException)
        {
            // No permissions — publish an empty listing below.
        }

        fresh.Sort(static (a, b) =>
        {
            int cmp = b.IsDirectory.CompareTo(a.IsDirectory); // dirs first
            return cmp != 0 ? cmp : StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
        });
        lock (_gate)
        {
            if (_entries.Count == 0)
            {
                _entries = fresh;
                _currentDir = dir;
                _displayDir = dir;
                if (_cursor >= _entries.Count)
                {
                    _cursor = 0;
                }
            }
        }
    }

    private sealed record Entry(string Name, string FullPath, bool IsDirectory, bool IsHidden);
}

// ── session-sidebar (Left/32, Alt+8) ─────────────────────────────────────

/// <summary>
///     Cell-native session sidebar panel: lists all known sessions from
///     <see cref="UiState.Sessions"/> with the active session highlighted.
///     Non-interactive (read-only view).
/// </summary>
public sealed class CellForgeSessionSidebarPanel : IPanelProvider
{
    /// <inheritdoc />
    public string Id => "session-sidebar";

    /// <inheritdoc />
    public string Title => "Sessions";

    /// <inheritdoc />
    public TuiPanelPlacement DefaultPlacement => TuiPanelPlacement.Left;

    /// <inheritdoc />
    public int DefaultSize => 32;

    /// <inheritdoc />
    public object? Build(PanelContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var rows = new List<string>(ctx.State.Sessions.Length + 4);
        rows.Add("Sessions");
        rows.Add(CellPanelText.Separator);
        if (ctx.State.Sessions.Length == 0)
        {
            rows.Add("(no sessions)");
            rows.Add("Start a new session to see it here.");
        }
        else
        {
            int maxVisible = Math.Max(2, ctx.Height - 4);
            int end = Math.Min(ctx.State.Sessions.Length, maxVisible);
            for (int i = 0; i < end; i++)
            {
                var session = ctx.State.Sessions[i];
                bool isActive = session.SessionId == ctx.State.ActiveSessionId;
                string marker = isActive ? "▸" : " ";
                string line = $"{marker} {CellPanelText.Truncate(session.Title, Math.Max(1, ctx.Width - 3))}";
                rows.Add(line);
            }

            if (ctx.State.Sessions.Length > maxVisible)
            {
                rows.Add($"  ↓ {ctx.State.Sessions.Length - maxVisible} more below");
            }
        }

        rows.Add(CellPanelText.Separator);
        rows.Add(ctx.State.IsLoading ? "loading…" : $"{ctx.State.Sessions.Length} session(s)");
        return CellPanelText.Clip(rows, ctx.Width, ctx.Height);
    }

    /// <inheritdoc />
    public bool OnKey(UiKey key, PanelContext ctx) => false;
}

// ── shared row helpers ─────────────────────────────────────────────────────

/// <summary>
///     Shared plain-text row helpers for the cell-native panels: geometry clipping
///     (the adapter flattens rows as-is, so every provider clips to
///     <c>Width</c> × <c>Height</c> itself) plus truncation utilities.
/// </summary>
internal static class CellPanelText
{
    /// <summary>Plain separator stamped between panel sections.</summary>
    internal const string Separator = "────────────────────────";

    /// <summary>
    ///     Clip rows to the available geometry: at most <paramref name="height"/>
    ///     rows, each at most <paramref name="width"/> columns (hard-truncated with
    ///     <c>…</c>). Returns an empty list for non-positive geometry instead of
    ///     throwing, so tiny viewports degrade gracefully.
    /// </summary>
    internal static IReadOnlyList<string> Clip(List<string> rows, int width, int height)
    {
        if (rows.Count == 0 || width <= 0 || height <= 0)
        {
            return Array.Empty<string>();
        }

        int take = Math.Min(rows.Count, height);
        var clipped = new List<string>(take);
        for (int i = 0; i < take; i++)
        {
            string line = rows[i];
            clipped.Add(line.Length <= width ? line : Truncate(line, width));
        }

        return clipped;
    }

    /// <summary>Hard-truncate <paramref name="text"/> to <paramref name="max"/> columns.</summary>
    internal static string Truncate(string text, int max)
    {
        if (string.IsNullOrEmpty(text) || max <= 0)
        {
            return string.Empty;
        }

        if (text.Length <= max)
        {
            return text;
        }

        return max == 1 ? "…" : text[..(max - 1)] + "…";
    }

    /// <summary>Keep the tail of a path visible (filename first), prefix with <c>…</c>.</summary>
    internal static string ShortenTail(string path, int max)
    {
        if (string.IsNullOrEmpty(path) || max <= 0)
        {
            return max <= 0 ? string.Empty : path;
        }

        if (path.Length <= max)
        {
            return path;
        }

        return max == 1 ? "…" : "…" + path[^(max - 1)..];
    }

    /// <summary>Collapse a multi-line log message onto one display row.</summary>
    internal static string SingleLine(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return text.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
    }
}
