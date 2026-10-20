// UnobservedTaskRuleTests.cs — HARCH003's own tests: the planted controls,
// the gate over the live tree, and the differential against the pattern
// matcher that HARCH003 replaces.
//
// WHAT THESE TESTS ARE
// --------------------
// The rule under test is `Roslyn/UnobservedTaskAnalyzers.cs` (HARCH003). The
// matcher it replaces is `AvaloniaFireAndForgetRules.cs`, which asks whether a
// line contains a call whose NAME looks async-shaped. The tester here is the
// difference between those two questions, and it is the whole point of the
// migration:
//
//   1. Can the semantic rule FAIL?          (planted offenders)
//   2. Does it stay SILENT where the pattern matcher is right to be silent?
//      (planted near-misses)
//   3. Does it see everything the pattern matcher sees?  (the differential)
//   4. Does it see what the pattern matcher CANNOT see?  (the same
//      differential, the other direction)
//   5. Is the gate LIVE — i.e. would it fail if the fix were reverted?
//      (a planted offender compiled INTO the gated project)
//
// THE PLANTED CONTROLS ARE THE POINT
// ----------------------------------
// A guard that matches nothing is indistinguishable from a guard that is
// broken, and a broken guard is worse than no guard because it is believed.
// Every planted source below holds BOTH an offender (the rule must report it)
// and a near-miss (the rule must stay quiet), and the near-misses are the
// ones the PATTERN matcher gets wrong in the other direction: a method whose
// NAME ends in `Async` and whose TYPE is `int`.
//
// THE DIFFERENTIAL IS AGAINST THE ORACLE, NOT AGAINST A COPY OF IT
// ----------------------------------------------------------------
// The pattern matcher's own detector is used — `AvaloniaFireAndForgetRules` —
// not a re-implementation of it. A second implementation of the same regular
// expressions would agree with itself and prove nothing.
//
// The pattern matcher is now a NON-GATING ORACLE: the gate is HARCH003, and
// the matcher survives so that the two can be compared. See the header of
// `AvaloniaFireAndForgetRules.cs` for why the coverage is not weaker.

using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Harbor.Architecture.Tests;

/// <summary>
///     HARCH003's tests: the planted controls, the live-tree gate, and the
///     differential against the pattern matcher it replaces.
/// </summary>
public sealed class UnobservedTaskRuleTests
{
    /// <summary>
    ///     The planted OFFENDERS. Every one of them is a Task whose value is
    ///     thrown away, and not one of them can be found by looking at the
    ///     spelling of the call: two of the four call names do not end in
    ///     `Async`, and the third returns a type DERIVED from `Task` whose
    ///     own name says nothing about being asynchronous.
    /// </summary>
    private const string OffendersSource = """
        using System;
        using System.Threading.Tasks;

        namespace Pilot
        {
            public sealed class Offenders
            {
                // A Task-typed call whose NAME says nothing about being async.
                public void DetachByName() => _ = Fetch();

                // A Task<T>.
                public void DetachGeneric() => _ = ComputeAsync();

                // A type DERIVED from Task.
                public void DetachDerived() => _ = MakeDerived();

                // The expression-bodied void member: there is no `_ =` at all.
                public void DetachImplicit() => Produce();

                // The same shape with the discard inside a block.
                public void DetachInBlock()
                {
                    _ = Fetch();
                }

                public Task Fetch() => Task.CompletedTask;

                public Task<int> ComputeAsync() => Task.FromResult(1);

                public DerivedTask MakeDerived() => new DerivedTask();

                public Task Produce() => Task.CompletedTask;
            }

            public sealed class DerivedTask : Task
            {
                public DerivedTask()
                {
                }
            }
        }
        """;

    /// <summary>
    ///     The planted NEAR-MISSES. Every one of them is a Task that IS
    ///     observed — awaited, returned, stored, handed to an observer — or a
    ///     discard of something that is not a Task at all.
    /// </summary>
    /// <remarks>
    ///     The last two are the ones the PATTERN matcher gets wrong: an
    ///     `Async`-named method that returns `int`. The matcher flags the
    ///     call; the type does not.
    /// </remarks>
    private const string NearMissesSource = """
        using System;
        using System.Threading.Tasks;

        namespace Pilot
        {
            public sealed class NearMisses
            {
                // Awaited: the caller observes the outcome.
                public async Task AwaitedAsync() => await Fetch();

                // Returned: the caller observes the outcome.
                public Task ReturnedAsync() => Fetch();

                // Stored: the value is kept, not dropped.
                public Task StoredAsync()
                {
                    Task kept = Fetch();
                    return kept;
                }

                // Passed as an argument: the callee receives the value.
                public void PassedAsync() => Observe(Fetch());

                // The house form: the Task is an ARGUMENT to an observer, not a
                // discarded value.
                public void ObservedForm() => Observer.Forget(Fetch(), ex => { });

                // NOT a Task: an ordinary discard.
                public void OrdinaryDiscard() => _ = new object();

                // An Async-NAMED method that is NOT a Task.
                public int NotATaskAsync() => 42;

                // The call the pattern matcher misreads.
                public void Mispreads() => _ = NotATaskAsync();

                public Task Fetch() => Task.CompletedTask;

                public static void Observe(Task task)
                {
                }
            }

            public static class Observer
            {
                public static void Forget(Task task, Action<Exception> onError)
                {
                }
            }
        }
        """;

    /// <summary>
    ///     The paths the rule is declared to gate, each with the reason it is
    ///     inside the perimeter.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The perimeter is the CALLER's decision, not the analyzer's: an
    ///         analyzer sees one compilation, and a perimeter is a statement
    ///         about a set of projects. The three entries are the desktop
    ///         shell (the perimeter the retired pattern matcher converted in
    ///         #569) and the two files the vs-threading audit named in #1852.
    ///     </para>
    ///     <para>
    ///         The rule is NOT armed over the whole product tree, and the
    ///         reason is written down rather than implied: over the whole
    ///         tree the rule finds sites — the CLI's own detaches, the LSP
    ///         client's, the IDE bridge's — that no one has fixed yet, and a
    ///         gate that is red on the day it is written is a gate that gets
    ///         switched off. Those sites are the follow-up; the ones the
    ///         issue named are fixed here.
    ///     </para>
    /// </remarks>
    private static readonly (string Path, string Reason)[] GatedPaths =
    [
        ("src/Harbor.Ipc.Server", "the IPC accept loop — the site #1852 named"),
        ("apps/Harbor.App.Avalonia", "the desktop shell — the perimeter the retired pattern matcher converted (#569)"),
        ("apps/Harbor.App.Cli/Repl/PromptPipeline.cs", "the REPL entry point — the site #1852 named"),
    ];

    /// <summary>
    ///     The projects that have to be compiled for the gate. Only these
    ///     three: the gate is over three paths, and compiling the whole
    ///     product tree to look at three files is the kind of cost that gets
    ///     a rule deleted.
    /// </summary>
    private static readonly string[] GatedProjects = ["Harbor.Ipc.Server", "Harbor.App.Avalonia", "Harbor.App.Cli"];

    /// <summary>
    ///     The planted offender used to prove the gate is LIVE over a gated
    ///     project: it is compiled INTO that project, so a rule that had
    ///     stopped seeing that project's files would report nothing and the
    ///     zero-finding verdict below would be a verdict about nothing.
    /// </summary>
    private const string PlantedOffenderSource = """
        using System.Threading.Tasks;

        namespace Planted
        {
            public sealed class Offender
            {
                public void Detach() => _ = Produce();

                public Task Produce() => Task.CompletedTask;
            }
        }
        """;

    /// <summary>The live-tree findings, measured once.</summary>
    private static readonly Lazy<Task<IReadOnlyList<(string Path, int Line, string Message)>>> LiveFindings =
        new(MeasureLiveAsync);

    // =====================================================================
    // 1. The semantic rule can fail.
    // =====================================================================

    /// <summary>
    ///     Every planted offender is reported, and the set of DISTINCT symbols
    ///     reported is exactly the set of Task shapes the rule knows.
    /// </summary>
    /// <remarks>
    ///     The assertion is on the SET and on the COUNT, not on a count alone:
    ///     a rule that reported one offender twice would still satisfy a
    ///     count, and a rule that missed one would too.
    /// </remarks>
    [Test]
    public async Task Harch003_Reports_Every_Planted_Unobserved_Task()
    {
        CSharpCompilation compilation = Compile(("Pilot/Offenders.cs", OffendersSource));
        List<Diagnostic> diagnostics = await RunAsync(compilation, new UnobservedTaskAnalyzer());

        var reported = diagnostics
            .Select(static d => Quoted(d.GetMessage(CultureInfo.InvariantCulture)))
            .Where(static s => s.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static s => s, StringComparer.Ordinal)
            .ToList();

        var expected = new List<string>
        {
            "Pilot.DerivedTask",
            "System.Threading.Tasks.Task",
            "System.Threading.Tasks.Task<int>",
        };

        await Assert.That(string.Join(" | ", reported)).IsEqualTo(string.Join(" | ", expected))
            .Because(
                "the rule is a TYPE rule, so the symbols it names are the Task types themselves: the "
                + "framework's Task, its generic form, and the type DERIVED from Task — the last one is "
                + "a shape no spelling match can reach. A symbol missing from the left side is a Task "
                + "shape the rule cannot see; a symbol missing from the right side is a false positive. "
                + "Reported: " + string.Join(" | ", reported));

        await Assert.That(diagnostics.Count).IsEqualTo(5)
            .Because(
                "the planted source holds five discarded Tasks: two discards of the framework's Task, one "
                + "of Task<int>, one of the derived type, and the expression-bodied void member that has "
                + "no discard marker at all. A count that is too small means the rule missed a shape; a "
                + "count that is too large means the same site is reported twice, which makes the count "
                + "meaningless. Reported " + diagnostics.Count + " diagnostic(s): "
                + string.Join(" | ", diagnostics.Select(static d => d.GetMessage(CultureInfo.InvariantCulture))));
    }

    // =====================================================================
    // 2. The semantic rule stays silent where it should.
    // =====================================================================

    /// <summary>
    ///     The negative control: the rule stays silent on every near-miss.
    /// </summary>
    /// <remarks>
    ///     The near-misses are the ones the pattern matcher has to work
    ///     hardest for, and the two that matter most are the `Async`-named
    ///     method that returns `int`: the pattern matcher flags the call,
    ///     the type does not, and a rule that cries wolf on correct code is a
    ///     rule that is switched off within a week.
    /// </remarks>
    [Test]
    public async Task Harch003_Stays_Silent_On_The_Planted_Near_Misses()
    {
        CSharpCompilation compilation = Compile(("Pilot/NearMisses.cs", NearMissesSource));
        List<Diagnostic> diagnostics = await RunAsync(compilation, new UnobservedTaskAnalyzer());

        await Assert.That(diagnostics.Count).IsEqualTo(0)
            .Because(
                "a rule that reports an awaited Task, a returned Task, a stored Task, a Task handed to an "
                + "observer, or a discard of an ordinary value is a noise machine. Reported: "
                + string.Join(" | ", diagnostics.Select(static d => d.GetMessage(CultureInfo.InvariantCulture))));
    }

    // =====================================================================
    // 3. The differential against the pattern matcher.
    // =====================================================================

    /// <summary>
    ///     The semantic rule sees every site the pattern matcher sees, and
    ///     sees the ones it cannot.
    /// </summary>
    /// <remarks>
    ///     The direction of the first assertion is the one that matters: a
    ///     migration is safe when the new rule sees everything the old rule
    ///     saw. The second assertion is the precision claim — the semantic
    ///     rule reaches the sites the SPELLING cannot, which is the whole
    ///     reason it exists.
    /// </remarks>
    [Test]
    public async Task The_Semantic_Rule_Sees_Every_Site_The_Pattern_Matcher_Sees_And_More()
    {
        CSharpCompilation compilation = Compile(("Pilot/Offenders.cs", OffendersSource));

        List<Diagnostic> diagnostics = await RunAsync(compilation, new UnobservedTaskAnalyzer());
        var semanticLines = diagnostics
            .Where(static d => d.Location.SourceTree?.FilePath == "Pilot/Offenders.cs")
            .Select(static d => d.Location.GetLineSpan().StartLinePosition.Line + 1)
            .Distinct()
            .OrderBy(static l => l)
            .ToList();

        List<int> matcherLines = MatcherLines(("Pilot/Offenders.cs", OffendersSource));

        var missed = matcherLines.Where(l => !semanticLines.Contains(l)).ToList();

        await Assert.That(missed.Count).IsEqualTo(0)
            .Because(
                "the pattern matcher finds the planted `_ = ComputeAsync();` — a call whose NAME ends in "
                + "Async — and the semantic rule must find it too. A line the matcher flags and the "
                + "semantic rule does not is a hole in the replacement. Missed lines: "
                + string.Join(" | ", missed));

        await Assert.That(semanticLines.Count).IsEqualTo(5)
            .Because(
                "the semantic rule must reach the sites the spelling cannot: a Task-returning method "
                + "called `Fetch`, a Task-returning method called `Produce` in an expression-bodied void "
                + "member, and a type derived from Task. The pattern matcher finds ONE of the five; the "
                + "difference IS the migration. The semantic rule found: "
                + string.Join(" | ", semanticLines));
    }

    /// <summary>
    ///     The pattern matcher flags a call that is not a Task at all, and
    ///     the semantic rule does not.
    /// </summary>
    /// <remarks>
    ///     This is the false positive the migration removes, and it is the
    ///     reason a spelling-based guard is a liability rather than an asset:
    ///     a rule that flags correct code teaches its readers to ignore it.
    /// </remarks>
    [Test]
    public async Task The_Pattern_Matcher_Flags_A_Non_Task_That_The_Semantic_Rule_Does_Not()
    {
        CSharpCompilation compilation = Compile(("Pilot/NearMisses.cs", NearMissesSource));
        List<Diagnostic> diagnostics = await RunAsync(compilation, new UnobservedTaskAnalyzer());

        List<int> matcherLines = MatcherLines(("Pilot/NearMisses.cs", NearMissesSource));

        await Assert.That(diagnostics.Count).IsEqualTo(0)
            .Because(
                "the planted near-misses hold one `_ = NotATaskAsync();` — a call whose NAME ends in Async "
                + "and whose TYPE is int. The semantic rule resolves the type and stays quiet. Reported: "
                + string.Join(" | ", diagnostics.Select(static d => d.GetMessage(CultureInfo.InvariantCulture))));

        await Assert.That(matcherLines.Count).IsGreaterThan(0)
            .Because(
                "the pattern matcher flags that call — which is exactly the false positive the migration "
                + "removes. If the matcher did not flag it, this test would be proving nothing about the "
                + "difference between the two rules.");
    }

    /// <summary>
    ///     The LIVE differential: over the desktop shell, the pattern matcher
    ///     and the semantic rule see the same files.
    /// </summary>
    /// <remarks>
    ///     The direction of the assertion is the one that matters: a
    ///     migration is safe when the new rule sees everything the old rule
    ///     saw. The old rule's own detector is used — not a copy of its
    ///     patterns — so this is a comparison between two detectors.
    /// </remarks>
    [Test]
    public async Task The_Semantic_Rule_Sees_Every_Live_File_The_Pattern_Matcher_Sees()
    {
        List<ScanHit> matcherHits = ScanRunner.Collect(
            AvaloniaFireAndForgetRules.Rule,
            ScanRunner.ReadSources(ScanRunner.ScopeFiles(AvaloniaFireAndForgetRules.Rule)));

        var matcherFiles = matcherHits
            .Select(static h => h.File)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static f => f, StringComparer.Ordinal)
            .ToList();

        IReadOnlyList<(string Path, int Line, string Message)> findings = await LiveFindings.Value;
        var semanticFiles = findings
            .Select(static f => f.Path)
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);

        var missed = matcherFiles.Where(f => !semanticFiles.Contains(f)).ToList();

        await Assert.That(missed.Count).IsEqualTo(0)
            .Because(
                "the semantic rule must see every file the pattern matcher sees on the live tree — the "
                + "direction a migration has to hold in. Files the matcher flags and the semantic rule does "
                + "not: " + string.Join(" | ", missed));
    }

    // =====================================================================
    // 4. The gate over the live tree.
    // =====================================================================

    /// <summary>
    ///     No Task is discarded without an observer inside the gated paths.
    /// </summary>
    /// <remarks>
    ///     The gate is the reason the two sites of #1852 are fixed: revert
    ///     either fix and this test names the file. The perimeter is the
    ///     caller's, and the reasons for each entry are in
    ///     <see cref="GatedPaths" />.
    /// </remarks>
    [Test]
    public async Task Harch003_Finds_No_Unobserved_Task_Inside_The_Gated_Paths()
    {
        IReadOnlyList<(string Path, int Line, string Message)> findings = await LiveFindings.Value;

        await Assert.That(findings.Count).IsEqualTo(0)
            .Because(
                "§FP-003. A Task whose exception nobody observes is a fault that is reported nowhere: "
                + ".NET parks it on the Task and only raises UnobservedTaskException at finalization, "
                + "which for a process that lives for hours is never. Await it, return it, store it, or "
                + "route it through TaskFireAndForget.Forget(task, ex => log(ex)). Findings: "
                + string.Join(" | ", findings.Select(static f => f.Path + ":" + f.Line + " " + f.Message)));
    }

    /// <summary>
    ///     The gate is LIVE: a planted offender compiled into a gated project
    ///     is reported.
    /// </summary>
    /// <remarks>
    ///     Without this, "no findings" and "the analyzer saw nothing" are the
    ///     same report, and a rule that stopped seeing a project's files would
    ///     report green forever. The planted file is compiled INTO the
    ///     project, so the control exercises the very compilation the gate
    ///     grades.
    /// </remarks>
    [Test]
    public async Task Harch003_Reports_A_Planted_Offender_Compiled_Into_A_Gated_Project()
    {
        CSharpCompilation? gated = null;
        foreach (RoslynProjectCompilation.Project project in RoslynProjectCompilation.ProductProjects())
        {
            if (project.Dir == "Harbor.Ipc.Server")
            {
                gated = RoslynProjectCompilation.ForProject(project);
                break;
            }
        }

        await Assert.That(gated).IsNotNull()
            .Because("the gate grades the IPC server project; if the project is not in the product "
                   + "universe, the gate grades nothing");

        Compilation withPlanted = gated!.AddSyntaxTrees(
            CSharpSyntaxTree.ParseText(
                PlantedOffenderSource,
                new CSharpParseOptions(LanguageVersion.Preview),
                path: "src/Harbor.Ipc.Server/Protocol/PlantedUnobservedTask.cs"));

        List<Diagnostic> diagnostics = await RunAsync(withPlanted, new UnobservedTaskAnalyzer());

        await Assert.That(diagnostics.Count).IsGreaterThan(0)
            .Because(
                "the planted offender is a discarded Task compiled into the very project the gate grades. "
                + "If the analyzer does not report it, the zero-finding verdict above is a verdict about "
                + "nothing. Reported: "
                + string.Join(" | ", diagnostics.Select(static d => d.GetMessage(CultureInfo.InvariantCulture))));
    }

    // =====================================================================
    // Harness.
    // =====================================================================

    /// <summary>Compiles one synthetic source file as one assembly.</summary>
    private static CSharpCompilation Compile(params (string DisplayPath, string Source)[] sources)
    {
        var trees = new List<SyntaxTree>(sources.Length);
        foreach ((string displayPath, string source) in sources)
        {
            trees.Add(CSharpSyntaxTree.ParseText(
                source,
                new CSharpParseOptions(LanguageVersion.Preview),
                path: displayPath));
        }

        return CSharpCompilation.Create(
            "Pilot.Harch003",
            trees,
            RoslynProjectCompilation.References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    /// <summary>Runs one analyzer over one compilation.</summary>
    private static async Task<List<Diagnostic>> RunAsync(Compilation compilation, DiagnosticAnalyzer analyzer)
    {
        CompilationWithAnalyzers withAnalyzers = compilation.WithAnalyzers(
            [analyzer],
            new AnalyzerOptions(ImmutableArray<AdditionalText>.Empty));

        ImmutableArray<Diagnostic> diagnostics = await withAnalyzers.GetAnalyzerDiagnosticsAsync();
        return [.. diagnostics];
    }

    /// <summary>
    ///     The lines the PATTERN matcher flags in one synthetic source, from
    ///     the matcher's own detector — not from a copy of its patterns,
    ///     which is what would make this a second implementation rather than
    ///     a differential.
    /// </summary>
    private static List<int> MatcherLines(params (string DisplayPath, string Source)[] sources)
    {
        List<ScanHit> hits = ScanRunner.Collect(AvaloniaFireAndForgetRules.Rule, sources);

        return
        [
            .. hits.Select(static h => h.Line).Distinct().OrderBy(static l => l)
        ];
    }

    /// <summary>
    ///     The live-tree findings inside the gated paths, measured once.
    /// </summary>
    private static async Task<IReadOnlyList<(string Path, int Line, string Message)>> MeasureLiveAsync()
    {
        var findings = new List<(string Path, int Line, string Message)>();

        foreach (RoslynProjectCompilation.Project project in RoslynProjectCompilation.ProductProjects())
        {
            if (!GatedProjects.Contains(project.Dir, StringComparer.Ordinal))
            {
                continue;
            }

            CSharpCompilation compilation = RoslynProjectCompilation.ForProject(project);
            List<Diagnostic> diagnostics = await RunAsync(compilation, new UnobservedTaskAnalyzer());

            foreach (Diagnostic diagnostic in diagnostics)
            {
                string? path = diagnostic.Location.SourceTree?.FilePath;
                if (path is null)
                {
                    continue;
                }

                string relative = SourceScan.Relative(path);
                if (!IsGated(relative))
                {
                    continue;
                }

                findings.Add((
                    relative,
                    diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1,
                    diagnostic.GetMessage(CultureInfo.InvariantCulture)));
            }
        }

        return findings;
    }

    /// <summary>Whether a repo-relative path is inside the gated perimeter.</summary>
    private static bool IsGated(string relative) =>
        GatedPaths.Any(g => relative.Equals(g.Path, StringComparison.Ordinal)
                            || relative.StartsWith(g.Path + "/", StringComparison.Ordinal));

    /// <summary>
    ///     The symbol a diagnostic names: the message format is
    ///     <c>'{0}' …</c>, so the quoted part is the symbol and the rest is
    ///     the explanation.
    /// </summary>
    private static string Quoted(string message)
    {
        int open = message.IndexOf('\'');
        if (open < 0)
        {
            return string.Empty;
        }

        int close = message.IndexOf('\'', open + 1);
        if (close < 0)
        {
            return string.Empty;
        }

        return message.Substring(open + 1, close - open - 1);
    }
}
