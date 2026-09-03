using CSharpFunctionalExtensions;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Tui;
using Harbor.Terminal.Abstractions;
using Harbor.Terminal.Abstractions.Renderers;
using Harbor.Terminal.Abstractions.ViewModels;
using Harbor.Terminal.Abstractions.Views;
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
    private readonly UiStore _store;
    private readonly StatusBarViewModel _statusVm;
    private readonly ChatHistoryViewModel _chatVm;

    public CellForgeTuiRenderer(
        ILogger<CellForgeTuiRenderer> logger,
        StatusBarViewModel? statusVm = null,
        ChatHistoryViewModel? chatVm = null)
        : base(logger)
    {
        _store = new UiStore();
        _statusVm = statusVm ?? ViewModels.Get<StatusBarViewModel>("status-bar")!;
        _chatVm = chatVm ?? ViewModels.Get<ChatHistoryViewModel>("chat-history")!;
        Context = new CellForgeRenderContext();
    }

    /// <summary>Golden-frame test seam.</summary>
    public CellForgeTuiRenderer(
        ILogger<CellForgeTuiRenderer> logger,
        ITerminalBackend backend,
        StatusBarViewModel? statusVm = null,
        ChatHistoryViewModel? chatVm = null)
        : base(logger)
    {
        _store = new UiStore();
        _statusVm = statusVm ?? ViewModels.Get<StatusBarViewModel>("status-bar")!;
        _chatVm = chatVm ?? ViewModels.Get<ChatHistoryViewModel>("chat-history")!;
        Context = new CellForgeRenderContext(backend);
    }

    public override ITuiRenderContext Context { get; }

    protected override bool ShouldRenderPlacement(TuiViewPlacement placement, AgentEvent @event)
    {
        if (placement is TuiViewPlacement.ChatHistory or TuiViewPlacement.Input)
        {
            return false;
        }

        return base.ShouldRenderPlacement(placement, @event);
    }

    public override Task<Result> InitializeAsync(CancellationToken ct = default)
    {
        try
        {
            _store.Changed += OnStoreChanged;
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

    private void ProjectStateIntoWidgets(UiState state)
    {
        if (_statusVm is StatusBarViewModel svm)
        {
            svm.Status = state.Status;
            svm.Model = state.Model;
            svm.TokensIn = (int)Math.Min(state.Cost.TokensIn, int.MaxValue);
            svm.TokensOut = (int)Math.Min(state.Cost.TokensOut, int.MaxValue);
            svm.Cost = state.Cost.CostUsd;
        }

        if (_chatVm is ChatHistoryViewModel chvm)
        {
            chvm.IsStreaming = state.IsStreaming;
            chvm.StreamingText = state.Active.TextBuffer;
        }
    }

    public override void Dispose()
    {
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
