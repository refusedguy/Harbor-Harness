using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Harbor.Abstractions.Events;
using Harbor.Terminal.Abstractions.ViewModels;
using Harbor.Ui.Framework.State;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Tui.CellForge.Tests.Pilot;

/// <summary>
/// In-process headless CellForge driver (#1183, textual Pilot steal).
/// <para />
/// Textual splits testing into two layers: <c>Pilot</c> (fast logic layer —
/// press keys, wait for idle, read the frame, hook messages) over a headless
/// driver, and the slow byte layer. Harbor HAS the slow byte layer
/// (<c>CellForgePtyScenarioBase</c>: real PTY, real child process) but lacked
/// the fast one — every scenario paid spawn + PTY + fixed-delay costs. This
/// driver closes that gap: the real <see cref="CellForgeTuiRenderer" /> paints
/// into a <see cref="RecordingBackend" /> in-process, keys travel the real
/// reducer path (<c>KeyEvent → KeyInput → UiStore → projection</c>), and
/// events travel the real bus + render path.
/// </para>
/// <para>
/// The four Pilot primitives, mapped:
/// <list type="bullet">
/// <item><c>press</c> → <see cref="Press" /> / <see cref="TypeText" /> /
/// <see cref="PressEnter" /> / <see cref="PressBackspace" />: keys resolve
/// through <see cref="ChatKeyMap" /> and dispatch into <see cref="Store" />,
/// exactly like the interactive host.</item>
/// <item><c>wait_for_idle</c> → <see cref="WaitForIdleAsync" />: drains the
/// parked projection via <c>CellForgeTuiRenderer.WaitForIdleAsync</c> instead
/// of sleeping a fixed delay.</item>
/// <item><c>assert frame</c> → <see cref="FrameText" /> / <see cref="FrameLines" />:
/// ANSI-stripped text of the last rendered pass. <see cref="FrameLines" />
/// keeps the PTY <c>NormalizeLines</c> contract (trailing whitespace trimmed
/// per line, trailing empty lines dropped) so ported assertions read the
/// same.</item>
/// <item><c>message_hook</c> → <see cref="Hook" /> / <see cref="Hook{TEvent}" /> /
/// <see cref="SeenEvents" /> / <see cref="WaitForEventAsync{TEvent}" />: every
/// event fed via <see cref="PublishAsync" /> goes through <see cref="Bus" />
/// (a real <c>IEventBus</c>) before reaching the renderer, so hooks observe
/// the same traffic a subscriber would see out-of-process.</item>
/// </list>
/// </para>
/// <para>
/// What the pilot does NOT do: submit delivery (Enter clears the composer via
/// the reducer's <c>Submit</c> arm; running the agent loop on the submitted
/// prompt is the host's effect — the pilot asserts the composer cleared and
/// feeds the answering events explicitly), and byte-exact goldens (streaming
/// cadence is nondeterministic by design — celldiff §8; marker asserts only).
/// </para>
/// </summary>
public sealed class CellForgePilot : IAsyncDisposable
{
    private static readonly Regex AnsiEscape = new(
        @"\x1B\][^\x07\x1B]*(?:\x07|\x1B\\)|\x1B\[[0-9;?]*[A-Za-z]|\x1B[()][0-9A-Z]|\x1B[@-Z\\-_]",
        RegexOptions.Compiled);

    private readonly ChatKeyMap _keyMap = new();
    private bool _disposed;

    public RecordingBackend Backend { get; } = new();

    public UiStore Store { get; } = new();

    /// <summary>The event bus every published event travels before rendering (message_hook seam).</summary>
    public FakeEventBus Bus { get; } = new();

    public CellForgeTuiRenderer Renderer { get; }

    public StatusBarViewModel StatusVm { get; } = new();

    public ChatHistoryViewModel ChatVm { get; } = new();

    public InputViewModel InputVm { get; } = new();

    public CellForgePilot()
    {
        Renderer = new CellForgeTuiRenderer(
            NullLogger<CellForgeTuiRenderer>.Instance,
            Backend,
            StatusVm,
            ChatVm,
            InputVm,
            store: Store);
    }

    /// <summary>Launch: initialize the renderer and seed the runtime chrome (model/provider/agent).</summary>
    public static async Task<CellForgePilot> LaunchAsync(
        string model = "mock/test-model",
        string provider = "mock",
        string agent = "code",
        CancellationToken ct = default)
    {
        var pilot = new CellForgePilot();
        var init = await pilot.Renderer.InitializeAsync(ct).ConfigureAwait(false);
        if (init.IsFailure)
            throw new InvalidOperationException($"pilot init failed: {init.Error}");
        _ = pilot.Store.Dispatch(new ChatAppMsg.ConfigureRuntime(model, provider, agent));
        await pilot.WaitForIdleAsync(ct).ConfigureAwait(false);
        return pilot;
    }

    // -- press API (keys -> idle) --

    /// <summary>Press one decoded key through the real keymap → store path, then drain to idle.</summary>
    public async Task Press(KeyEvent key, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (KeyEventAdapter.TryConvert(key, _keyMap, out var msg))
            _ = Store.Dispatch(msg);
        await WaitForIdleAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Type printable text char-by-char (each char is its own key press).</summary>
    public async Task TypeText(string text, CancellationToken ct = default)
    {
        foreach (char c in text)
            await Press(KeyEvent.Char(new Rune(c)), ct).ConfigureAwait(false);
    }

    /// <summary>Press Enter (submit arm: consumes the draft, clears the composer).</summary>
    public Task PressEnter(CancellationToken ct = default) =>
        Press(KeyEvent.Simple(KeyCode.Enter), ct);

    /// <summary>Press Backspace (edits the draft).</summary>
    public Task PressBackspace(CancellationToken ct = default) =>
        Press(KeyEvent.Simple(KeyCode.Backspace), ct);

    /// <summary>Type a line and submit it, like the PTY <c>SubmitLine</c> (minus the child process).</summary>
    public async Task SubmitLine(string text, CancellationToken ct = default)
    {
        await TypeText(text, ct).ConfigureAwait(false);
        await PressEnter(ct).ConfigureAwait(false);
    }

    // -- event feed (bus -> render -> idle) --

    /// <summary>
    /// Feed one agent event: publish on <see cref="Bus" /> (hooks fire),
    /// render the pass, drain to idle.
    /// </summary>
    public async Task PublishAsync(AgentEvent @event, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await Bus.PublishAsync(@event, ct).ConfigureAwait(false);
        await Renderer.RenderAsync(@event, ct).ConfigureAwait(false);
        await WaitForIdleAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Drain parked projections until the pump is idle (textual <c>wait_for_idle</c>).</summary>
    public async Task<bool> WaitForIdleAsync(CancellationToken ct = default) =>
        await Renderer.WaitForIdleAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);

    // -- message_hook asserts --

    /// <summary>textual <c>message_hook</c>: observe every event the pilot feeds.</summary>
    public IDisposable Hook(Func<AgentEvent, CancellationToken, ValueTask> handler) =>
        Bus.Subscribe(handler);

    /// <summary>Typed <c>message_hook</c>: observe only events of <typeparamref name="TEvent" />.</summary>
    public IDisposable Hook<TEvent>(Func<TEvent, CancellationToken, ValueTask> handler)
        where TEvent : AgentEvent =>
        Bus.Subscribe(handler);

    /// <summary>Every event fed so far, oldest first.</summary>
    public IReadOnlyList<AgentEvent> SeenEvents => Bus.Events;

    /// <summary>
    /// Wait until a matching event was fed (checks the already-seen first, so
    /// an event that already arrived resolves without any delay — the textual
    /// "deterministic all-processed instead of sleep" rule).
    /// </summary>
    public async Task<TEvent> WaitForEventAsync<TEvent>(
        Func<TEvent, bool>? predicate = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
        where TEvent : AgentEvent
    {
        predicate ??= _ => true;
        TimeSpan deadline = timeout ?? TimeSpan.FromSeconds(5);
        var sw = Stopwatch.StartNew();
        while (true)
        {
            foreach (var e in Bus.Events.ToArray())
            {
                if (e is TEvent typed && predicate(typed))
                    return typed;
            }

            if (sw.Elapsed >= deadline)
                throw new TimeoutException($"pilot never saw {typeof(TEvent).Name} within {deadline}.");
            await Task.Delay(10, ct).ConfigureAwait(false);
        }
    }

    // -- frame reads --

    /// <summary>Last rendered pass as plain text (ANSI escapes stripped).</summary>
    public string FrameText() => AnsiEscape.Replace(Backend.Text, string.Empty);

    /// <summary>
    /// Last rendered pass as normalized lines — same contract as the PTY
    /// <c>NormalizeLines</c>: trailing whitespace trimmed per line, trailing
    /// empty lines dropped.
    /// </summary>
    public string[] FrameLines()
    {
        var lines = FrameText().Split('\n');
        for (int i = 0; i < lines.Length; i++)
            lines[i] = lines[i].TrimEnd();
        int last = lines.Length - 1;
        while (last >= 0 && lines[last].Length == 0)
            last--;
        return lines[..(last + 1)];
    }

    /// <summary>Current composer draft (store → projection → buffer, after idle).</summary>
    public string ComposerText() => Renderer.PromptBuffer.SnapshotText();

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            Renderer.Dispose();
        }

        return ValueTask.CompletedTask;
    }
}
