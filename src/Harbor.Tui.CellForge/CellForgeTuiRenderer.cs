using System.Collections.Immutable;
using System.ComponentModel;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Tui;
using Harbor.Terminal.Abstractions;
using Harbor.Terminal.Abstractions.Renderers;
using Harbor.Terminal.Abstractions.ViewModels;
using Harbor.Terminal.Abstractions.Views;
using Harbor.Tui.CellForge.Panels;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.Rendering;
using Harbor.Ui.Framework.State;
using Microsoft.Extensions.Logging;

namespace Harbor.Tui.CellForge;

/// <summary>
///     Adapter that exposes the CellForge engine through the standard
///     <see cref="ITuiRenderer" /> / <see cref="BaseTuiRenderer" /> contract
///     (renderer-unification sprint, Phase 2 + TEA integration). The interactive
///     raw-mode entry point remains <c>CellForgeReplRunner</c>; this adapter serves
///     the non-interactive/event-driven path so <c>HARBOR_TUI=cellforge</c>
///     produces a first-class CellForge renderer instead of falling back to
///     the Ansi backend. Output goes through <see cref="AnsiWriter"/>'s SGR
///     automaton: styles are diffed against the writer's tracked state, so
///     redundant escape codes are never emitted.
/// </summary>
[Harbor.Abstractions.Contracts.TuiRenderer(Backend = "cellforge")]
public sealed partial class CellForgeTuiRenderer : BaseTuiRenderer
{
    /// <summary>Input placeholder while the agent is idle and waiting for a prompt.</summary>
    internal const string IdlePlaceholder = "Type your message...";

    /// <summary>Input placeholder while the agent is running a prompt.</summary>
    internal const string BusyPlaceholder = "Agent is running… (Esc to stop)";

    private readonly UiStore _store;
    private readonly StatusBarViewModel _statusVm;
    private readonly ChatHistoryViewModel _chatVm;
    private readonly InputViewModel _inputVm;
    private readonly ComposerController _composer = new();
    private bool _syncingInput;

    /// <summary>
    /// CF-E-002 wiring (TOP-1 #27): renderer-owned panel registry holding the 7
    /// cell-native builtin providers (see <see cref="RegisterBuiltinPanels"/>).
    /// Registration order is significant — Alt+1..9 hotkey slots follow it.
    /// All visibility / focus / size state lives in <see cref="UiState"/> (seeded
    /// via <see cref="CellForgePanelRegistry.EnsureSeeded"/> in
    /// <see cref="InitializeAsync"/>); the registry itself is registration-only.
    /// </summary>
    public CellForgePanelRegistry Panels { get; }

    public CellForgeTuiRenderer(
        ILogger<CellForgeTuiRenderer> logger,
        StatusBarViewModel? statusVm = null,
        ChatHistoryViewModel? chatVm = null,
        InputViewModel? inputVm = null)
        : base(logger)
    {
        _store = new UiStore();
        Panels = new CellForgePanelRegistry();
        RegisterBuiltinPanels(Panels);
        _statusVm = statusVm ?? ViewModels.Get<StatusBarViewModel>("status-bar")!;
        _chatVm = chatVm ?? ViewModels.Get<ChatHistoryViewModel>("chat-history")!;
        _inputVm = ResolveInputVm(inputVm);
        Context = new CellForgeRenderContext();
    }

    /// <summary>Golden-frame test seam.</summary>
    public CellForgeTuiRenderer(
        ILogger<CellForgeTuiRenderer> logger,
        ITerminalBackend backend,
        StatusBarViewModel? statusVm = null,
        ChatHistoryViewModel? chatVm = null,
        InputViewModel? inputVm = null)
        : base(logger)
    {
        _store = new UiStore();
        Panels = new CellForgePanelRegistry();
        RegisterBuiltinPanels(Panels);
        _statusVm = statusVm ?? ViewModels.Get<StatusBarViewModel>("status-bar")!;
        _chatVm = chatVm ?? ViewModels.Get<ChatHistoryViewModel>("chat-history")!;
        _inputVm = ResolveInputVm(inputVm);
        Context = new CellForgeRenderContext(backend);
    }

    /// <summary>
    /// CF-E-002: registers the 7 cell-native builtin panels. Slot order mirrors
    /// <c>SpectreTuiRenderer.RunInteractiveAsync</c> (Alt+1..9 follow registration
    /// order): help, todo-list, diff-preview, file-tree, token-breakdown,
    /// diagnostics, logs.
    /// </summary>
    private static void RegisterBuiltinPanels(CellForgePanelRegistry panels)
    {
        panels.Register(new CellForgeHelpPanel()); // Alt+1
        panels.Register(new CellForgeTodoListPanel()); // Alt+2
        panels.Register(new CellForgeDiffPreviewPanel()); // Alt+3
        panels.Register(new CellForgeFileTreePanel()); // Alt+4
        panels.Register(new CellForgeTokenBreakdownPanel()); // Alt+5
        panels.Register(new CellForgeDiagnosticsPanel()); // Alt+6
        panels.Register(new CellForgeLogsPanel()); // Alt+7
    }

    /// <summary>
    ///     Resolves the input view model (explicit instance or the registry default),
    ///     keeps field/registry/bound-view coherent, and wires the two-way binding
    ///     with the composer buffer.
    /// </summary>
    private InputViewModel ResolveInputVm(InputViewModel? inputVm)
    {
        var resolved = inputVm ?? ViewModels.Get<InputViewModel>("input")!;
        ViewModels.Register(resolved);
        resolved.PropertyChanged += OnInputVmChanged;
        return resolved;
    }

    public override ITuiRenderContext Context { get; }

    // CF-F-001: intentionally no ShouldRenderPlacement override — the base filter
    // already paints the ChatHistory/Input placements, and both builtin views write
    // only through ITuiRenderContext (CellForgeRenderContext/AnsiWriter), so no
    // CellForge fallback painter is needed.

    public override Task<Result> InitializeAsync(CancellationToken ct = default)
    {
        try
        {
            _store.Changed += OnStoreChanged;
            // CF-E-002: seed registered panel ids + Hidden states + DefaultSizes
            // into UiState so the reducer becomes the single source of truth.
            // Already-known states/sizes survive re-seeding (plugin reload path).
            _ = Panels.EnsureSeeded(_store);
            Context.HideCursor();
            return base.InitializeAsync(ct);
        }
        catch (Exception ex)
        {
            return Task.FromResult(Result.Failure(ex.Message));
        }
    }

    private void OnStoreChanged(object? sender, UiStateChangedEventArgs e)
    {
        ProjectStateIntoWidgets(e.State);
    }

    public override Task RenderAsync(AgentEvent @event, CancellationToken ct = default)
    {
        _store.Dispatch(@event);
        return base.RenderAsync(@event, ct);
    }

    private static void RenderLiveToken(LlmEvent evt, ITuiRenderContext ctx)
    {
        switch (evt)
        {
            case TextDeltaEvent td:
                ctx.Write(td.Delta);
                break;

            case ThinkingDeltaEvent thd:
                ctx.WriteStyled(thd.Delta, TuiStyle.Dim | TuiStyle.Italic);
                break;

            case ToolCallDeltaEvent tcd:
                ctx.WriteStyled(tcd.ArgsDelta, TuiStyle.Dim);
                break;
        }
    }

    /// <summary>Composer buffer mirrored from <see cref="UiState.Input"/> (test seam).</summary>
    internal PromptBuffer PromptBuffer => _composer.Buffer;

    /// <summary>TEA store owning the <see cref="UiState"/> snapshot (test seam for panel-seeding assertions).</summary>
    internal UiStore Store => _store;

    /// <summary>Last projected session list (test seam; SideBarView/QuickSwitchSlots wiring is CF-B-008).</summary>
    internal ImmutableArray<SessionInfo> SessionsSnapshot { get; private set; } = ImmutableArray<SessionInfo>.Empty;

    /// <summary>Last projected active session id (test seam, see <see cref="SessionsSnapshot"/>).</summary>
    internal SessionId? ActiveSessionIdSnapshot { get; private set; }

    /// <summary>Last projected session-list loading flag (test seam, see <see cref="SessionsSnapshot"/>).</summary>
    internal bool SessionsLoading { get; private set; }

    internal void ProjectStateIntoWidgets(UiState state)
    {
        if (_statusVm is StatusBarViewModel svm)
        {
            svm.Status = state.Status;
            svm.Model = state.Model;
            svm.Provider = state.Provider;
            svm.Agent = state.AgentName;
            svm.TokensIn = (int)Math.Min(state.Cost.TokensIn, int.MaxValue);
            svm.TokensOut = (int)Math.Min(state.Cost.TokensOut, int.MaxValue);
            svm.Cost = state.Cost.CostUsd;
        }

        if (_chatVm is ChatHistoryViewModel chvm)
        {
            chvm.IsStreaming = state.IsStreaming;
            chvm.StreamingText = state.Active.TextBuffer;
            chvm.ThinkingText = state.Active.ThinkBuffer;
            chvm.IsThinking = state.Active.ThinkBuffer.Length != 0;
        }

        SyncInputFromState(state);

        SessionsSnapshot = state.Sessions;
        ActiveSessionIdSnapshot = state.ActiveSessionId;
        SessionsLoading = state.IsLoading;
        // TODO(CF-B-008): project Sessions/ActiveSessionId/IsLoading into
        // SideBarView + QuickSwitchSlots instead of these snapshot fields.
    }

    /// <summary>
    ///     Store → VM + composer-buffer sync: draft text and idle/busy placeholder
    ///     flow from <see cref="UiState"/> (the source of truth). The
    ///     <c>_syncingInput</c> flag suppresses the <see cref="OnInputVmChanged"/>
    ///     echo while VM properties are being applied.
    ///     NOTE(CF-B-005): <c>InputModel.History/HistoryIndex</c> are intentionally
    ///     not mirrored here — history recall stays in
    ///     <c>PromptHistory/ComposerController</c> until CF-B-005.
    /// </summary>
    private void SyncInputFromState(UiState state)
    {
        _syncingInput = true;
        try
        {
            string text = state.Input.Text ?? string.Empty;
            if (_inputVm.Text != text)
            {
                _inputVm.Text = text;
                _inputVm.CursorPosition = text.Length;
            }

            _inputVm.Placeholder = state.IsAgentRunning ? BusyPlaceholder : IdlePlaceholder;

            if (_composer.Buffer.SnapshotText() != text)
            {
                _composer.Buffer.Clear();
                if (text.Length != 0)
                    _ = _composer.Buffer.InsertText(text);
            }

            _ = _composer.Buffer.MoveTo(Math.Clamp(_inputVm.CursorPosition, 0, _composer.Buffer.Length));
        }
        finally
        {
            _syncingInput = false;
        }
    }

    /// <summary>
    ///     VM → composer-buffer sync: user edits applied to the
    ///     <see cref="InputViewModel"/> (text or caret) are mirrored into the
    ///     composer buffer so the interactive prompt paints the same draft.
    /// </summary>
    private void OnInputVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_syncingInput)
            return;

        if (e.PropertyName is not (nameof(InputViewModel.Text) or nameof(InputViewModel.CursorPosition)))
            return;

        string text = _inputVm.Text ?? string.Empty;
        if (_composer.Buffer.SnapshotText() != text)
        {
            _composer.Buffer.Clear();
            if (text.Length != 0)
                _ = _composer.Buffer.InsertText(text);
        }

        _ = _composer.Buffer.MoveTo(Math.Clamp(_inputVm.CursorPosition, 0, _composer.Buffer.Length));
    }

    public override void Dispose()
    {
        _inputVm.PropertyChanged -= OnInputVmChanged;
        _store.Changed -= OnStoreChanged;
        Context.ShowCursor();
        base.Dispose();
    }

    public override Task<Result<string>> ReadLineAsync(string prompt, CancellationToken ct = default)
    {
        Context.ShowCursor();
        Context.WriteColored(prompt, TuiColor.Green);
        string? line = Console.ReadLine();
        Context.HideCursor();
        return Task.FromResult(Result.Success(line ?? string.Empty));
    }

    public override Task<Result> WriteAsync(string text, CancellationToken ct = default)
    {
        Context.Write(text);
        return Task.FromResult(Result.Success());
    }

    public override Task<Result> WriteLineAsync(string? text = null, CancellationToken ct = default)
    {
        Context.WriteLine(text);
        return Task.FromResult(Result.Success());
    }

    public override Task<Result> ClearAsync(CancellationToken ct = default)
    {
        Context.Clear();
        return Task.FromResult(Result.Success());
    }
}

/// <summary>
///     CellForge render context — routes every write through the
///     <see cref="AnsiWriter" /> SGR automaton over <see cref="StdoutBackend" />.
///     The writer diffs styles frame-to-frame, so styling two consecutive spans
///     with the same color emits zero escape codes for the second span.
/// </summary>
public sealed class CellForgeRenderContext : ITuiRenderContext
{
    private readonly AnsiWriter _writer;

    public CellForgeRenderContext() : this(new StdoutBackend())
    {
    }

    public CellForgeRenderContext(ITerminalBackend backend)
    {
        _writer = new AnsiWriter(backend);
    }

    /// <summary>Exposed for diagnostics and golden-frame tests.</summary>
    public AnsiWriter Writer => _writer;

    public int Width
    {
        get
        {
            try
            {
                return Console.WindowWidth;
            }
            catch
            {
                return 0;
            }
        }
    }

    public int Height
    {
        get
        {
            try
            {
                return Console.WindowHeight;
            }
            catch
            {
                return 0;
            }
        }
    }

    public bool SupportsColor => true;

    public void Write(string text)
    {
        _writer.WriteText(text);
        Flush();
    }

    public void WriteLine(string? text = null)
    {
        if (!string.IsNullOrEmpty(text))
        {
            _writer.WriteText(text);
        }

        _writer.WriteLineBreak();
        Flush();
    }

    public void WriteColored(string text, TuiColor foreground, TuiColor? background = null)
    {
        var style = new CellStyle(
            PackedColor.Rgb(foreground.R, foreground.G, foreground.B),
            background.HasValue ? PackedColor.Rgb(background.Value.R, background.Value.G, background.Value.B) : default);
        _writer.WriteStyledText(text, style);
        Flush();
    }

    public void WriteStyled(string text, TuiStyle style)
    {
        _writer.WriteStyledText(text, MapStyle(default, style));
        Flush();
    }

    public void SetCursorPosition(int row, int col) => _writer.MoveTo(col, row);

    public void ClearLine()
    {
        _writer.EraseEntireLine();
        Flush();
    }

    public void Clear()
    {
        _writer.EmitEraseInDisplay(2);
        _writer.InvalidateCursorPosition();
        Flush();
    }

    public void HideCursor()
    {
        _writer.HideCursor();
        Flush();
    }

    public void ShowCursor()
    {
        _writer.ShowCursor();
        Flush();
    }

    public void EnterAlternateScreen()
    {
        _writer.Raw("\x1B[?1049h");
        Flush();
    }

    public void ExitAlternateScreen()
    {
        _writer.Raw("\x1B[?1049l");
        Flush();
    }

    /// <summary>
    ///     AnsiWriter buffers; the sync <see cref="AnsiWriter.FlushSync"/> path
    ///     keeps the render-context contract synchronous without sync-over-async.
    /// </summary>
    public void Flush() => _writer.FlushSync();

    private static CellStyle MapStyle(PackedColor color, TuiStyle style)
    {
        StyleAttr attrs = StyleAttr.None;
        if (style.HasFlag(TuiStyle.Bold)) attrs |= StyleAttr.Bold;
        if (style.HasFlag(TuiStyle.Italic)) attrs |= StyleAttr.Italic;
        if (style.HasFlag(TuiStyle.Underline)) attrs |= StyleAttr.Underline;
        if (style.HasFlag(TuiStyle.Dim)) attrs |= StyleAttr.Dim;
        if (style.HasFlag(TuiStyle.Strike)) attrs |= StyleAttr.Strike;
        if (style.HasFlag(TuiStyle.Reverse)) attrs |= StyleAttr.Reverse;
        return new CellStyle(color, default, attrs);
    }
}
