using System.Text;
using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.App.Cli.Repl;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Streaming;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.Rendering;
using Harbor.Ui.Framework.Rendering.Widgets;
using Microsoft.Extensions.DependencyInjection;

namespace Harbor.App.Cli.Demo;

/// <summary>
///     CellForge playback for <c>harbor demo --tui cellforge</c> — the canonical
///     fullscreen cell-diff renderer the README GIFs are recorded on (issue #440).
/// </summary>
/// <remarks>
///     <para>
///         This is the demo's answer to the question the fullscreen backend
///         raises for any headless host: the cell-diff renderer is not a writer
///         of lines, it is a painter of whole frames. It owns the alternate
///         screen buffer, so <b>stdout must stay the tty</b> (a pipe would make
///         <see cref="ScreenSession" /> fall back to an 80x24 grid) and nothing
///         else may write to it while a frame is live. Hence the shape here:
///         build the screen, enter the alternate screen, and paint.
///     </para>
///     <para>
///         <b>Why record-then-replay.</b> A live frame loop is a wall-clock
///         loop: how many events land between two frames — and therefore which
///         intermediate screens a recorder ever samples — depends on how fast
///         the machine produced the turn. That is exactly the nondeterminism
///         issue #426 removed from the line-oriented renderer, and it would come
///         straight back through the cell-diff door. So the turn is run for real
///         (mock LLM, real agent loop, real permission service) with the screen
///         untouched, every event is captured, and only then is it replayed onto
///         the screen in fixed steps. The chunking is a pure function of the
///         captured event list and the pacing is a fixed number of sampling
///         intervals, so the <em>sequence of screens</em> — the invariant
///         <c>tools/demo_repro.py compare</c> asserts — is a constant of the
///         scene, not of the machine.
///     </para>
/// </remarks>
internal sealed class DemoCellForgeScreen
{
    /// <summary>Model painted in the footer — the mock provider's, never a real one.</summary>
    private const string ModelId = "demo/harbor-1";

    /// <summary>Context window painted in the footer (mirrors the mock provider config).</summary>
    private const int ContextWindow = 128_000;

    /// <summary>
    ///     Recorder sampling interval, in milliseconds. Every painted screen is
    ///     held for a whole number of these, so a screen survives several full
    ///     sampling periods no matter where the sampler fires — the same
    ///     frame-locked contract <c>demo/run-scene.sh</c> gives the line renderer.
    /// </summary>
    private static readonly int StepMs = ResolveStepMs();

    /// <summary>Sampling periods each painted screen stays on screen.</summary>
    private const int HoldFrames = 3;

    /// <summary>Sampling periods the settled scene is held before the next scene starts.</summary>
    private const int SettleHoldFrames = 6;

    /// <summary>Target number of replay chunks (and therefore screens) per recorded turn.</summary>
    private const int TargetChunks = 12;

    /// <summary>
    ///     Frames a replayed approval card stays open before it resolves — the
    ///     beat a human takes to read the card and press a key, with the tool
    ///     result deliberately held back until the decision is stamped.
    /// </summary>
    private const int GateHoldFrames = 3;

    /// <summary>Longest one-line tool detail printed on the approval card.</summary>
    private const int MaxDetailChars = 96;

    /// <summary>Hide-cursor sequence, re-applied after the alternate screen is left so
    /// the restored console has no blinking block in the recorded tail.</summary>
    private const string HideCursor = "\x1B[?25l";

    /// <summary>Closing line, written to the restored console right after the screen switch.</summary>
    private const string Footer = "\r\nharbor demo finished — no API keys were used. Record GIFs: vhs demo/hero.tape\r\n";

    private readonly IServiceProvider _sp;
    private readonly IEventBus _bus;
    private readonly IAgent _agent;
    private readonly IReadOnlyList<(string Id, string Prompt)> _scenes;

    private ScreenSession _session = null!;
    private ChatScreen _screen = null!;
    private ChatScreenBridge _bridge = null!;
    private long _nowMs;

    public DemoCellForgeScreen(
        IServiceProvider sp,
        IEventBus bus,
        IAgent agent,
        IReadOnlyList<(string Id, string Prompt)> scenes)
    {
        _sp = sp;
        _bus = bus;
        _agent = agent;
        _scenes = scenes;
    }

    /// <summary>
    ///     Pacing step, published by <c>demo/run-scene.sh</c> from
    ///     <c>demo/toolchain.lock</c> so the CLI and the recorder agree on what a
    ///     "frame interval" is. Anything below two sampling periods would let a
    ///     screen slip between two captures, so it is rejected.
    /// </summary>
    private static int ResolveStepMs()
    {
        string raw = Environment.GetEnvironmentVariable("HARBOR_DEMO_STEP_MS") ?? string.Empty;
        return int.TryParse(raw, out int ms) && ms >= 80 ? ms : 250;
    }

    public async Task<int> RunAsync(CancellationToken ct)
    {
        _session = _sp.GetRequiredService<ScreenSession>();
        var backend = _sp.GetRequiredService<ITerminalBackend>();

        var composer = new ComposerController();
        var status = new StatusViewModel { Model = ModelId, Mode = StatusBarMode.Idle };
        status.SetContext(0, ContextWindow);

        // The same three zones the interactive composition root builds (feed
        // above, composer, status footer) minus the sidebar, the tab strip and
        // the mascot: the demo shows one session, and an empty panel would only
        // add pixels the recorder has to hold. Chrome is on, because the zone
        // rules are what makes the footer read as part of the UI.
        _screen = ChatScreen.Build(
            composer,
            status,
            includeSidebar: false,
            includeTabStrip: false,
            mascotMode: MascotMode.Off);
        _screen.Timeline.Timeline.ShowSeparators = true;
        _screen.Composer.ShowChrome = true;
        _screen.Composer.Placeholder = "Type your message...";
        _screen.Status.ShowChrome = true;

        // autoSubscribe:false — the bridge is driven by the replay below, so
        // every timeline mutation happens on this thread, in a known order.
        _bridge = new ChatScreenBridge(_bus, _screen.Timeline, status, autoSubscribe: false);

        await backend.WriteAsync(Utf8(CellForgeReplRunner.SeqEnterAltScreen), ct).ConfigureAwait(false);
        try
        {
            foreach (var scene in _scenes)
            {
                _bridge.AppendSystemLine($"harbor demo · scene: {scene.Id} · tui: cellforge");
                if (!await PlaySceneAsync(composer, scene.Prompt, ct).ConfigureAwait(false))
                {
                    return 1;
                }
            }
        }
        finally
        {
            // Leave the alternate screen even on failure: a demo that exits
            // inside it leaves the operator staring at a stale full-screen frame.
            // One write, for two reasons. The restored console must not show a
            // block cursor again — it blinks on its own ~0.5 s cycle,
            // asynchronous to the recorder, so a visible cursor puts a
            // different cell in roughly every other tail frame (the same drift
            // run-scene.sh hides the cursor for on the line renderers). And the
            // closing line has to land in the same write as the screen switch:
            // split across two, a sampler firing in the gap would record the
            // restored console without it, and the two passes of the
            // record-twice check would disagree on how many screens were shown.
            await backend
                .WriteAsync(Utf8(CellForgeReplRunner.SeqLeaveAltScreen + HideCursor + Footer), CancellationToken.None)
                .ConfigureAwait(false);
        }

        return 0;
    }

    private async Task<bool> PlaySceneAsync(ComposerController composer, string prompt, CancellationToken ct)
    {
        // 1. The prompt sits in the composer exactly as if it had been typed.
        composer.Buffer.InsertText(prompt);
        await RenderAsync().ConfigureAwait(false);
        await HoldAsync(4, ct).ConfigureAwait(false);

        // 2. Record the real turn. Nothing paints while the agent runs, so what
        //    the recorder can possibly see is decided by the replay below alone.
        var recorded = new List<AgentEvent>();
        composer.Buffer.Clear();
        var promptResult = await RecordTurnAsync(prompt, recorded, ct).ConfigureAwait(false);
        if (promptResult.IsFailure)
        {
            _bridge.AppendSystemLine("scene failed: " + promptResult.Error);
            await RenderAsync().ConfigureAwait(false);
            await HoldAsync(SettleHoldFrames, ct).ConfigureAwait(false);
            return false;
        }

        // 3. Replay onto the screen, one chunk per painted frame.
        foreach (var chunk in SplitChunks(recorded))
        {
            foreach (AgentEvent evt in chunk.Events)
            {
                await _bridge.AcceptAsync(evt, ct).ConfigureAwait(false);
            }

            // The scripted asker resolves the request silently (see
            // DemoRuntime), so the card is replayed here: it lands on the frame
            // that carries the tool call, then hangs for GateHoldFrames frames
            // with *no further events fed* — the beat a human takes to read the
            // card and press a key. Without that pause the tool result lands
            // while the gate is still open and the card reads as an
            // after-the-fact annotation on an already-completed call.
            if (chunk.Tool.HasValue)
            {
                ApprovalGateView gate = _bridge.RequestApprovalGate(chunk.Tool.Value.Name, chunk.Tool.Value.Detail);
                for (int held = 0; held < GateHoldFrames; held++)
                {
                    await StepAsync(HoldFrames, ct).ConfigureAwait(false);
                }

                gate.TryDecide(ApprovalChoice.Approve);
                await StepAsync(HoldFrames, ct).ConfigureAwait(false);
            }
            else
            {
                await StepAsync(HoldFrames, ct).ConfigureAwait(false);
            }
        }

        await HoldAsync(SettleHoldFrames, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    ///     One replay step: advance the synthetic clock, let the bridge drain its
    ///     queues, paint the frame, then hold it. The clock is synthetic on
    ///     purpose — the stream pacer and the tool-card timers read it, so the
    ///     reveal is a function of the step index rather than of the wall clock.
    /// </summary>
    private async Task StepAsync(int holdFrames, CancellationToken ct)
    {
        _nowMs += StepMs;
        _bridge.Tick(_nowMs);
        await RenderAsync().ConfigureAwait(false);
        await HoldAsync(holdFrames, ct).ConfigureAwait(false);
    }

            await RenderAsync().ConfigureAwait(false);
            await HoldAsync(HoldFrames, ct).ConfigureAwait(false);
        }

        await HoldAsync(SettleHoldFrames, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Run one real turn with the screen untouched, capturing every event.</summary>
    private async Task<Result> RecordTurnAsync(string prompt, List<AgentEvent> recorded, CancellationToken ct)
    {
        // The captured list is the whole point: the replay, not this turn, decides
        // what the recorder can possibly see.
        using IDisposable subscription = _bus.Subscribe((evt, _) =>
        {
            recorded.Add(evt);
            return ValueTask.CompletedTask;
        });
        return await _agent.PromptAsync(prompt, ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     One painted frame, in the canonical order the interactive frame loop
    ///     uses (goldens included): solve → prepare → begin → overlays → paint →
    ///     flush. The palette pin is owned by the frame scope, so a throw here
    ///     cannot leave the renderer pinned for the rest of the run.
    /// </summary>
    private async Task RenderAsync()
    {
        _session.CheckAutoSize();
        int cols = _session.CurrentCols;
        int rows = _session.CurrentRows;

        _screen.Tree.Solve(cols, rows);
        Rect feed = _screen.Timeline.Rect;
        _ = _screen.Timeline.Timeline.PrepareFrame(feed.Width > 0 ? feed.Width : cols, Math.Max(0, feed.Height));

        using var frame = _session.BeginFrameScope();
        _screen.SyncOverlays(new Rect(0, 0, cols, rows));
        _screen.Tree.PaintAll(_session.Back);
        await frame.FlushAsync().ConfigureAwait(false);
    }

    private static Task HoldAsync(int frames, CancellationToken ct) =>
        Task.Delay(frames * StepMs, ct);

    private static ReadOnlyMemory<byte> Utf8(string s) => Encoding.UTF8.GetBytes(s);

    /// <summary>
    ///     Slice a recorded turn into replay chunks. The rules are deliberately
    ///     boring: aim for <see cref="TargetChunks" /> chunks, then cut early on
    ///     every event that ends a visible unit of work so no chunk straddles a
    ///     message boundary, a tool call, or the approval gate.
    /// </summary>
    private static List<Chunk> SplitChunks(List<AgentEvent> events)
    {
        var chunks = new List<Chunk>();
        if (events.Count == 0)
        {
            return chunks;
        }

        int size = Math.Max(1, events.Count / TargetChunks);
        var current = new List<AgentEvent>();
        (string Name, string Detail)? tool = null;

        foreach (AgentEvent evt in events)
        {
            current.Add(evt);
            if (evt is ToolExecutionStartEvent start)
            {
                tool = (start.ToolName, Describe(start));
            }

            bool boundary = evt is MessageEndEvent
                or ToolExecutionStartEvent
                or ToolExecutionEndEvent
                or TurnEndEvent
                or AgentEndEvent;
            if (current.Count >= size || boundary)
            {
                chunks.Add(new Chunk(current, tool));
                current = new List<AgentEvent>();
                tool = null;
            }
        }

        if (current.Count > 0)
        {
            chunks.Add(new Chunk(current, tool));
        }

        return chunks;
    }

    /// <summary>One replay step: the events it feeds, plus the tool whose gate it opens.</summary>
    private sealed record Chunk(List<AgentEvent> Events, (string Name, string Detail)? Tool);

    private static string Describe(ToolExecutionStartEvent start)
    {
        string args = start.Args.ValueKind is JsonValueKind.Object or JsonValueKind.Array
            ? start.Args.GetRawText()
            : string.Empty;
        args = args.Replace('\n', ' ').Replace('\r', ' ');
        return args.Length <= MaxDetailChars ? args : args[..(MaxDetailChars - 1)] + "…";
    }
}
