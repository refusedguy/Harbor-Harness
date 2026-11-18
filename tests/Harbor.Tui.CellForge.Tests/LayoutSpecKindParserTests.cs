using Harbor.Abstractions.Dsl;
using Harbor.Tui.CellForge.CodeGen;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
///     Issue #2518: Row/Column/Overlay join the widget vocabulary so markup
///     can declare arbitrary layout. The parser stays grammar-agnostic: it
///     resolves names through the enum and records dot-ops generically, so
///     these tests pin that a layout node and its .track/.gap/.justify ops
///     reach the arena unchanged for the generator to interpret.
/// </summary>
public class LayoutSpecKindParserTests
{
    [Test]
    public async Task Parse_LayoutKinds_FromPascalSnakeAndKebabNames()
    {
        await AssertKind("row", SpecKind.Row);
        await AssertKind("Row", SpecKind.Row);
        await AssertKind("column", SpecKind.Column);
        await AssertKind("Column", SpecKind.Column);
        await AssertKind("overlay", SpecKind.Overlay);
        await AssertKind("Overlay", SpecKind.Overlay);
        await AssertKind("over-lay", SpecKind.Overlay);
    }

    [Test]
    public async Task Parse_LayoutTree_RecordsTrackGapAndJustifyOps()
    {
        const string markup = """
            row "main":
                .gap 2
                .justify space-between
                text "left":
                    .track fill 2
                text "right":
                    .track fixed 12
            column "side":
                text "top":
                    .track min 4
            overlay "stack":
                text "front":
                    .track max 8
            """;

        var result = MarkupParser.Parse(markup);

        await Assert.That(result.IsValid).IsTrue();
        await Assert.That(result.Arena.Roots.Count).IsEqualTo(3);

        var row = result.Arena.Roots[0];
        await Assert.That(row.Kind).IsEqualTo(SpecKind.Row);
        await Assert.That(row.Operations[0].Name).IsEqualTo("new");
        await Assert.That(row.Operations[1].Name).IsEqualTo("gap");
        await Assert.That(row.Operations[1].Arguments[0].Text).IsEqualTo("2");
        await Assert.That(row.Operations[2].Name).IsEqualTo("justify");
        await Assert.That(row.Operations[2].Arguments[0].Text).IsEqualTo("space-between");
        await Assert.That(row.Children.Count).IsEqualTo(2);
        await Assert.That(row.Children[0].Operations[1].Name).IsEqualTo("track");
        await Assert.That(row.Children[0].Operations[1].Arguments[0].Text).IsEqualTo("fill");
        await Assert.That(row.Children[0].Operations[1].Arguments[1].Text).IsEqualTo("2");
        await Assert.That(row.Children[1].Operations[1].Arguments[0].Text).IsEqualTo("fixed");

        var column = result.Arena.Roots[1];
        await Assert.That(column.Kind).IsEqualTo(SpecKind.Column);
        await Assert.That(column.Children[0].Operations[1].Arguments[0].Text).IsEqualTo("min");

        var overlay = result.Arena.Roots[2];
        await Assert.That(overlay.Kind).IsEqualTo(SpecKind.Overlay);
        await Assert.That(overlay.Children[0].Operations[1].Arguments[0].Text).IsEqualTo("max");
    }

    private static async Task AssertKind(string name, SpecKind expected)
    {
        var result = MarkupParser.Parse($"{name} \"x\"");
        await Assert.That(result.IsValid).IsTrue();
        await Assert.That(result.Arena.Roots[0].Kind).IsEqualTo(expected);
    }
}
