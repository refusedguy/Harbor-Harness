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
// table between frames. So the drain at the tail of `ArmGateGlow` — the loop that
// walks the slots from the ledger's count up to `VirtualizedChatTimeline.MaxFxDamage`
// writing null into each — is the ONLY thing standing between "the gate was answered"
// and "the gate glows forever". Delete it and every test in `PostFxTests` stays green,
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
// WHAT THE FIRST RED RUN CAUGHT, WHICH WAS THIS GUARD SATISFYING ITSELF
// -----------------------------------------------------------------------
// The first CI run of this file failed one assertion and passed the other, and
// the one it passed is the one this issue is about. `EveryGateGlowConsumer_IsNamedByATest`
// went GREEN with `ArmGateGlow` having no caller anywhere, because the file
// doing the looking was itself a test file, and its own positive control asserts
// `IsEqualTo("ArmGateGlow")`. `SourceCommentStripper` strips comments and
// deliberately KEEPS string literals, so a name that appears only inside an
// expected-value string counts as a caller. A golden fixture, an
// expected-string table or a rule's own control would each have done it.
//
// So the test side now has literals blanked as well as comments stripped, and the
// positive control carries the exact shape that did it — a name in prose, a name
// in a string constant, and a longer identifier that starts with it. That is the
// finding worth carrying out of #889 rather than a detail of this rule: a guard
// that greps test files for a name is not measuring coverage, and the file that
// breaks first is its own.
//
// THE SECOND RED RUN, WHICH WAS THE CONTROL NOT RUNNING THE MATCHER
// ------------------------------------------------------------------
// With the self-satisfaction fixed, the control still failed — and the log showed
// the comment-only decoy coming back as a CONSUMER and the comment-only caller
// coming back as a CALLER. Both at once, both from one cause: `ScanFiles` took
// pre-stripped lines, and the control was handing it raw ones, so nothing was
// being stripped at all. The control had been asserting that the decoys are
// rejected while reading them verbatim, and it only surfaced because it also
// asserted a total, so the two phantom rows showed up in a count.
//
// `ScanFiles` now takes raw lines and strips them itself, which is also what
// "the control drives the REAL matcher" has to mean. The general lesson is the
// one worth keeping: a control that asserts a decoy is ABSENT, without also
// asserting how many rows there are in total, proves nothing about a
// pre-processing step it skipped.
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
//   * Not a claim that the name is a WHOLE causal chain. "Some test calls this
//     method" is a proxy for "deleting the drain turns something red", and it is a
//     proxy on purpose: a causal dependency between a source edit and a test outcome
//     is not statically decidable, whereas a call site is. A test that drove the
//     consumer only INDIRECTLY (through the frame loop) would leave this rule red
//     even though its coverage was real — the failure message names the method and
//     says what to do, and widening the rule to indirect coverage is a decision
//     somebody should make on purpose rather than a gap to paper over.

using System.Text;
using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>One product method that turns the glow ledger into post-fx slot writes.</summary>
/// <param name="Method">The enclosing method's name — what a test has to name.</param>
/// <param name="File">Repo-relative declaration file.</param>
/// <param name="Line">1-based line of the first ledger read inside it.</param>
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

        var sources = new List<(string Relative, string[] Lines)>();
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

            sources.Add((DiffSurfaceNameCollisionProbe.MakeRelative(repoRoot, file), lines));
        }

        int sharedHelperTestFiles = SourceScan.EnumerateCsFiles("tests").Count;
        return ScanFiles(sources, sharedHelperTestFiles);
    }

    /// <summary>
    ///     Grades already-read files, taking them RAW. Exposed so the positive control drives the
    ///     REAL matcher end to end — comment stripping, literal blanking and declaration detection
    ///     included — rather than a second implementation of it, which is the only way "it can fail"
    ///     means anything.
    /// </summary>
    /// <remarks>
    ///     Raw, not pre-stripped, and that is load-bearing rather than a convenience. The first
    ///     version of the positive control handed this method unstripped fixture lines, and the
    ///     control then "proved" the decoys were rejected while the probe was in fact reading them
    ///     verbatim: the comment-only fixture came back as a consumer and the comment-only caller
    ///     came back as a caller. The failure was caught only because the control also asserted a
    ///     total, so the extra rows showed up in the count. A control that asserts "the decoy is
    ///     absent" without also asserting how many consumers there are proves nothing about a
    ///     pre-processing step it skipped.
    /// </remarks>
    internal static GateGlowConsumerReport ScanFiles(
        List<(string Relative, string[] Lines)> sources,
        int sharedHelperTestFiles = 0)
    {
        var testNames = new List<(string Relative, string Text)>();
        var found = new List<GateGlowConsumer>();

        foreach (var source in sources)
        {
            string[] stripped = SourceCommentStripper.StripAll(source.Lines);
            string joined = string.Join("\n", stripped);

            if (IsTestFile(source.Relative))
            {
                // Literals blanked as well as comments: see BlankLiterals. Without this the guard
                // satisfies ITSELF — its own positive control asserts IsEqualTo("ArmGateGlow"), and a
                // string literal survives comment stripping, so the one file guaranteed to mention
                // the name is the file that must not count. That is not hypothetical: the first red
                // run of this rule passed its own assertion for exactly that reason.
                testNames.Add((source.Relative, BlankLiterals(joined)));
                continue;
            }

            if (!joined.Contains(LedgerRead, StringComparison.Ordinal)
                || !joined.Contains(SlotWrite, StringComparison.Ordinal))
            {
                continue;
            }

            foreach ((string method, int readLine) in ConsumersIn(stripped))
            {
                found.Add(new GateGlowConsumer(
                    method,
                    source.Relative,
                    readLine,
                    CountSlotWrites(stripped, disarm: false),
                    CountSlotWrites(stripped, disarm: true),
                    []));
            }
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

    /// <summary>
    ///     Replaces the CONTENTS of every string, verbatim-string and character literal with
    ///     spaces, leaving the delimiters and the line structure intact.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <see cref="SourceCommentStripper" /> strips comments and deliberately KEEPS string
    ///         literals, because the rules built on it look for identifiers and a literal rarely
    ///         carries one. This rule is the exception: what it looks for on the test side is a CALL,
    ///         and a call is not spelled inside a literal.
    ///     </para>
    ///     <para>
    ///         The self-satisfaction this closes is not a corner case. A rule that greps test files
    ///         for a method name is satisfied by any test that merely ASSERTS the name — a golden
    ///         fixture, an expected-string table, a rule's own positive control. All three are
    ///         ordinary things to have in a test tree, and each one would have turned this guard
    ///         into a permanent green light with no caller anywhere. Blanking the literals makes the
    ///         rule answer the question it claims to answer: is the method CALLED from a test.
    ///     </para>
    /// </remarks>
    private static string BlankLiterals(string text)
    {
        var output = new StringBuilder(text.Length);
        LiteralState state = LiteralState.Code;
        int i = 0;

        while (i < text.Length)
        {
            char c = text[i];
            char next = i + 1 < text.Length ? text[i + 1] : '\0';

            if (state == LiteralState.Code)
            {
                if (c == '@' && next == '"')
                {
                    state = LiteralState.VerbatimString;
                    Blank(output, 2);
                    i += 2;
                    continue;
                }

                if (c == '"' || c == '\'')
                {
                    state = c == '"' ? LiteralState.String : LiteralState.Char;
                    Blank(output, 1);
                    i++;
                    continue;
                }

                output.Append(c);
                i++;
                continue;
            }

            // Inside a literal: every character is replaced, delimiters included, so no partial
            // identifier can survive. An escape consumes the next character with it.
            if (c == '\\' && next != '\0' && state != LiteralState.VerbatimString)
            {
                Blank(output, 2);
                i += 2;
                continue;
            }

            if (state == LiteralState.VerbatimString)
            {
                if (c == '"' && next == '"')
                {
                    Blank(output, 2);
                    i += 2;
                    continue;
                }

                if (c == '"')
                {
                    state = LiteralState.Code;
                }
            }
            else if (state == LiteralState.String && c == '"')
            {
                state = LiteralState.Code;
            }
            else if (state == LiteralState.Char && c == '\'')
            {
                state = LiteralState.Code;
            }

            Blank(output, 1);
            i++;
        }

        return output.ToString();

        static void Blank(StringBuilder target, int count)
        {
            for (int n = 0; n < count; n++)
            {
                target.Append(' ');
            }
        }
    }

    private enum LiteralState
    {
        Code,
        String,
        VerbatimString,
        Char,
    }

    /// <summary>1-based line of every line containing <paramref name="needle" />, in order.</summary>
    private static List<int> LinesOf(string[] stripped, string needle)
    {
        var lines = new List<int>();
        for (int i = 0; i < stripped.Length; i++)
        {
            if (stripped[i].Contains(needle, StringComparison.Ordinal))
            {
                lines.Add(i + 1);
            }
        }

        return lines;
    }

    /// <summary>
    ///     Every method name in one file that reads the ledger, with the line of the first read
    ///     inside it, deduplicated by name and in first-read order. A file with two consumers
    ///     yields two entries, which is what makes the rule's "deleting one cannot be masked by
    ///     adding another" claim true rather than aspirational.
    /// </summary>
    /// <remarks>
    ///     Overloads of the same name collapse to one entry on purpose: what a test has to do is
    ///     name the method, and a test that names the name has satisfied the rule.
    /// </remarks>
    private static List<(string Method, int Line)> ConsumersIn(string[] stripped)
    {
        var found = new List<(string Method, int Line)>();
        var names = new List<string>();
        foreach (int line in LinesOf(stripped, LedgerRead))
        {
            string? method = EnclosingMethod(stripped, line);
            if (method is null || names.Contains(method))
            {
                continue;
            }

            names.Add(method);
            found.Add((method, line));
        }

        return found;
    }

    /// <summary>
    ///     The innermost declaration preceding <paramref name="line" /> — the LAST one before it by
    ///     position. No brace matching is involved, deliberately: counting braces correctly across
    ///     string literals is a second parser, and the property that matters here ("who names the
    ///     method a test must call") does not need one.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The cost of not parsing braces is one known false positive, and it is stated here
    ///         rather than discovered later: a consumer written as a LOCAL FUNCTION is attributed to
    ///         the local, and no test can call a local, so the rule would demand a name that cannot
    ///         be reached. That is a loud failure, not a silent one — the message prints the attributed
    ///         name with its file and line, and an attributed name that is not a member is obvious on
    ///         sight. The alternative was to walk out to the enclosing member, which needs the brace
    ///         parser this deliberately does not have, and the glow consumer is a frame-loop method by
    ///         construction, so the case is not reachable from the code this rule watches today.
    ///     </para>
    ///     <para>
    ///         Class and record primary constructors are accepted as declarations too, which is
    ///         harmless and slightly useful: they give the walk a floor, so a ledger read that somehow
    ///         sat directly in a type body is attributed to the type rather than to nothing.
    ///     </para>
    /// </remarks>
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
    ///     The real probe, driven over synthetic sources: it finds every consumer and names its
    ///     enclosing method, and it rejects the shapes that would let it go green for free.
    /// </summary>
    /// <remarks>
    ///     Reusing <see cref="GateGlowConsumerProbe.ScanFiles" /> is the point — a hand-written
    ///     matcher in the control would prove the control, not the rule. The decoys are the ways this
    ///     rule could pass without looking at anything: a file that writes post-fx slots without
    ///     touching the glow ledger, and a file whose two tokens exist only inside comments (whose
    ///     prose spells both out on purpose, so comment stripping is doing real work). The fourth
    ///     case is the opposite failure — TWO consumers in one file — which is what makes the rule's
    ///     per-method claim true rather than aspirational, since a per-file probe would report one
    ///     and let a deletion hide behind its neighbour. The last tripwire is on the test side, and
    ///     it is the important one: a mention in prose, a mention in a string constant, and a longer
    ///     identifier that merely starts with the name are the three ways to fake a caller — and
    ///     this rule's own first red run was fooled by the middle one.
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

            // Case 4: TWO consumers in one file. The rule asserts a list of method names, not a
            // count, so it has to find both — a per-file probe would let one deletion hide behind
            // the other.
            ("apps/Harbor.App.Cli/Repl/ProbeTwo.cs",
            [
                "namespace Harbor.App.Cli.Repl;",
                "internal sealed class ProbeTwo",
                "{",
                "    private void ArmOne()",
                "    {",
                "        ConsumeGlowRegions(one);",
                "        Effects.Set(0, first);",
                "    }",
                "",
                "    private void ArmTwo()",
                "    {",
                "        ConsumeGlowRegions(two);",
                "        Effects.Set(1, second);",
                "    }",
                "}",
            ]),

            // The test side: one file naming the real consumer, one naming the second of the pair,
            // and one that fakes a caller twice.
            ("tests/Harbor.GateGlow.Tests/NamesTheConsumer.cs",
            [
                "namespace Harbor.GateGlow.Tests;",
                "public sealed class NamesTheConsumer",
                "{",
                "    public void Drive() => new ReplLifecycle(host).ArmGateGlow();",
                "}",
            ]),
            ("tests/Harbor.GateGlow.Tests/NamesTheSecond.cs",
            [
                "namespace Harbor.GateGlow.Tests;",
                "public sealed class NamesTheSecond",
                "{",
                "    public void Drive() => new ReplLifecycle(host).ArmTwo();",
                "}",
            ]),
            ("tests/Harbor.GateGlow.Tests/NamesNothing.cs",
            [
                "namespace Harbor.GateGlow.Tests;",
                "public sealed class NamesNothing",
                "{",
                "    // A comment that MENTIONS ArmGateGlow in prose must not count as a caller.",
                "    private const string Expected = \"ArmGateGlow\";",
                "    public void Drive() => ArmGateGlowRenamed();",
                "}",
            ]),
        ]);

        var consumers = report.Consumers;
        await Assert.That(consumers.Count).IsEqualTo(3)
            .Because(
                "three synthetic consumers exist: the transcribed ArmGateGlow shape, plus the two "
                + "methods of ProbeTwo. Decoy 1 writes slots without reading the ledger, decoy 2 has "
                + "both tokens only inside comments (its prose spells them out on purpose), and a "
                + "per-file probe would report ProbeTwo once instead of twice. Found: "
                + string.Join(" | ", consumers.Select(Describe)));

        var real = consumers.SingleOrDefault(c => c.File.EndsWith("Probe.cs", StringComparison.Ordinal));
        await Assert.That(real).IsNotNull()
            .Because("the transcribed ArmGateGlow shape must be found");
        await Assert.That(real!.Method).IsEqualTo("ArmGateGlow")
            .Because(
                "the enclosing method is the name a test has to call, so the attribution must be the "
                + "DECLARATION the ledger read sits inside and not one of the statements around it. "
                + "Both Effects.Set lines end in a semicolon and neither is a declaration, so the "
                + "last declaration before the read is the method. Got: " + real.Method);
        await Assert.That(real.ArmCalls).IsEqualTo(1)
            .Because("the transcribed body writes one non-null effect into a slot");
        await Assert.That(real.DisarmCalls).IsEqualTo(1)
            .Because("the transcribed body writes one null into a slot — the drain this issue is about");

        // The whole point of the tripwire. `NamesNothing` is the fifth case and it lives on the
        // TEST side, where the three ways to fake a caller live: a mention in prose, a mention in a
        // string literal, and a longer identifier that merely starts with the name. Any one of them
        // counting would let this rule go green with no caller anywhere — and the literal one is not
        // hypothetical, it is what this guard did to itself on its first red run.
        await Assert.That(string.Join(" | ", real.NamedBy))
            .IsEqualTo("tests/Harbor.GateGlow.Tests/NamesTheConsumer.cs")
            .Because(
                "NamesNothing names ArmGateGlow in a comment, in a string constant, and calls "
                + "ArmGateGlowRenamed — a different whole word. Comment stripping, literal blanking "
                + "and the whole-word boundary are the three things that stop it counting, and a rule "
                + "satisfied by any of them is the failure mode this whole file is about. Callers "
                + "found: " + string.Join(" | ", real.NamedBy));

        var pair = consumers.Where(c => c.File.EndsWith("ProbeTwo.cs", StringComparison.Ordinal))
            .Select(c => c.Method)
            .ToArray();
        await Assert.That(string.Join(" | ", pair))
            .IsEqualTo("ArmOne | ArmTwo")
            .Because(
                "a file holding two consumers must yield two entries, or the rule's per-method claim "
                + "is really a per-file claim and deleting one consumer would be masked by the other. "
                + "Found: " + string.Join(" | ", pair));

        var second = consumers.Single(c => c.Method == "ArmTwo");
        await Assert.That(string.Join(" | ", second.NamedBy))
            .IsEqualTo("tests/Harbor.GateGlow.Tests/NamesTheSecond.cs")
            .Because(
                "the second consumer of the pair is covered by its own file, and the file that names "
                + "ArmGateGlow does not name it — otherwise a single test naming one method would "
                + "satisfy the rule for both. Callers found: " + string.Join(" | ", second.NamedBy));
    }

    /// <summary>One consumer, rendered for a failure message.</summary>
    private static string Describe(GateGlowConsumer consumer) =>
        consumer.Method + " (" + consumer.File + ":" + consumer.Line
        + ", arm=" + consumer.ArmCalls + " disarm=" + consumer.DisarmCalls
        + ", named by: " + (consumer.NamedBy.Count == 0 ? "NOTHING" : string.Join(", ", consumer.NamedBy))
        + ")";
}
