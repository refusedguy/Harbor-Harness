using System.Text;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Tui;
using Harbor.Terminal.Abstractions;
using Harbor.Terminal.Abstractions.Renderers;
using Harbor.Terminal.Abstractions.Views;
using Microsoft.Extensions.Logging;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Configuration;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Drivers;

namespace Harbor.Tui.NickConsoleEx;

/// <summary>
///     Wrapper backend over <c>nickprotop/ConsoleEx</c> (the SharpConsoleUI
///     window system) — renderer-unification sprint Phase 3. ADDITIVE by hard
///     rule: this backend complements <see cref="Harbor.Tui.CellForge"/>, it
///     does not replace it. Selected via <c>HARBOR_TUI=nickconsoleex</c> or
///     <c>ui.renderer: "nickconsoleex"</c>.
///     <para>
///         The Harbor chat state is mirrored into a SharpConsoleUI window with
///         a <see cref="MarkupControl"/> log surface; every agent event appends
///         markup lines and drives one differential render cycle through their
///         window system (<c>ForceRender</c> — their own cell-diff blitter).
///         On a redirected stdout (pipes, CI) a <see cref="HeadlessConsoleDriver"/>
///         is used; on a real terminal, their <see cref="NetConsoleDriver"/>.
///     </para>
/// </summary>
[Harbor.CodeGen.TuiRenderer("nickconsoleex")]
public sealed partial class NickConsoleExTuiRenderer : BaseTuiRenderer
{
    private readonly Lock _gate = new();
    private readonly List<string> _lines = [];
    private readonly Dictionary<string, string> _toolNames = new();
    private readonly int _maxLines;
    private ConsoleWindowSystem? _windowSystem;
    private Window? _window;
    private MarkupControl? _log;
    private StringBuilder? _pendingTokenLine;

    public NickConsoleExTuiRenderer(
        ILogger<NickConsoleExTuiRenderer> logger,
        int maxLines = 500,
        IConsoleDriver? driverOverride = null)
        : base(logger)
    {
        _maxLines = maxLines;
        _driverOverride = driverOverride;
    }

    private readonly IConsoleDriver? _driverOverride;

    public override ITuiRenderContext Context { get; } = new NickConsoleExRenderContext();

    public override Task<Result> InitializeAsync(CancellationToken ct = default)
    {
        try
        {
            EnsureWindowSystem();
            return base.InitializeAsync(ct);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "NickConsoleEx backend failed to initialize");
            return Task.FromResult(Result.Failure(ex.Message));
        }
    }

    protected override bool ShouldRenderPlacement(TuiViewPlacement placement, AgentEvent @event)
    {
        // The whole chat surface lives in the SharpConsoleUI window; the
        // builtin views stay registered (plugin contract) but don't repaint.
        return false;
    }

    public override Task RenderAsync(AgentEvent @event, CancellationToken ct = default)
    {
        switch (@event)
        {
            case MessageStartEvent:
                Append("[bold cyan]assistant[/] ");
                break;

            case MessageUpdateEvent mu:
                AppendToken(mu.LlmEvent);
                break;

            case MessageEndEvent:
                CommitTokenLine();
                Append(string.Empty);
                break;

            case ToolExecutionStartEvent tes:
                CommitTokenLine();
                lock (_gate)
                {
                    _toolNames[tes.ToolCallId] = tes.ToolName;
                }

                string args = tes.Args.GetRawText();
                Append(string.IsNullOrEmpty(args) || args == "{}"
                    ? $"[blue]→ {tes.ToolName}[/]"
                    : $"[blue]→ {tes.ToolName}[/] [dim]{Escape(args)}[/]");
                break;

            case ToolExecutionEndEvent tee:
                CommitTokenLine();
                string toolName = "tool";
                lock (_gate)
                {
                    if (_toolNames.Remove(tee.ToolCallId, out var found))
                    {
                        toolName = found;
                    }
                }

                Append(tee.IsError
                    ? $"[red]✗ {Escape(toolName)} — {Escape(tee.Result.Output)}[/]"
                    : $"[green]✓ {Escape(toolName)}[/]");
                break;

            case CompactionStartedEvent:
                Append("[dim]compacting context...[/]");
                break;

            case CompactionCompletedEvent cc:
                Append($"[dim]compacted: pruned {cc.PrunedMessageCount} msgs, saved ~{cc.TokensSaved} tokens[/]");
                break;

            case AgentErrorEvent err:
                CommitTokenLine();
                Append($"[bold red]error[/] {Escape(err.Message)}");
                break;
        }

        return base.RenderAsync(@event, ct);
    }

    public override Task<Result<string>> ReadLineAsync(string prompt, CancellationToken ct = default)
    {
        // SharpConsoleUI owns its own input loop in interactive mode; the
        // event-driven adapter falls back to the line-based console reader.
        Console.Write(prompt);
        return Task.FromResult(Result.Success(Console.ReadLine() ?? string.Empty));
    }

    public override Task<Result> WriteAsync(string text, CancellationToken ct = default)
    {
        Append(Escape(text));
        return Task.FromResult(Result.Success());
    }

    public override Task<Result> WriteLineAsync(string? text = null, CancellationToken ct = default)
    {
        Append(Escape(text ?? string.Empty));
        return Task.FromResult(Result.Success());
    }

    public override Task<Result> ClearAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            _lines.Clear();
            _log?.SetContent([.. _lines]);
        }

        ForceRender();
        return Task.FromResult(Result.Success());
    }

    public override void Dispose()
    {
        try
        {
            // ConsoleWindowSystem has no IDisposable — Shutdown() unsubscribes
            // driver event handlers and disposes internal services (timers,
            // watchdog, wake signal). Safe to call before the loop ever ran.
            _windowSystem?.Shutdown();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "NickConsoleEx window system disposal issue");
        }

        _windowSystem = null;
        _window = null;
        _log = null;
        base.Dispose();
    }

    private void AppendToken(LlmEvent evt)
    {
        switch (evt)
        {
            case TextDeltaEvent td:
                _pendingTokenLine ??= new StringBuilder();
                _pendingTokenLine.Append(Escape(td.Delta));
                // Stream incrementally: keep the open tail visible.
                CommitTokenLine(commit: false);
                break;

            case ThinkingDeltaEvent thd:
                CommitTokenLine();
                Append($"[dim]{Escape(thd.Delta)}[/]");
                break;

            case ToolCallDeltaEvent tcd:
                CommitTokenLine();
                Append($"[dim]{Escape(tcd.ArgsDelta)}[/]");
                break;
        }
    }

    private void CommitTokenLine(bool commit = true)
    {
        var pending = _pendingTokenLine;
        if (pending is null)
        {
            return;
        }

        if (commit)
        {
            _pendingTokenLine = null;
        }

        lock (_gate)
        {
            _lines.Add(pending.ToString());
            Trim();
            if (_log is not null)
            {
                lock (ScreenGate)
                {
                    _log.SetContent([.. _lines]);
                }
            }
        }

        ForceRender();
    }

    private void Append(string markup)
    {
        lock (_gate)
        {
            _lines.Add(markup);
            Trim();
            if (_log is not null)
            {
                lock (ScreenGate)
                {
                    _log.SetContent([.. _lines]);
                }
            }
        }

        ForceRender();
    }

    private void Trim()
    {
        if (_lines.Count <= _maxLines)
        {
            return;
        }

        _lines.RemoveRange(0, _lines.Count - _maxLines);
    }

    private void EnsureWindowSystem()
    {
        if (_windowSystem is not null)
        {
            return;
        }

        IConsoleDriver driver = _driverOverride
            ?? (Console.IsOutputRedirected
                ? new HeadlessConsoleDriver(120, 40)
                : new NetConsoleDriver(new NetConsoleDriverOptions { RenderMode = RenderMode.Buffer }));

        // Harbor provides its own status surface; the SharpConsoleUI default
        // desktop panels (app-name + live clock top bar, task bar bottom bar)
        // are disabled — the clock would also make golden-frame tests
        // inherently nondeterministic (renderer-unification Phase 5).
        var options = new ConsoleWindowSystemOptions(
            ShowTopPanel: false,
            ShowBottomPanel: false);

        var system = new ConsoleWindowSystem(driver, options: options);
        _log = new MarkupControl([]);
        var window = new WindowBuilder(system)
            .WithTitle("Harbor — nickprotop/ConsoleEx backend")
            .WithSize(Math.Max(60, driver.ScreenSize.Width - 4), Math.Max(20, driver.ScreenSize.Height - 4))
            .AtPosition(1, 1)
            .AddControl(_log)
            .Build();
        system.AddWindow(window);

        lock (ScreenGate)
        {
            _windowSystem = system;
            _window = window;
        }
    }

    private void ForceRender()
    {
        // One differential render cycle through their pipeline; no-op when the
        // window system is not up yet (pre-InitializeAsync appends buffer only).
        lock (ScreenGate)
        {
            try
            {
                _windowSystem?.ForceRender();
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "NickConsoleEx render cycle failed");
            }
        }
    }

    /// <summary>Serializes buffer swaps with the render cycle.</summary>
    private static readonly object ScreenGate = new();

    private static string Escape(string text) =>
        text.Replace("[", "[[", StringComparison.Ordinal).Replace("]", "]]", StringComparison.Ordinal);
}

/// <summary>
///     Minimal render context for builtin views: builtins render through the
///     captured console writer so plugin views keep working; the actual chat
///     surface is the SharpConsoleUI window.
/// </summary>
public sealed class NickConsoleExRenderContext : ITuiRenderContext
{
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

    public void Write(string text) => Console.Write(text);

    public void WriteLine(string? text = null) => Console.WriteLine(text ?? string.Empty);

    public void WriteColored(string text, TuiColor foreground, TuiColor? background = null) =>
        Console.Write($"\x1b[38;2;{foreground.R};{foreground.G};{foreground.B}m{text}\x1b[0m");

    public void WriteStyled(string text, TuiStyle style)
    {
        string codes = (style.HasFlag(TuiStyle.Bold) ? "1;" : string.Empty)
            + (style.HasFlag(TuiStyle.Dim) ? "2;" : string.Empty)
            + (style.HasFlag(TuiStyle.Italic) ? "3;" : string.Empty)
            + (style.HasFlag(TuiStyle.Underline) ? "4;" : string.Empty);
        Console.Write(codes.Length > 0 ? $"\x1b[{codes.TrimEnd(';')}m{text}\x1b[0m" : text);
    }

    public void SetCursorPosition(int row, int col) { }

    public void ClearLine() { }

    public void Clear() { }

    public void HideCursor() { }

    public void ShowCursor() { }

    public void EnterAlternateScreen() { }

    public void ExitAlternateScreen() { }

    public void Flush() { }
}
