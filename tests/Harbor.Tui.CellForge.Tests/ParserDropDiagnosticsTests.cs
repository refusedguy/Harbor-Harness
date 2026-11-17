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
    // #1808 (partial): the malformed and ignored sink proofs had the same body
    // and differed only in the sequence fed and the two expected counts. Both
    // cases are rows now, so the copied comparison block exists once.
    [Test]
    [Arguments("\u001B[ 5A", 1, 0)]     // parameter byte after an intermediate — malformed per ECMA-48
    [Arguments("\u001B[?1049h", 0, 1)] // unrequested private-mode report — ignored silently
    public async Task DropSink_ReceivesDropWithBothCounts(string sequence, int malformed, int ignored)
    {
        var drops = new List<ParserDrop>();
        var parser = new EscapeSequenceParser(new ParserOptions
        {
            OnDrop = drop => drops.Add(drop),
        });

        _ = T.Feed(parser, sequence);

        await Assert.That(parser.MalformedSequenceCount).IsEqualTo(malformed);
        await Assert.That(parser.IgnoredSequenceCount).IsEqualTo(ignored);
        await Assert.That(drops.Count).IsEqualTo(1);
        await Assert.That(drops[0].MalformedCount).IsEqualTo(malformed);
        await Assert.That(drops[0].IgnoredCount).IsEqualTo(ignored);
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
