// GateGlowConsumerReachabilityRule.cs — issue #889. The gate-glow pipeline has a
// producer with a test and a consumer with none, and the missing half is the half
// the user can see.
//
// THE HOLE
// --------
// #648 built the gate glow: a pending approval gate pulses, the timeline publishes
// a glow region per frame, and the REPL's frame loop arms the session's post-fx
// slot table from that ledger. The two halves live in different assemblies and the
// tests only ever reached one of them:
//
//   * PRODUCER — `VirtualizedChatTimeline.ConsumeGlowRegions`, in
//     `src/Harbor.Tui.CellForge`. Covered: `PostFxTests.Timeline_PublishesGateGlowRegions_AndStopsOnDecision`
//     asserts the ledger reports one region while the gate pings and ZERO after the
//     decision. That test is the one that flaked (#648), was diagnosed as a palette
//     race, and was fixed in #703. It is a good test and it is on the wrong half.
//
//   * CONSUMER — `ReplLifecycle.ArmGateGlow`, in `apps/Harbor.App.Cli`, which turns
//     the ledger into `ScreenSession.Effects.Set(i, …)` and then DRAINS the slots the
//     ledger stopped feeding. Zero occurrences of `ArmGateGlow`, `_glowScratch` or
//     `_glowEffects` anywhere under `tests/`, which is the whole of the coverage.
//
// WHY THE MISSING HALF IS THE EXPENSIVE ONE
// -----------------------------------------
// `PostFxPipeline` holds a FIXED slot table (`IPostEffect?[] _slots`, 8 entries) and
// `Flush` runs every armed effect whose region contains the cell. Nothing resets the
// table between frames. So the drain loop at the tail of `ArmGateGlow` —
//
//     for (int i = count; i < VirtualizedChatTimeline.MaxFxDamage; i++)
//         host.ScreenSession.Effects.Set(i, null);
//
// — is the ONLY thing standing between "the gate was answered" and "the gate glows
// forever". Delete those three lines and every test in `PostFxTests` stays green,
// and the reason is worth spelling out because none of them is dumb:
// `ArmedGlow_TransformsEmittedStyle_AndMirrorsTerminalView` does disarm a glow and
// does assert that the plain cell is repainted — but it disarms by writing
// `intensity: 0.0` into the effect, which is a different mechanism from removing the
// slot, so it exercises `GlowEffect.Transform`'s guard and not the consumer's drain.
// `Set_Clear_MaintainSlotBookkeeping` calls `Clear()` on its own pipeline and asserts
// `Count`, never bytes. `ScreenSession_ArmsEffectsThroughFlushFrame` arms nothing at
// all, so its "Effects empty → classic path" comment describes a state that holds
// whether or not the pipeline exists. A green suite over a hole is the expensive kind
// of green.
//
// WHAT THIS RULE IS, AND WHY IT IS A REACHABILITY RULE
// ----------------------------------------------------
// The obvious guard is a source-text pin on that loop: assert the file still contains
// `Effects.Set(i, null)`. It is rejected here because it answers the wrong question.
// It says "these three lines are still written", which is not the property anyone
// cares about; the property is "if those three lines disappear, something goes red".
// A text pin cannot be that property, because the failure it prevents is a deletion —
// and a guard that only notices a deletion still present is a comment.
//
// So the rule is the stronger claim: EVERY method in the product that turns the glow
// ledger into post-fx slot writes must be named by at least one test. That makes the
// drain untouchable in the only way that matters — delete the loop and the test that
// drives the real method goes red on its own — and it answers the question #889 was
// actually opened to ask, which is whether the consumer is reachable from a test
// project at all. The answer, measured: the consumer is in `apps/`, and the one test
// project that references `apps/` is `Harbor.App.Cli.Tests` (via the
// `InternalsVisibleTo` already declared in `Harbor.App.Cli.csproj`), so it is. No new
// test project, no new axis (#555), and the method itself had to be opened from
// `private` to `internal` — a one-word change to a class that already carries
// test-visibility seams (`BuildStatusSnapshot`, `ShouldKeepHeartbeat`, `Utf8`).
//
// NON-VACUITY, AND THE SECOND "CANNOT SEE"
// ----------------------------------------
// The measurement below has to read `tests/`, and the shared helper cannot:
// `SourceScan.IsBuildOutput` rejects any path containing `/tests/`, so
// `SourceScan.EnumerateCsFiles("tests")` returns an EMPTY LIST BY CONSTRUCTION
// (#877 measured this and deliberately did not change it). A reachability rule built
// on that helper would report "no test drives the consumer" forever, in every
// configuration, and read exactly like a clean bill of health. So this walk uses
// `DiffSurfaceNameCollisionProbe.EnumerateFiles` — the prune-don't-filter walk, whose
// skip list never named `tests` in the first place — and
// `TheWalkCanSeeTheTestTree_AndTheSharedHelperCannot` pins BOTH answers, so a future
// refactor that swaps the walk back to `SourceScan` turns this rule into a green
// light wired to nothing. That is the same non-vacuity contract
// `ContribBoundaryNameRule` uses for the `contrib/` side, and it is deliberately the
// same shape: assert the far side produced files, and assert it in the same test that
// consumes the number.
//
// WHAT THIS FILE IS NOT
// ---------------------
//   * Not a second `SourceScan` decision. #877 declared the `/tests/` clause correct
//     for product-scanning rules and measured the cost of changing it; this file
//     neither re-argues that nor changes it. It picks the walk that can see the tree
//     it needs and says so.
//   * Not a second copy of #872's typing-gate rule or #885's failure-text parity
//     rule. Different subject, different seam.
//   * Not a re-test of the producer. The ledger already has a test; duplicating it
//     would be the same "13 tests green in vain" shape this issue is about.
//   * Not a pin on the drain loop's TEXT. The arm/disarm call counts appear in the
//     failure message as evidence a human reads; they are not an assertion. The
//     assertion is reachability, and the drain's semantics are carried by the
//     behavioural test that calls the real method.

using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>One product method that turns the glow ledger into post-fx slot writes.</summary>
/// <param name="Method">The enclosing method's name — what a test has to name.</param>
/// <param name="File">Repo-relative declaration file.</param>
/// <param name="Line">1-based line of the ledger read inside it.</param>
/// <param name="ArmCalls">How many of the FILE's slot writes carry a non-null effect.</param>
/// <param name="DisarmCalls">How many of the FILE's slot writes carry <c>null</c>.</param>
/// <param name="NamedBy">Repo-relative test files whose code names <paramref name="Method" />.</param>
internal sealed record GateGlowConsumer(
    string Method,
    string File,
    int Line,
    int ArmCalls,
    int DisarmCalls,
    IReadOnlyList<string> NamedBy);

/// <summary>Everything this measurement needs from one repository scan.</summary>
/// <param name="Consumers">Every ledger-to-slot-writer method found, sorted by file then line.</param>
/// <param name="ProductFilesRead">How many <c>.cs</c> files the <c>src/</c>+<c>apps/</c> side contributed.</param>
/// <param name="TestFilesRead">How many <c>.cs</c> files the <c>tests/</c> side contributed.</param>
/// <param name="SharedHelperTestFilesRead">
///     How many <c>.cs</c> files the SHARED <c>SourceScan</c> helper returns for <c>tests</c>. Zero by
///     construction, and asserted as zero so the two walks cannot be confused for one another.
/// </param>
internal sealed record GateGlowConsumerReport(
    IReadOnlyList<GateGlowConsumer> Consumers,
    int ProductFilesRead,
    int TestFilesRead,
    int SharedHelperTestFilesRead);

/// <summary>Finds the gate-glow ledger consumers in the product and who names them in tests.</summary>
internal static class GateGlowConsumerProbe
{
    /// <summary>
    ///     Roots walked in one pass. <c>tests</c> is here rather than scanned separately because the
    ///     two sides have to be read by the SAME walk: a product-side hit with no test-side read
    ///     behind it is indistinguishable from a test-side read that found nothing.
    /// </summary>
    internal static readonly string[] Roots = ["src", "apps", "tests"];

    /// <summary>The ledger read. Presence marks a file as a glow consumer candidate.</summary>
    private const string LedgerRead = "ConsumeGlowRegions(";

    /// <summary>The slot write. Paired with <see cref="LedgerRead" />, it marks the consumer.</summary>
    private const string SlotWrite = "Effects.Set(";

    /// <summary>
    ///     Identifiers that can precede a parameter list without being a declaration — control flow
    ///     and expression keywords. A line ending in <c>)</c> is a candidate declaration; this set
    ///     removes the ones that are not.
    /// </summary>
    private static readonly HashSet<string> NotADeclaration = new(StringComparer.Ordinal)
    {
        "if", "for", "foreach", "while", "switch", "catch", "lock", "using", "fixed", "do",
        "return", "throw", "await", "yield", "new", "case", "default", "is", "as", "in", "out",
        "ref", "from", "into", "let", "when", "where", "base", "this", "nameof", "typeof",
        "sizeof", "checked", "unchecked", "stackalloc", "record", "init", "get", "set", "value",
    };

    /// <summary>
    ///     Tokens that disqualify the text BEFORE the candidate name, which is what separates
    ///     <c>private void ArmGateGlow()</c> from <c>return Wrap(x);</c>. Generic return types are
    ///     deliberately NOT in the set — <c>Task&lt;int&gt;</c> is a legitimate prefix.
    /// </summary>
    private static readonly Regex StatementPrefix =
        new(@"\b(return|await|throw|yield|new|case|default|is|as|out|ref|in|when|where|base|this|"
          + @"nameof|typeof|sizeof|checked|unchecked|stackalloc|record|init|value)\b|=>|=",
            RegexOptions.Compiled);

    /// <summary>Walks the repository. A missing checkout scans nothing, which the rule reports.</summary>
    internal static GateGlowConsumerReport Scan(string? repoRoot)
    {
        if (repoRoot is null || !Directory.Exists(repoRoot))
        {
            return new GateGlowConsumerReport([], 0, 0, 0);
        }

        var sources = new List<(string Relative, string[] Stripped)>();
        foreach (string file in DiffSurfaceNameCollisionProbe.EnumerateFiles(repoRoot, Roots, "*.cs"))
        {
            string[] lines;
            try
            {
                lines = File.ReadAllLines(file);
            }
            catch (IOException)
            {
                continue;
            }

            sources.Add((
                DiffSurfaceNameCollisionProbe.MakeRelative(repoRoot, file),
                SourceCommentStripper.StripAll(lines)));
        }

        int sharedHelperTestFiles = SourceScan.EnumerateCsFiles("tests").Count;
        return ScanFiles(sources, sharedHelperTestFiles);
    }

    /// <summary>
    ///     Grades already-read files. Exposed so the positive control drives the REAL matcher —
    ///     comment stripping and declaration detection included — rather than a second
    ///     implementation of it, which is the only way "it can fail" means anything.
    /// </summary>
    internal static GateGlowConsumerReport ScanFiles(
        List<(string Relative, string[] Stripped)> sources,
        int sharedHelperTestFiles = 0)
    {
        var testNames = new List<(string Relative, string Text)>();
        var found = new List<GateGlowConsumer>();

        foreach (var source in sources)
        {
            string joined = string.Join("\n", source.Stripped);

            if (IsTestFile(source.Relative))
            {
                testNames.Add((source.Relative, joined));
                continue;
            }

            if (!joined.Contains(LedgerRead, StringComparison.Ordinal)
                || !joined.Contains(SlotWrite, StringComparison.Ordinal))
            {
                continue;
            }

            int readLine = LineOfFirst(source.Stripped, LedgerRead);
            string? method = EnclosingMethod(source.Stripped, readLine);
            if (method is null)
            {
                continue;
            }

            found.Add(new GateGlowConsumer(
                method,
                source.Relative,
                readLine,
                CountSlotWrites(source.Stripped, disarm: false),
                CountSlotWrites(source.Stripped, disarm: true),
                []));
        }

        var consumers = new List<GateGlowConsumer>();
        foreach (GateGlowConsumer consumer in found)
        {
            var namedBy = new List<string>();
            foreach ((string relative, string text) in testNames)
            {
                if (NamesWholeWord(text, consumer.Method))
                {
                    namedBy.Add(relative);
                }
            }

            consumers.Add(consumer with { NamedBy = namedBy });
        }

        consumers.Sort(static (a, b) =>
        {
            int byFile = string.CompareOrdinal(a.File, b.File);
            return byFile != 0 ? byFile : a.Line.CompareTo(b.Line);
        });

        return new GateGlowConsumerReport(consumers, sources.Count - testNames.Count, testNames.Count, sharedHelperTestFiles);
    }

    /// <summary>Whether a repo-relative path is on the <c>tests/</c> side. Keyed on the first segment.</summary>
    private static bool IsTestFile(string relative) =>
        relative.StartsWith("tests/", StringComparison.Ordinal);

    /// <summary>1-based line of the first line containing <paramref name="needle" />, or 0.</summary>
    private static int LineOfFirst(string[] stripped, string needle)
    {
        for (int i = 0; i < stripped.Length; i++)
        {
            if (stripped[i].Contains(needle, StringComparison.Ordinal))
            {
                return i + 1;
            }
        }

        return 0;
    }

    /// <summary>
    ///     The innermost declaration preceding <paramref name="line" /> — the LAST one before it by
    ///     position, which is what makes a local function win over its enclosing method. No brace
    ///     matching is involved, deliberately: counting braces correctly across string literals is a
    ///     second parser, and the property that matters here ("who names the method a test must
    ///     call") does not need one.
    /// </summary>
    internal static string? EnclosingMethod(string[] stripped, int line)
    {
        string? best = null;
        int limit = Math.Clamp(line, 0, stripped.Length);
        for (int i = 0; i < limit; i++)
        {
            if (TryDeclarationName(stripped[i], out string name))
            {
                best = name;
            }
        }

        return best;
    }

    /// <summary>
    ///     Whether a line is a declaration head — <c>head(params)</c> and nothing after it — and, if
    ///     so, the identifier immediately before the parameter list.
    /// </summary>
    /// <remarks>
    ///     The "nothing after it" half does the work, and it is checked by the line's terminator
    ///     rather than by looking for a <c>{</c> on the next line. Every statement that ends in a call
    ///     ends in a semicolon in this codebase, so that alone separates a declaration from
    ///     <c>host.ScreenSession.Effects.Set(i, null);</c> and from <c>ArmGateGlow();</c> — the two
    ///     lines that sit either side of the ledger read and would otherwise be the last
    ///     "declaration" seen before it. Not hunting for the brace also means a declaration split
    ///     across lines by an attribute is not silently mis-attributed, which is a failure a brace
    ///     check would introduce without removing any.
    /// </remarks>
    private static bool TryDeclarationName(string line, out string name)
    {
        name = string.Empty;
        string trimmed = line.Trim();
        if (trimmed.Length == 0 || !trimmed.EndsWith(')'))
        {
            return false;
        }

        int open = trimmed.LastIndexOf('(');
        if (open <= 0)
        {
            return false;
        }

        string head = trimmed[..open].TrimEnd();
        int start = head.Length;
        while (start > 0 && (char.IsLetterOrDigit(head[start - 1]) || head[start - 1] == '_'))
        {
            start--;
        }

        if (start >= head.Length)
        {
            return false;
        }

        string candidate = head[start..];
        if (NotADeclaration.Contains(candidate) || StatementPrefix.IsMatch(head[..start]))
        {
            return false;
        }

        name = candidate;
        return true;
    }

    /// <summary>
    ///     How many of the file's slot writes carry <c>null</c> (or, for <paramref name="disarm" />
    ///     false, do not). Reported as evidence in the failure message; asserted by nothing here.
    /// </summary>
    private static int CountSlotWrites(string[] stripped, bool disarm)
    {
        int count = 0;
        foreach (string line in stripped)
        {
            int at = line.IndexOf(SlotWrite, StringComparison.Ordinal);
            if (at < 0)
            {
                continue;
            }

            bool isDisarm = line[(at + SlotWrite.Length)..].Contains("null", StringComparison.Ordinal);
            if (isDisarm == disarm)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Whether <paramref name="text" /> names <paramref name="name" /> as a WHOLE word.</summary>
    private static bool NamesWholeWord(string text, string name) =>
        Regex.IsMatch(text, $@"(?<![A-Za-z0-9_]){Regex.Escape(name)}(?![A-Za-z0-9_])", RegexOptions.CultureInvariant);
}

/// <summary>
///     Requires every product method that translates the gate-glow ledger into post-fx slot writes to
///     be named by a test, so the slot drain cannot be deleted without a red run.
/// </summary>
public sealed class GateGlowConsumerReachabilityRule
{
    private static readonly Lazy<GateGlowConsumerReport> Report = new(
        () => GateGlowConsumerProbe.Scan(RepoPaths.RepoRoot));

    // =====================================================================
    // 1. The rule. Red on arrival, green once the consumer is driven.
    // =====================================================================

    /// <summary>
    ///     Every gate-glow consumer is named by at least one test file.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is the assertion #889 was opened for. <c>PostFxTests</c> covers the ledger
    ///         (<c>VirtualizedChatTimeline.ConsumeGlowRegions</c>) and nothing covers the method that
    ///         consumes it, so the loop which disarms the slots the ledger stopped feeding can be
    ///         deleted with a fully green suite — and a persistent, undrained
    ///         <c>PostFxPipeline</c> paints every armed effect on every <c>Flush</c>, which is a gate
    ///         that glows forever after it has been answered.
    ///     </para>
    ///     <para>
    ///         Asserted as a list of <c>Method</c> names rather than a count, so deleting a consumer
    ///         cannot be masked by adding another, and so the failure names the method a test has to
    ///         call instead of printing a number. The message is the useful part: it reports where
    ///         the consumer is declared, how many of its slot writes arm and how many drain, and how
    ///         many files each side of the walk produced.
    ///     </para>
    /// </remarks>
    [Test]
    public async Task EveryGateGlowConsumer_IsNamedByATest()
    {
        var report = Report.Value;
        var untested = report.Consumers.Where(c => c.NamedBy.Count == 0).ToArray();

        await Assert.That(string.Join(" | ", untested.Select(Describe)))
            .IsEqualTo(string.Empty)
            .Because(
                "a product method that turns the gate-glow ledger into post-fx slot writes has NO test "
                + "naming it. The ledger's PRODUCER half is covered (PostFxTests asserts the region "
                + "count goes to zero once the gate is decided); the CONSUMER half is "
                + "ReplLifecycle.ArmGateGlow, and nothing under tests/ mentions it. That matters more "
                + "than an average uncovered method because PostFxPipeline's slot table is persistent "
                + "and Flush runs every armed effect, so the drain loop at the tail of ArmGateGlow is "
                + "the only thing that turns the glow off. Delete it and PostFxTests stays green while "
                + "a user watches an answered gate keep pulsing — which is the second half of #648, "
                + "uncovered. Untested consumers: " + untested.Length + " of " + report.Consumers.Count
                + ". Files read — product: " + report.ProductFilesRead + ", tests: " + report.TestFilesRead
                + ". All consumers: " + string.Join(" | ", report.Consumers.Select(Describe)));

        // Non-vacuity, in the same test as the number it qualifies: a rule that found no consumer at
        // all would satisfy the assertion above for the wrong reason, and a silent rename of the
        // seam would look exactly like the defect being fixed.
        await Assert.That(report.Consumers).IsNotEmpty()
            .Because(
                "the gate-glow seam is measurable — one product method reads ConsumeGlowRegions and "
                + "writes ScreenSession.Effects slots. If this is empty, either the seam was renamed "
                + "(update the two literals in GateGlowConsumerProbe) or the product tree stopped being "
                + "read, and in both cases the assertion above is green while checking nothing. "
                + "Files read — product: " + report.ProductFilesRead + ", tests: " + report.TestFilesRead);
    }

    // =====================================================================
    // 2. Non-vacuity. The walk has to be able to see tests/.
    // =====================================================================

    /// <summary>
    ///     The walk that finds test-side callers really reads <c>tests/</c> — and the shared
    ///     <c>SourceScan</c> helper still cannot, which is why this file does not use it.
    /// </summary>
    /// <remarks>
    ///     <c>SourceScan.IsBuildOutput</c> rejects any path containing <c>/tests/</c>, so
    ///     <c>SourceScan.EnumerateCsFiles("tests")</c> returns an empty list by construction (#877
    ///     measured this and deliberately left the filter alone, because every shipped guard built on
    ///     that helper is written for product code). A reachability rule built on it would report
    ///     "no test drives the consumer" forever and read like a clean tree. This file therefore walks
    ///     with <c>DiffSurfaceNameCollisionProbe.EnumerateFiles</c>, whose skip list never named
    ///     <c>tests</c>. Pinning BOTH counts is what stops a future refactor from quietly swapping
    ///     one walk for the other and turning the rule into a green light wired to nothing.
    /// </remarks>
    [Test]
    public async Task TheWalkCanSeeTheTestTree_AndTheSharedHelperCannot()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("the measurement needs a repository checkout; without one both sides read zero "
                   + "files and the rule above is satisfied by having looked at nothing");

        var report = Report.Value;

        await Assert.That(report.TestFilesRead).IsGreaterThan(0)
            .Because(
                "this rule's entire subject is the test side. A zero here is the failure mode the file "
                + "was written against: every consumer would be reported as untested because nothing "
                + "was read, which is indistinguishable from a genuine hole. Files read — product: "
                + report.ProductFilesRead + ", tests: " + report.TestFilesRead);

        await Assert.That(report.ProductFilesRead).IsGreaterThan(0)
            .Because(
                "a reachability rule needs two sides. If the product side read nothing there would be "
                + "no consumers to find and the rule would pass over an empty subject. Files read — "
                + "product: " + report.ProductFilesRead + ", tests: " + report.TestFilesRead);

        await Assert.That(report.SharedHelperTestFilesRead).IsEqualTo(0)
            .Because(
                "the shared SourceScan helper is expected to return ZERO files for tests/ — "
                + "IsBuildOutput rejects any path containing /tests/, which is what makes it the wrong "
                + "walk for this rule and the right one for the 15 product-scanning guards #877 counted. "
                + "This assertion is a tripwire, not a request: if it goes red, SourceScan's filter "
                + "changed and the boundary this file depends on moved. That is a reviewed decision "
                + "with its own blast radius (pointing product-scanning rules at tests/ would make every "
                + "planted positive control fail every other gate), not something to paper over here. "
                + "Actual count: " + report.SharedHelperTestFilesRead);
    }

    // =====================================================================
    // 3. The positive control.
    // =====================================================================

    /// <summary>
    ///     The real probe, driven over synthetic sources: it finds the consumer and names its
    ///     enclosing method, and it rejects three shapes that look like one.
    /// </summary>
    /// <remarks>
    ///     Reusing <see cref="GateGlowConsumerProbe.ScanFiles" /> is the point — a hand-written
    ///     matcher in the control would prove the control, not the rule. The three decoys are the ways
    ///     this rule could go green for free: a file that writes post-fx slots without touching the
    ///     glow ledger, a ledger read that exists only in a comment, and a slot write that exists only
    ///     in a comment. The nested local function is a fourth case and it pins the attribution
    ///     choice: a call inside a local function belongs to the local, because that is the method a
    ///     test would have to call.
    /// </remarks>
    [Test]
    public async Task TheConsumerProbe_FindsTheRealShape_AndRejectsTheDecoys()
    {
        var report = GateGlowConsumerProbe.ScanFiles(
        [
            // The real shape, transcribed: a ledger read followed by slot writes in the same method.
            ("apps/Harbor.App.Cli/Repl/Probe.cs",
            [
                "namespace Harbor.App.Cli.Repl;",
                "internal sealed class Probe",
                "{",
                "    private void ArmGateGlow()",
                "    {",
                "        int count = host._timeline.ConsumeGlowRegions(host._glowScratch);",
                "        for (int i = 0; i < count; i++)",
                "        {",
                "            host.ScreenSession.Effects.Set(i, host._glowEffects[i]);",
                "        }",
                "",
                "        for (int i = count; i < 8; i++)",
                "        {",
                "            host.ScreenSession.Effects.Set(i, null);",
                "        }",
                "    }",
                "}",
            ]),

            // Decoy 1: writes post-fx slots, never reads the glow ledger. Not a glow consumer.
            ("apps/Harbor.App.Cli/Repl/ProbeSlotsOnly.cs",
            [
                "namespace Harbor.App.Cli.Repl;",
                "internal sealed class ProbeSlotsOnly",
                "{",
                "    private void SomethingElse()",
                "    {",
                "        host.ScreenSession.Effects.Set(0, null);",
                "    }",
                "}",
            ]),

            // Decoy 2: the ledger read exists only in prose. Not a consumer.
            ("apps/Harbor.App.Cli/Repl/ProbeDocOnly.cs",
            [
                "namespace Harbor.App.Cli.Repl;",
                "internal sealed class ProbeDocOnly",
                "{",
                "    /// <summary>Call ConsumeGlowRegions( here once, and also Effects.Set( on the next line.</summary>",
                "    private void RealWork()",
                "    {",
                "        // ConsumeGlowRegions(regions);",
                "        // host.ScreenSession.Effects.Set(0, null);",
                "    }",
                "}",
            ]),

            // Case 4: the read sits inside a local function, so the LOCAL is what a test must call.
            ("apps/Harbor.App.Cli/Repl/ProbeLocal.cs",
            [
                "namespace Harbor.App.Cli.Repl;",
                "internal sealed class ProbeLocal",
                "{",
                "    private void Outer()",
                "    {",
                "        void Inner()",
                "        {",
                "            ConsumeGlowRegions(scratch);",
                "            Effects.Set(0, effect);",
                "        }",
                "        Inner();",
                "    }",
                "}",
            ]),

            // The test side: one file naming the real consumer, one naming the local, one naming neither.
            ("tests/Harbor.GateGlow.Tests/NamesTheConsumer.cs",
            [
                "namespace Harbor.GateGlow.Tests;",
                "public sealed class NamesTheConsumer",
                "{",
                "    public void Drive() => new ReplLifecycle(host).ArmGateGlow();",
                "}",
            ]),
            ("tests/Harbor.GateGlow.Tests/NamesTheLocal.cs",
            [
                "namespace Harbor.GateGlow.Tests;",
                "public sealed class NamesTheLocal",
                "{",
                "    public void Drive() => Outer();",
                "}",
            ]),
            ("tests/Harbor.GateGlow.Tests/NamesNothing.cs",
            [
                "namespace Harbor.GateGlow.Tests;",
                "public sealed class NamesNothing",
                "{",
                "    // A comment that MENTIONS ArmGateGlow in prose must not count as a caller.",
                "    public void Drive() => ArmGateGlowRenamed();",
                "}",
            ]),
        ]);

        var consumers = report.Consumers;
        await Assert.That(consumers.Count).IsEqualTo(2)
            .Because(
                "exactly two synthetic files are glow consumers: the one that reads the ledger and "
                + "writes slots, and the one that does both inside a local function. Decoy 1 writes "
                + "slots without reading the ledger, decoy 2 has both only inside comments, and "
                + "decoy 2's prose deliberately spells both tokens out. Found: "
                + string.Join(" | ", consumers.Select(Describe)));

        var real = consumers.SingleOrDefault(c => c.File.EndsWith("Probe.cs", StringComparison.Ordinal));
        await Assert.That(real).IsNotNull()
            .Because("the transcribed ArmGateGlow shape must be found");
        await Assert.That(real!.Method).IsEqualTo("ArmGateGlow")
            .Because(
                "the enclosing method is the name a test has to call, so the attribution must be the "
                + "DECLARATION the ledger read sits inside and not one of the statements around it. "
                + "Both `Effects.Set(i, …);` lines end in a semicolon and neither is a declaration, so "
                + "the last declaration before the read is the method. Got: " + real.Method);
        await Assert.That(real.ArmCalls).IsEqualTo(1)
            .Because("the transcribed body writes one non-null effect into a slot");
        await Assert.That(real.DisarmCalls).IsEqualTo(1)
            .Because("the transcribed body writes one null into a slot — the drain this issue is about");
        await Assert.That(real.NamedBy).IsNotEmpty()
            .Because("a test file calling ArmGateGlow() is what makes the consumer covered");

        var local = consumers.SingleOrDefault(c => c.File.EndsWith("ProbeLocal.cs", StringComparison.Ordinal));
        await Assert.That(local).IsNotNull()
            .Because("a local function that reads the ledger and writes slots is still a consumer");
        await Assert.That(local!.Method).IsEqualTo("Inner")
            .Because(
                "attribution is innermost-by-position, so the local wins over Outer. A test cannot call "
                + "a local function, which is exactly why the OUTER name would be the useless answer. "
                + "Got: " + local.Method);
        await Assert.That(local.NamedBy).IsNotEmpty()
            .Because("a test file naming Inner is what makes that consumer covered");

        // The whole point of the tripwire. `NamesNothing` is the fourth decoy and it lives on the TEST
        // side, where the two ways to fake a caller live: a mention in prose, and a longer identifier
        // that merely starts with the name. Either one counting would let this rule go green without a
        // single real call.
        await Assert.That(string.Join(" | ", real.NamedBy))
            .IsEqualTo("tests/Harbor.GateGlow.Tests/NamesTheConsumer.cs")
            .Because(
                "NamesNothing names ArmGateGlow only inside a comment and calls ArmGateGlowRenamed, a "
                + "different whole word. Comment stripping plus the whole-word boundary are what stop "
                + "either from counting as a caller, and a rule satisfied by prose is the failure mode "
                + "this whole file is about. Callers found: " + string.Join(" | ", real.NamedBy));

        await Assert.That(string.Join(" | ", local.NamedBy))
            .IsEqualTo("tests/Harbor.GateGlow.Tests/NamesTheLocal.cs")
            .Because(
                "Inner is named by exactly one file — the one that calls Outer(), because Inner is a "
                + "local function. NamesTheConsumer names ArmGateGlow only, and NamesNothing names "
                + "neither. Callers found: " + string.Join(" | ", local.NamedBy));
    }

    /// <summary>One consumer, rendered for a failure message.</summary>
    private static string Describe(GateGlowConsumer consumer) =>
        consumer.Method + " (" + consumer.File + ":" + consumer.Line
        + ", arm=" + consumer.ArmCalls + " disarm=" + consumer.DisarmCalls
        + ", named by: " + (consumer.NamedBy.Count == 0 ? "NOTHING" : string.Join(", ", consumer.NamedBy))
        + ")";
}
