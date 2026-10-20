// UnguardedResultReadAnalyzerTests.cs — the MEASUREMENT for issue #1868: the
// unguarded-Result rule as a type-resolving analyzer, measured against the
// text scan it is meant to replace.
//
// WHAT THESE TESTS ARE
// --------------------
// The analyzer lives in `Roslyn/UnguardedResultReadAnalyzer.cs`; the harness
// that gives it a compilation lives in `Roslyn/RoslynProjectCompilation.cs`.
// These tests answer four questions:
//
//   1. Can the analyzer be made to FAIL?   (planted offenders)
//   2. Does it stay SILENT where a human would say the read is guarded?
//      (planted near-misses)
//   3. Does it agree with the TEXT RULE on the live tree? (the differential)
//   4. Does it invent findings the tree does not have? (the new-file check)
//
// THE PLANTED CONTROLS ARE THE POINT
// ----------------------------------
// A guard that matches nothing is indistinguishable from a guard that is
// broken, and a broken guard is worse than no guard because it is believed.
// Every control below plants BOTH an offender (the analyzer must report it)
// and a near-miss (the analyzer must stay silent), and the assertions are on
// the EXACT set of reports — a matcher that reported one extra site, or
// missed one, fails.
//
// The near-misses are the interesting half, because they are the shapes the
// dominance model has to get right:
//
//   * the early-return guard — `if (r.IsFailure) return; … r.Value` — which
//     CFE0001 does NOT model (that is 14 of its 33 production false
//     positives, docs/ROP-API-INVENTORY.md §5.4);
//   * the if-branch, the ternary, the `&&` short circuit and the property
//     pattern guard;
//   * a guard that DOMINATES a read inside a lambda;
//   * a same-shaped type that is NOT a `Result` — `Box.Value` and
//     `Maybe<T>.Value` — which the type-resolving analyzer must ignore and
//     a spelling matcher would have to be taught to ignore.
//
// One offender is a read of a DIFFERENT `Result` instance than the one that
// was checked. It is an OFFENDER, not a near-miss: `if (a.IsFailure) return;
// … b.Value` is exactly the bug the rule is for, because the check says
// nothing about `b`. Planting it as an offender is what proves the analyzer
// does not confuse two values that happen to share a shape.
//
// THE DIFFERENTIAL
// ----------------
// The text rule is `tests/Harbor.Architecture.Tests/UnguardedResultReadRules.cs`
// (788 lines, three regexes, its own comment stripper, nine planted
// controls). The live tree has exactly ONE baselined site under it
// (`apps/Harbor.App.Avalonia/ViewModels/SettingsViewModel.cs`, the
// `GETRESULT-VALUE` form) and it is a REAL defect awaiting a product
// decision.
//
// The differential therefore has to answer: does the analyzer see that site,
// and what else does it see? The test prints the whole table in both
// directions, and asserts:
//
//   * every site the TEXT rule finds is a site the ANALYZER finds (the
//     safety direction: a migration is safe when the new rule sees
//     everything the old rule saw);
//   * the analyzer flags no FILE that is not already known to hold a
//     `Result` read — the migration must not turn a one-file wave into a
//     thirty-file wave;
//   * the analyzer's footprint stays inside the ceiling CFE0001's own
//     measurement implies (35 production `.Value` reads), because its
//     dominance model is strictly MORE precise than CFE0001's.
//
// NOTHING HERE IS A GATE YET
// --------------------------
// The analyzer is REPORT-ONLY in this PR: the text rule keeps being the
// gate, and the analyzer's findings are measured, not enforced. Replacing
// the text rule is a separate decision, made on the strength of this
// differential — the same order issue #1868 sets out ("first the analyzer in
// report-only mode, then a differential test against the text rule, and
// only after a green differential the removal of the text part").

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Harbor.Architecture.Tests;

/// <summary>
///     The analyzer's own tests: the planted controls, and the differential
///     against the text rule on the live tree.
/// </summary>
public sealed class UnguardedResultReadAnalyzerTests
{
    /// <summary>
    ///     The planted OFFENDERS: six ways to read a <c>Result</c> that no
    ///     check protects.
    /// </summary>
    /// <remarks>
    ///     Offender 1 and 2 are the shapes CFE0001 is blind to (the receiver of
    ///     <c>.Value</c> is an await, then an invocation) and that the text
    ///     rule has to spell as two regexes. Offender 3 is the plain unguarded
    ///     local. Offender 4 reads a DIFFERENT instance than the one that was
    ///     checked. Offender 5 reassigns the checked value, which invalidates
    ///     the check.
    /// </remarks>
    private const string OffendersSource = """
        using System.Threading.Tasks;
        using CSharpFunctionalExtensions;

        namespace Harch003
        {
            public sealed class Store
            {
                public Task<Result<string>> LoadAsync() => Task.FromResult(Result.Success("ok"));
            }

            public sealed class Offenders
            {
                private readonly Store _store = new Store();

                // OFFENDER 1 — the CFE0001 blind spot: the receiver of `.Value`
                // is an await, so no symbol resolves to a Result.
                public async Task<string> AwaitValueAsync()
                    => (await _store.LoadAsync()).Value;

                // OFFENDER 2 — the same blind spot, sync spelling.
                public string GetResultValue()
                    => _store.LoadAsync().GetAwaiter().GetResult().Value;

                // OFFENDER 3 — a named local nobody checked.
                public async Task<string> UncheckedLocalAsync()
                {
                    var loaded = await _store.LoadAsync();
                    return loaded.Value;
                }

                // OFFENDER 4 — a DIFFERENT instance than the one that was checked.
                public async Task<string> OtherInstanceAsync()
                {
                    var checkedOne = await _store.LoadAsync();
                    if (checkedOne.IsFailure)
                    {
                        return "failed";
                    }

                    var other = await _store.LoadAsync();
                    return other.Value;
                }

                // OFFENDER 5 — the check was invalidated by a reassignment.
                public async Task<string> ReassignedAsync()
                {
                    var reloaded = await _store.LoadAsync();
                    if (reloaded.IsFailure)
                    {
                        return "failed";
                    }

                    reloaded = await _store.LoadAsync();
                    return reloaded.Value;
                }

                // OFFENDER 6 — a `||` condition that can be true BECAUSE the
                // result failed: the ternary's true arm is then reachable with
                // a FAILED result, so the read there is not protected.
                public async Task<string> UnguardedTernaryOverOrAsync()
                {
                    var guarded = await _store.LoadAsync();
                    return guarded.IsFailure || guarded.Value.Length > 0 ? guarded.Value : "failed";
                }
            }
        }
        """;

    /// <summary>
    ///     The planted NEAR-MISSES: nine reads a human would call guarded, and
    ///     reads of types that are not <c>Result</c> at all.
    /// </summary>
    private const string NearMissesSource = """
        using System.Threading.Tasks;
        using CSharpFunctionalExtensions;

        namespace Harch003
        {
            public sealed class Store
            {
                public Task<Result<string>> LoadAsync() => Task.FromResult(Result.Success("ok"));
            }

            public sealed class NearMisses
            {
                private readonly Store _store = new Store();

                // NEAR-MISS 1 — the early-return guard. CFE0001 does not model
                // this shape, which is why 14 of its production sites are
                // false positives.
                public async Task<string> GuardedByEarlyReturnAsync()
                {
                    var loaded = await _store.LoadAsync();
                    if (loaded.IsFailure)
                    {
                        return "failed";
                    }

                    return loaded.Value;
                }

                // NEAR-MISS 2 — the if-branch guard.
                public async Task<string> GuardedByBranchAsync()
                {
                    var loaded = await _store.LoadAsync();
                    if (loaded.IsSuccess)
                    {
                        return loaded.Value;
                    }

                    return "failed";
                }

                // NEAR-MISS 3 — the ternary guard.
                public async Task<string> GuardedByTernaryAsync()
                {
                    var loaded = await _store.LoadAsync();
                    return loaded.IsSuccess ? loaded.Value : "failed";
                }

                // NEAR-MISS 4 — the `&&` short-circuit guard.
                public async Task<string> GuardedByShortCircuitAsync()
                {
                    var loaded = await _store.LoadAsync();
                    return loaded.IsSuccess && loaded.Value.Length > 0 ? loaded.Value : "failed";
                }

                // NEAR-MISS 5 — the `||` short-circuit guard: the right operand
                // runs only when the left one is false, i.e. only when the
                // result is a SUCCESS. This is the shape the live tree uses
                // (McpOAuthHandler: `if (refreshed.IsFailure || refreshed.Value.HasValue)`).
                public async Task<string> GuardedByShortCircuitOrAsync()
                {
                    var loaded = await _store.LoadAsync();
                    if (loaded.IsFailure || loaded.Value.Length > 0)
                    {
                        return "present";
                    }

                    return "failed";
                }

                // NEAR-MISS 6 — the property-pattern guard.
                public async Task<string> GuardedByPatternAsync()
                {
                    var loaded = await _store.LoadAsync();
                    if (loaded is { IsSuccess: true } matched)
                    {
                        return matched.Value;
                    }

                    return "failed";
                }

                // NEAR-MISS 7 — the guard dominates a read inside a lambda.
                public async Task<string> GuardedInsideLambdaAsync()
                {
                    var loaded = await _store.LoadAsync();
                    if (loaded.IsFailure)
                    {
                        return "failed";
                    }

                    System.Func<string> read = () => loaded.Value;
                    return read();
                }

                // NEAR-MISS 8 — a same-shaped type that is NOT a Result.
                public string NotAResult()
                {
                    Box box = new Box();
                    return box.Value;
                }

                // NEAR-MISS 9 — the OTHER value type of the same library.
                public string NotAResultEither()
                {
                    Maybe<string> maybe = Maybe<string>.None;
                    return maybe.HasValue ? maybe.Value : "none";
                }
            }

            public sealed class Box
            {
                public string Value { get; init; } = "";
            }
        }
        """;

    /// <summary>
    ///     The ceiling on the analyzer's live-tree footprint.
    /// </summary>
    /// <remarks>
    ///     CFE0001 measures 35 production <c>.Value</c> reads
    ///     (docs/ROP-API-INVENTORY.md §5.2), and its model is COARSER than
    ///     this analyzer's: it does not model the early-return, early-continue,
    ///     fail-fast-throw, <c>&amp;&amp;</c>-short-circuit or property-pattern
    ///     guards, which is why 33 of those 35 are false positives carrying a
    ///     pragma. A dominance model that is strictly more precise cannot
    ///     report more sites than the coarser one, so a number above this
    ///     ceiling means the model is not working — it is reporting reads a
    ///     human would call guarded.
    /// </remarks>
    private const int MaxLiveFindings = 40;

    /// <summary>
    ///     The analyzer reports every planted offender.
    /// </summary>
    /// <remarks>
    ///     The assertion is on the SET of receivers the reports name, not on a
    ///     count: a matcher that reported one offender twice would still pass
    ///     a count assertion, and a matcher that reported a near-miss as well
    ///     would inflate the count.
    /// </remarks>
    [Test]
    public async Task Analyzer_Reports_Every_Planted_Unguarded_Result_Read()
    {
        CSharpCompilation compilation = Compile(OffendersSource, "Harbor.Architecture.Tests.Offenders");
        List<Diagnostic> diagnostics = await RunAsync(compilation, new UnguardedResultReadAnalyzer());

        List<string> receivers = [.. diagnostics
            .Select(static d => ReceiverOf(d.GetMessage(System.Globalization.CultureInfo.InvariantCulture)))
            .OrderBy(static r => r, StringComparer.Ordinal)];

        var expected = new List<string>
        {
            "_store.LoadAsync().GetAwaiter().GetResult()",
            "await _store.LoadAsync()",
            "guarded",
            "loaded",
            "other",
            "reloaded",
        };

        await Assert.That(string.Join(" | ", receivers)).IsEqualTo(string.Join(" | ", expected))
            .Because(
                "the rule bans a Result read that no IsSuccess/IsFailure check dominates, and the "
                + "planted source contains one of each: the await spelling CFE0001 cannot see, the "
                + "synchronous spelling of the same hole, a local nobody checked, a DIFFERENT "
                + "instance than the one that was checked, a value that was reassigned after "
                + "the check, and a ternary whose condition can be true because the result failed. "
                + "A receiver missing from the left side is a shape the analyzer cannot "
                + "see; one missing from the right side is a false positive. Reported: "
                + string.Join(" | ", receivers));
    }

    /// <summary>
    ///     The analyzer stays silent on every planted near-miss.
    /// </summary>
    /// <remarks>
    ///     This is the half that decides whether the rule can be adopted. The
    ///     early-return guard alone accounts for 14 of CFE0001's 33 production
    ///     false positives, and a rule that flagged it would drown the tree.
    /// </remarks>
    [Test]
    public async Task Analyzer_Stays_Silent_On_The_Planted_Guarded_Reads()
    {
        CSharpCompilation compilation = Compile(NearMissesSource, "Harbor.Architecture.Tests.NearMisses");
        List<Diagnostic> diagnostics = await RunAsync(compilation, new UnguardedResultReadAnalyzer());

        await Assert.That(diagnostics.Count).IsEqualTo(0)
            .Because(
                "a rule that reports a read a dominating IsSuccess/IsFailure check protects, or a "
                + "read of a type that merely SHARES the shape (Box.Value, Maybe<T>.Value), is a "
                + "noise machine — and a noise machine is switched off within a week. Reported: "
                + string.Join(" | ", diagnostics.Select(static d => d.GetMessage(System.Globalization.CultureInfo.InvariantCulture))));
    }

    /// <summary>
    ///     The differential: the analyzer sees every site the text rule sees,
    ///     on the live tree.
    /// </summary>
    [Test]
    public async Task The_Analyzer_Sees_Every_Site_The_Text_Rule_Sees()
    {
        List<ScanHit> textHits = ScanRunner.Collect(
            UnguardedResultReadRules.Rule,
            ScanRunner.ReadSources(ScanRunner.ScopeFiles(UnguardedResultReadRules.Rule)));

        List<string> analyzerSites = await AnalyzerSitesAsync();

        // The table goes out BEFORE the assertions: a red run must still carry
        // the measurement, because the measurement is the point of the run.
        PrintDifferential(textHits, analyzerSites);

        var missed = textHits
            .Where(hit => !analyzerSites.Contains(TextSite(hit), StringComparer.Ordinal))
            .ToList();

        await Assert.That(missed.Count).IsEqualTo(0)
            .Because(
                "the text rule matches a SPELLING and the analyzer matches the TYPE, so the analyzer "
                + "must see a SUPERSET of the text rule's sites: `(await …).Value` and "
                + "`…GetResult().Value` are one and the same operation to it. A site the text rule "
                + "flags and the analyzer does not is a hole in the analyzer. Sites the text rule "
                + "flags and the analyzer does not: " + string.Join(" | ", missed));

        // The analyzer must not flag a FILE that is not already known to hold
        // a Result read. The text rule's live footprint is one file; the
        // analyzer's may not be thirty.
        List<string> textFiles = [.. textHits.Select(static hit => hit.File).Distinct(StringComparer.Ordinal)];
        List<string> analyzerFiles = [.. analyzerSites
            .Select(static site => FileOf(site))
            .Distinct(StringComparer.Ordinal)];

        var newFiles = analyzerFiles
            .Where(file => !textFiles.Contains(file, StringComparer.Ordinal) && !CarriesCfeExemption(file))
            .ToList();

        await Assert.That(newFiles.Count).IsEqualTo(0)
            .Because(
                "a file the analyzer flags that neither the text rule nor a documented CFE0001 "
                + "exemption knows about is a file whose Result reads the tree has never been "
                + "judged against — the migration wave would go red on a site nobody reviewed. "
                + "Files: " + string.Join(" | ", newFiles));

        await Assert.That(analyzerSites.Count).IsLessThanOrEqualTo(MaxLiveFindings)
            .Because(
                "CFE0001 measures 35 production `.Value` reads and its model is COARSER than this "
                + "analyzer's, so the analyzer cannot report more sites than that without its "
                + "dominance model failing to recognise a guard shape the tree uses constantly "
                + "(early return, early continue, fail-fast throw, `&&` short circuit, property "
                + "pattern). Reported " + analyzerSites.Count + " site(s).");
    }

    // =====================================================================
    // Harness.
    // =====================================================================

    /// <summary>Compiles one synthetic source file as one assembly.</summary>
    private static CSharpCompilation Compile(string source, string assemblyName)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(
            source,
            new CSharpParseOptions(LanguageVersion.Preview),
            path: assemblyName + ".cs");

        return CSharpCompilation.Create(
            assemblyName,
            [tree],
            RoslynProjectCompilation.References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    /// <summary>Runs one analyzer over one compilation.</summary>
    private static async Task<List<Diagnostic>> RunAsync(CSharpCompilation compilation, DiagnosticAnalyzer analyzer)
    {
        CompilationWithAnalyzers withAnalyzers = compilation.WithAnalyzers(
            [analyzer],
            new AnalyzerOptions(ImmutableArray<AdditionalText>.Empty));

        ImmutableArray<Diagnostic> diagnostics = await withAnalyzers.GetAnalyzerDiagnosticsAsync();
        return [.. diagnostics];
    }

    /// <summary>
    ///     Every site the analyzer reports on the live tree, as
    ///     <c>repo-relative/path.cs:line</c>, sorted and distinct.
    /// </summary>
    private static async Task<List<string>> AnalyzerSitesAsync()
    {
        var sites = new List<string>();

        foreach ((_, CSharpCompilation compilation) in PilotRoslynAnalyzerTests.LiveCompilations.Value)
        {
            foreach (Diagnostic diagnostic in await RunAsync(compilation, new UnguardedResultReadAnalyzer()))
            {
                string? path = diagnostic.Location.SourceTree?.FilePath;
                if (path is null)
                {
                    continue;
                }

                int line = diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1;
                sites.Add(SourceScan.Relative(path) + ":" + line);
            }
        }

        return
        [
            .. sites.Distinct(StringComparer.Ordinal).OrderBy(static s => s, StringComparer.Ordinal)
        ];
    }

    /// <summary>The text rule's site, in the analyzer's site format.</summary>
    private static string TextSite(ScanHit hit) => hit.File + ":" + hit.Line;

    /// <summary>The file part of an analyzer site.</summary>
    private static string FileOf(string site)
    {
        int colon = site.LastIndexOf(':');
        return colon < 0 ? site : site[..colon];
    }

    /// <summary>
    ///     Whether the file already carries a documented CFE0001 exemption —
    ///     the tree's own statement that the file holds a Result read.
    /// </summary>
    private static bool CarriesCfeExemption(string relativePath)
    {
        if (RepoPaths.RepoRoot is not { } root)
        {
            return false;
        }

        string full = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(full))
        {
            return false;
        }

        return File.ReadAllText(full).Contains("#pragma warning disable CFE0001", StringComparison.Ordinal);
    }

    /// <summary>
    ///     Prints the differential table: both directions, so the PR body can
    ///     quote what the analyzer actually sees.
    /// </summary>
    private static void PrintDifferential(List<ScanHit> textHits, List<string> analyzerSites)
    {
        List<string> textSites = [.. textHits.Select(TextSite).Distinct(StringComparer.Ordinal).OrderBy(static s => s, StringComparer.Ordinal)];

        List<string> textOnly = [.. textSites.Where(site => !analyzerSites.Contains(site, StringComparer.Ordinal))];
        List<string> analyzerOnly = [.. analyzerSites.Where(site => !textSites.Contains(site, StringComparer.Ordinal))];

        Console.WriteLine(
            "[harch003] text rule: " + textSites.Count + " site(s); analyzer: " + analyzerSites.Count + " site(s)");
        Console.WriteLine("[harch003] text sites: " + string.Join(" | ", textSites));
        Console.WriteLine("[harch003] analyzer sites: " + string.Join(" | ", analyzerSites));
        Console.WriteLine("[harch003] text-only: " + string.Join(" | ", textOnly));
        Console.WriteLine("[harch003] analyzer-only: " + string.Join(" | ", analyzerOnly));
    }

    /// <summary>
    ///     The receiver a diagnostic names: the message format quotes the
    ///     member first and the receiver second.
    /// </summary>
    private static string ReceiverOf(string message)
    {
        List<string> quoted = QuotedParts(message);
        return quoted.Count > 1 ? quoted[1] : string.Empty;
    }

    /// <summary>Every quoted segment of a message, in order.</summary>
    private static List<string> QuotedParts(string message)
    {
        var parts = new List<string>();
        int index = 0;

        while (index < message.Length)
        {
            int open = message.IndexOf('\'', index);
            if (open < 0)
            {
                break;
            }

            int close = message.IndexOf('\'', open + 1);
            if (close < 0)
            {
                break;
            }

            parts.Add(message.Substring(open + 1, close - open - 1));
            index = close + 1;
        }

        return parts;
    }
}
