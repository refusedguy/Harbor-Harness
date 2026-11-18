using System;
using System.Linq;
using Harbor.Abstractions.Dsl;
using Harbor.Tui.CellForge.CodeGen;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
///     Issue #2426: the <c>.harbor-ui</c> grammar produces one arena that both
///     the generator and the dev interpreter can read.
/// </summary>
public class MarkupParserTests
{
    [Test]
    public async Task Parse_SampleMarkup_BuildsArenaTreeWithOperations()
    {
        const string markup = """
            panel "main":
                .title "Files"
                filter_list "files":
                    .height 10
                    .multiple true
                text "done"
            """;

        var result = MarkupParser.Parse(markup);

        await Assert.That(result.IsValid).IsTrue();
        await Assert.That(result.Arena.Roots.Count).IsEqualTo(1);
        await Assert.That(result.Arena.Nodes.Count).IsEqualTo(3);

        var root = result.Arena.Roots[0];
        await Assert.That(root.Kind).IsEqualTo(SpecKind.Panel);
        await Assert.That(root.SourceLine).IsEqualTo(1);
        await Assert.That(root.Operations.Count).IsEqualTo(2);
        await Assert.That(root.Operations[0].Name).IsEqualTo("new");
        await Assert.That(root.Operations[0].Arguments[0].Text).IsEqualTo("main");
        await Assert.That(root.Operations[1].Name).IsEqualTo("title");
        await Assert.That(root.Children.Count).IsEqualTo(2);

        var list = root.Children[0];
        await Assert.That(list.Kind).IsEqualTo(SpecKind.FilterList);
        await Assert.That(list.SourceLine).IsEqualTo(3);
        await Assert.That(list.Operations.Count).IsEqualTo(3);
        await Assert.That(list.Operations[1].Name).IsEqualTo("height");
        await Assert.That(list.Operations[1].Arguments[0].Kind).IsEqualTo(SpecValueKind.Number);
        await Assert.That(list.Operations[2].Arguments[0].Kind).IsEqualTo(SpecValueKind.Boolean);
        await Assert.That(list.Operations[2].Arguments[0].Text).IsEqualTo("true");
        await Assert.That(root.Children[1].Kind).IsEqualTo(SpecKind.Text);
    }

    [Test]
    public async Task Parse_IdOperation_SetsStableNodeId()
    {
        const string markup = """
            panel "main":
                .id "files-panel"
                text "x":
                    .id "row-1"
            """;

        var result = MarkupParser.Parse(markup);

        await Assert.That(result.IsValid).IsTrue();
        var root = result.Arena.Roots[0];
        await Assert.That(root.Id).IsEqualTo("files-panel");
        await Assert.That(result.Arena.Nodes[1].Id).IsEqualTo("row-1");
        // .id is still a regular operation in the IR stream.
        await Assert.That(root.Operations.Any(o => o.Name == "id" && o.Arguments[0].Text == "files-panel")).IsTrue();
    }

    [Test]
    public async Task Parse_InvalidId_ReportsDiagnostic()
    {
        var result = MarkupParser.Parse("panel \"main\":\n    .id 17\n");

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Diagnostics[0].Message).Contains(".id needs one quoted name");
        await Assert.That(result.Arena.Roots[0].Id).IsNull();
    }

    [Test]
    public async Task Parse_EveryDeclaredKind_ResolvesFromSnakeAndPascalNames()
    {
        foreach (var kind in Enum.GetValues<SpecKind>())
        {
            var name = kind.ToString();
            var snake = string.Concat(name.Select((c, i) =>
                i > 0 && char.IsUpper(c) ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));

            var snakeResult = MarkupParser.Parse($"{snake} \"x\"");
            await Assert.That(snakeResult.IsValid).IsTrue();
            await Assert.That(snakeResult.Arena.Roots[0].Kind).IsEqualTo(kind);

            var pascalResult = MarkupParser.Parse($"{name} \"x\"");
            await Assert.That(pascalResult.IsValid).IsTrue();
            await Assert.That(pascalResult.Arena.Roots[0].Kind).IsEqualTo(kind);
        }
    }

    [Test]
    public async Task Parse_UnknownKind_ReportsLineAndColumn()
    {
        var result = MarkupParser.Parse("not_a_widget \"x\"");

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Diagnostics.Count).IsEqualTo(1);
        await Assert.That(result.Diagnostics[0].Line).IsEqualTo(1);
        await Assert.That(result.Diagnostics[0].Column).IsEqualTo(1);
        await Assert.That(result.Diagnostics[0].Message).Contains("Unknown node kind");
        await Assert.That(result.Arena.Roots.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Parse_TabsAndUnterminatedString_ReportBothErrors()
    {
        var result = MarkupParser.Parse("\ttext \"x\"\ntext \"unterminated\n");

        await Assert.That(result.Diagnostics.Count).IsEqualTo(2);
        await Assert.That(result.Diagnostics[0].Line).IsEqualTo(1);
        await Assert.That(result.Diagnostics[0].Message).Contains("Tabs");
        await Assert.That(result.Diagnostics[1].Line).IsEqualTo(2);
        await Assert.That(result.Diagnostics[1].Message).Contains("Unterminated");
    }

    [Test]
    public async Task SingleUse_AddingSameChildTwice_ThrowsWithNodeIdentity()
    {
        var arena = new SpecArena();
        var child = arena.CreateNode(SpecKind.Text, 1);
        var first = arena.CreateNode(SpecKind.Panel, 2);
        var second = arena.CreateNode(SpecKind.Panel, 3);

        first.AddChild(child);
        var error = Assert.Throws<SpecNodeReuseException>(() => second.AddChild(child));

        await Assert.That(error.Message).Contains("Text at line 1");
        await Assert.That(error.Message).Contains("already has a parent");
        await Assert.That(second.Children.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SingleUse_ClaimingTwiceOrAddingAsRoot_ThrowsWithNodeIdentity()
    {
        var arena = new SpecArena();
        var node = arena.CreateNode(SpecKind.TextInput, 7);

        node.Claim();
        var error = Assert.Throws<SpecNodeReuseException>(() => node.Claim());

        await Assert.That(node.IsClaimed).IsTrue();
        await Assert.That(error.Message).Contains("TextInput at line 7");
        await Assert.That(error.Message).Contains("already claimed");
        Assert.Throws<SpecNodeReuseException>(() => arena.AddRoot(node));
    }
}
