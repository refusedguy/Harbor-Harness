using System.Text;
using Harbor.Tui.ConsoleEx.Input;
using Harbor.Tui.ConsoleEx.Parsing;
using Harbor.Tui.ConsoleEx.Rendering;

namespace Harbor.Tui.ConsoleEx.Tests;

public class CyrillicRenderingTests
{
    // ── Viewport ─────────────────────────────────────────────────────────

    [Test]
    public async Task Viewport_CyrillicLongLine_CaretAtEnd_ScrollsCharOffsetCorrect()
    {
        // "приветпривет" Cyrillic 12 chars, 12 cells, width 10
        const string line = "приветпривет";
        int totalCells = PromptBuffer.DisplayCells(line);
        await Assert.That(totalCells).IsEqualTo(12);

        var vp = PromptViewport.ScrollIntoView(line, caretInLine: 12, widthCells: 10);
        // totalCells(12) - width(10)=2, caretCell(12)-width+1=3 => startCell=2 => Start=2
        await Assert.That(vp.Start).IsEqualTo(2);
        await Assert.That(line[vp.Start..]).IsEqualTo("иветпривет");
        int visibleCells = PromptBuffer.DisplayCells(line.AsSpan(vp.Start));
        await Assert.That(visibleCells).IsEqualTo(10);
        // width is exactly visible slice
        await Assert.That(visibleCells).IsLessThanOrEqualTo(10);
    }

    [Test]
    public async Task Viewport_CyrillicShortLine_FitsWidth_StartsAtZero()
    {
        const string line = "привет";
        var vp = PromptViewport.ScrollIntoView(line, caretInLine: 3, widthCells: 10);
        await Assert.That(vp.Start).IsEqualTo(0);
    }

    [Test]
    public async Task Viewport_CyrillicCaretAtStart_NoScroll()
    {
        const string line = "приветпривет";
        var vp = PromptViewport.ScrollIntoView(line, caretInLine: 0, widthCells: 10);
        await Assert.That(vp.Start).IsEqualTo(0);
    }

    [Test]
    public async Task Viewport_CyrillicMidCaret_WindowContainsCaret()
    {
        // "привет мир" = 10 cells; width 6; caret at end(10) => startCell 4 => Start 4
        const string line = "привет мир";
        var vp = PromptViewport.ScrollIntoView(line, caretInLine: line.Length, widthCells: 6);
        await Assert.That(vp.Start).IsEqualTo(4);
        await Assert.That(line[vp.Start..]).IsEqualTo("ет мир");
        // caret must be inside window [startCell .. startCell+width-1]
        int startCell = PromptBuffer.DisplayCells(line.AsSpan(0, vp.Start));
        int caretCell = PromptBuffer.DisplayCells(line.AsSpan(0, line.Length));
        await Assert.That(caretCell).IsLessThanOrEqualTo(startCell + 6);
        await Assert.That(caretCell).IsGreaterThanOrEqualTo(startCell);
    }

    [Test]
    public async Task Viewport_CyrillicWidthOne_ShowsLastChar()
    {
        const string line = "привет";
        var vp = PromptViewport.ScrollIntoView(line, caretInLine: 6, widthCells: 1);
        // total 6, width 1 => startCell 5 => Start 5 => last char "т"
        await Assert.That(vp.Start).IsEqualTo(5);
        await Assert.That(line[vp.Start..]).IsEqualTo("т");
    }

    // ── TextWrap ─────────────────────────────────────────────────────────

    [Test]
    public async Task TextWrap_CyrillicGreedy_WordBoundaries()
    {
        var lines = new List<string>();
        TextWrap.WrapTo("привет мир как дела", 6, lines);
        await Assert.That(lines).IsEquivalentTo(["привет", "мир", "как", "дела"]);
    }

    [Test]
    public async Task TextWrap_CyrillicWordLongerThanWidth_HardBreaks()
    {
        var lines = new List<string>();
        TextWrap.WrapTo("привет", 3, lines);
        await Assert.That(lines).IsEquivalentTo(["при", "вет"]);
    }

    [Test]
    public async Task TextWrap_CyrillicLongWordWithSpaces_HardBreakMidWord()
    {
        // width 5: "привет мир" => first fit "приве" (5 cells, next char 'т' mid-word, no space to back off)
        var lines = new List<string>();
        TextWrap.WrapTo("привет мир", 5, lines);
        await Assert.That(lines).IsEquivalentTo(["приве", "т мир"]);
    }

    [Test]
    public async Task TextWrap_CyrillicExactWidth_NoSplit()
    {
        var lines = new List<string>();
        TextWrap.WrapTo("привет", 6, lines);
        await Assert.That(lines).IsEquivalentTo(["привет"]);
    }

    // ── ScreenBuffer ─────────────────────────────────────────────────────

    [Test]
    public async Task ScreenBuffer_SetText_Cyrillic_NarrowCells()
    {
        var buf = new ScreenBuffer(10, 1);
        buf.SetText(0, 0, "привет", CellStyle.Plain);

        await Assert.That(buf.Get(0, 0).Rune).IsEqualTo('п');
        await Assert.That(buf.Get(1, 0).Rune).IsEqualTo('р');
        await Assert.That(buf.Get(2, 0).Rune).IsEqualTo('и');
        await Assert.That(buf.Get(3, 0).Rune).IsEqualTo('в');
        await Assert.That(buf.Get(4, 0).Rune).IsEqualTo('е');
        await Assert.That(buf.Get(5, 0).Rune).IsEqualTo('т');
        await Assert.That(buf.Get(0, 0).Width).IsEqualTo(Cell.Narrow);
        await Assert.That(buf.Get(5, 0).Width).IsEqualTo(Cell.Narrow);
        // trailing cell stays blank
        await Assert.That(buf.Get(6, 0).IsBlankSpace).IsTrue();
    }

    [Test]
    public async Task ScreenBuffer_SetText_Cyrillic_RowHash()
    {
        var buf = new ScreenBuffer(10, 1);
        buf.SetText(0, 0, "привет", CellStyle.Plain);
        ulong hashAfterWrite = buf.RowHashCode(0);
        await Assert.That(buf.IsRowHashValid(0)).IsTrue();

        // mutation invalidates, recomputed differs
        ulong before = hashAfterWrite;
        _ = buf.SetRune(0, 0, new Rune('я'), CellStyle.Plain);
        await Assert.That(buf.IsRowHashValid(0)).IsFalse();
        ulong after = buf.RowHashCode(0);
        await Assert.That(after).IsNotEqualTo(before);
        await Assert.That(buf.Get(0, 0).Rune).IsEqualTo('я');
    }

    [Test]
    public async Task ScreenBuffer_SetRune_Cyrillic_SingleCell()
    {
        var buf = new ScreenBuffer(10, 1);
        bool placed = buf.SetRune(4, 0, new Rune('я'), CellStyle.Plain);
        await Assert.That(placed).IsTrue();
        await Assert.That(buf.Get(4, 0).Rune).IsEqualTo(0x044F);
        await Assert.That(buf.Get(4, 0).Width).IsEqualTo(Cell.Narrow);
        // adjacent cells untouched
        await Assert.That(buf.Get(3, 0).IsBlankSpace).IsTrue();
        await Assert.That(buf.Get(5, 0).IsBlankSpace).IsTrue();
    }

    [Test]
    public async Task ScreenBuffer_SetRune_Cyrillic_Overwrite()
    {
        var buf = new ScreenBuffer(10, 1);
        buf.SetText(0, 0, "привет", CellStyle.Plain);
        _ = buf.SetRune(2, 0, new Rune('я'), CellStyle.Plain);
        await Assert.That(buf.Get(2, 0).Rune).IsEqualTo('я');
        await Assert.That(buf.Get(1, 0).Rune).IsEqualTo('р');
        await Assert.That(buf.Get(3, 0).Rune).IsEqualTo('в');
    }

    [Test]
    public async Task ScreenBuffer_RowHash_EqualRows_WithCyrillic()
    {
        var buf = new ScreenBuffer(10, 2);
        buf.SetText(0, 0, "привет", CellStyle.Plain);
        buf.SetText(0, 1, "привет", CellStyle.Plain);
        await Assert.That(buf.RowHashCode(0)).IsEqualTo(buf.RowHashCode(1));
    }

    // ── AnsiWriter ───────────────────────────────────────────────────────

    private static (AnsiWriter Writer, RecordingBackend Backend) MakeWriter(bool sync = false)
    {
        var backend = new RecordingBackend();
        return (new AnsiWriter(backend, sync), backend);
    }

    [Test]
    public async Task AnsiWriter_PutRune_Cyrillic_Ya_BytesAndWidth()
    {
        var (w, backend) = MakeWriter();
        w.BeginFrame();
        w.MoveTo(0, 0);
        w.PutRune(new Rune('я')); // U+044F -> D1 8F
        await Assert.That(w.TrackedX).IsEqualTo(1);
        await Assert.That(w.TrackedY).IsEqualTo(0);
        await w.EndFrameAsync();

        // collect raw bytes after CUP
        var bytes = backend.Writes.SelectMany(b => b).ToArray();
        // CUP "\x1B[1;1H" is 6 bytes, then D1 8F
        await Assert.That(bytes.Length).IsEqualTo(8);
        await Assert.That(bytes[6]).IsEqualTo((byte)0xD1);
        await Assert.That(bytes[7]).IsEqualTo((byte)0x8F);
        // decode the payload bytes (skip CUP)
        string payload = Encoding.UTF8.GetString(bytes, 6, 2);
        await Assert.That(payload).IsEqualTo("я");
    }

    [Test]
    public async Task AnsiWriter_WriteText_Cyrillic_RoundTripsViaUtf8()
    {
        var (w, backend) = MakeWriter();
        w.BeginFrame();
        w.WriteText("привет");
        await w.EndFrameAsync();

        var bytes = backend.Writes.SelectMany(b => b).ToArray();
        string decoded = Encoding.UTF8.GetString(bytes);
        await Assert.That(decoded).IsEqualTo("привет");
        // each Cyrillic char is 2 bytes in UTF-8
        await Assert.That(bytes.Length).IsEqualTo(12);
        // TrackedX was WriteText without MoveTo: pen stays unknown (-1) or advances only if MoveTo set?
        // PutRune advances only when TrackedX >=0. Without MoveTo, pen is unknown so stays -1.
        // Verify explicit move case advances to 6.
        var (w2, backend2) = MakeWriter();
        w2.BeginFrame();
        w2.MoveTo(0, 0);
        w2.WriteText("привет");
        await Assert.That(w2.TrackedX).IsEqualTo(6);
        await w2.EndFrameAsync();
        var bytes2 = backend2.Writes.SelectMany(b => b).ToArray();
        // 6 bytes CUP + 12 bytes text
        await Assert.That(bytes2.Length).IsEqualTo(18);
        string textPart = Encoding.UTF8.GetString(bytes2, 6, 12);
        await Assert.That(textPart).IsEqualTo("привет");
    }

    [Test]
    public async Task AnsiWriter_WriteText_Cyrillic_MixedAscii()
    {
        var (w, backend) = MakeWriter();
        w.BeginFrame();
        w.MoveTo(0, 0);
        w.WriteText("hi привет");
        await w.EndFrameAsync();

        var bytes = backend.Writes.SelectMany(b => b).ToArray();
        string decoded = Encoding.UTF8.GetString(bytes, 6, bytes.Length - 6);
        await Assert.That(decoded).IsEqualTo("hi привет");
        // "hi " 3 cells + "привет" 6 cells = 9
        await Assert.That(w.TrackedX).IsEqualTo(9);
    }

    // ── Parser ───────────────────────────────────────────────────────────

    [Test]
    public async Task Parser_Alt_Cyrillic_SingleChunk()
    {
        var parser = new EscapeSequenceParser();
        var events = T.FeedBytes(parser, [0x1B, 0xD0, 0xBF]);
        await Assert.That(events.Length).IsEqualTo(1);
        await A.IsChar(events[0], new Rune('п'), KeyModifiers.Alt);
    }

    [Test]
    public async Task Parser_Alt_Cyrillic_SplitAcrossChunks()
    {
        var parser = new EscapeSequenceParser();
        var events = T.FeedBytes(parser, [0x1B, 0xD0], [0xBF]);
        await Assert.That(events.Length).IsEqualTo(1);
        await A.IsChar(events[0], new Rune('п'), KeyModifiers.Alt);
    }

    [Test]
    public async Task Parser_Alt_Ascii_StillCarriesAlt()
    {
        var parser = new EscapeSequenceParser();
        var events = T.FeedBytes(parser, [0x1B, (byte)'a']);
        await Assert.That(events.Length).IsEqualTo(1);
        await A.IsChar(events[0], new Rune('a'), KeyModifiers.Alt);
    }

    [Test]
    public async Task Parser_Cyrillic_PlainChar_SingleChunk()
    {
        var parser = new EscapeSequenceParser();
        // 'я' D1 8F
        var events = T.FeedBytes(parser, [0xD1, 0x8F]);

        await Assert.That(events.Length).IsEqualTo(1);
        await A.IsChar(events[0], new Rune('я'));
    }

    [Test]
    public async Task Parser_CyrillicPaste_Verbatim()
    {
        var parser = new EscapeSequenceParser();
        var events = T.Feed(parser, "\u001B[200~привет\u001B[201~");

        await Assert.That(events.Length).IsEqualTo(1);
        await Assert.That(events[0].Kind).IsEqualTo(InputEventKind.Paste);
        await Assert.That(events[0].Paste.Text).IsEqualTo("привет");
        await Assert.That(events[0].Paste.WasTruncated).IsFalse();
    }

    [Test]
    public async Task Parser_CyrillicPaste_WithEmoji_Verbatim()
    {
        var parser = new EscapeSequenceParser();
        var events = T.Feed(parser, "\u001B[200~привет 😀 мир\u001B[201~");

        await Assert.That(events.Length).IsEqualTo(1);
        await Assert.That(events[0].Paste.Text).IsEqualTo("привет 😀 мир");
    }

    [Test]
    public async Task Parser_Cyrillic_Sequence_SplitAcrossChunks()
    {
        var parser = new EscapeSequenceParser();
        // "привет" UTF-8 bytes split in middle of a 2-byte cyrillic char
        byte[] all = Encoding.UTF8.GetBytes("привет");
        // split after 5 bytes: D0 BF D1 80 D0 | B8 D0 B2 D0 B5 D1 82
        // 5 bytes cuts inside third char (D0 | B8)
        var events = T.FeedBytes(parser, all[..5], all[5..]);

        await Assert.That(events.Length).IsEqualTo(6);
        string reassembled = new string(events.Select(e => (char)e.Key.Character.Value).ToArray());
        await Assert.That(reassembled).IsEqualTo("привет");
    }

    // ── Utf8IncrementalDecoder ───────────────────────────────────────────

    [Test]
    public async Task Utf8Decoder_Cyrillic_SingleChar_Split()
    {
        var dec = new Utf8IncrementalDecoder();
        // 'п' D0 BF
        var s1 = dec.DecodeStep(0xD0, out var r1);
        await Assert.That(s1).IsEqualTo(Utf8DecodeStatus.NeedMoreData);
        await Assert.That(dec.HasPending).IsTrue();

        var s2 = dec.DecodeStep(0xBF, out var r2);
        await Assert.That(s2).IsEqualTo(Utf8DecodeStatus.Decoded);
        await Assert.That(r2).IsEqualTo(new Rune('п'));
        await Assert.That(dec.HasPending).IsFalse();
    }

    [Test]
    public async Task Utf8Decoder_Cyrillic_Ya_TwoBytes()
    {
        var dec = new Utf8IncrementalDecoder();
        var s1 = dec.DecodeStep(0xD1, out _);
        await Assert.That(s1).IsEqualTo(Utf8DecodeStatus.NeedMoreData);
        var s2 = dec.DecodeStep(0x8F, out var rune);
        await Assert.That(s2).IsEqualTo(Utf8DecodeStatus.Decoded);
        await Assert.That(rune).IsEqualTo(new Rune('я'));
        await Assert.That(rune.Value).IsEqualTo(0x044F);
    }

    [Test]
    public async Task Utf8Decoder_Cyrillic_Privet_SplitInMiddle()
    {
        var dec = new Utf8IncrementalDecoder();
        byte[] bytes = Encoding.UTF8.GetBytes("привет");
        var runes = new List<Rune>();
        // feed byte-by-byte simulating chunk boundary at byte 5 (mid-char)
        // to prove incremental holds pending across calls
        foreach (byte b in bytes)
        {
            var status = dec.DecodeStep(b, out var rune);
            if (status == Utf8DecodeStatus.Decoded)
                runes.Add(rune);
            else if (status == Utf8DecodeStatus.NeedMoreData)
                await Assert.That(dec.HasPending).IsTrue();
        }

        string text = string.Concat(runes.Select(r => r.ToString()));
        await Assert.That(text).IsEqualTo("привет");
        await Assert.That(runes.Count).IsEqualTo(6);
        await Assert.That(dec.HasPending).IsFalse();
    }

    [Test]
    public async Task Utf8Decoder_Cyrillic_MultipleChars_InterleavedAscii()
    {
        var dec = new Utf8IncrementalDecoder();
        // "aпbя" -> 61 D0 BF 62 D1 8F
        byte[] bytes = [0x61, 0xD0, 0xBF, 0x62, 0xD1, 0x8F];
        var runes = new List<Rune>();
        foreach (byte b in bytes)
        {
            var status = dec.DecodeStep(b, out var rune);
            // NeedMoreData means char not yet complete
            if (status == Utf8DecodeStatus.Decoded)
                runes.Add(rune);
        }

        await Assert.That(runes.Count).IsEqualTo(4);
        await Assert.That(runes[0]).IsEqualTo(new Rune('a'));
        await Assert.That(runes[1]).IsEqualTo(new Rune('п'));
        await Assert.That(runes[2]).IsEqualTo(new Rune('b'));
        await Assert.That(runes[3]).IsEqualTo(new Rune('я'));
    }

    [Test]
    public async Task Parser_Cyrillic_BulkSplit_ByteByByte()
    {
        var parser = new EscapeSequenceParser();
        byte[] bytes = Encoding.UTF8.GetBytes("привет");
        // feed one byte at a time
        foreach (byte b in bytes)
        {
            parser.Parse([b]);
        }

        var events = T.Drain(parser);
        await Assert.That(events.Length).IsEqualTo(6);
        string text = new string(events.Select(e => (char)e.Key.Character.Value).ToArray());
        await Assert.That(text).IsEqualTo("привет");
    }
}
