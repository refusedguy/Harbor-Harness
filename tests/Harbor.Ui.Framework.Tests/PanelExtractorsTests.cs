using System.Collections.Immutable;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.State;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
///     Tests for <see cref="PanelExtractors" /> (todo / diff / diagnostics parsing).
/// </summary>
public class PanelExtractorsTests
{
    private static ChatLine Tool(string text, string id) => new(ChatRole.Tool, text, id);

    private static ChatLine ToolResult(string text, string id) => new(ChatRole.ToolResult, text, id);

    [Test]
    public async Task ExtractTodos_ParsesAllMarkerVariants()
    {
        var lines = new List<ChatLine>
        {
            Tool("→ todo  {\"action\":\"list\"}", "tc1"),
            ToolResult("✓ Todos (4):\n  [ ] pending task\n  [~] active task\n  [x] done task\n  [X] done upper", "tc1"),
        };

        var todos = PanelExtractors.ExtractTodos(lines);

        await Assert.That(todos.Count).IsEqualTo(4);
        await Assert.That(todos[0].Marker).IsEqualTo("[ ]");
        await Assert.That(todos[0].Content).IsEqualTo("pending task");
        await Assert.That(todos[1].Marker).IsEqualTo("[~]");
        await Assert.That(todos[1].Content).IsEqualTo("active task");
        await Assert.That(todos[2].Marker).IsEqualTo("[x]");
        await Assert.That(todos[3].Marker).IsEqualTo("[X]");
    }

    [Test]
    public async Task ExtractTodos_ReturnsOnlyMostRecentBlock()
    {
        var lines = new List<ChatLine>
        {
            Tool("→ todo  {\"action\":\"list\"}", "tc1"),
            ToolResult("✓   [ ] stale task", "tc1"),
            Tool("→ todo  {\"action\":\"list\"}", "tc2"),
            ToolResult("✓   [x] fresh task", "tc2"),
        };

        var todos = PanelExtractors.ExtractTodos(lines);

        await Assert.That(todos.Count).IsEqualTo(1);
        await Assert.That(todos[0].Content).IsEqualTo("fresh task");
    }

    [Test]
    public async Task ExtractTodos_StopsAtToolBoundary()
    {
        var lines = new List<ChatLine>
        {
            ToolResult("✓   [ ] orphan stale", "tc0"),
            Tool("→ todo  {\"action\":\"list\"}", "tc1"),
            ToolResult("✓   [ ] fresh", "tc1"),
        };

        var todos = PanelExtractors.ExtractTodos(lines);

        await Assert.That(todos.Count).IsEqualTo(1);
        await Assert.That(todos[0].Content).IsEqualTo("fresh");
    }

    [Test]
    public async Task ExtractRecentChanges_TracksEditWriteReadPatch()
    {
        var lines = new List<ChatLine>
        {
            Tool("→ edit  {\"path\":\"a.cs\"}", "t1"),
            ToolResult("✓ @@ -1 +1 @@\n-old\n+new", "t1"),
            Tool("→ write  {\"path\":\"b.cs\"}", "t2"),
            ToolResult("✓ created", "t2"),
            Tool("→ read  {\"path\":\"c.cs\"}", "t3"),
            ToolResult("✓ content", "t3"),
            Tool("→ patch  {\"path\":\"d.cs\"}", "t4"),
            ToolResult("✓ patched", "t4"),
        };

        var changes = PanelExtractors.ExtractRecentChanges(lines, 8);

        await Assert.That(changes.Count).IsEqualTo(4);
        string sorted = string.Join("|", changes.Select(c => c.ToolName).OrderBy(n => n));
        await Assert.That(sorted).IsEqualTo("edit|patch|read|write");
    }

    [Test]
    public async Task ExtractRecentChanges_ReturnsMostRecentFirst()
    {
        var lines = new List<ChatLine>
        {
            Tool("→ edit  {\"path\":\"first.cs\"}", "t1"),
            ToolResult("✓ first", "t1"),
            Tool("→ write  {\"path\":\"second.cs\"}", "t2"),
            ToolResult("✓ second", "t2"),
        };

        var changes = PanelExtractors.ExtractRecentChanges(lines, 8);

        await Assert.That(changes.Count).IsEqualTo(2);
        await Assert.That(changes[0].FilePath).IsEqualTo("second.cs");
        await Assert.That(changes[1].FilePath).IsEqualTo("first.cs");
    }

    [Test]
    public async Task ExtractRecentChanges_ExtractsFilePaths()
    {
        var lines = new List<ChatLine>
        {
            Tool("→ edit  {\"path\":\"src/Foo.cs\"}", "t1"),
            ToolResult("✓ @@ -1 +1 @@\n-a\n+b", "t1"),
        };

        var changes = PanelExtractors.ExtractRecentChanges(lines, 8);

        await Assert.That(changes.Count).IsEqualTo(1);
        await Assert.That(changes[0].ToolName).IsEqualTo("edit");
        await Assert.That(changes[0].FilePath).IsEqualTo("src/Foo.cs");
    }

    [Test]
    public async Task ExtractRecentChanges_UsesUnknownWhenPathMissing()
    {
        var lines = new List<ChatLine>
        {
            Tool("→ edit  {}", "t1"),
            ToolResult("✓ ok", "t1"),
        };

        var changes = PanelExtractors.ExtractRecentChanges(lines, 8);

        await Assert.That(changes.Count).IsEqualTo(1);
        await Assert.That(changes[0].FilePath).IsEqualTo("<unknown>");
    }

    [Test]
    public async Task ExtractRecentChanges_MarksIsErrorFromPrefix()
    {
        var lines = new List<ChatLine>
        {
            Tool("→ edit  {\"path\":\"a.cs\"}", "t1"),
            ToolResult("✓ ok", "t1"),
            Tool("→ edit  {\"path\":\"b.cs\"}", "t2"),
            ToolResult("✗ failed", "t2"),
        };

        var changes = PanelExtractors.ExtractRecentChanges(lines, 8);

        await Assert.That(changes.Count).IsEqualTo(2);
        await Assert.That(changes[0].IsError).IsTrue();
        await Assert.That(changes[1].IsError).IsFalse();
    }

    [Test]
    public async Task CollectDiagnostics_DetectsCSharpError()
    {
        var lines = new List<ChatLine>
        {
            ToolResult("✓ error CS0246: The type or namespace name 'Foo' could not be found", "b1"),
        };

        var diags = PanelExtractors.CollectDiagnostics(lines);

        await Assert.That(diags.Count).IsEqualTo(1);
        await Assert.That(diags[0].Severity).IsEqualTo(PanelDiagnosticSeverity.Error);
        await Assert.That(diags[0].Source).IsEqualTo("csharp");
        await Assert.That(diags[0].Message.Contains("CS0246", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task CollectDiagnostics_DetectsRustAndPython()
    {
        var lines = new List<ChatLine>
        {
            ToolResult("✗ error[E0308]: mismatched types", "b1"),
            ToolResult("✓ File \"app.py\", line 10, in <module>", "b2"),
        };

        var diags = PanelExtractors.CollectDiagnostics(lines);

        await Assert.That(diags.Count).IsEqualTo(2);
        await Assert.That(diags[0].Source).IsEqualTo("rust");
        await Assert.That(diags[0].Severity).IsEqualTo(PanelDiagnosticSeverity.Error);
        await Assert.That(diags[1].Source).IsEqualTo("python");
        await Assert.That(diags[1].Severity).IsEqualTo(PanelDiagnosticSeverity.Error);
    }

    [Test]
    public async Task CollectDiagnostics_DetectsNodeAndGenericException()
    {
        var lines = new List<ChatLine>
        {
            ToolResult("✓ TypeError: Cannot read properties of undefined", "b1"),
            ToolResult("✓ System.NullReferenceException: Object reference not set", "b2"),
        };

        var diags = PanelExtractors.CollectDiagnostics(lines);

        await Assert.That(diags.Count).IsEqualTo(2);
        await Assert.That(diags[0].Source).IsEqualTo("node");
        await Assert.That(diags[1].Source).IsEqualTo("exception");
    }

    [Test]
    public async Task CollectDiagnostics_MarksWarningSeverity()
    {
        var lines = new List<ChatLine>
        {
            ToolResult("✓ warning: unused variable 'x'", "b1"),
        };

        var diags = PanelExtractors.CollectDiagnostics(lines);

        await Assert.That(diags.Count).IsEqualTo(1);
        await Assert.That(diags[0].Severity).IsEqualTo(PanelDiagnosticSeverity.Warning);
    }

    [Test]
    public async Task CollectDiagnostics_IncludesErrorRoleAndReturnsEmptyWhenClean()
    {
        var errors = new List<ChatLine>
        {
            new(ChatRole.Error, "agent exploded"),
        };

        var diags = PanelExtractors.CollectDiagnostics(errors);

        await Assert.That(diags.Count).IsEqualTo(1);
        await Assert.That(diags[0].Severity).IsEqualTo(PanelDiagnosticSeverity.Error);

        var clean = new List<ChatLine>
        {
            new(ChatRole.Assistant, "all good"),
            ToolResult("✓ ok", "t1"),
        };

        await Assert.That(PanelExtractors.CollectDiagnostics(clean).Count).IsEqualTo(0);
        await Assert.That(PanelExtractors.CollectDiagnostics(new List<ChatLine>()).Count).IsEqualTo(0);
    }

    [Test]
    public async Task Overloads_SupportListAndUiStateEqually()
    {
        var list = new List<ChatLine>
        {
            Tool("→ todo  {\"action\":\"list\"}", "tc1"),
            ToolResult("✓   [ ] item", "tc1"),
            Tool("→ edit  {\"path\":\"a.cs\"}", "t2"),
            ToolResult("✓ done", "t2"),
        };
        var state = new UiState { Lines = list.ToImmutableArray() };

        var todosList = PanelExtractors.ExtractTodos(list);
        var todosState = PanelExtractors.ExtractTodos(state);
        await Assert.That(todosState.Count).IsEqualTo(todosList.Count);

        var changesList = PanelExtractors.ExtractRecentChanges(list, 8);
        var changesState = PanelExtractors.ExtractRecentChanges(state, 8);
        await Assert.That(changesState.Count).IsEqualTo(changesList.Count);

        var diagsList = PanelExtractors.CollectDiagnostics(list);
        var diagsState = PanelExtractors.CollectDiagnostics(state);
        await Assert.That(diagsState.Count).IsEqualTo(diagsList.Count);
    }
}
