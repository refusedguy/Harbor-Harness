using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Tests;

public class TextWrapTests
{
    [Test]
    public async Task Wrap_ShortLine_StaySingle()
    {
        var lines = new List<string>();
        TextWrap.WrapTo("hello", 10, lines);
        await Assert.That(lines).IsEquivalentTo(["hello"]);
    }

    [Test]
    public async Task Wrap_BreaksAtLastSpaceWithinWindow()
    {
        var lines = new List<string>();
        TextWrap.WrapTo("hello world foo", 11, lines);
        await Assert.That(lines).IsEquivalentTo(["hello world", "foo"]);
    }

    [Test]
    public async Task Wrap_HardBreaksWhenWordExceedsWidth()
    {
        var lines = new List<string>();
        TextWrap.WrapTo("abcdefghijkl", 5, lines);
        await Assert.That(lines).IsEquivalentTo(["abcde", "fghij", "kl"]);
    }

    [Test]
    public async Task Wrap_WideRunesNeverSplitAcrossLines()
    {
        // 中(2) + 中(2) = 4 cells fit; third would exceed width 4 → wraps whole.
        var lines = new List<string>();
        TextWrap.WrapTo("中中中", 4, lines);
        await Assert.That(lines).IsEquivalentTo(["中中", "中"]);
    }

    [Test]
    public async Task WrapDocument_PreservesEmptyLines()
    {
        var lines = new List<string>();
        TextWrap.WrapDocument("a\n\nb", 10, lines);
        await Assert.That(lines).IsEquivalentTo(["a", "", "b"]);
    }
}

public class InlineSessionTests
{
    private static (AnsiWriter Writer, RecordingBackend Backend, InlineSession Session) Make(bool sync = false)
    {
        var backend = new RecordingBackend();
        var writer = new AnsiWriter(backend, sync);
        return (writer, backend, new InlineSession(writer));
    }

    private static async Task WriterFlush(AnsiWriter w) => await w.FlushAsync();

    [Test]
    public async Task EraseLiveRegion_MultiLine_MovesUpAndClearsBelow()
    {
        var (w, backend, session) = Make();
        session.SetLiveLines(3);
        session.EraseLiveRegion();
        await WriterFlush(w);

        await Assert.That(backend.Escaped).IsEqualTo("\\e[2A\\r\\e[0m\\e[0J");
        await Assert.That(session.LiveLines).IsEqualTo(0);
    }

    [Test]
    public async Task EraseLiveRegion_SingleLine_ClearsLineOnly()
    {
        var (w, backend, session) = Make();
        session.SetLiveLines(1);
        session.EraseLiveRegion();
        await WriterFlush(w);

        await Assert.That(backend.Escaped).IsEqualTo("\\r\\e[0m\\e[2K");
    }

    [Test]
    public async Task EraseLiveRegion_EmptyRegion_WritesNothing()
    {
        var (w, backend, session) = Make();
        session.EraseLiveRegion();

        await Assert.That(backend.Escaped).IsEqualTo("");
    }

    [Test]
    public async Task CommitBlock_PrintsWrappedLinesWithBreaks()
    {
        var (w, backend, session) = Make();
        session.SetLiveLines(2);
        session.EraseLiveRegion();
        int written = session.WriteFinalizedBlock("Hello\nWorld", 40);
        session.SetLiveLines(1);
        await WriterFlush(w);

        await Assert.That(written).IsEqualTo(2);
        await Assert.That(backend.Escaped).IsEqualTo("\\e[A\\r\\e[0m\\e[0JHello\\r\\nWorld\\r\\n");
    }

    [Test]
    public async Task CommitBlock_EmptyText_IsNoOp()
    {
        var (w, backend, session) = Make();
        int written = session.WriteFinalizedBlock("", 40);

        await Assert.That(written).IsEqualTo(0);
        await Assert.That(backend.Escaped).IsEqualTo("");
    }

    [Test]
    public async Task CommitBlock_Styled_WrapsWithSgrReset()
    {
        var (w, backend, session) = Make();
        var dim = new CellStyle(attrs: StyleAttr.Dim);
        session.WriteFinalizedBlock("ok", 10, dim);
        await WriterFlush(w);

        await Assert.That(backend.Escaped).IsEqualTo("\\e[0m\\e[2mok\\r\\n\\e[0m");
    }

    [Test]
    public async Task CommitSequence_RoundsKeepViewportConsistent()
    {
        var (w, backend, session) = Make();

        // Round 1: prompt live line exists; commit shifts it into scrollback.
        session.SetLiveLines(1);
        session.EraseLiveRegion();
        await WriterFlush(w);
        session.WriteFinalizedBlock("first", 80);
        session.SetLiveLines(1);

        // Round 2: same again — erase must return to the prompt row.
        session.EraseLiveRegion();
        session.WriteFinalizedBlock("second", 80);
        session.SetLiveLines(1);
        await WriterFlush(w);

        await Assert.That(backend.Escaped)
            .IsEqualTo("\\r\\e[0m\\e[2Kfirst\\r\\n\\r\\e[0m\\e[2Ksecond\\r\\n");
    }
}
