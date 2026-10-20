// TestDiagnosticsOutputRule.cs — the guard for #1856: a test's diagnostics
// belong to that test.
//
// THE INCIDENT
// ------------
// #1856 measured the console writes under `tests/`: 117 lines mention the
// API, 5 of them in comments, leaving 112 call sites in 52 files. Most of
// them are diagnostics: a PTY scenario that could not see its marker dumps
// the screen it did see, a perf tripwire prints the number it measured, a
// load test prints the round timings. Those dumps are the answer to "why did
// this test go red" — and a red test with no answer is a CI minute spent on
// nothing.
//
// They were printed with `Console.WriteLine`, which is AMBIENT: the write is
// attributed to whichever context the WRITING THREAD happens to be in. Two
// failures follow from that, and both are silent:
//
//   * A write from a thread that does not flow the ExecutionContext resolves
//     to the global context — the runner's stdout, past every test's report.
//     The dump exists, in the CI log, attributed to nothing.
//   * A test that replaced `Console.Out` (the stdout-capture suites) has no
//     interceptor left at all: the dump goes into that suite's StringWriter
//     and out of the report entirely.
//
// `TestContext.Output` is ADDRESSED, not ambient: it names the test whose
// report the line belongs to. That is the whole change, and it is a
// one-for-one substitution at each call site.
//
// HONESTY NOTE ON WHAT THE SUBSTITUTION BUYS
// --------------------------------------------
// TUnit 1.75 (the pin since #1885) installs a console interceptor that routes
// `Console.Out` writes into the current context's output sink, so a
// `Console.WriteLine` from the test's OWN thread also reaches the report.
// The change is therefore not "the output used to be lost" — it is "the
// output used to reach the report only if the writing thread happened to
// resolve to that test's context". Addressed writes do not depend on the
// writer's thread. The claim this file's own sibling
// (`tests/Harbor.Tui.CellForge.Tests/TestContextOutputDiagnosticsTests.cs`)
// makes is the narrow one: the diagnostic is IN the captured output the
// report is built from.
//
// WHY A SOURCE SCAN AND NOT A BASELINE ROW
// ----------------------------------------
// "This file must not print to the console" is not a symbol, so it cannot be a
// banned-API row, and it is not a shape the reference matrix can see: the
// diagnostic is a statement in a test, and the tests are not in the layering
// matrix at all. It is a source rule, in the shape the other source rules
// here use.
//
// WHY THE SCOPE IS THE CONVERTED FILES, NOT THE WHOLE TREE
// ---------------------------------------------------------
// A whole-tree rule needs an allowlist, and the allowlist would have to name
// every file that legitimately keeps a console write:
//
//   * a BenchmarkDotNet row whose MEASURED OPERATION is the console write
//     (`TerminalScreenBufferBlitBenchmark`) — converting it would measure a
//     different thing;
//   * a helper that deliberately writes to BOTH the console and the test
//     output (`ViewRegistryFreezeTests.Report`), because the console line is
//     for local runs and the output line is for the report;
//   * `Console.WriteLine` inside a string literal (a fixture that the
//     architecture rules feed to a Roslyn analyzer);
//   * and every file another agent has in flight — the guard-test conversions
//     of #1859, the TestKit split of #1889, the Avalonia helper of #1819, the
//     LSP and TUI-perf branches. Those are separate PRs on separate branches.
//
// Naming them would put this gate in the middle of other people's work: their
// branch would go red on a rule that is not about their change. So the scope
// is the files THIS change converted, and the list below is also the
// inventory of the conversion. A file that is missing from the checkout is a
// violation, not a skip: a renamed file would otherwise silently un-guard
// itself, and a guard that un-guards itself is not a guard.
//
// WHAT IS RULED
// -------------
//   1. NO CONSOLE WRITE. A file in the converted set must contain no call to
//      `Console.Write`, `Console.WriteLine` or `Console.WriteError` — not
//      through `Console.Out` either.
//   2. COMMENTS ARE NOT CODE. The scan runs over comment-stripped source, so
//      prose that NAMES the console API (`<c>Console.Write</c>` in a doc
//      comment) is not a violation. String literals are not stripped, which is
//      the documented behaviour of `SourceScan.StripComments` and matches every
//      other gate here.
//   3. NON-VACUITY. The detector is run against a synthetic positive (a real
//      call) and a synthetic negative (the same token in prose), so a detector
//      that silently stopped matching cannot leave this gate green for the
//      wrong reason. The same is true of the scope: the rule asserts that it
//      READ all 20 files, so an empty scan cannot pass as "no violations".

using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     #1856 — the test files whose diagnostics were moved onto
///     <c>TestContext.Output</c> must not print to the console any more, so
///     that a failing test's report carries the reason it failed. See the file
///     header for the incident, the scope decision, and the limits.
/// </summary>
public sealed class TestDiagnosticsOutputRule
{
    /// <summary>
    ///     The files #1856 converted, repo-relative. This list is the scope of
    ///     the rule AND the inventory of the conversion: every diagnostic call
    ///     site that moved onto <c>TestContext.Output</c> is in one of these
    ///     files.
    /// </summary>
    private static readonly string[] ConvertedDiagnosticFiles =
    [
        "tests/Harbor.Application.Tests/AgentLoopAllocationTests.cs",
        "tests/Harbor.Application.Tests/TurnRunnerUpdateCoalescingTests.cs",
        "tests/Harbor.Core.Tests/CompactionPolicyMaterializeScalingTests.cs",
        "tests/Harbor.E2E.Framework/TuiDriver.cs",
        "tests/Harbor.E2E.Framework/TuiE2eTestBase.cs",
        "tests/Harbor.LoadTests/MultiSessionLoadTests.cs",
        "tests/Harbor.Plugins.Host.Tests/PluginHostJsonRpcTests.cs",
        "tests/Harbor.Plugins.Runtime.Tests/Hosting/DebouncedPluginWatcherTests.cs",
        "tests/Harbor.Registries.Tests/AllocationBudgetTests.cs",
        "tests/Harbor.Storage.Jsonl.Tests/JsonlAllocationBudgetTests.cs",
        "tests/Harbor.Storage.Jsonl.Tests/SessionFileReaderScalingTests.cs",
        "tests/Harbor.Tui.CellForge.PtyTests/MouseEdgeScenarioTests.cs",
        "tests/Harbor.Tui.CellForge.PtyTests/MouseScenarioTests.cs",
        "tests/Harbor.Tui.CellForge.PtyTests/TerminalModesScenarioTests.cs",
        "tests/Harbor.Tui.CellForge.PtyTests/TermiosRestoreScenarioTests.cs",
        "tests/Harbor.Tui.CellForge.Tests/PerfBudgetTests.cs",
        "tests/Harbor.Tui.CellForge.Tests/ProjectionCoalescingAllocationTests.cs",
        "tests/Harbor.Tui.CellForge.Tests/RendererMoatPerfTests.cs",
        "tests/Harbor.Tui.CellForge.Tests/StreamCoalescerScanTests.cs",
        "tests/Harbor.Ui.Framework.Tests/StoreDispatchAllocTests.cs",
    ];

    /// <summary>
    ///     A call to the console write API — <c>Console.Write</c>,
    ///     <c>Console.WriteLine</c>, <c>Console.WriteError</c>, optionally
    ///     reached through <c>Console.Out</c>. The trailing parenthesis is what
    ///     makes the shape a CALL rather than a mention: prose that names the
    ///     API without calling it is not a violation.
    /// </summary>
    private static readonly Regex ConsoleWriteCall = new(
        @"Console\s*\.\s*(Out\s*\.\s*)?Write(Line|Error)?\s*\(",
        RegexOptions.Compiled);

    [Test]
    public async Task Converted_Diagnostic_Files_Print_Nothing_To_The_Console()
    {
        var violations = new List<string>();
        var missing = new List<string>();
        var scanned = 0;

        if (RepoPaths.RepoRoot is not { } root)
        {
            // Outside a checkout there is no tree to scan. The other gates here
            // degrade the same way rather than failing a published test host
            // with an unrelated error.
            return;
        }

        foreach (string relative in ConvertedDiagnosticFiles)
        {
            string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                // A renamed or deleted file would silently un-guard itself.
                missing.Add(relative);
                continue;
            }

            string code = SourceScan.StripComments(File.ReadAllText(path));
            foreach (Match match in ConsoleWriteCall.Matches(code))
            {
                violations.Add($"{relative}: {match.Value}");
            }

            scanned++;
        }

        await Assert.That(missing).IsEmpty()
            .Because(
                "the file list IS the scope of this rule, so a file that is no longer in the checkout "
                + "means the rule is guarding nothing for it — a renamed file would otherwise silently "
                + "un-guard itself, and a guard that un-guards itself is not a guard. Missing: "
                + (missing.Count == 0 ? "(none)" : string.Join("\n", missing)));
        await Assert.That(scanned).IsEqualTo(ConvertedDiagnosticFiles.Length)
            .Because(
                "the rule has to READ every file in its scope for the absence of console writes to mean "
                + "anything. A scope that silently shrank would turn this gate into a pass on nothing. "
                + $"Read {scanned} of {ConvertedDiagnosticFiles.Length}.");
        await Assert.That(violations).IsEmpty()
            .Because(
                "a diagnostic printed with Console.WriteLine is attributed to whichever context the "
                + "WRITING THREAD is in, not to the test that produced it: a write from a thread that "
                + "does not flow the ExecutionContext lands in the runner's stdout, and a test that "
                + "replaced Console.Out loses the line entirely. TestContext.Output names the test "
                + "whose report the line belongs to, so the failing test says WHY it failed. A console "
                + "write here means the conversion was reverted. Found: "
                + (violations.Count == 0 ? "(none)" : string.Join("\n", violations)));
    }

    [Test]
    public async Task The_Detector_Finds_A_Console_Write_And_Ignores_A_Mention_Of_One()
    {
        const string RealCall = """
            public void Report(string message)
            {
                Console.WriteLine(message);
            }
            """;

        const string ProseOnly = """
            /// <summary>
            ///     The measured operation is a raw <c>Console.Write</c> per row.
            /// </summary>
            public void Report(string message)
            {
            }
            """;

        await Assert.That(ConsoleWriteCall.IsMatch(SourceScan.StripComments(RealCall))).IsTrue()
            .Because(
                "positive control: a real console write has to be detected, or the rule above passes on "
                + "a detector that matches nothing");
        await Assert.That(ConsoleWriteCall.IsMatch(SourceScan.StripComments(ProseOnly))).IsFalse()
            .Because(
                "negative control: prose that NAMES the console API is not a call, and a rule that "
                + "flagged documentation would be a permanently red false positive — which is how a "
                + "guard gets deleted");
    }
}
