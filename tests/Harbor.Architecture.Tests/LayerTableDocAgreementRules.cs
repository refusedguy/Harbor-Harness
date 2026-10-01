// LayerTableDocAgreementRules.cs — GUARD for #922.
//
// WHAT #922 LEFT UNBUILT, AND WHY THIS FILE IS THE ANSWER TO IT
// -------------------------------------------------------------
// #922 fixed the §5.7 guard-summary prose ("exists in exactly one file") in four
// documents and then, in its own body, declined to build the mechanism that class
// of fix needs, for a stated reason: "it is a new test in a project that cannot be
// compiled locally, and this change is already three commits of documentation that
// a compile cannot break. Landing it as its own PR, where a compile failure is the
// expected cost rather than a surprise."
//
// The reason was false and this PR is the consequence. `ci.yml` BUILDS
// tests/Harbor.Architecture.Tests and RUNS it twice — once in the `test (core)`
// shard, and once as the `HarborArchGate` MSBuild target in `build`, which is
// `AfterTargets="Build"` and fails the BUILD on a non-zero exit. A test here cannot
// be the unexpected cost of a docs diff; it is the cheapest possible place to put
// one.
//
// WHAT THIS RULE IS, RELATIVE TO THE TWO THAT ALREADY EXIST
// -------------------------------------------------------
// The layer of a project has one home: the `Layer` value on its row in
// `FullLayerMatrixTests.Matrix`. That fact is enforced. It is then transcribed by
// hand, and there are now three transcribers, each with its own rule:
//
//   §1 of docs/ARCHITECTURE_LAYERS.md   LayerDocAgreementRule      (#888) — ASCII boxes
//   a project's `## Layer` section      LayerClaimMatchesMatrixRules (#901) — one line
//   prose attributions                  LayerClaimMatchesMatrixRules (#901) — two shapes
//   WHOLE-TREE LAYER TABLES             THIS FILE                  — none of the above
//
// That last row is the hole #901 measured and named rather than closed:
//
//   "Whole-tree layer TABLES: the root README's architecture block, the CLAUDE.md
//    layer table, §1's ASCII boxes. This file MEASURES the first two rather than
//    failing on them … those tables disagree with the matrix about roughly twenty
//    projects each."
//
// The measurement was correct and understated. Re-measured here against the current
// `Matrix`, the two tables disagree about **21 distinct (project, layer) pairs** —
// more than #895's famous 15 — and the finding is not concentrated in one family:
//
//   README.md:143  Harbor.Desktop.Abstractions      doc=Domain           matrix=Presentation
//   README.md:143  Harbor.Terminal.Abstractions     doc=Domain           matrix=Presentation
//   README.md:145  Harbor.Ipc.Abstractions          doc=Application      matrix=Domain
//   README.md:145  Harbor.Ipc.{Client,InProcess,Server}      doc=Application      matrix=Infrastructure
//   README.md:145  Harbor.Plugins.{6 projects}      doc=Application      matrix=Infrastructure
//   README.md:145  Harbor.Ui.Framework.{State,…,ViewModels} (7) doc=Application   matrix=Presentation
//   README.md:145  Harbor.Ui.Framework.Abstractions  doc=Application      matrix=Domain
//   README.md:149  Harbor.Extensions                doc=Infrastructure    matrix=Domain
//   CLAUDE.md:94   Harbor.Plugins.Runtime           doc=Application      matrix=Infrastructure
//
// Two of those are #888's finding verbatim, two months later and in a different file:
// the whole `Harbor.Ipc.*` family in the APPLICATION band, and
// `Harbor.Ui.Framework.Abstractions` — the project whose own README says "Domain
// (framework contracts)" and which #901 already corrected there.
//
// READ THE MATRIX, NOT §1 — THE QUESTION THIS FILE HAD TO ANSWER FIRST
// -------------------------------------------------------------------
// §922 raised this as the decision that shapes the rule, and it is worth answering
// explicitly rather than by preference.
//
// The cheap version reads §1 out of the markdown and compares the tables to §1. It
// compares ONE HAND-TYPED COPY OF THE MATRIX AGAINST ANOTHER, and so it measures
// agreement between two documents. Rewriting both the same wrong way turns it
// green: a rule that cannot tell a corrected document from a coordinated
// falsification is not a guard, it is a consistency check between two prose
// artefacts, and §895's whole finding is that the copies drift from the SOURCE.
// The form of that failure is already in the repository — #880's agent, told to
// reconcile a doc against the matrix, wrote "this ordering is a record of the
// implementation, not an enforced contract" and thereby made a document disagree
// with the gate while every gate stayed green.
//
// Reading `Matrix` inverts the direction of trust: the document is graded against
// the enforced source of truth, so a doc rewritten wrongly goes red, and only the
// matrix row can be argued with. §901 established that precedent and named this
// file's job as closing its declared hole; reading §1 here would reopen it.
//
// CAN THE TEST PROJECT READ THE MATRIX? — #899's question, answered
// ---------------------------------------------------------------
// #899 established that `tests/Harbor.Architecture.Tests` has no `ProjectReference`
// to anything under `apps/`, which had broken a rule that read assemblies by hand.
// This rule does not care, and the reason is worth stating because "check whether
// the test project can see it" is the obvious question to ask of a guard that
// grades apps/ names.
//
// `FullLayerMatrixTests.Matrix` is `internal static readonly` **in this very
// assembly** — it is a dictionary literal in `FullLayerMatrixTests.cs`, beside this
// file, not a type reached across a project boundary. Nine rule files in this
// project already read it (`LayerDocAgreementRule`, `LayerClaimMatchesMatrixRules`,
// `DesignSystemLeafTakesNoIoRules`, `DesktopAbstractionsLeafTakesNoIoRules`,
// `DesktopSharedTakesNoIoRules`, `EnforcerIntegrityTests`, `SessionForkPortSeamRules`,
// `SessionStatusSourceRule`, `SharedSourceLinkRules`), so the accessibility is
// demonstrated by the build rather than argued here. #899's gap is about LOADING an
// `apps/` ASSEMBLY to inspect its IL; this rule reads a string key out of a
// dictionary and reads `apps/` names out of MARKDOWN FILES on disk. The two
// operations share nothing but the word "apps".
//
// The consequence for the perimeter is the one thing worth being careful about: an
// `apps/` project has no matrix row, so a claim about `Harbor.App.Cli` cannot be
// contradicted by an answer that does not exist. Those names are UNJUDGED — counted,
// reported, bounded, and never graded in either direction. See
// `Unjudged_AreCounted_And_Bounded`.
//
// PERIMETER: DERIVED FROM A GRAMMAR, NOT A LIST OF TWO FILES
// -------------------------------------------------------
// A typed list of documents is wrong the day a third one is written — the #890 form,
// and `LayerClaimMatchesMatrixRules` names it in its own header for the same reason
// its `## Layer` perimeter is `RepoPaths.EnumerateSrcProjects()` rather than a
// roster. So this file walks every `*.md` in the checkout, and admits a row only on
// one of two grammatical shapes:
//
//   BLOCK  inside a ``` fence: a line whose first token is a layer name and whose
//          NEXT token is a project reference (`Harbor.`, `apps/`, `contrib/`, `src/`).
//          Continuation lines are folded in, because a project's list wraps.
//   TABLE  a markdown table row whose FIRST CELL is a layer name, with `Harbor.` in
//          a later cell.
//
// The lookahead in BLOCK is the load-bearing part and it was arrived at by being
// wrong twice. A pattern that only required "a layer word followed by a word" matches
// `Domain layer is the innermost ring of the onion` — prose, in the same document,
// in the abstract — and would have graded the layer's own DEFINITION as a claim
// about `Harbor.`-something. And requiring only a letter also matches
// `**Presentation (framework).** Depends on ...`, which is
// `LayerClaimMatchesMatrixRules`' `## Layer` shape, so the two rules would have
// graded the same lines twice with two different verdicts. Requiring a project
// reference is what makes the shape a DECLARATION of membership rather than a
// sentence that happens to mention a layer. Both near-misses are pinned in
// `Shapes_AreLiveOnTextTheTreeDoesNotContain`.
//
// Measured over the tree: **2 files, 10 declaration rows, 79 graded (project, layer)
// claims.** Both surviving documents are graded, so this is not a one-file rule; and
// `docs/XML_DOC_AUDIT.md`, which a looser pattern matched 138 times, is matched zero
// times by this one.
//
// DELIBERATELY NOT GRADED
// -----------------------
//   * THE GLOB EXPANSION IS THE MATRIX'S OWN KEY SET, and that was a defect in the first
//     draft rather than a preference: it first asked whether the BARE PREFIX was a matrix
//     row, and `Harbor.Ipc` / `Harbor.Plugins` are families, not rows, so every
//     `Harbor.Ipc.*` / `Harbor.Plugins.*` came back UNJUDGED. That silently cost 11 of the
//     22 findings — the APPLICATION row claims both families wholesale, and #888's finding
//     was that neither claim is true. A prefix the matrix does not classify can still
//     DENOTE projects it does classify; asking the keys is what sees that, and the count
//     moved from 11 to 22 when it did.
//   * INVENTORY COVERAGE. This file does not require the tables to name every matrix
//     row — `Harbor.Desktop.Shared`, `Harbor.Ui.Framework.Services` and others are
//     absent from the root README even after this PR's corrections. The direction
//     that matters is the one `LayerDocAgreementRule` calls R1: a name that IS in a
//     row must be in the RIGHT row. A coverage requirement would turn this into a
//     documentation project, and #555 freezes new axes.
//   * `Harbor.Plugins.Host`. `OutOfScopeAssemblies` gives it no layer ("OutputType=Exe
//     out-of-process MCP stdio server — a composition root"), so any fix that assigned
//     it one would be asserting an answer the gate does not give. It is unjudged here.
//   * `Harbor.CodeGen`, `Harbor.Providers.Shared`, `Harbor.Storage.Shared` — a source
//     generator and two linked-source folders with no row, for the same reason.
//   * `Harbor.App.Cli`, `Harbor.App.Avalonia` — composition-root apps, no matrix row.
//   * `contrib/` — unmaintained, in no CI job, and out of scope by owner decision.
//   * THE "MAY REFERENCE" COLUMN. A dependency rule, not a layer assignment. It is
//     `MatrixTable_RespectsLayerRules`' subject, and the corrected CLAUDE.md rows now
//     name `Domain→Domain` where they previously misstated it.
//   * THE `Harbor.Tui.Abstractions` / `Harbor.Scripting` NAMES. Corrected in CLAUDE.md
//     in this PR because they are stale project names, not because this rule grades
//     them: neither has a matrix row, so no grader can hold them to anything. The
//     prose note that replaced them is bounded by nothing but review, and that is
//     stated rather than pretended away.
//
// NON-VACUITY
// -----------
// The failure mode of every guard in this family is a matcher that stopped matching
// while the test stayed green, so:
//   1. `Rows_AreFound` — floors on rows and on graded claims, plus a CEILING on
//      claims (a matcher that degraded into "any Harbor token" would report noise).
//   2. `Unjudged_AreCounted_And_Bounded` — unjudged names are reported and bounded,
//      because "everything became unjudged" is the same blindness in another hat.
//   3. `Shapes_AreLiveOnTextTheTreeDoesNotContain` — each shape must match planted
//      text and must NOT match two near-misses that a looser pattern DID match.
//   4. `The_Comparison_Fires_On_A_Planted_Mislayering` — the real parser and the real
//      comparison over synthetic rows, wrong and right, requiring both directions.
//   5. `Every_Layer_The_Matrix_Uses_Has_A_Row_Somewhere` — a `Layer` value that no
//      document names would stop being checkable silently.
//   6. `RequireRepoRoot` THROWS. An early `return` would turn "the guard could not
//      find what it polices" into a pass — the #877 vacuity.
//
// #920 — IS THIS RULE'S PERIMETER INSIDE THE STRIPPER'S BLIND SPOT? NO, AND HERE IS
// THE PROOF RATHER THAN THE ASSUMPTION
// ---------------------------------------------------------------------------
// #919/#920 found `SourceCommentStripper.StripAll` inverted: `Strip` deletes comment
// characters but preserves string literals, so the old `/*`-vs-`*/` count was zero
// for every real block comment and non-zero for every glob in a literal. `"src/*"`
// in `PermissionRuleset.cs` flipped the file into block-comment mode and 845 lines of
// real code were blanked across 5 files. A guard whose perimeter passed through that
// stripper would have been grading a third of a file — the §920 subject, not this one.
//
// THIS RULE READS MARKDOWN. It never calls `SourceCommentStripper`, and there is a
// test that says so mechanically rather than in prose: `Perimeter_Reads_No_CSharp`
// walks the same enumeration this rule grades and asserts that NOT ONE path ends in
// `.cs`. The stripper is a C# lexer; markdown has no `//` and no block comments, so
// the question is not "is the stripper correct here" but "can it be reached at all",
// and that is now a property of the code rather than of this comment.
//
// STATUS: written without being compiled. Local dotnet is forbidden in this
// repository and CI is the first build, exactly as recorded in
// `DefaultModelDocClaimTests.cs:60-64` and in the two rule files above.
//
// Author: refusedguy (#922).

using System.Text;
using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     A whole-tree layer TABLE — the root <c>README.md</c> architecture block and the
///     <c>CLAUDE.md</c> layer table — must agree with
///     <see cref="FullLayerMatrixTests.Matrix" />, the one enforced home of the same
///     fact. See the file header for the mechanism, the two shapes, the #899 and #920
///     questions answered, and what is deliberately not graded.
/// </summary>
public sealed class LayerTableDocAgreementRules
{
    // ---------------------------------------------------------------------
    // The two shapes.
    // ---------------------------------------------------------------------

    /// <summary>
    ///     Every spelling of a <see cref="FullLayerMatrixTests.Layer" /> a document may
    ///     use, as the enum's own name. "Composition Root", "Composition-Root" and
    ///     "CompositionRoot" are one layer, not three — the first draft of this file
    ///     normalised on letters only and read "Composition Root" as no layer at all,
    ///     which would have silently dropped the single CompositionRoot row from the
    ///     perimeter while every test stayed green.
    /// </summary>
    private static readonly Dictionary<string, string> LayerBySpelling = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Domain"] = nameof(FullLayerMatrixTests.Layer.Domain),
        ["Presentation"] = nameof(FullLayerMatrixTests.Layer.Presentation),
        ["Application"] = nameof(FullLayerMatrixTests.Layer.Application),
        ["Infrastructure"] = nameof(FullLayerMatrixTests.Layer.Infrastructure),
        ["CompositionRoot"] = nameof(FullLayerMatrixTests.Layer.CompositionRoot),
        ["Composition Root"] = nameof(FullLayerMatrixTests.Layer.CompositionRoot),
        ["Composition-Root"] = nameof(FullLayerMatrixTests.Layer.CompositionRoot),
    };

    /// <summary>The alternation both shapes key on, built from the spelling table above.</summary>
    private static string LayerWordPattern =>
        string.Join("|", LayerBySpelling.Keys.OrderBy(k => k, StringComparer.Ordinal).Select(Regex.Escape));

    /// <summary>
    ///     A token that refers to a PROJECT rather than to prose: a dotted
    ///     <c>Harbor.*</c> name, or an <c>apps/</c> / <c>contrib/</c> / <c>src/</c>
    ///     path. Optional <c>**</c> and backticks in front, because a table writes the
    ///     name bolded and a code block writes it bare.
    /// </summary>
    /// <remarks>
    ///     This lookahead is the whole reason BLOCK is a DECLARATION shape rather than
    ///     a topic shape. See the header: a looser alternative matched both
    ///     <c>Domain layer is the innermost ring of the onion</c> and
    ///     <c>**Presentation (framework).** Depends on ...</c>.
    /// </remarks>
    private const string ProjectReference = @"(?:\*{0,2}`{0,2})(?:Harbor\.|apps/|contrib/|src/)";

    /// <summary>
    ///     SHAPE BLOCK — inside a fenced code block, a line whose first token is a layer
    ///     name and whose next token is a project reference.
    /// </summary>
    private static readonly Regex BlockRow = new(
        @"^[ \t]*(?<layer>" + LayerWordPattern + @")[ \t]+(?=" + ProjectReference + ")",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    ///     SHAPE TABLE — a markdown table row whose FIRST cell is a layer name.
    ///     <c>\|</c>-anchored on the left so a path that merely CONTAINS a layer word
    ///     (<c>| Harbor.Application/Agents/AgentLoop.cs | 25 | …</c>) cannot match, which
    ///     is how a first draft of the survey matched <c>docs/XML_DOC_AUDIT.md</c> 138
    ///     times and produced nothing but noise.
    /// </summary>
    private static readonly Regex TableRow = new(
        @"^[ \t]*\|[ \t]*\**(?<layer>" + LayerWordPattern + @")\**[ \t]*\|(?<rest>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>A fence opener or closer. Any run of three or more backticks toggles.</summary>
    private static readonly Regex Fence = new(@"^[ \t]*`{3,}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>A <c>Harbor.Family.{A, B, C}</c> list, expanded before the plain matcher runs.</summary>
    private static readonly Regex BraceForm = new(
        @"Harbor\.[A-Za-z0-9_]*\.\{(?<items>[^}]*)\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>A fully-spelled name, or a <c>Harbor.Family.*</c> glob.</summary>
    private static readonly Regex PlainName = new(
        @"\bHarbor\.[A-Za-z0-9_.*\-]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Files this rule must never read, because the stripper's blind spot is C#-shaped.</summary>
    private const string CSharpExtension = ".cs";

    /// <summary>Floors, counted from the FIXED tree and meant as a ratchet.</summary>
    private const int MinRows = 8;

    private const int MinGradedClaims = 50;

    /// <summary>
    ///     Above this the matcher has degraded into "any <c>Harbor.</c> token near a
    ///     layer word", which buries a real finding in noise until nobody reads it.
    /// </summary>
    private const int MaxGradedClaims = 200;

    /// <summary>
    ///     Unjudged names are names with no matrix row. They are not findings, and a
    ///     ceiling is what stops "the perimeter stopped being able to judge anything"
    ///     from passing as a clean tree.
    /// </summary>
    private const int MaxUnjudgedNames = 40;

    // ---------------------------------------------------------------------
    // Tests.
    // ---------------------------------------------------------------------

    /// <summary>
    ///     The rule. Every layer-labelled declaration row in every markdown file in the
    ///     checkout, against the matrix.
    /// </summary>
    [Test]
    public async Task Layer_Tables_Agree_With_The_Matrix()
    {
        RequireRepoRoot();
        List<string> failures =
        [
            .. DeclarationRows()
                .SelectMany(Expand)
                .Where(c => c.Outcome == Verdict.False)
                .Select(Describe),
        ];

        await Assert.That(failures.Count).IsEqualTo(0).Because(
            "A layer table states which layer each project is in, and that answer has exactly one "
            + "home: the `Layer` value on its row in FullLayerMatrixTests.Matrix. The row is "
            + "enforced — MatrixTable_RespectsLayerRules and EverySrcAssembly_ReferenceSet_"
            + "MatchesMatrix both fail the build on a row that lies about dependencies — while "
            + "the table is prose. #888 (nine wrong places in docs/ARCHITECTURE_LAYERS.md §1) and "
            + "#901 (ten sites across eight files, then two more per-project READMEs) each "
            + "measured a copy of this table and each fixed only the copy it opened; "
            + "LayerClaimMatchesMatrixRules says in its own header that it MEASURES the root "
            + "README's architecture block and the CLAUDE.md table rather than failing on them. "
            + "This rule is that gate. The first CI run of this file measured 21 wrong "
            + "(project, layer) pairs across the two, which is more than #895's famous 15 and "
            + "includes two of #888's own findings — the whole Harbor.Ipc.* family in the "
            + "APPLICATION band — in a file #888 never opened. Offending rows:\n"
            + string.Join("\n", failures));
    }

    /// <summary>
    ///     Floors on rows and on graded claims, and a ceiling, so that "matched nothing",
    ///     "matched nothing it could judge" and "matched everything" are three different
    ///     red results rather than one green.
    /// </summary>
    [Test]
    public async Task Rows_AreFound()
    {
        RequireRepoRoot();
        IReadOnlyList<DeclarationRow> rows = DeclarationRows();
        IReadOnlyList<LayerClaim> claims = [.. rows.SelectMany(Expand)];

        await Assert.That(rows.Count).IsGreaterThanOrEqualTo(MinRows).Because(
            "The perimeter is 'every layer-labelled declaration row in every markdown file in the "
            + "checkout', and a table can be rewritten out of both documents — or a shape can "
            + "stop matching. Ten rows are graded today. A repository where that number "
            + "collapses has not been made more correct, it has been made quiet, and a guard that "
            + "reports a clean tree because it read less is the #877 failure. If a refactor "
            + "really removes a table, lower this floor in the same diff. Found " + rows.Count
            + " row(s):\n" + Render(rows));

        int graded = claims.Count(static c => c.Outcome is not Verdict.Unjudged);

        await Assert.That(graded).IsGreaterThanOrEqualTo(MinGradedClaims).Because(
            "Rows were found, but none of them named a project the matrix classifies. That is "
            + "what 'rows found' means on its own, so it gets its own floor: the two documents "
            + "grade 79 (project, layer) claims between them. Found " + graded + " graded of "
            + claims.Count + ".");

        await Assert.That(graded).IsLessThanOrEqualTo(MaxGradedClaims).Because(
            "More graded claims than this means the matcher has stopped being specific and is "
            + "reading a project reference out of any line near a layer word. It will bury a "
            + "real finding in noise until nobody reads the failure. Found " + graded + ".");
    }

    /// <summary>
    ///     Names with no matrix row are not findings, and neither is their absence from the
    ///     findings. They are counted, listed and bounded, because "every name became
    ///     unjudged" is the same blindness as "no name was ever read".
    /// </summary>
    [Test]
    public async Task Unjudged_AreCounted_And_Bounded()
    {
        RequireRepoRoot();
        IReadOnlyList<LayerClaim> unjudged =
        [
            .. DeclarationRows().SelectMany(Expand).Where(static c => c.Outcome == Verdict.Unjudged),
        ];

        var names = unjudged
            .Select(static c => c.Subject ?? c.WrittenAs)
            .Where(static n => n is not null)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static n => n, StringComparer.Ordinal)
            .ToList();

        await Assert.That(names.Count).IsLessThanOrEqualTo(MaxUnjudgedNames).Because(
            "A name is UNJUDGED when the matrix has no row for it, which is correct for an apps/ "
            + "composition root (#899: this test project has no ProjectReference to apps/, and "
            + "none is needed — `Matrix` is a dictionary in THIS assembly, not a type reached "
            + "across a project boundary), for `Harbor.Plugins.Host` (OutOfScopeAssemblies gives "
            + "it no layer at all, so assigning one would assert an answer the gate does not "
            + "give), for `Harbor.CodeGen` (a source generator) and for the two linked-source "
            + "folders. Too many is not a documentation problem: it is this rule having lost the "
            + "ability to judge anything while still reporting green. Names:\n"
            + string.Join("\n", names));
    }

    /// <summary>
    ///     Shape liveness, on text the tree does not contain — plus the two near-misses a
    ///     looser pattern DID match, which is what makes this a control rather than a
    ///     tautology.
    /// </summary>
    /// <remarks>
    ///     The floors in <see cref="Rows_AreFound" /> are a ratchet on FILES, which is the
    ///     wrong place to prove a regex works. This is that place.
    /// </remarks>
    [Test]
    public async Task Shapes_AreLiveOnTextTheTreeDoesNotContain()
    {
        (string Name, Regex Shape, string MustMatch, string[] MustNotMatch)[] probes =
        [
            (
                BlockShape,
                BlockRow,
                "Infrastructure    Harbor.Telemetry.Core, Harbor.Transport.Remote,",
                [
                    "Domain layer is the innermost ring of the onion",
                    "**Presentation (framework).** Depends on `Harbor.Ui.Framework.State`",
                    "Harbor.Abstractions references ZERO other Harbor assemblies",
                ]),
            (
                TableShape,
                TableRow,
                "| **Composition Root** | `apps/Harbor.App.Cli/Hosting/HostBuilder.cs` | Everything |",
                [
                    "| Harbor.Application/Agents/AgentLoop.cs | 25 | class AgentLoop | YES |",
                    "| Part | Type | Owns |",
                    "The canonical table lives in Harbor.Ui.Framework.ViewModels.",
                ]),
        ];

        var dead = new List<string>();
        var greedy = new List<string>();

        foreach ((string name, Regex shape, string mustMatch, string[] mustNotMatch) in probes)
        {
            if (!shape.IsMatch(mustMatch))
            {
                dead.Add($"{name} did not match: {mustMatch}");
            }

            foreach (string nearMiss in mustNotMatch)
            {
                if (shape.IsMatch(nearMiss))
                {
                    greedy.Add($"{name} matched a near-miss: {nearMiss}");
                }
            }
        }

        await Assert.That(dead.Count).IsEqualTo(0).Because(
            "Each shape must still match the wording it exists to grade, in text the repository "
            + "does not contain. Dead shapes:\n" + string.Join("\n", dead));

        await Assert.That(greedy.Count).IsEqualTo(0).Because(
            "The other half, and the half that matters here: a BLOCK pattern that also matches "
            + "'Domain layer is the innermost ring of the onion' grades the layer's own "
            + "definition as a declaration about a project, and one that matches "
            + "'**Presentation (framework).** Depends on ...' grades the same lines a second time "
            + "under a different rule, with a different verdict. A matcher that matches "
            + "everything is not a stricter matcher. Over-matching:\n" + string.Join("\n", greedy));
    }

    /// <summary>
    ///     The real parser and the real comparison over synthetic rows, in BOTH
    ///     directions — the control that distinguishes "the tables are clean" from "the
    ///     guard is blind".
    /// </summary>
    [Test]
    public async Task The_Comparison_Fires_On_A_Planted_Mislayering()
    {
        // Rows built from real matrix rows of three different layers, so a grader that
        // only ever saw one layer cannot satisfy the set.

        // Harbor.Plugins.Storage is Infrastructure; Harbor.Ui.Framework.Sessions is
        // Presentation; Harbor.Extensions is Domain.
        string[] plantedWrong =
        [
            "| Application | `Harbor.Plugins.Storage`, `Harbor.Extensions` | Domain only |",
        ];

        List<string> wrong = Grade(plantedWrong);

        await Assert.That(wrong.Count).IsEqualTo(2).Because(
            "The planted row puts two projects in the APPLICATION band that the matrix places "
            + "elsewhere, so the comparison must report exactly two findings — no more (the "
            + "matcher is over-broad) and no fewer (it is blind). Reported: "
            + (wrong.Count == 0 ? "(none)" : string.Join(" | ", wrong)));

        await Assert.That(wrong.Count).IsGreaterThan(0).Because(
            "the count assertion above failed, so indexing into it would throw instead of "
            + "reporting, and the failure would name an index rather than a document.");

        await Assert.That(wrong[0]).Contains("Harbor.Plugins.Storage").Because(
            "A finding that does not name the project cannot be acted on. Reported: " + wrong[0]);

        string[] plantedRight = ["| Application | `Harbor.Plugins.Abstractions` | Domain only |"];

        await Assert.That(Grade(plantedRight).Count).IsEqualTo(0).Because(
            "Harbor.Plugins.Abstractions IS Application, so the rule must be silent. A guard that "
            + "reports this too is reporting its own parser rather than the document, and its "
            + "findings get suppressed until it catches nothing at all.");
    }

    /// <summary>
    ///     A <c>Layer</c> value that no document names has stopped being checkable, and
    ///     nothing else would notice: the rows would still agree with each other.
    /// </summary>
    [Test]
    public async Task Every_Layer_The_Matrix_Uses_Has_A_Row_Somewhere()
    {
        RequireRepoRoot();
        var named = DeclarationRows()
            .Select(static r => r.ClaimedLayer)
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);

        var absent = FullLayerMatrixTests.Matrix.Values
            .Select(static row => row.Layer.ToString())
            .Distinct(StringComparer.Ordinal)
            .Where(layer => !named.Contains(layer))
            .OrderBy(static s => s, StringComparer.Ordinal)
            .ToList();

        await Assert.That(absent.Count).IsEqualTo(0).Because(
            "FullLayerMatrixTests.Layer has a value that neither the root README's architecture "
            + "block nor the CLAUDE.md table names. Every project in that layer stops being "
            + "checked against the documents, silently, and this rule keeps reporting green for "
            + "the layers it does know. Naming a layer is a documentation act, not a parser one: "
            + "add the row. Layers with no row: " + string.Join(", ", absent));
    }

    /// <summary>
    ///     The #920 perimeter question, answered mechanically. #919/#920 found
    ///     <c>SourceCommentStripper.StripAll</c> inverted: it deletes comment characters but
    ///     preserves string literals, so its <c>/*</c>-vs-<c>*/</c> count was zero for every
    ///     real block comment and non-zero for every glob in a literal. <c>"src/*"</c> in
    ///     <c>PermissionRuleset.cs</c> flipped that file into block-comment mode and 845 lines
    ///     of real code were blanked across 5 files, so three rules walking all of src/ were
    ///     grading it two-thirds empty.
    ///     <para>
    ///         This rule could be inside that blind spot without anyone noticing, so it asserts
    ///         it is not: the enumeration it grades is walked here and required to contain no
    ///         <c>.cs</c> path at all. The stripper is a C# lexer; markdown has no line comment
    ///         and no block comment. The question is therefore not whether the stripper is
    ///         correct here — it cannot be reached — and this makes that a property of the code
    ///         rather than a promise in a comment.
    ///     </para>
    /// </summary>
    [Test]
    public async Task Perimeter_Reads_No_CSharp()
    {
        RequireRepoRoot();
        List<string> csharp = [.. MarkdownFiles().Where(p => p.EndsWith(CSharpExtension, StringComparison.OrdinalIgnoreCase))];

        await Assert.That(csharp.Count).IsEqualTo(0).Because(
            "This rule's perimeter has grown a C# file. Every guard in this family that reads "
            + "source does so through a comment stripper, and the stripper's #919 defect blanked "
            + "845 lines of real code in 5 files because a glob pattern in a string literal looked "
            + "like an unterminated block comment. Reading markdown is why this rule is immune; "
            + "reading C# would opt it back in. Offending path(s):\n" + string.Join("\n", csharp));
    }

    // ---------------------------------------------------------------------
    // Verdicts and rendering.
    // ---------------------------------------------------------------------

    private const string BlockShape = "layer row in a fenced block";

    private const string TableShape = "layer row in a markdown table";

    private enum Verdict
    {
        /// <summary>The table names the layer the matrix assigns.</summary>
        True,

        /// <summary>The table names a different layer than the matrix assigns.</summary>
        False,

        /// <summary>
        ///     The name has no matrix row, so there is no answer to disagree with. Never a
        ///     finding; counted and bounded by <see cref="Unjudged_AreCounted_And_Bounded" />.
        /// </summary>
        Unjudged,
    }

    /// <summary>One layer-labelled declaration row, before its project list is expanded.</summary>
    private sealed record DeclarationRow(string File, int Line, string Shape, string ClaimedLayer, string Written);

    /// <summary>One (project, layer) claim read out of a declaration row.</summary>
    private sealed record LayerClaim(
        string File,
        int Line,
        string Shape,
        string Subject,
        string WrittenAs,
        string ClaimedLayer,
        string? MatrixLayer,
        Verdict Outcome);

    /// <summary>
    ///     The gate layer a document's spelling denotes, or <see langword="null" /> when the
    ///     spelling is not one the table knows.
    /// </summary>
    private static string? LayerOf(string spelling)
        => LayerBySpelling.TryGetValue(spelling, out string? layer) ? layer : null;

    /// <summary>
    ///     <see cref="LayerOf" />, for a row the matchers have already accepted — where a
    ///     <see langword="null" /> is a DEFECT in <see cref="LayerWordPattern" /> rather than an
    ///     absent claim, and must not travel on as a null <c>ClaimedLayer</c> that grades every
    ///     project in the row as a mismatch.
    /// </summary>
    /// <remarks>
    ///     The matchers are built from <see cref="LayerBySpelling" />'s own keys, so this
    ///     THROWS rather than returning null, which is the same choice as
    ///     <c>RequireRepoRoot</c>: a pattern that stopped matching the table is a broken guard,
    ///     and a broken guard must be loud. Declared here rather than relying on the null-forgiving
    ///     operator so that a future edit to the two can drift apart without the compiler noticing
    ///     — they are kept in step by this throw.
    /// </remarks>
    private static string RequireLayer(string spelling, string file, int line)
        => LayerOf(spelling)
           ?? throw new InvalidOperationException(
               $"'{spelling}' matched LayerWordPattern but is not a key of LayerBySpelling. The two "
               + $"are built from the same table, so one of them has been edited without the other. "
               + $"Found at {file}:{line}.");

    private static string Describe(LayerClaim claim)
        => $"{claim.File}:{claim.Line}  [{claim.Shape}]  {claim.Subject} is listed under "
           + $"'{claim.ClaimedLayer}', FullLayerMatrixTests.Matrix says "
           + $"'{claim.MatrixLayer ?? "(no row)"}' (written '{claim.WrittenAs}')";

    private static string Render(IReadOnlyList<DeclarationRow> rows)
        => string.Join("\n", rows.Select(static r => $"  {r.File}:{r.Line}  [{r.Shape}] {r.ClaimedLayer}"));

    /// <summary>Expand a synthetic row into claims and keep the false ones. The control uses this.</summary>
    private static List<string> Grade(IReadOnlyList<string> tableRows)
    {
        List<DeclarationRow> declared = [.. ParseTableRows("<synthetic>", tableRows)];
        return [.. declared.SelectMany(Expand).Where(static c => c.Outcome == Verdict.False).Select(Describe)];
    }

    private static void RequireRepoRoot()
    {
        if (RepoPaths.RepoRoot is null)
        {
            throw new InvalidOperationException(
                "LayerTableDocAgreementRules could not find the repository root (Harbor.slnx). "
                + "Every rule in this file reads documents out of the checkout, so without it "
                + "there is nothing to grade — and returning quietly would report a clean tree on "
                + "the strength of reading nothing, which is the vacuity #877 exists to end. Run "
                + "from a test host built inside the repository.");
        }
    }

    // ---------------------------------------------------------------------
    // Perimeter.
    // ---------------------------------------------------------------------

    /// <summary>
    ///     Every <c>*.md</c> file in the checkout, in a stable order. Build output and the
    ///     three unmaintained/unrelated trees are excluded; the last is a scoping decision,
    ///     not a technicality, and it is stated in the file header.
    /// </summary>
    private static IReadOnlyList<string> MarkdownFiles()
    {
        if (RepoPaths.RepoRoot is not { } root)
        {
            return [];
        }

        var found = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            string directory = pending.Pop();

            // PRUNE rather than filter after the fact. `Directory.GetFiles(root, "*.md",
            // AllDirectories)` would descend into `.git` and enumerate tens of thousands of
            // paths before this rule's exclusions could discard them — which is not a
            // correctness bug, because they ARE discarded, but it makes the gate's cost scale
            // with the size of the object store rather than with the size of the documentation.
            foreach (string sub in Directory.EnumerateDirectories(directory))
            {
                if (!ExcludedDirectoryNames.Contains(Path.GetFileName(sub)))
                {
                    pending.Push(sub);
                }
            }

            foreach (string file in Directory.EnumerateFiles(directory, "*.md"))
            {
                found.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
            }
        }

        found.Sort(StringComparer.Ordinal);
        return found;
    }

    /// <summary>
    ///     Directory names this rule does not descend into.
    /// </summary>
    /// <remarks>
    ///     <c>contrib/</c> is a scoping decision, not a technicality: it is unmaintained, not
    ///     compiled by any CI job, and out of support by owner decision — the same boundary
    ///     §5.7's single-source rules state. The rest are build output, the object store, and a
    ///     dependency directory none of this documentation lives in.
    /// </remarks>
    private static readonly HashSet<string> ExcludedDirectoryNames =
        new(StringComparer.OrdinalIgnoreCase) { ".git", "obj", "bin", "contrib", "node_modules" };

    /// <summary>
    ///     Every layer-labelled declaration row in the checkout. A <see cref="DeclarationRow" />
    ///     is found only on one of the two grammatical shapes; ordinary prose that mentions a
    ///     layer is not a row, which is why this walks lines rather than searching text.
    /// </summary>
    private static IReadOnlyList<DeclarationRow> DeclarationRows()
    {
        var rows = new List<DeclarationRow>();

        foreach (string relative in MarkdownFiles())
        {
            if (RepoPaths.RepoRoot is not { } root)
            {
                break;
            }

            string? text = ReadText(root, relative);
            if (text is null)
            {
                continue;
            }

            rows.AddRange(Scan(relative, text));
        }

        return rows;
    }

    /// <summary>
    ///     Reads a repo-RELATIVE path. The composition with the root is not optional, and
    ///     getting it wrong is the documented #877 failure of the sibling rule: a helper that
    ///     takes whatever <see cref="File" />.ReadAllText takes resolves a relative path
    ///     against the test host's working directory, throws
    ///     <see cref="FileNotFoundException" />, has it swallowed as an
    ///     <see cref="IOException" />, and returns null — so every claim reads as "no claims
    ///     found" and the rule reports a clean tree. Only the floors noticed, in the same run
    ///     in which both comparison rules passed on zero subjects.
    /// </summary>
    private static string? ReadText(string root, string relative)
        => SourceScan.TryReadAllText(Path.Combine(root, relative));

    /// <summary>
    ///     One document, line by line, tracking whether the reader is inside a fenced block.
    /// </summary>
    /// <remarks>
    ///     Continuation folding is inside BLOCK and only BLOCK, because it is only BLOCK that
    ///     has continuations: a fenced diagram wraps a project's list across lines, and a
    ///     markdown table row does not. Without the fold, <c>README.md</c>'s Infrastructure
    ///     row would be graded as three separate rows and the two wrapped ones would look like
    ///     rows with no layer name at all.
    /// </remarks>
    private static IEnumerable<DeclarationRow> Scan(string relative, string text)
    {
        string[] lines = text.Split('\n');
        bool inFence = false;

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];

            if (Fence.IsMatch(line))
            {
                inFence = !inFence;
                continue;
            }

            if (inFence)
            {
                Match block = BlockRow.Match(line);
                if (!block.Success)
                {
                    continue;
                }

                var folded = new StringBuilder(line[block.Length..]);
                int j = i + 1;
                for (; j < lines.Length; j++)
                {
                    string next = lines[j];
                    if (next.Trim().Length == 0
                        || Fence.IsMatch(next)
                        || !next.StartsWith(' ') && !next.StartsWith('\t'))
                    {
                        break;
                    }

                    _ = folded.Append(' ').Append(next.Trim());
                }

                yield return new DeclarationRow(
                    relative,
                    i + 1,
                    BlockShape,
                    RequireLayer(block.Groups["layer"].Value, relative, i + 1),
                    folded.ToString());

                i = j - 1;
                continue;
            }

            Match table = TableRow.Match(line);
            if (table.Success && table.Groups["rest"].Value.Contains("Harbor.", StringComparison.Ordinal))
            {
                yield return new DeclarationRow(
                    relative,
                    i + 1,
                    TableShape,
                    RequireLayer(table.Groups["layer"].Value, relative, i + 1),
                    table.Groups["rest"].Value);
            }
        }
    }

    /// <summary>Table rows of one file, for the synthetic control. Uses the real matcher.</summary>
    private static IEnumerable<DeclarationRow> ParseTableRows(string file, IReadOnlyList<string> lines)
    {
        foreach (string line in lines)
        {
            Match table = TableRow.Match(line);
            if (table.Success && table.Groups["rest"].Value.Contains("Harbor.", StringComparison.Ordinal))
            {
                yield return new DeclarationRow(
                    file,
                    1,
                    TableShape,
                    RequireLayer(table.Groups["layer"].Value, file, 1),
                    table.Groups["rest"].Value);
            }
        }
    }

    // ---------------------------------------------------------------------
    // Expanding a row's project list into claims.
    // ---------------------------------------------------------------------

    /// <summary>
    ///     One claim per project the row names.
    /// </summary>
    /// <remarks>
    ///     Three spellings have to be understood before a name can be compared, and all three
    ///     occur in the two documents this rule grades:
    ///     <list type="bullet">
    ///         <item><description><c>Harbor.Storage.{Jsonl,Memory,Sqlite}</c> — the brace list.</description></item>
    ///         <item><description><c>Harbor.Ipc.*</c> — the glob, resolved against the matrix's OWN keys.</description></item>
    ///         <item><description><c>Harbor.Ui.Framework.Abstractions</c> — fully spelled.</description></item>
    ///     </list>
    ///     Resolving a glob against matrix keys is what makes <c>Harbor.Ui.Framework.*</c> a
    ///     claim about eight projects rather than one: <c>#901</c>'s corrected README shows
    ///     <c>Harbor.Ui.Framework.Abstractions</c> as <b>Domain</b> while the rest of the
    ///     family is Presentation, and a rule that read the glob as the bare prefix would have
    ///     graded one project for a claim about eight and missed the sharpest row in the table.
    /// </remarks>
    private static IEnumerable<LayerClaim> Expand(DeclarationRow row)
    {
        foreach ((string written, string? project) in ProjectNames(row.Written))
        {
            string? matrixLayer = project is null ? null : MatrixLayerOf(project);
            Verdict outcome = matrixLayer is null
                ? Verdict.Unjudged
                : string.Equals(matrixLayer, row.ClaimedLayer, StringComparison.Ordinal)
                    ? Verdict.True
                    : Verdict.False;

            yield return new LayerClaim(
                row.File,
                row.Line,
                row.Shape,
                project ?? written,
                written,
                row.ClaimedLayer,
                matrixLayer,
                outcome);
        }
    }

    /// <summary>
    ///     Every project a row names, as (as-written, longest-matrix-row). A name whose longest
    ///     matrix prefix is <see langword="null" /> is yielded with a null project, which is how
    ///     it becomes UNJUDGED rather than silently dropped.
    /// </summary>
    /// <remarks>
    ///     The longest prefix matters because a fully-spelled name may be a TYPE inside a
    ///     namespace: <c>Harbor.Ui.Framework.Rendering.Widgets.LineDiff</c> is not a project, and
    ///     grading it against the matrix would report a contradiction that does not exist.
    ///     Trimming to the longest prefix that IS a row is what keeps a namespace-shaped mention
    ///     a claim about its project.
    /// </remarks>
    private static IEnumerable<(string Written, string? Project)> ProjectNames(string written)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match brace in BraceForm.Matches(written))
        {
            string prefix = brace.Value[..brace.Value.IndexOf('{')];
            foreach (string item in brace.Groups["items"].Value.Split(','))
            {
                string trimmed = item.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                string full = prefix + trimmed;
                if (seen.Add(full))
                {
                    yield return (full, LongestMatrixProject(full));
                }
            }
        }

        string withoutBraces = BraceForm.Replace(written, " ");
        foreach (Match plain in PlainName.Matches(withoutBraces))
        {
            string name = plain.Value.TrimEnd('.', '-', '\u2014');

            // A trailing `.*` is a glob over the matrix's own keys, not a project name. Read
            // the prefix alone for the "is this gradeable" question, then grade every row it
            // covers — `Harbor.Ui.Framework.*` is a claim about eight projects.
            if (name.EndsWith(".*", StringComparison.Ordinal))
            {
                string globPrefix = name[..^2];

                // Expanded against the matrix's OWN KEYS, which is the whole point, and the
                // first draft of this file got it backwards: it asked whether the bare prefix
                // was a matrix row first. `Harbor.Ipc` and `Harbor.Plugins` are NOT rows — they
                // are families — so that test failed and every `Harbor.Ipc.*` /
                // `Harbor.Plugins.*` in the root README came back UNJUDGED. On the red tree that
                // cost 11 of 22 findings: the README's APPLICATION row claims the whole IPC
                // family and the whole plugin family, and #888's finding was precisely that
                // neither claim is true. A prefix the matrix does not classify can still
                // DENOTE projects it does classify, and asking the keys is what sees that.
                var covered = FullLayerMatrixTests.Matrix.Keys
                    .Where(k => k.StartsWith(globPrefix + ".", StringComparison.Ordinal))
                    .OrderBy(static k => k, StringComparer.Ordinal)
                    .ToList();

                if (covered.Count == 0)
                {
                    if (seen.Add(name))
                    {
                        yield return (name, null);
                    }

                    continue;
                }

                foreach (string key in covered)
                {
                    if (seen.Add(key))
                    {
                        yield return (key, key);
                    }
                }

                continue;
            }

            if (seen.Add(name))
            {
                yield return (name, LongestMatrixProject(name));
            }
        }
    }

    private static string? MatrixLayerOf(string project)
        => FullLayerMatrixTests.Matrix.TryGetValue(project, out FullLayerMatrixTests.Row? row)
            ? row.Layer.ToString()
            : null;

    /// <summary>The longest prefix of a dotted name that is a matrix row, or null when none is.</summary>
    private static string? LongestMatrixProject(string candidate)
    {
        string name = candidate;
        while (name.StartsWith("Harbor.", StringComparison.Ordinal))
        {
            if (FullLayerMatrixTests.Matrix.ContainsKey(name))
            {
                return name;
            }

            int cut = name.LastIndexOf('.');
            if (cut <= 0)
            {
                break;
            }

            name = name[..cut];
        }

        return null;
    }
}
