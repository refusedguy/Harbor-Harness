using System.Text.Json;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Harbor.Application.Tests.Fakes;
using Harbor.Application.Sessions;
using Harbor.TestKit;
using TUnit.Assertions;

namespace Harbor.Application.Tests;

// CachingSystemPromptBuilderKeyScalingTests.cs — #1532: the KEY the prompt
// cache is read by is a hot-path slice of every turn, so its SCALING is a
// merge gate, and the gate is RELATIVE to a measurement taken in the same run
// — the same convention #410 established for the store path and #1528 for
// pattern matching.
//
// WHAT #1532 FOUND
// ----------------
// The old ComputeKey joined EVERY context field with a StringBuilder (which
// itself doubled the context bytes) and then hashed `Encoding.UTF8.GetBytes
// (sb.ToString())` — one full prompt text per turn, one whole-context byte
// array per turn, both existing only to be hashed. It also kept a static
// `ConcurrentDictionary<JsonDocument, string> _schemaTextCache`: the KEY was
// the JsonDocument REFERENCE, a fresh document instance per tool resolution
// could never hit, so the map never served one hit and grew one entry per
// distinct document instance for the decorator's process-long lifetime.
//
// WHAT THIS FILE GATES (and does NOT)
// -----------------------------------
// The gate: hashing the context must stay byte-linear — doubling the context
// must not double-plus the per-turn cost. The rewrite streams every field's
// UTF-8 encoding into ONE IncrementalHash through a bounded ~243-byte stack
// buffer and hashes the schema as canonical JSON in place, so neither the
// joined prompt text nor the whole-context byte array exists anymore. A
// regression that re-materializes the intermediate string+byte[] — or adds a
// quadratic walk over the fields — lands at 4x and cannot hide under 3x,
// which is the standard #410 argument.
//
// The absolute milliseconds a run prints are informational (#618): only the
// ratios are gated, because an absolute wall-clock budget measures the runner
// (see StorePathScalingGates for the full #460/#980 argument).
//
// ISOLATION
// ---------
// Each leg measures `CachingSystemPromptBuilder.BuildAsync` over a PRE-BUILT
// context with a COUNTING fake as the inner builder, so the inner prompt
// assembly never contributes: the window is the key computation plus cache
// bookkeeping, which is exactly what #1532 slices. The builder is created
// per unit OUTSIDE the measured window — a fresh `ConcurrentDictionary`
// per unit keeps every unit a miss without a cache that grows `units`
// entries per leg — where that constant ctor cost lands equally on both
// legs and cancels in the ratio.
//
// The `[NotInParallel]` is the BARE form, and it is load-bearing (#1790). The
// time leg reads wall-clock elapsed, and elapsed time is not thread-local: a
// neighbouring test burning cores — or, under the server GC every CI job
// enables, a collection that pauses this thread mid-window — decides the
// ratio. Run 38129838108 (PR #1285) is the receipt: the same tree measured
// time x3.10 against the x3.00 limit while the same run measured alloc x1.99
// — linear memory with an inflated stopwatch, on a tree that had passed the
// identical binary forty minutes earlier. A NAMED key would not help: it
// excludes only same-key peers, and every other class in this assembly still
// runs alongside. See docs/TEST_PATTERNS.md §Parallelism.
[NotInParallel]
public class CachingSystemPromptBuilderKeyScalingTests
{
    /// <summary>Context-file payload, in UTF-16 chars (~64 KiB of context content).</summary>
    private const int BaseFileChars = 32_768;

    /// <summary>
    ///     The context size the gate MEASURES at - twice
    ///     <see cref="BaseFileChars" />, because a per-turn cost that grows
    ///     with the context must land in the measurement instead of hiding
    ///     behind the smaller one (the same argument
    ///     <c>ProjectHistoryScalingGates</c> makes).
    /// </summary>
    private const int MeasuredFileChars = BaseFileChars * 2;

    /// <summary>
    ///     Turns per measured leg - the FIXED amount of work. 1 000 turns at the
    ///     measured context size is about 65 ms of real work on the CI runner.
    /// </summary>
    private const int TurnsPerLeg = 1_000;

    /// <summary>
    ///     Allocation budget for ONE turn's key computation, in bytes.
    ///     Machine-independent, so it is compared as measured, with no band.
    ///     Pinned from the CI measurement of the ratio gate this replaced (run
    ///     39423151864): 2 954 192 B over 3 002 turns = 984 B per turn - and
    ///     the SAME number at twice the context size, which is the point: the
    ///     key streams the context through one bounded buffer, so its
    ///     allocation does not grow with the context. The budget leaves ~1.56x
    ///     headroom.
    /// </summary>
    private const long MaxBytesPerTurn = 1_536;

    /// <summary>
    ///     Time budget for ONE turn's key computation, in PROBE UNITS (see
    ///     <see cref="Harbor.TestKit.PerfGate" />). Machine-scaled and SOFT.
    ///     Pinned from the same run at the measured context size: 195.252 ms
    ///     over 3 002 turns = 65.0 us per turn against a probe that costs
    ///     0.000380 ms/unit, i.e. ~171 probe units per turn.
    ///     The budget is wide on purpose: the probe's OWN measured cost moved
    ///     5.3x between two CI runs of the SAME tree (0.000380 ms/unit in run
    ///     39423151864, 0.000072 ms/unit in run 39436405496), so the
    ///     probe-unit currency is only as stable as the yardstick behind it.
    ///     The BYTE budget is the branch that can fail on any runner - it is
    ///     exact - and the time budget is the gross-regression detector.
    /// </summary>
    private const double MaxProbeUnitsPerTurn = 2_400.0;

    /// <summary>One fixed real working directory for both legs (the key holds the path).</summary>
    private static readonly string WorkDir = TestTempDirs.NewDirectory("harbor-prompt-key-scaling");

    private static readonly CountingPromptBuilder Inner = new();

    private static readonly ModelInfo TestModel =
        new("test-model", "test", "Test Model", 200_000, 4096, false, false, true, Pricing.Unknown, "openai");

    /// <summary>
    ///     THE GATE IS A BUDGET PER UNIT OF WORK (#1948), not a ratio. It used
    ///     to be <c>t(2N) &lt;= 3*t(N)</c> and <c>alloc(2N) &lt;= 3*alloc(N)</c>
    ///     over a 32 768-char against a 65 536-char context, decided by the
    ///     shared TestScalingGate (#1652). It is now:
    ///     <code>
    ///     bytes per turn        &lt;= 1 536   (machine-independent, HARD)
    ///     probe units per turn  &lt;= 2 400   (machine-scaled time, SOFT)
    ///     </code>
    ///     measured over a FIXED amount of work (1 000 turns at the LARGER
    ///     context) on the SAME runner in the SAME run. The allocation branch
    ///     is machine-independent and is asserted hard; the time branch is in
    ///     PROBE UNITS and stands down when the rounds do not reproduce each
    ///     other. The report prints on every run, pass or fail.
    ///     <para>
    ///         A regression that re-materialises the intermediate string and
    ///         byte array - the #1532 pre-rewrite shape, StringBuilder +
    ///         GetBytes + HashData - allocates the whole context per turn and
    ///         lands far above the byte budget. A regression that re-walks the
    ///         fields many times lands above the probe-unit budget.
    ///     </para>
    /// </summary>
    [Test]
    [Timeout(300_000)]
    public async Task KeyCost_PerTurn_StaysWithinBudget()
    {
        PerfGateResult result = await PerfGate.MeasureAsync(
            work: turns => Task.FromResult(TurnLeg(MeasuredFileChars, turns)),
            units: TurnsPerLeg,
            options: new PerfGateOptions
            {
                Label = "prompt-key-cost",
                MaxBytesPerUnit = MaxBytesPerTurn,
                MaxProbeUnitsPerUnit = MaxProbeUnitsPerTurn,
            });

        await result.AssertWithinBudgetsAsync("system prompt key computation per turn");
    }

    /// <summary>
    ///     One measured leg: <paramref name="units" /> fresh-key turns over one
    ///     context file of <paramref name="fileChars" /> UTF-16 chars, elapsed
    ///     wall time and thread allocations summed over the repeats. The content
    ///     string and the context are built ONCE per leg, outside the measured
    ///     window, so the window is exactly the per-turn slice #1532 targets.
    /// </summary>
    private static PerfGateLeg TurnLeg(int fileChars, int units)
    {
        // Built OUTSIDE the measured window: an O(fileChars) string, identical
        // in kind and count for both legs (one per unit), so its cost cancels
        // in the ratio — the windows measure the key pass.
        string content = new('x', fileChars);
        var context = new SystemPromptContext(
            TestAgents.AllowAll(),
            TestModel,
            [],
            [new ContextFile("AGENTS.md", content)],
            [],
            null,
            WorkDir);

        double ms = 0.0;
        long allocated = 0;
        var sw = new System.Diagnostics.Stopwatch();
        for (int u = 0; u < units; u++)
        {
            // One fresh decorator per unit: its cache starts empty, so the turn
            // is a REAL miss (the key is computed, the inner fake answers).
            // The ctor stays outside the window on purpose — see the header.
            var caching = new CachingSystemPromptBuilder(Inner);
            long probed;
            sw.Restart();
            probed = AllocationProbe.MeasureThread(() =>
            {
                _ = caching.BuildAsync(context).GetAwaiter().GetResult();
            });
            sw.Stop();
            ms += sw.Elapsed.TotalMilliseconds;
            allocated += probed;
        }

        return new PerfGateLeg(ms, allocated);
    }

    // ── The key-hit contract — what a relative gate cannot assert ──

    /// <summary>
    ///     Same context served twice → ONE ComputeKey result and ONE inner
    ///     build. The issue asked for a hit-rate assertion; a 256 KiB context
    ///     file makes the case where it matters largest — the #792/#815 history
    ///     is a key that silently served the previous prompt.
    /// </summary>
    [Test]
    public async Task BuildAsync_SameContentsTwice_ServesOneBuild()
    {
        var inner = new CountingPromptBuilder();
        var caching = new CachingSystemPromptBuilder(inner);
        string content = new('a', 256 * 1024);
        var context = new SystemPromptContext(
            TestAgents.AllowAll(),
            TestModel,
            [],
            [new ContextFile("AGENTS.md", content)],
            [],
            null,
            WorkDir);

        string first = await caching.BuildAsync(context);
        string second = await caching.BuildAsync(context);

        await Assert.That(inner.BuildCalls).IsEqualTo(1);
        await Assert.That(caching.Misses).IsEqualTo(1);
        await Assert.That(caching.CacheHits).IsEqualTo(1);
        await Assert.That(first).IsEqualTo(second);
    }

    /// <summary>
    ///     The schema field is hashed as CANONICAL JSON: two documents whose
    ///     literal text differs only in whitespace print equal canonical bytes,
    ///     so their keys match and the second turn is a HIT. The old
    ///     reference-keyed `_schemaTextCache` (#1532) never served a hit at all,
    ///     and a raw-text key would have served a rebuild here.
    /// </summary>
    [Test]
    public async Task BuildAsync_SameSchemaDifferentWhitespace_ServesOneBuild()
    {
        const string firstSchema = """{"type":"object","properties":{"a":{"type":"string"}}}""";
        const string secondSchema =
            """{ "type" : "object" , "properties" : { "a" : { "type" : "string" } } }""";

        var inner = new CountingPromptBuilder();
        var caching = new CachingSystemPromptBuilder(inner);

        _ = await caching.BuildAsync(ContextWith(Tool("alpha", firstSchema)));
        _ = await caching.BuildAsync(ContextWith(Tool("alpha", secondSchema)));

        await Assert.That(inner.BuildCalls).IsEqualTo(1)
            .Because("the two schemas are the same VALUE tree; canonical JSON prints them as equal "
                   + "bytes, so the keys match and the second turn is a cache hit, not a rebuild.");
        await Assert.That(caching.CacheHits).IsEqualTo(1);
        await Assert.That(caching.Misses).IsEqualTo(1);
    }

    /// <summary>
    ///     The negative control for the test above: a DIFFERENT value tree must
    ///     not be served the previous prompt. Kept so the canonical-JSON
    ///     comparison cannot be weakened into a constant hit.
    /// </summary>
    [Test]
    public async Task BuildAsync_DifferentSchemaValue_RebuildsPrompt()
    {
        var inner = new CountingPromptBuilder();
        var caching = new CachingSystemPromptBuilder(inner);

        _ = await caching.BuildAsync(ContextWith(Tool("alpha", """{"type":"object"}""")));
        _ = await caching.BuildAsync(ContextWith(Tool("alpha", """{"type":"object","properties":{}}""")));

        await Assert.That(inner.BuildCalls).IsEqualTo(2);
    }

    private static ToolDescriptor Tool(string name, string schemaJson) => new(
        ToolName.Create(name),
        name,
        $"{name} description",
        JsonDocument.Parse(schemaJson),
        ExecutionMode.Parallel,
        null,
        []);

    private static SystemPromptContext ContextWith(ToolDescriptor tool) => new(
        TestAgents.AllowAll(),
        TestModel,
        [tool],
        [],
        [],
        null,
        WorkDir);

    /// <summary>Inner builder that counts invocations and returns a constant.</summary>
    private sealed class CountingPromptBuilder : ISystemPromptBuilder
    {
        public int BuildCalls => Volatile.Read(ref _buildCalls);

        private int _buildCalls;

        public Task<string> BuildAsync(SystemPromptContext context, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _buildCalls);
            return Task.FromResult("built");
        }
    }
}
