using System.Text;
using Harbor.Tui.ConsoleEx.Input;
using Harbor.Tui.ConsoleEx.Parsing;

namespace Harbor.Tui.ConsoleEx.Tests;

/// <summary>State-machine transition and chunking tests for the raw parser.</summary>
public class ParserStateMachineTests
{
    private readonly EscapeSequenceParser _parser = new();

    // ── Split sequences across reads ──────────────────────────────────────

    [Test]
    public async Task Csi_Split_Across_Chunks_Produces_One_Event()
    {
        var events = T.Feed(_parser, "\u001B[", "1;5", "A");

        await Assert.That(events.Length).IsEqualTo(1);
        await A.IsKey(events[0], KeyCode.Up, KeyModifiers.Ctrl);
        await Assert.That(_parser.State).IsEqualTo(ParserState.Ground);
    }

    [Test]
    public async Task Utf8_Rune_Split_Across_Chunks_Decodes_Once()
    {
        // 😀 U+1F600 = F0 9F 98 80
        byte[] emoji = [0xF0, 0x9F, 0x98, 0x80];
        var events = T.FeedBytes(_parser, emoji[..2], emoji[2..]);

        await Assert.That(events.Length).IsEqualTo(1);
        await A.IsChar(events[0], new Rune(0x1F600));
    }

    [Test]
    public async Task Cyrillic_TwoByte_Split_Across_Chunks()
    {
        // п U+043F = D0 BF
        var events = T.FeedBytes(_parser, [0xD0], [0xBF]);

        await Assert.That(events.Length).IsEqualTo(1);
        await A.IsChar(events[0], new Rune('п'));
    }

    // ── ESC-timeout policy ────────────────────────────────────────────────

    [Test]
    public async Task Lone_Esc_At_Chunk_Boundary_Flushes_As_Escape_Key()
    {
        _parser.Parse("\u001B"u8);
        await Assert.That(_parser.State).IsEqualTo(ParserState.Escape);
        await Assert.That(_parser.AvailableEvents).IsEqualTo(0);

        _parser.FlushPendingEscape();

        var events = T.Drain(_parser);
        await Assert.That(events.Length).IsEqualTo(1);
        await A.IsKey(events[0], KeyCode.Escape);
        await Assert.That(_parser.State).IsEqualTo(ParserState.Ground);
    }

    [Test]
    public async Task Esc_Continued_In_Next_Chunk_Never_Emits_Phantom_Escape()
    {
        _parser.Parse("\u001B"u8);
        var events = T.Feed(_parser, "[A");

        await Assert.That(events.Length).IsEqualTo(1);
        await A.IsKey(events[0], KeyCode.Up);
    }

    [Test]
    public async Task Flush_In_Ground_Is_Noop()
    {
        _parser.FlushPendingEscape();
        await Assert.That(_parser.AvailableEvents).IsEqualTo(0);
    }

    [Test]
    public async Task Double_Esc_Yields_Escape_Then_Sequence()
    {
        var events = T.Feed(_parser, "\u001B\u001B[A");

        await Assert.That(events.Length).IsEqualTo(2);
        await A.IsKey(events[0], KeyCode.Escape);
        await A.IsKey(events[1], KeyCode.Up);
    }

    // ── OSC / DCS / string states ─────────────────────────────────────────

    [Test]
    public async Task Osc_String_With_Bel_Terminator_Is_Consumed_Silently()
    {
        var events = T.Feed(_parser, "\u001B]0;window title\u0007ok");

        await Assert.That(events.Length).IsEqualTo(2); // o, k
        await A.IsChar(events[0], new Rune('o'));
    }

    [Test]
    public async Task Osc_String_With_St_Terminator_Is_Consumed_Silently()
    {
        var events = T.Feed(_parser, "\u001B]52;c;aGk=\u001B\\done");

        await Assert.That(events.Length).IsEqualTo(4); // d,o,n,e
    }

    [Test]
    public async Task Dcs_String_Is_Consumed_Silently()
    {
        var events = T.Feed(_parser, "\u001BPq#0;2;0;0;0#1~~\u001B\\X");

        await Assert.That(events.Length).IsEqualTo(1);
        await A.IsChar(events[0], new Rune('X'));
    }

    [Test]
    public async Task Unterminated_Osc_Hits_String_Budget_And_Resyncs()
    {
        var parser = new EscapeSequenceParser(new ParserOptions { MaxStringBytes = 8 });
        var events = T.Feed(parser, "\u001B]aaaaaaaaaaaaaaaaaaaaZZZ");

        // Budget overrun emits one Unknown and resyncs to Ground — the tail
        // bytes then parse as plain characters.
        await Assert.That(events.Any(e => e.Kind == InputEventKind.Unknown)).IsTrue();
        await Assert.That(parser.State).IsEqualTo(ParserState.Ground);
        await Assert.That(events.Any(e => e.Kind == InputEventKind.Key && e.Key.Character == new Rune('Z'))).IsTrue();
        await Assert.That(parser.MalformedSequenceCount).IsGreaterThanOrEqualTo(1);
    }

    // ── Malformed CSI handling ────────────────────────────────────────────

    [Test]
    public async Task Param_Budget_Overflow_Enters_Ignore_And_Resyncs_On_Final()
    {
        var overflow = new string('9', ParserOptions.DefaultMaxParamsBytes + 5);
        var events = T.Feed(_parser, $"\u001B[{overflow}A\u001B[B");

        await Assert.That(events.Any(e => e.Kind == InputEventKind.Unknown)).IsTrue();
        await Assert.That(_parser.MalformedSequenceCount).IsEqualTo(1);
        // The next sequence after the aborted one decodes normally.
        await A.IsKey(events[^1], KeyCode.Down);
    }

    [Test]
    public async Task Param_After_Intermediate_Is_Malformed()
    {
        var events = T.Feed(_parser, "\u001B[ 5A");

        await Assert.That(events.Any(e => e.Kind == InputEventKind.Unknown)).IsTrue();
        await Assert.That(_parser.MalformedSequenceCount).IsEqualTo(1);
    }

    [Test]
    public async Task Can_Aborts_Csi_And_Resyncs_To_Ground()
    {
        var events = T.Feed(_parser, "\u001B[12;\u0018X");

        // CAN cancels the sequence; X arrives as a plain character.
        await Assert.That(events.Length).IsEqualTo(1);
        await A.IsChar(events[0], new Rune('X'));
        await Assert.That(_parser.MalformedSequenceCount).IsEqualTo(0);
    }

    [Test]
    public async Task Esc_Restart_Inside_Csi_Discards_Partial_Sequence()
    {
        var events = T.Feed(_parser, "\u001B[1\u001B[A");

        await Assert.That(events.Length).IsEqualTo(1);
        await A.IsKey(events[0], KeyCode.Up);
    }

    [Test]
    public async Task Del_Inside_Csi_Is_Skipped_Per_ECMA48()
    {
        var events = T.Feed(_parser, "\u001B[1\u007FA");

        await Assert.That(events.Length).IsEqualTo(1);
        await A.IsKey(events[0], KeyCode.Up);
    }

    [Test]
    public async Task Unrequested_Private_Mode_Report_Is_Ignored_Silently()
    {
        var events = T.Feed(_parser, "\u001B[?1049h");

        await Assert.That(events.Length).IsEqualTo(0);
        await Assert.That(_parser.IgnoredSequenceCount).IsEqualTo(1);
    }

    // ── UTF-8 error paths ─────────────────────────────────────────────────

    [Test]
    public async Task Invalid_Lead_Byte_Becomes_Replacement_Char()
    {
        var events = T.FeedBytes(_parser, [0xFF]);

        await Assert.That(events.Length).IsEqualTo(1);
        await A.IsChar(events[0], Rune.ReplacementChar);
    }

    [Test]
    public async Task Broken_Sequence_Flushes_Replacement_And_Reprocesses_Current_Byte()
    {
        // E0 starts a 3-byte lead, but 'A' is not a continuation.
        var events = T.FeedBytes(_parser, [0xE0, (byte)'A']);

        await Assert.That(events.Length).IsEqualTo(2);
        await A.IsChar(events[0], Rune.ReplacementChar);
        await A.IsChar(events[1], new Rune('A'));
    }

    [Test]
    public async Task Overlong_Encoding_Collapses_To_Replacement()
    {
        // C0 AF is the classic overlong '/'.
        var events = T.FeedBytes(_parser, [0xC0, 0xAF]);

        await Assert.That(events.Count(e => e.Kind == InputEventKind.Key && e.Key.Key == KeyCode.Char)).IsGreaterThanOrEqualTo(1);
        await Assert.That(events.Any(e => e.Key.Character == Rune.ReplacementChar)).IsTrue();
        await Assert.That(events.Any(e => e.Key.Character == new Rune('/'))).IsFalse();
    }

    [Test]
    public async Task Surrogate_Encoding_Collapses_To_Replacement()
    {
        // ED A0 80 encodes U+D800 (surrogate) — invalid scalar.
        var events = T.FeedBytes(_parser, [0xED, 0xA0, 0x80]);

        await Assert.That(events.Length).IsEqualTo(1);
        await A.IsChar(events[0], Rune.ReplacementChar);
    }

    [Test]
    public async Task Mixed_Text_And_Emoji_Decodes_In_Order()
    {
        var events = T.Feed(_parser, "a😀b");

        await Assert.That(events.Length).IsEqualTo(3);
        await A.IsChar(events[0], new Rune('a'));
        await A.IsChar(events[1], new Rune(0x1F600));
        await A.IsChar(events[2], new Rune('b'));
    }

    // ── Event queue behaviour ─────────────────────────────────────────────

    [Test]
    public async Task Queue_Grows_Beyond_Initial_Capacity_Without_Loss()
    {
        const int count = 200;
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < count; i++)
        {
            sb.Append((char)('a' + (i % 26)));
        }

        var events = T.Feed(_parser, sb.ToString());

        await Assert.That(events.Length).IsEqualTo(count);
        await Assert.That(events[0].Key.Character).IsEqualTo(new Rune('a'));
        await Assert.That(events[count - 1].Key.Character).IsEqualTo(new Rune((char)('a' + ((count - 1) % 26))));
    }

    [Test]
    public async Task TryTake_Event_Returns_Fifo_Order()
    {
        _parser.Parse("abc"u8);

        await Assert.That(_parser.TryTakeEvent(out var first)).IsTrue();
        await Assert.That(first.Key.Character).IsEqualTo(new Rune('a'));
        await Assert.That(_parser.TryTakeEvent(out var second)).IsTrue();
        await Assert.That(second.Key.Character).IsEqualTo(new Rune('b'));
        await Assert.That(_parser.TryTakeEvent(out _)).IsTrue();
        await Assert.That(_parser.TryTakeEvent(out _)).IsFalse();
        await Assert.That(_parser.AvailableEvents).IsEqualTo(0);
    }

    [Test]
    public async Task Reset_Clears_State_Events_And_Counters_Context()
    {
        _parser.Parse("\u001B[1;"u8);
        _parser.Reset();

        await Assert.That(_parser.State).IsEqualTo(ParserState.Ground);
        await Assert.That(_parser.AvailableEvents).IsEqualTo(0);
        // Parser remains usable after reset.
        var events = T.Feed(_parser, "\u001B[A");
        await Assert.That(events.Length).IsEqualTo(1);
    }
}
