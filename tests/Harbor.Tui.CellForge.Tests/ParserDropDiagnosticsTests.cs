using Harbor.Tui.CellForge.Parsing;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// #1110 / silent drops: malformed and ignored input sequences used to only
/// bump counters that were surfaced nowhere. With an opt-in sink the drop is
/// observable at the moment it happens; without one the counters behave
/// exactly as before.
/// </summary>
public class ParserDropDiagnosticsTests
{
    [Test]
    public async Task DropSink_ReceivesMalformedSequence()
    {
        var drops = new List<ParserDrop>();
        var parser = new EscapeSequenceParser(new ParserOptions
        {
            OnDrop = drop => drops.Add(drop),
        });

        // Parameter byte after an intermediate — malformed per ECMA-48.
        _ = T.Feed(parser, "\u001B[ 5A");

        await Assert.That(parser.MalformedSequenceCount).IsEqualTo(1);
        await Assert.That(drops.Count).IsEqualTo(1);
        await Assert.That(drops[0].MalformedCount).IsEqualTo(1);
        await Assert.That(drops[0].IgnoredCount).IsEqualTo(0);
    }

    [Test]
    public async Task DropSink_ReceivesIgnoredSequence()
    {
        var drops = new List<ParserDrop>();
        var parser = new EscapeSequenceParser(new ParserOptions
        {
            OnDrop = drop => drops.Add(drop),
        });

        // Unrequested private-mode report — ignored silently.
        _ = T.Feed(parser, "\u001B[?1049h");

        await Assert.That(parser.IgnoredSequenceCount).IsEqualTo(1);
        await Assert.That(drops.Count).IsEqualTo(1);
        await Assert.That(drops[0].MalformedCount).IsEqualTo(0);
        await Assert.That(drops[0].IgnoredCount).IsEqualTo(1);
    }

    [Test]
    public async Task DropSink_SnapshotCarriesBothCounts()
    {
        var drops = new List<ParserDrop>();
        var parser = new EscapeSequenceParser(new ParserOptions
        {
            OnDrop = drop => drops.Add(drop),
        });

        _ = T.Feed(parser, "\u001B[ 5A");
        _ = T.Feed(parser, "\u001B[?1049h");

        await Assert.That(parser.MalformedSequenceCount).IsEqualTo(1);
        await Assert.That(parser.IgnoredSequenceCount).IsEqualTo(1);
        await Assert.That(drops.Count).IsEqualTo(2);
        await Assert.That(drops[^1]).IsEqualTo(new ParserDrop(1, 1));
    }

    [Test]
    public async Task NoSink_CountingUnchanged()
    {
        var parser = new EscapeSequenceParser();

        _ = T.Feed(parser, "\u001B[ 5A");
        _ = T.Feed(parser, "\u001B[?1049h");

        await Assert.That(parser.MalformedSequenceCount).IsEqualTo(1);
        await Assert.That(parser.IgnoredSequenceCount).IsEqualTo(1);
    }
}
