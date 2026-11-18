using Harbor.Abstractions.Dsl;
using Harbor.TestKit;
using Harbor.Tui.CellForge.CodeGen;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
///     Issue #2427: source spans live on every IR node and operation, and
///     <c>debug_tree</c> renders the arena as text a snapshot can pin.
/// </summary>
/// <remarks>
///     These tests parse text and compare strings. They never open a
///     CellForge renderer, start a session or touch a terminal, so the
///     snapshots are a function of the parser and the dumper alone.
/// </remarks>
public class SpecTreeDebugTreeTests
{
    private const string Sample = """
        panel "main":
            .title "Files"
            filter_list "files":
                .height 10
                .multiple true
            text "done"
        """;

    private static string FixtureDir() => Golden.FixtureDir("dsl");

    [Test]
    public async Task DebugTree_SampleMarkup_MatchesGolden()
    {
        var result = MarkupParser.Parse(Sample);

        await Assert.That(result.IsValid).IsTrue();

        string actual = result.Arena.DebugTree();
        string expected = Golden.VerifyText(FixtureDir(), "arena-sample", actual);
        await Assert.That(Golden.Normalize(actual)).IsEqualTo(expected);
    }

    [Test]
    public async Task DebugTree_AfterClaim_ChangesOnlyTheClaimFlag()
    {
        var result = MarkupParser.Parse(Sample);
        result.Arena.Roots[0].Children[0].Claim();

        string actual = result.Arena.DebugTree();
        string expected = Golden.VerifyText(FixtureDir(), "arena-sample-claimed", actual);
        await Assert.That(Golden.Normalize(actual)).IsEqualTo(expected);
    }

    [Test]
    public async Task DebugTree_SameArenaTwice_IsByteIdentical()
    {
        var arena = MarkupParser.Parse(Sample).Arena;

        string first = arena.DebugTree();
        string second = arena.DebugTree();

        await Assert.That(second).IsEqualTo(first);
        await Assert.That(SpecTreeDumper.DebugTree(arena)).IsEqualTo(first);
    }

    [Test]
    public async Task DebugTree_EmptyArena_IsEmpty()
    {
        await Assert.That(new SpecArena().DebugTree()).IsEmpty();
    }

    [Test]
    public async Task DebugTree_OneNode_RootsAtZeroDepth()
    {
        var arena = new SpecArena();
        var node = arena.CreateNode(SpecKind.Text, 4, 9);
        node.AddOperation(new SpecOp("new", [new SpecValue(SpecValueKind.String, "hi")], 4, 9));
        arena.AddRoot(node);

        await Assert.That(node.DebugTree()).IsEqualTo("node Text@4:9 claimed=false\n  op new(\"hi\")@4:9\n");
    }

    [Test]
    public async Task Spans_ParserRecordsOneBasedLineAndColumnOnNodesAndOps()
    {
        var root = MarkupParser.Parse(Sample).Arena.Roots[0];

        await Assert.That(root.SourceLine).IsEqualTo(1);
        await Assert.That(root.SourceColumn).IsEqualTo(1);
        await Assert.That(root.Operations[0].SourceLine).IsEqualTo(1);
        await Assert.That(root.Operations[0].SourceColumn).IsEqualTo(1);
        await Assert.That(root.Operations[1].SourceLine).IsEqualTo(2);
        await Assert.That(root.Operations[1].SourceColumn).IsEqualTo(6);

        var list = root.Children[0];
        await Assert.That(list.SourceLine).IsEqualTo(3);
        await Assert.That(list.SourceColumn).IsEqualTo(5);
        await Assert.That(list.Operations[1].SourceLine).IsEqualTo(4);
        await Assert.That(list.Operations[1].SourceColumn).IsEqualTo(10);
        await Assert.That(list.Operations[2].SourceLine).IsEqualTo(5);
        await Assert.That(list.Operations[2].SourceColumn).IsEqualTo(10);

        var text = root.Children[1];
        await Assert.That(text.SourceLine).IsEqualTo(6);
        await Assert.That(text.SourceColumn).IsEqualTo(5);
        await Assert.That(text.Operations[0].SourceLine).IsEqualTo(6);
        await Assert.That(text.Operations[0].SourceColumn).IsEqualTo(5);
    }

    [Test]
    public async Task Spans_ManualConstructorsKeepTheirOldDefaults()
    {
        var arena = new SpecArena();
        var node = arena.CreateNode(SpecKind.Panel, 7);
        var op = new SpecOp("title", []);

        await Assert.That(node.SourceColumn).IsEqualTo(1);
        await Assert.That(op.SourceLine).IsEqualTo(0);
        await Assert.That(op.SourceColumn).IsEqualTo(0);
    }
}
