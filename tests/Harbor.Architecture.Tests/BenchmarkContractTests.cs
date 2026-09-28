// BenchmarkContractTests.cs — #408, slice 1 of #46.
//
// Every benchmark class in tests/Harbor.Benchmarks must carry a written
// measurement contract with seven fields:
//
//   Operation / Payload / StateReset / Drain / RetainedState /
//   AwaitSemantics / AllocAttribution
//
// The point is not ceremony: a number that nobody can attribute cannot answer
// "is this faster than before?", and a benchmark that silently carries state
// between iterations measures the wrong thing while looking fine. Review
// discipline does not survive a 30-file benchmark suite, so the contract is
// enforced here by enumerating the benchmark sources themselves.
//
// The checks are source-text based on purpose — they run without building the
// benchmark project (which pulls BenchmarkDotNet and ~20 Harbor projects) and
// they fail on the missing marker, not on a style opinion.

using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Source-level hygiene gate for <c>tests/Harbor.Benchmarks</c> (#408).
/// </summary>
public class BenchmarkContractTests
{
    /// <summary>
    ///     The seven fields every benchmark class must document. Matched as
    ///     <c>Field:</c> so the marker is greppable from a plain diff.
    /// </summary>
    private static readonly string[] RequiredContractMarkers =
    [
        "Operation:",
        "Payload:",
        "StateReset:",
        "Drain:",
        "RetainedState:",
        "AwaitSemantics:",
        "AllocAttribution:"
    ];

    /// <summary>
    ///     A class declaration at the start of a line (namespace-scoped, optionally
    ///     sealed/static/partial/abstract). Record and struct declarations are not
    ///     benchmark classes and are intentionally not matched.
    /// </summary>
    private static readonly Regex ClassDeclaration = new(
        @"^(?:public|internal)\s+(?:sealed\s+|static\s+|partial\s+|abstract\s+)*class\s+(?<name>\w+)",
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>
    ///     Benchmarks that append to a store, file, channel, pipe or cache across
    ///     iterations, and the cleanup each one therefore has to declare. This is the
    ///     #408 audit result — if a benchmark is added to the accumulating set it must
    ///     come with a cleanup, and if it is removed from it the doc has to say why it
    ///     does not accumulate.
    /// </summary>
    private static readonly (string ClassName, string RequiredMember)[] AccumulatingBenchmarks =
    [
        // .jsonl file, rewritten per iteration.
        ("JsonlSessionStoreBenchmark", "[IterationCleanup]"),
        // .jsonl file + fresh store per iteration.
        ("MessageConverterBenchmark", "[IterationCleanup]"),
        // SQLite .db file recreated per iteration.
        ("SqliteSessionStoreWalBenchmark", "[IterationCleanup]"),
        // Target file is mutated by ApplyPatch.
        ("PatchToolUnifiedDiffBenchmark", "[IterationCleanup]"),
        // Nobody reads the client pipes; pooled pipe memory would back-pressure.
        ("EventBroadcasterThroughputBenchmark", "[IterationCleanup]"),
        // Same, for the delivery-contract split.
        ("EventBroadcasterDeliveryBenchmark", "[IterationCleanup]"),
        // A single Pipe is shared by write-then-read rows.
        ("IpcFramingBenchmark", "[IterationCleanup]"),
        // Every row allocates a disposable StreamingCoalescer and overwrites the field.
        ("StreamingCoalescerBenchmark", "[IterationCleanup]"),
        // Build_Miss mints a unique cache key per call, so the cache would grow forever.
        ("CachingPromptBenchmark", "[IterationSetup]"),
        // Process-wide static workspace cache + a temp tree of files.
        ("WorkspaceContextSourceBenchmark", "[GlobalCleanup]")
    ];

    /// <summary>
    ///     The three bus benchmark files and the three separately-named delivery rows
    ///     each must expose (#408): enqueue-only, enqueue + consumer drain, steady state.
    /// </summary>
    private static readonly (string FileName, string[] RequiredMethods)[] BusDeliveryFiles =
    [
        ("EventBusBenchmark.cs", ["EnqueueOnly", "EnqueueAndDrainConsumer", "SteadyState"]),
        ("EventBusScrollbackBenchmark.cs", ["EnqueueOnly", "EnqueueAndDrainConsumer", "SteadyState"]),
        ("EventBroadcasterThroughputBenchmark.cs", ["EnqueueOnly", "EnqueueAndDrainConsumer", "SteadyState"])
    ];

    [Test]
    public async Task EveryBenchmarkClass_DeclaresTheSevenFieldContract()
    {
        var missing = new List<string>();

        foreach (BenchmarkClass benchmark in BenchmarkClasses())
        {
            foreach (string marker in RequiredContractMarkers)
            {
                if (!benchmark.DocRegion.Contains(marker, StringComparison.Ordinal))
                {
                    missing.Add($"{benchmark.FileName}::{benchmark.ClassName} — missing '{marker}'");
                }
            }
        }

        await Assert.That(missing).IsEmpty()
            .Because(
                "Every benchmark class must document all 7 contract fields in its OWN doc comment "
                + "(#408). Add the missing marker next to the existing contract block.");
    }

    [Test]
    public async Task ContractScanner_FindsEveryBenchmarkClass()
    {
        // Guards the scanner itself: if the class-declaration regex or the
        // doc-region heuristic ever breaks, the tests above would pass vacuously
        // with zero classes inspected.
        var classes = BenchmarkClasses();

        await Assert.That(classes.Count).IsGreaterThanOrEqualTo(30)
            .Because("tests/Harbor.Benchmarks currently holds 30+ benchmark classes.");

        var names = classes.Select(c => c.ClassName).ToHashSet(StringComparer.Ordinal);

        // One class per file at minimum, plus the multi-class files.
        string[] expected =
        [
            "AgentLoopBenchmark",
            "EventBusBenchmark",
            "EventBusDeliveryBenchmark",
            "EventBusScrollbackBenchmark",
            "EventBusScrollbackDeliveryBenchmark",
            "EventBusContentionBenchmark",
            "EventBroadcasterThroughputBenchmark",
            "EventBroadcasterDeliveryBenchmark",
            "ToolRegistryBenchmark",
            "PermissionRulesetBenchmark",
            "ProviderRegistryBenchmark",
            "JsonlSessionStoreBenchmark",
            "JsonlParseBenchmark",
            "StringBuilderPoolBenchmark",
            "BodyLinesBenchmark",
            "WorkspaceContextSourceBenchmark",
            "CachingPromptBenchmark"
        ];

        var notFound = expected.Where(n => !names.Contains(n)).ToList();

        await Assert.That(notFound).IsEmpty()
            .Because("The contract scanner must see these benchmark classes; a shrink here means the scanner, not the benchmarks, regressed.");
    }

    [Test]
    public async Task BusBenchmarks_ExposeTheThreeDeliveryRows()
    {
        var problems = new List<string>();

        foreach ((string fileName, string[] required) in BusDeliveryFiles)
        {
            string text = ReadBenchmarksSource(fileName);

            foreach (string method in required)
            {
                if (!Regex.IsMatch(text, $@"\[Benchmark\([^\]]*\)\]\s*public[^\n]*\b{method}\s*\("))
                {
                    problems.Add($"{fileName}: no [Benchmark] method named '{method}'");
                }
            }

            // The enqueue-only row must say out loud that it says nothing about
            // delivery — that is the entire reason the row exists separately.
            if (!text.Contains("nothing about", StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"{fileName}: the enqueue-only row does not state that it measures nothing about delivery");
            }
        }

        await Assert.That(problems).IsEmpty()
            .Because("#408: each bus benchmark file must expose EnqueueOnly / EnqueueAndDrainConsumer / SteadyState as separate rows.");
    }

    [Test]
    public async Task AccumulatingBenchmarks_DeclareCleanup()
    {
        var classes = BenchmarkClasses().ToDictionary(c => c.ClassName, StringComparer.Ordinal);
        var problems = new List<string>();

        foreach ((string className, string requiredMember) in AccumulatingBenchmarks)
        {
            if (!classes.TryGetValue(className, out BenchmarkClass benchmark))
            {
                problems.Add($"{className}: class not found in tests/Harbor.Benchmarks (rename it or drop it from the audit table)");
                continue;
            }

            if (!benchmark.BodyRegion.Contains(requiredMember, StringComparison.Ordinal))
            {
                problems.Add($"{className}: accumulates state across iterations but declares no {requiredMember}");
            }
        }

        await Assert.That(problems).IsEmpty()
            .Because("#408: a benchmark that appends to a store/list/channel/file/cache must reset or drain it between iterations.");
    }

    [Test]
    public async Task IterationSetup_AlwaysHasAMatchingCleanup()
    {
        var problems = new List<string>();

        foreach (BenchmarkClass benchmark in BenchmarkClasses())
        {
            if (!benchmark.BodyRegion.Contains("[IterationSetup]", StringComparison.Ordinal))
            {
                continue;
            }

            if (benchmark.BodyRegion.Contains("[IterationCleanup]", StringComparison.Ordinal)
                || benchmark.BodyRegion.Contains("[GlobalCleanup]", StringComparison.Ordinal))
            {
                continue;
            }

            problems.Add($"{benchmark.FileName}::{benchmark.ClassName} — has [IterationSetup] but no [IterationCleanup]/[GlobalCleanup]");
        }

        await Assert.That(problems).IsEmpty()
            .Because("Resetting state per iteration without a matching cleanup only moves the accumulation.");
    }

    /// <summary>
    ///     Every class in <c>tests/Harbor.Benchmarks</c> that owns at least one
    ///     <c>[Benchmark]</c> method, split into the doc region above the declaration
    ///     (where the contract must live) and the body below it.
    /// </summary>
    private static List<BenchmarkClass> BenchmarkClasses()
    {
        var result = new List<BenchmarkClass>();

        foreach (string path in Directory.GetFiles(BenchmarksDirectory(), "*.cs").OrderBy(p => p, StringComparer.Ordinal))
        {
            string text = NormalizeNewlines(File.ReadAllText(path));
            MatchCollection matches = ClassDeclaration.Matches(text);
            string fileName = Path.GetFileName(path);

            for (int i = 0; i < matches.Count; i++)
            {
                Match match = matches[i];
                int declarationStart = match.Index;

                // A class "owns" the text from its declaration to the next class
                // declaration (or EOF). Helper types that follow a benchmark class
                // therefore get their own block and are not counted as benchmarks.
                int bodyEnd = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
                string body = text[declarationStart..bodyEnd];
                if (!body.Contains("[Benchmark", StringComparison.Ordinal))
                {
                    continue;
                }

                // The doc region starts after the last blank line above the
                // declaration, so the contract is attributed to the class whose
                // doc comment actually carries it.
                int blankLine = text.LastIndexOf("\n\n", declarationStart, StringComparison.Ordinal);
                int docStart = blankLine < 0 ? 0 : blankLine + 2;
                string doc = text[docStart..declarationStart];

                result.Add(new BenchmarkClass(fileName, match.Groups["name"].Value, doc, body));
            }
        }

        return result;
    }

    private static string BenchmarksDirectory()
    {
        // Walk up from the test host output directory to the repository root.
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "tests", "Harbor.Benchmarks"))
                && File.Exists(Path.Combine(dir.FullName, "Harbor.slnx")))
            {
                return Path.Combine(dir.FullName, "tests", "Harbor.Benchmarks");
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Repository root not found — cannot resolve tests/Harbor.Benchmarks. "
            + "The benchmark-contract gate needs the sources, so it fails loudly instead of passing vacuously.");
    }

    private static string ReadBenchmarksSource(string fileName)
    {
        string path = Path.Combine(BenchmarksDirectory(), fileName);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"Missing benchmark source: {fileName}");
        }

        return NormalizeNewlines(File.ReadAllText(path));
    }

    /// <summary>
    ///     Normalizes CRLF to LF before the source is scanned. The block boundaries are
    ///     detected by blank lines, and a blank line in a CRLF file is <c>"\r\n\r\n"</c> —
    ///     without this the doc region of a CRLF file would silently extend over the whole
    ///     file and the per-class attribution below would stop meaning anything.
    /// </summary>
    private static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal);

    /// <summary>
    ///     One benchmark class: its name, its doc region, and its body.
    /// </summary>
    private readonly record struct BenchmarkClass(string FileName, string ClassName, string DocRegion, string BodyRegion);
}
