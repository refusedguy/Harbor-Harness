using System.Text;
using Harbor.Tui.CellForge.Capabilities;
using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// Issue #2217: OSC 8 links stay out of Cell. These tests pin the wire format,
/// the capability fallback, and the frame contract: the escape bytes wrap the
/// visible rune but leave the MoveTo/advance column sequence byte-identical to
/// a frame without links.
/// </summary>
public class Osc8HyperlinkTests
{
    private const string Url = "https://example.com/docs";
    private const string Open = "\u001B]8;;https://example.com/docs\u001B\\";
    private const string Close = "\u001B]8;;\u001B\\";

    [Test]
    public async Task Formatter_OpenClose_ExactBytes()
    {
        await Assert.That(Encoding.ASCII.GetString(Osc8Hyperlink.Open(Url))).IsEqualTo(Open);
        await Assert.That(Encoding.ASCII.GetString(Osc8Hyperlink.Close)).IsEqualTo(Close);
    }

    [Test]
    public async Task Formatter_OpenWithId_UsesParamsField()
    {
        string seq = Encoding.ASCII.GetString(Osc8Hyperlink.Open(Url, "doc-1"));
        await Assert.That(seq).IsEqualTo("\u001B]8;id=doc-1;https://example.com/docs\u001B\\");
    }

    [Test]
    public async Task Formatter_SanitizesEnvelopeBreakers()
    {
        string seq = Encoding.ASCII.GetString(Osc8Hyperlink.Open("https://x/\u001B]52;c;AAA\u0007?a=1;b=2"));
        // ESC and BEL cannot terminate or chain the OSC string; the query
        // string's semicolons, which are legal inside the URI field, survive.
        await Assert.That(seq.Contains("\u001B]52")).IsFalse();
        await Assert.That(seq.Contains("\u0007")).IsFalse();
        await Assert.That(seq.Contains("?a=1;b=2")).IsTrue();
    }

    [Test]
    public async Task Probe_OverrideOffAndOn_Win()
    {
        var off = Osc8HyperlinkProbe.Detect(k => k == Osc8HyperlinkProbe.OverrideVar ? "off" : null);
        var on = Osc8HyperlinkProbe.Detect(k => k == Osc8HyperlinkProbe.OverrideVar ? "on" : (k == "TMUX" ? "1" : null));
        await Assert.That(off).IsFalse();
        await Assert.That(on).IsTrue(); // overrides the multiplexer guard
    }

    [Test]
    public async Task Probe_MultiplexerOff_UnknownOff_KnownTerminalOn()
    {
        string? None(string k) => null;
        await Assert.That(Osc8HyperlinkProbe.Detect(None)).IsFalse();
        await Assert.That(Osc8HyperlinkProbe.Detect(k => k == "TMUX" ? "/tmp/t" : null)).IsFalse();
        await Assert.That(Osc8HyperlinkProbe.Detect(k => k == "WT_SESSION" ? "abc" : null)).IsTrue();
        await Assert.That(Osc8HyperlinkProbe.Detect(k => k == "TERM_PROGRAM" ? "WezTerm" : null)).IsTrue();
    }

    [Test]
    public async Task Golden_LinkWrapsTextWithoutChangingColumns()
    {
        string plain = Frame(linksEnabled: false, attachLinks: false);
        string linked = Frame(linksEnabled: true, attachLinks: true);

        // The link bytes are present and bracket the visible text.
        await Assert.That(linked.Contains(Open)).IsTrue();
        await Assert.That(linked.Contains(Close)).IsTrue();
        await Assert.That(linked.Contains("hello")).IsTrue();

        // Strong geometry pin: remove ONLY the OSC 8 pairs and the stream is
        // byte-for-byte the frame that never had links. Cursor moves, SGR and
        // rune bytes are untouched, so a link cannot consume a column.
        await Assert.That(StripOsc8(linked)).IsEqualTo(plain);
    }

    [Test]
    public async Task DisabledCapability_EmitsOnlyVisibleText()
    {
        string plain = Frame(linksEnabled: false, attachLinks: false);
        string disabled = Frame(linksEnabled: false, attachLinks: true);
        await Assert.That(disabled).IsEqualTo(plain);
        await Assert.That(disabled.Contains("\u001B]8;")).IsFalse();
    }

    [Test]
    public async Task CellBudget_StillSixteenBytes()
    {
        // The whole point of the side-table: no link field was added to Cell.
        await Assert.That(EngineCells.Cell.SizeBytes).IsEqualTo(16);
    }

    private static string Frame(bool linksEnabled, bool attachLinks)
    {
        var backend = new RecordingBackend();
        var writer = new AnsiWriter(backend, syncUpdates: true);
        var engine = new EngineCells.DiffEngine(8, 1);
        var next = new EngineCells.ScreenBuffer(8, 1);
        next.SetText(0, 0, "hello", EngineCells.CellStyle.Plain);

        var links = new HyperlinkTable(8, 1) { Enabled = linksEnabled };
        if (attachLinks)
        {
            for (int x = 0; x < 5; x++)
            {
                links.Set(x, 0, Url);
            }
        }

        writer.BeginFrame();
        engine.Flush(next, writer, links);
        writer.EndFrame();
        return backend.Text;
    }

    /// <summary>Removes every «ESC ] 8 ; … ESC \» pair from a captured frame.</summary>
    private static string StripOsc8(string frame)
    {
        var sb = new StringBuilder(frame.Length);
        for (int i = 0; i < frame.Length;)
        {
            if (i + 3 < frame.Length && frame[i] == '\u001B' && frame[i + 1] == ']' && frame[i + 2] == '8' && frame[i + 3] == ';')
            {
                int j = i + 4;
                while (j + 1 < frame.Length && !(frame[j] == '\u001B' && frame[j + 1] == '\\'))
                {
                    j++;
                }

                i = j + 2 <= frame.Length ? j + 2 : frame.Length;
                continue;
            }

            sb.Append(frame[i++]);
        }

        return sb.ToString();
    }
}
