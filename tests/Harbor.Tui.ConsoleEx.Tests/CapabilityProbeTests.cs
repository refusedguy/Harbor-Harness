using Harbor.Tui.ConsoleEx.Capabilities;
using Harbor.Tui.ConsoleEx.Input;
using Harbor.Tui.ConsoleEx.Parsing;

namespace Harbor.Tui.ConsoleEx.Tests;

/// <summary>
/// Capability detection tests (design §2.4): probe sequence golden strings,
/// response routing, Evaluate mapping, timeout fallback ladder and the
/// tmux/screen guardrail.
/// </summary>
public class CapabilityProbeTests
{
    // ── Probe sequence golden strings ─────────────────────────────────────

    [Test]
    public async Task Kitty_Probe_Sequences_Are_Exact_Bytes()
    {
        await Assert.That(TerminalQueries.KittyQuery).IsEqualTo("\u001B[?u");
        await Assert.That(TerminalQueries.KittyPush(1)).IsEqualTo("\u001B[>1u");
        await Assert.That(TerminalQueries.KittyPush(17)).IsEqualTo("\u001B[>17u");
        await Assert.That(TerminalQueries.KittyPop).IsEqualTo("\u001B[<u");
        await Assert.That(TerminalQueries.DecRqmBracketedPaste).IsEqualTo("\u001B[?2004$p");
    }

    [Test]
    public async Task Mouse_And_Paste_Mode_Sequences_Are_Exact_Bytes()
    {
        await Assert.That(TerminalQueries.MouseClickEnable).IsEqualTo("\u001B[?1000h\u001B[?1006h");
        await Assert.That(TerminalQueries.MouseDragEnable).IsEqualTo("\u001B[?1002h");
        await Assert.That(TerminalQueries.MouseDisable).IsEqualTo("\u001B[?1006l\u001B[?1002l\u001B[?1000l");
        await Assert.That(TerminalQueries.PasteEnable).IsEqualTo("\u001B[?2004h");
        await Assert.That(TerminalQueries.PasteDisable).IsEqualTo("\u001B[?2004l");
    }

    // ── Parser routing of probe answers ───────────────────────────────────

    [Test]
    [Arguments("\u001B[?1u", 1)]
    [Arguments("\u001B[?0u", 0)]
    [Arguments("\u001B[?3u", 3)]
    public async Task Kitty_Query_Answer_Routes_To_Capability_Not_User_Input(string input, uint expectedFlags)
    {
        var parser = new EscapeSequenceParser();
        var events = T.Feed(parser, input);

        await Assert.That(events.Length).IsEqualTo(1);
        await Assert.That(events[0].Kind).IsEqualTo(InputEventKind.Capability);
        await Assert.That(events[0].Capability.Kind).IsEqualTo(CapabilityEventKind.KittyFlagsReport);
        await Assert.That((int)events[0].Capability.Flags).IsEqualTo((int)expectedFlags);
        // Never surfaces as a keypress.
        await Assert.That(parser.AvailableEvents).IsEqualTo(0);
    }

    [Test]
    public async Task Decrqm_Answer_Routes_To_Capability()
    {
        var events = T.Feed(new EscapeSequenceParser(), "\u001B[?2004;2$y");

        await Assert.That(events.Length).IsEqualTo(1);
        await Assert.That(events[0].Capability.Kind).IsEqualTo(CapabilityEventKind.DecRqmReport);
        await Assert.That(events[0].Capability.Mode).IsEqualTo(2004);
        await Assert.That(events[0].Capability.Value).IsEqualTo(2);
    }

    [Test]
    public async Task Device_Attributes_Route_To_Capability()
    {
        var events = T.Feed(new EscapeSequenceParser(), "\u001B[?62;1;6c");

        await Assert.That(events.Length).IsEqualTo(1);
        await Assert.That(events[0].Capability.Kind).IsEqualTo(CapabilityEventKind.DeviceAttributes);
        await Assert.That(events[0].Capability.Mode).IsEqualTo(62);
    }

    [Test]
    public async Task Cursor_Position_Report_Routes_To_Capability()
    {
        var events = T.Feed(new EscapeSequenceParser(), "\u001B[10;20R");

        await Assert.That(events.Length).IsEqualTo(1);
        await Assert.That(events[0].Capability.Kind).IsEqualTo(CapabilityEventKind.CursorPositionReport);
        await Assert.That(events[0].Capability.Row).IsEqualTo(10);
        await Assert.That(events[0].Capability.Column).IsEqualTo(20);
    }

    // ── Evaluate mapping ──────────────────────────────────────────────────

    [Test]
    public async Task Evaluate_Kitty_Report_Confirms_Kitty_With_Flags()
    {
        var caps = CapabilityProber.Evaluate([CapabilityEvent.KittyFlags(1)]);

        await Assert.That(caps.Probed).IsTrue();
        await Assert.That(caps.Kitty).IsTrue();
        await Assert.That((int)caps.KittyFlags).IsEqualTo(1);
        await Assert.That(caps.VtResponsive).IsTrue();
    }

    [Test]
    public async Task Evaluate_Decrqm_Paste_Confirms_VT_Legacy_Path()
    {
        var caps = CapabilityProber.Evaluate([CapabilityEvent.DecRqm(2004, 1)]);

        await Assert.That(caps.Probed).IsTrue();
        await Assert.That(caps.Kitty).IsFalse();
        await Assert.That(caps.VtResponsive).IsTrue();
        await Assert.That(caps.BracketedPasteConfirmed).IsTrue();
    }

    [Test]
    public async Task Evaluate_Empty_Is_Conservative_Default()
    {
        var caps = CapabilityProber.Evaluate([]);

        await Assert.That(caps.Probed).IsTrue();
        await Assert.That(caps.Kitty).IsFalse();
        await Assert.That(caps.VtResponsive).IsFalse();
        await Assert.That(caps.BracketedPasteConfirmed).IsFalse();
    }

    // ── Fallback ladder orchestration ─────────────────────────────────────

    [Test]
    public async Task ProbeAsync_Kitty_Answer_Skips_Fallback()
    {
        var transport = new FakeTransport([CapabilityEvent.KittyFlags(1)]);
        var prober = new CapabilityProber(_ => null);

        var caps = await prober.ProbeAsync(transport);

        await Assert.That(caps.Kitty).IsTrue();
        await Assert.That(transport.Sent.Count).IsEqualTo(1);
        await Assert.That(transport.Sent[0]).IsEqualTo(TerminalQueries.KittyQuery);
    }

    [Test]
    public async Task ProbeAsync_Kitty_Silence_Triggers_Decrqm_Fallback()
    {
        var transport = new FakeTransport([null, CapabilityEvent.DecRqm(2004, 1)]);
        var prober = new CapabilityProber(_ => null);

        var caps = await prober.ProbeAsync(transport);

        await Assert.That(caps.Kitty).IsFalse();
        await Assert.That(caps.VtResponsive).IsTrue();
        await Assert.That(transport.Sent.Count).IsEqualTo(2);
        await Assert.That(transport.Sent[1]).IsEqualTo(TerminalQueries.DecRqmBracketedPaste);
    }

    [Test]
    public async Task ProbeAsync_Total_Silence_Yields_Conservative_Defaults()
    {
        var transport = new FakeTransport([null, null]);
        var prober = new CapabilityProber(_ => null);

        var caps = await prober.ProbeAsync(transport);

        await Assert.That(caps.Probed).IsTrue();
        await Assert.That(caps.Kitty).IsFalse();
        await Assert.That(caps.VtResponsive).IsFalse();
    }

    [Test]
    public async Task ProbeAsync_Inside_Multiplexer_Never_Queries_Kitty()
    {
        var transport = new FakeTransport([null, null]);
        var prober = new CapabilityProber(env => env == "TMUX" ? "/run/user/1000/tmx-sock,42,0" : null);

        await prober.ProbeAsync(transport);

        await Assert.That(transport.Sent.Any(s => s == TerminalQueries.KittyQuery)).IsFalse();
    }

    [Test]
    [Arguments("TMUX", "sock,42,0")]
    [Arguments("STY", "12345.pts-0.host")]
    public async Task IsInsideMultiplexer_Detects_Tmux_And_Screen(string envVar, string value)
    {
        var prober = new CapabilityProber(env => env == envVar ? value : null);

        await Assert.That(prober.IsInsideMultiplexer()).IsTrue();
    }

    [Test]
    public async Task IsInsideMultiplexer_Clear_Outside()
    {
        var prober = new CapabilityProber(_ => null);

        await Assert.That(prober.IsInsideMultiplexer()).IsFalse();
    }

    private sealed class FakeTransport(CapabilityEvent?[] answers) : ICapabilityProbeTransport
    {
        private readonly Queue<CapabilityEvent?> _answers = new(answers);

        public List<string> Sent { get; } = [];

        public Task SendAsync(string sequence, CancellationToken cancellationToken = default)
        {
            Sent.Add(sequence);
            return Task.CompletedTask;
        }

        public Task<CapabilityEvent?> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult(_answers.Count > 0 ? _answers.Dequeue() : null);
    }
}
