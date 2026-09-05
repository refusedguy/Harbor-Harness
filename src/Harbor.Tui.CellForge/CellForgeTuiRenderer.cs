using CSharpFunctionalExtensions;
using Harbor.Abstractions.Contracts;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Tui;
using Harbor.Terminal.Abstractions;
using Harbor.Terminal.Abstractions.Renderers;
using Harbor.Terminal.Abstractions.Views;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Ui.Framework.Rendering;
using Microsoft.Extensions.Logging;

namespace Harbor.Tui.CellForge;

/// <summary>
///     Adapter that exposes the CellForge engine through the standard
///     <see cref="ITuiRenderer" /> / <see cref="BaseTuiRenderer" /> contract
///     (renderer-unification sprint, Phase 2). The interactive raw-mode entry
///     point remains <c>CellForgeReplRunner</c>; this adapter serves the
///     non-interactive/event-driven path so <c>HARBOR_TUI=cellforge</c>
///     produces a first-class CellForge renderer instead of falling back to
///     the Ansi backend. Output goes through <see cref="AnsiWriter"/>'s SGR
///     automaton: styles are diffed against the writer's tracked state, so
///     redundant escape codes are never emitted.
/// </summary>
[TuiRenderer(Backend = "cellforge")]
public sealed partial class CellForgeTuiRenderer : BaseTuiRenderer
{
    /// <summary>Input placeholder while the agent is idle and waiting for a prompt.</summary>
    internal const string IdlePlaceholder = "Type your message...";

    /// <summary>Input placeholder while the agent is running a prompt.</summary>
    internal const string BusyPlaceholder = "Agent is running… (Esc to stop)";

    /// <summary>
    /// CF-D-002: host-provided <see cref="ChatScreen"/> so
    /// <see cref="ProjectStateIntoWidgets"/> can feed projected state into
    /// <see cref="Widgets.StatusPanel.ProjectedState"/>. Null (default) means
    /// the renderer is running in a context without a layout tree
    /// (e.g. event-driven non-interactive path) — projected state is skipped.
    /// </summary>
    public ChatScreen? Screen { get; set; }

    private readonly UiStore _store;
    private readonly CellForgePanelRegistry _panels = new();
    private readonly StatusBarViewModel _statusVm;
    private readonly ChatHistoryViewModel _chatVm;
    private readonly InputViewModel _inputVm;
    private readonly ComposerController _composer = new();
    private readonly QuickSwitchSlots _quickSwitchSlots = new();
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
        Context = new CellForgeRenderContext();
    }

    /// <summary>
    ///     Golden-frame test seam (renderer-unification Phase 5): route the
    ///     adapter's writes through a caller-supplied terminal backend
    ///     (e.g. an in-memory capture backend). Production code keeps using
    ///     the parameterless ctor (StdoutBackend).
    /// </summary>
    public CellForgeTuiRenderer(ILogger<CellForgeTuiRenderer> logger, ITerminalBackend backend) : base(logger)
    {
        Context = new CellForgeRenderContext(backend);
    }

    public override ITuiRenderContext Context { get; }

    /// <summary>Panel providers owned by this renderer (CF-E-002 wiring).</summary>
    public CellForgePanelRegistry Panels => _panels;

    /// <summary>TEA store backing this renderer (CF-E-002 wiring).</summary>
    public UiStore Store => _store;

    // CF-F-001: intentionally no ShouldRenderPlacement override — the base filter
    // already paints the ChatHistory/Input placements, and both builtin views write
    // only through ITuiRenderContext (CellForgeRenderContext/AnsiWriter), so no
    // CellForge fallback painter is needed.

    public override Task<Result> InitializeAsync(CancellationToken ct = default)
    {
        try
        {
            Context.HideCursor();
            return base.InitializeAsync(ct);
        }
        catch (Exception ex)
        {
            return Task.FromResult(Result.Failure(ex.Message));
        }
    }

    /// <summary>
    ///     Register the cell-native builtins in Spectre Alt+1..9 slot order
    ///     (CF-E-002). Idempotent: re-registration replaces by id, and
    ///     <see cref="CellForgePanelRegistry.EnsureSeeded"/> preserves
    ///     already-known panel state.
    /// </summary>
    private void RegisterBuiltinPanels()
    {
        _panels.Register(new CellForgeHelpPanel());
        _panels.Register(new CellForgeTodoListPanel());
        _panels.Register(new CellForgeDiffPreviewPanel());
        _panels.Register(new CellForgeFileTreePanel());
        _panels.Register(new CellForgeTokenBreakdownPanel());
        _panels.Register(new CellForgeDiagnosticsPanel());
        _panels.Register(new CellForgeLogsPanel());
    }

    private void OnStoreChanged(object? sender, UiStateChangedEventArgs e)
    {
        ProjectStateIntoWidgets(e.State);
    }

    public override Task RenderAsync(AgentEvent @event, CancellationToken ct = default)
    {
        // Live token feed — identical event routing to AnsiTuiRenderer, but the
        // writes land on the CellForge SGR automaton instead of raw Console.
        switch (@event)
        {
            case MessageStartEvent:
                Context.WriteColored("[assistant] ", TuiColor.Cyan);
                break;

            case MessageUpdateEvent mu:
                RenderLiveToken(mu.LlmEvent, Context);
                break;

            case MessageEndEvent:
                Context.WriteLine();
                break;

            case ToolExecutionStartEvent tes:
                Context.WriteLine();
                Context.WriteColored($"→ {tes.ToolName}", TuiColor.Blue);
                string args = tes.Args.GetRawText();
                if (!string.IsNullOrEmpty(args) && args != "{}")
                {
                    Context.WriteStyled($" {args}", TuiStyle.Dim);
                }
                Context.WriteLine();
                break;

            case CompactionStartedEvent:
                Context.WriteLine();
                Context.WriteStyled("[compacting context...]", TuiStyle.Dim);
                Context.WriteLine();
                break;

            case CompactionCompletedEvent cc:
                Context.WriteStyled(
                    $"[compacted: pruned {cc.PrunedMessageCount} msgs, saved ~{cc.TokensSaved} tokens in {cc.Duration.TotalSeconds:F1}s]",
                    TuiStyle.Dim);
                Context.WriteLine();
                break;

            case AgentErrorEvent err:
                Context.WriteLine();
                Context.WriteColored($"[error] {err.Message}", TuiColor.Red);
                Context.WriteLine();
                break;
        }

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

    public override void Dispose()
    {
        Context.ShowCursor();
        base.Dispose();
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
