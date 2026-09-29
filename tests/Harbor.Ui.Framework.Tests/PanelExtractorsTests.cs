using System.Collections.Immutable;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;

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

        // #680: the lines-only overload has no tool declarations in hand, so it
        // leaves the glyph empty rather than guessing one from the name. The
        // panel falls back to a neutral marker; it does not own a table.
        await Assert.That(changes.All(c => c.Glyph.Length == 0)).IsTrue();
    }

    /// <summary>
    ///     #680: the state overload carries the glyph the core published, so the
    ///     diff panel draws what each tool declared instead of consulting a table
    ///     of its own. This table was the FOURTH such table; the three chat-card
    ///     ones already disagreed with each other.
    /// </summary>
    [Test]
    public async Task ExtractRecentChanges_CarriesThePublishedGlyph()
    {
        var lines = new List<ChatLine>
        {
            Tool("→ edit  {\"path\":\"a.cs\"}", "t1"),
            ToolResult("✓ @@ -1 +1 @@\n-old\n+new", "t1"),
        };

        var state = new UiState
        {
            Chat = ChatDomainState.Empty with
            {
                Lines = lines.ToImmutableArray(),
                ToolCalls =
                [
                    new ToolCallSnapshot(
                        "t1", "edit", "✎", """{"path":"a.cs"}""",
                        ToolCallState.Success, "done", IsDiffTool: true, DiffFilePath: "a.cs"),
                ],
            },
        };

        IReadOnlyList<PanelFileChange> changes = PanelExtractors.ExtractRecentChanges(state, 8);

        await Assert.That(changes.Count).IsEqualTo(1);
        await Assert.That(changes[0].Glyph).IsEqualTo("✎")
            .Because("the glyph is the calling tool's own declaration, read from the published "
                   + "snapshot — not a value this projection looks up by tool name");
    }

    /// <summary>
    ///     A row with no published glyph renders the neutral marker rather than
    ///     the wrong icon — the fallback a caller cannot confuse for a tool's.
    /// </summary>
    [Test]
    public async Task DiffRows_MissingGlyph_RendersNeutralMarker()
    {
        List<string> rows = PanelRows.DiffRows(
            [new PanelFileChange("edit", "a.cs", string.Empty, false)], 40);

        await Assert.That(rows.Any(r => r.Contains("a.cs", StringComparison.Ordinal))).IsTrue();
        await Assert.That(rows.Any(r => r.StartsWith("·", StringComparison.Ordinal))).IsTrue()
            .Because("an absent glyph falls back to the neutral marker, not to a guessed tool icon");
    }

    /// <summary>The published glyph is what the row actually draws.</summary>
    [Test]
    public async Task DiffRows_DrawsTheCarriedGlyph()
    {
        List<string> rows = PanelRows.DiffRows(
            [new PanelFileChange("edit", "a.cs", string.Empty, false, "✎")], 40);

        await Assert.That(rows.Any(r => r.StartsWith("✎", StringComparison.Ordinal))).IsTrue();
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
    public async Task ExtractRecentChanges_ToolTextWithTrailingWhitespace_ParsesPath()
    {
        // #173: ExtractPath slices the string's memory (AsMemory) instead of
        // Substring-copying; trailing whitespace must still parse to a path.
        var lines = new List<ChatLine>
        {
            Tool("→ edit  {\"path\":\"a.cs\"}  ", "t1"),
            ToolResult("✓ ok", "t1"),
        };

        var changes = PanelExtractors.ExtractRecentChanges(lines, 8);

        await Assert.That(changes.Count).IsEqualTo(1);
        await Assert.That(changes[0].FilePath).IsEqualTo("a.cs");
    }

    [Test]
    public async Task ExtractRecentChanges_NonObjectJson_FallsBackToUnknown()
    {
        // #173: args that parse but carry no path key skip the DOM probe and
        // fall through to the regex fallback, which finds no path — same as
        // the old Substring path. Exercises the AsMemory slice (no copy).
        var lines = new List<ChatLine>
        {
            Tool("→ edit  {\"items\":[1,2]}", "t1"),
            ToolResult("✓ ok", "t1"),
        };

        var changes = PanelExtractors.ExtractRecentChanges(lines, 8);

        await Assert.That(changes.Count).IsEqualTo(1);
        await Assert.That(changes[0].ToolName).IsEqualTo("edit");
        await Assert.That(changes[0].FilePath).IsEqualTo("<unknown>");
    }

    [Test]
    public async Task ExtractRecentChanges_PerRowParse_StaysBounded()
    {
        // #173 coarse tripwire: the per-row args parse must not allocate an
        // intermediate args substring anymore. Bound carries multi-x headroom
        // over steady state; tighten only with fresh measurements.
        var lines = new List<ChatLine>();
        for (int i = 0; i < 20; i++)
        {
            lines.Add(Tool("→ edit  {\"path\":\"src/file" + i + ".cs\"}", "t" + i));
            lines.Add(ToolResult("✓ ok", "t" + i));
        }

        for (int i = 0; i < 20; i++)
            _ = PanelExtractors.ExtractRecentChanges(lines, 20);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 50; i++)
            _ = PanelExtractors.ExtractRecentChanges(lines, 20);
        long after = GC.GetAllocatedBytesForCurrentThread();

        await Assert.That(after - before).IsLessThanOrEqualTo(50 * 64 * 1024);
    }

    /// <summary>
    ///     THE #674 REGRESSION. The diagnostics extractor used to read the
    ///     transcript's <see cref="ChatRole.ToolResult" /> lines — the <c>bash</c>
    ///     tool's output — and classify them with its own regexes. A transcript
    ///     full of build-log text that would every one of those detectors match
    ///     must now produce NOTHING: the extractor has no opinion about a line of
    ///     text, and the only rows it can draw are the ones the headless core
    ///     classified and the host pushed.
    /// </summary>
    [Test]
    public async Task CollectDiagnostics_IgnoresTranscriptTextEntirely()
    {
        var state = new UiState
        {
            Chat = ChatDomainState.Empty with
            {
                Lines =
                [
                    ToolResult("✗ error CS0246: The type or namespace name 'Foo' could not be found", "b1"),
                    ToolResult("✗ error[E0308]: mismatched types", "b2"),
                    ToolResult("✗ File \"app.py\", line 10, in <module>", "b3"),
                    ToolResult("✗ TypeError: Cannot read properties of undefined", "b4"),
                    ToolResult("✗ System.NullReferenceException: Object reference not set", "b5"),
                    ToolResult("✗ warning: unused variable 'x'", "b6"),
                    new ChatLine(ChatRole.Error, "error MSB3021: could not copy"),
                ],
            },
        };

        await Assert.That(PanelExtractors.CollectDiagnostics(state)).IsEmpty()
            .Because("every line above is text a tool printed. Turning it into «an error, of this "
                   + "severity» is counting, and counting belongs to the headless core: the panel must "
                   + "show what the core classified, not re-derive it. A non-empty result here means a "
                   + "classifier crept back into the projection layer.");
    }

    /// <summary>
    ///     The rows the core pushed are drawn as they arrived, in its order, with
    ///     its severities — the extractor reorders and reclassifies nothing.
    /// </summary>
    [Test]
    public async Task CollectDiagnostics_ProjectsTheCoreSnapshotVerbatim()
    {
        var state = new UiState
        {
            Chat = ChatDomainState.Empty with
            {
                Diagnostics =
                [
                    Issue(DiagnosticIssueSource.LanguageServer, DiagnosticIssueSeverity.Error,
                        "csharp", "src/a.cs", 12, "CS0246: type not found"),
                    Issue(DiagnosticIssueSource.ToolOutput, DiagnosticIssueSeverity.Warning,
                        "node", null, 0, "npm WARN deprecated request"),
                ],
            },
        };

        IReadOnlyList<PanelDiagnostic> diags = PanelExtractors.CollectDiagnostics(state);

        await Assert.That(diags.Count).IsEqualTo(2);

        // The language-server row keeps its place, and its message gains the
        // file:line the server reported — a projection decision, not a count.
        await Assert.That(diags[0].Severity).IsEqualTo(PanelDiagnosticSeverity.Error);
        await Assert.That(diags[0].Source).IsEqualTo("csharp");
        await Assert.That(diags[0].Origin).IsEqualTo(DiagnosticIssueSource.LanguageServer);
        await Assert.That(diags[0].Message).IsEqualTo("src/a.cs:12 CS0246: type not found");

        // The tool-output row is tagged as such, so a renderer can section the
        // two instead of summing them under one heading.
        await Assert.That(diags[1].Severity).IsEqualTo(PanelDiagnosticSeverity.Warning);
        await Assert.That(diags[1].Origin).IsEqualTo(DiagnosticIssueSource.ToolOutput);
        await Assert.That(diags[1].Message).IsEqualTo("npm WARN deprecated request");
    }

    /// <summary>
    ///     A file with no place in it must not render a bare <c>:0</c>, and a
    ///     server that reported a file but no line must not render a fake one.
    /// </summary>
    [Test]
    public async Task CollectDiagnostics_FormatsLocationOnlyWhenThereIsOne()
    {
        var state = new UiState
        {
            Chat = ChatDomainState.Empty with
            {
                Diagnostics =
                [
                    Issue(DiagnosticIssueSource.ToolOutput, DiagnosticIssueSeverity.Error, "rust", null, 0, "error: boom"),
                    Issue(DiagnosticIssueSource.LanguageServer, DiagnosticIssueSeverity.Warning, "pyright", "app.py", 0, "unused import"),
                ],
            },
        };

        IReadOnlyList<PanelDiagnostic> diags = PanelExtractors.CollectDiagnostics(state);

        await Assert.That(diags[0].Message).IsEqualTo("error: boom");
        await Assert.That(diags[1].Message).IsEqualTo("app.py unused import");
    }

    /// <summary>
    ///     Advisory severities have no glyph of their own in a two-icon panel.
    ///     They are drawn quietly, and the distinction is not thrown away: it is
    ///     still in the snapshot the next projection reads.
    /// </summary>
    [Test]
    public async Task CollectDiagnostics_DrawsAdvisoriesAsWarnings()
    {
        var state = new UiState
        {
            Chat = ChatDomainState.Empty with
            {
                Diagnostics =
                [
                    Issue(DiagnosticIssueSource.LanguageServer, DiagnosticIssueSeverity.Information, "csharp", null, 0, "info"),
                    Issue(DiagnosticIssueSource.LanguageServer, DiagnosticIssueSeverity.Hint, "csharp", null, 0, "hint"),
                ],
            },
        };

        IReadOnlyList<PanelDiagnostic> diags = PanelExtractors.CollectDiagnostics(state);

        await Assert.That(diags.Count).IsEqualTo(2);
        foreach (PanelDiagnostic diagnostic in diags)
        {
            await Assert.That(diagnostic.Severity).IsEqualTo(PanelDiagnosticSeverity.Warning);
        }
    }

    [Test]
    public async Task CollectDiagnostics_EmptySnapshotAndEmptyStateYieldNoRows()
    {
        await Assert.That(PanelExtractors.CollectDiagnostics(new UiState())).IsEmpty();

        var emptyButPresent = new UiState
        {
            Chat = ChatDomainState.Empty with { Diagnostics = [] },
        };
        await Assert.That(PanelExtractors.CollectDiagnostics(emptyButPresent)).IsEmpty();
    }

    private static DiagnosticIssue Issue(
        DiagnosticIssueSource source,
        DiagnosticIssueSeverity severity,
        string producer,
        string? filePath,
        int line,
        string message) => new(source, severity, producer, filePath, line, message);

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
        var state = new UiState
        {
            Chat = ChatDomainState.Empty with
            {
                Lines = list.ToImmutableArray()
            }
        };

        var todosList = PanelExtractors.ExtractTodos(list);
        var todosState = PanelExtractors.ExtractTodos(state);
        await Assert.That(todosState.Count).IsEqualTo(todosList.Count);

        var changesList = PanelExtractors.ExtractRecentChanges(list, 8);
        var changesState = PanelExtractors.ExtractRecentChanges(state, 8);
        await Assert.That(changesState.Count).IsEqualTo(changesList.Count);
    }
}
