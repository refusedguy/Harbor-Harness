using System.Text;
using Harbor.Tui.ConsoleEx.Input;
using Harbor.Tui.ConsoleEx.Parsing;

namespace Harbor.Tui.ConsoleEx.Tests;

/// <summary>
/// Golden-byte vectors for bracketed paste (design §4) with the mandatory
/// anti-injection invariants of §4.2: markers never leak, content is never
/// interpreted as commands/keys, newlines never synthesize Enter, embedded
/// escape bytes stay literal, split blocks reassemble across chunks.
/// </summary>
public class BracketedPasteTests
{
    private const string Open = "\u001B[200~";
    private const string Close = "\u001B[201~";

    [Test]
    public async Task Simple_Paste_Is_One_Atomic_Event()
    {
        var parser = new EscapeSequenceParser();
        var events = T.Feed(parser, $"{Open}hello world{Close}");

        await Assert.That(events.Length).IsEqualTo(1);
        await Assert.That(events[0].Kind).IsEqualTo(InputEventKind.Paste);
        await Assert.That(events[0].Paste.Text).IsEqualTo("hello world");
        await Assert.That(events[0].Paste.WasTruncated).IsFalse();
        await Assert.That(parser.State).IsEqualTo(ParserState.Ground);
        await Assert.That(parser.IsAwaitingPasteClose).IsFalse();
    }

    [Test]
    public async Task Empty_Paste_Emits_Empty_Text()
    {
        var events = T.Feed(new EscapeSequenceParser(), $"{Open}{Close}");

        await Assert.That(events.Length).IsEqualTo(1);
        await Assert.That(events[0].Paste.Text).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Multiline_Paste_Never_Synthesizes_Enter_Keypress()
    {
        // THE anti-injection invariant (§4.2 #2): "вставь и выполни" payloads
        // must not submit. Newlines are literal text inside the single event.
        var parser = new EscapeSequenceParser();
        var events = T.Feed(parser, $"{Open}rm -rf /\r\nharbor --dangerous{Close}");

        await Assert.That(events.Length).IsEqualTo(1);
        await Assert.That(events[0].Kind).IsEqualTo(InputEventKind.Paste);
        await Assert.That(events[0].Paste.Text).IsEqualTo("rm -rf /\r\nharbor --dangerous");
        await Assert.That(events.Any(e => e.Kind == InputEventKind.Key)).IsFalse();
    }

    [Test]
    public async Task Escape_Sequence_Inside_Paste_Stays_Literal()
    {
        // §4.2 #4: ESC-in-paste attacks — raw arrow-key bytes must not decode.
        var events = T.Feed(new EscapeSequenceParser(), $"{Open}\u001B[A\u001B[13u\u0003{Close}");
        var paste = events.Single(e => e.Kind == InputEventKind.Paste);

        await Assert.That(paste.Paste.Text).IsEqualTo("\u001B[A\u001B[13u\u0003");
        await Assert.That(events.Any(e => e.Kind is InputEventKind.Key or InputEventKind.Mouse)).IsFalse();
    }

    [Test]
    public async Task Nested_Open_Marker_Is_Literal_Content_And_Counted()
    {
        var parser = new EscapeSequenceParser();
        var events = T.Feed(parser, $"{Open}a{Open}b{Close}");

        var paste = events.Single(e => e.Kind == InputEventKind.Paste);
        await Assert.That(paste.Paste.Text).IsEqualTo($"a{Open}b");
        await Assert.That(parser.NestedPasteMarkerCount).IsEqualTo(1);
    }

    [Test]
    public async Task Paste_Split_Across_Chunks_Reassembles()
    {
        var parser = new EscapeSequenceParser();
        var events = T.FeedBytes(
            parser,
            Encoding.UTF8.GetBytes(Open),
            Encoding.UTF8.GetBytes("multi "),
            Encoding.UTF8.GetBytes("chunk payload"),
            Encoding.UTF8.GetBytes(Close));

        await Assert.That(events.Length).IsEqualTo(1);
        await Assert.That(events[0].Paste.Text).IsEqualTo("multi chunk payload");
    }

    [Test]
    public async Task Closing_Marker_Split_Across_Chunks_Still_Closes()
    {
        var parser = new EscapeSequenceParser();
        var events = T.FeedBytes(
            parser,
            Encoding.UTF8.GetBytes(Open + "x"),
            [(byte)0x1B, (byte)'['],
            [(byte)'2', (byte)'0', (byte)'1', (byte)'~']);

        await Assert.That(events.Length).IsEqualTo(1);
        await Assert.That(events[0].Paste.Text).IsEqualTo("x");
        await Assert.That(parser.IsAwaitingPasteClose).IsFalse();
    }

    [Test]
    public async Task Esc_At_Chunk_End_Does_Not_Start_Key_Sequence_Inside_Paste()
    {
        // The trailing ESC belongs to the closing marker, not to a key event.
        var parser = new EscapeSequenceParser();
        var events = T.FeedBytes(
            parser,
            Encoding.UTF8.GetBytes(Open + "y"),
            [(byte)0x1B],
            Encoding.UTF8.GetBytes("[201~"));

        await Assert.That(events.Length).IsEqualTo(1);
        await Assert.That(events[0].Paste.Text).IsEqualTo("y");
    }

    [Test]
    public async Task Content_Partially_Matching_Closer_Stays_Literal()
    {
        // "\e[2" is a closer prefix but diverges before completion.
        var events = T.Feed(new EscapeSequenceParser(), $"{Open}\u001B[2z{Close}");

        await Assert.That(events.Length).IsEqualTo(1);
        await Assert.That(events[0].Paste.Text).IsEqualTo("\u001B[2z");
    }

    [Test]
    public async Task Oversize_Paste_Is_Truncated_At_Cap()
    {
        var parser = new EscapeSequenceParser(new ParserOptions { MaxPasteBytes = 16 });
        var payload = new string('x', 40);
        var events = T.Feed(parser, $"{Open}{payload}{Close}");

        await Assert.That(events.Length).IsEqualTo(1);
        await Assert.That(events[0].Paste.Text.Length).IsEqualTo(16);
        await Assert.That(events[0].Paste.WasTruncated).IsTrue();
        // The block still terminates cleanly at the marker.
        await Assert.That(parser.IsAwaitingPasteClose).IsFalse();
        var after = T.Feed(parser, "a");
        await A.IsChar(after[0], new Rune('a'));
    }

    [Test]
    public async Task Unclosed_Paste_Aborted_By_Watchdog_Emits_Truncated()
    {
        var parser = new EscapeSequenceParser();
        _ = T.Feed(parser, $"{Open}partial data");

        await Assert.That(parser.IsAwaitingPasteClose).IsTrue();

        parser.AbortPendingPaste();

        var events = T.Drain(parser);
        await Assert.That(events.Length).IsEqualTo(1);
        await Assert.That(events[0].Paste.Text).IsEqualTo("partial data");
        await Assert.That(events[0].Paste.WasTruncated).IsTrue();
        await Assert.That(parser.State).IsEqualTo(ParserState.Ground);

        // Parser remains fully usable after the emergency exit.
        var next = T.Feed(parser, "\u001B[A");
        await A.IsKey(next[0], KeyCode.Up);
    }

    [Test]
    public async Task Abort_Outside_Paste_Is_Noop()
    {
        var parser = new EscapeSequenceParser();

        parser.AbortPendingPaste();

        await Assert.That(parser.AvailableEvents).IsEqualTo(0);
    }

    [Test]
    public async Task Unicode_Payload_Is_Preserved_Verbatim()
    {
        var events = T.Feed(new EscapeSequenceParser(), $"{Open}привет 😀 мир{Close}");

        await Assert.That(events.Length).IsEqualTo(1);
        await Assert.That(events[0].Paste.Text).IsEqualTo("привет 😀 мир");
    }

    [Test]
    public async Task Consecutive_Pastes_Emit_Two_Atomic_Events()
    {
        var events = T.Feed(new EscapeSequenceParser(), $"{Open}one{Close}{Open}two{Close}");

        await Assert.That(events.Length).IsEqualTo(2);
        await Assert.That(events[0].Paste.Text).IsEqualTo("one");
        await Assert.That(events[1].Paste.Text).IsEqualTo("two");
    }

    [Test]
    public async Task Legacy_Tilde_Codes_Other_Than_200_Unaffected()
    {
        var events = T.Feed(new EscapeSequenceParser(), "\u001B[200~\u001B[201~".Replace("\u001B[200~", string.Empty) + "\u001B[3~");

        // Delete still decodes through the legacy tilde path.
        await A.IsKey(events[0], KeyCode.Delete);
    }
}
