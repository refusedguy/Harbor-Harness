// LayerClaimMatchesMatrixRules.cs — the guard for the layer-CLAIM class (#895).
//
// WHY THIS FILE EXISTS
// --------------------
// A project's layer has exactly ONE home: the `Layer` value on its row in
// `FullLayerMatrixTests.Matrix`. That is a machine table and it is enforced —
// `MatrixTable_RespectsLayerRules` and `EverySrcAssembly_ReferenceSet_MatchesMatrix`
// both fail the build on a row that lies about dependencies.
//
// The layer is then RE-TRANSCRIBED BY HAND, all over the repository, and no gate
// compares any of the transcriptions to the source. That is the mechanism, and
// it is named here because it has now produced three findings:
//
//   * #879 — `docs/ARCHITECTURE_LAYERS.md` named three projects in the wrong box.
//   * #896/#751 — the same file, NINE wrong places, all wrong FROM THE START
//     rather than out of date: the matrix has carried `Layer.Presentation` for
//     `Harbor.Desktop.Abstractions` since the row was created (5d2df19f).
//   * #895 — ten sites across eight files repeating the same false claim, seven
//     of them `.cs` comments — plus this file's own measurement, which found the
//     SAME claim alive in the root `README.md`, in `CLAUDE.md`, and in three
//     per-project `README.md` files that neither #751 nor #896 opened.
//
// Three findings in one class is not three coincidences. #879, #896 and #895
// were each found by a human reading a document, or by grepping for the WORDING
// an earlier fix had used ("Domain-labelled", "matrix calls Domain"). A phrase
// search is bounded by the phrases someone already thought of, which is exactly
// why the claim outlived two rounds of fixes across five files: the round that
// fixed §1 never opened the root README, and the round that fixed §1's table
// never opened a per-project README. Fixing #895's ten sites removes ten
// statements and leaves the mechanism, so the mechanism is what this file grades.
//
// The class, as one rule: A DOCUMENT OR COMMENT THAT ATTRIBUTES A LAYER TO A
// HARBOR PROJECT MUST AGREE WITH `FullLayerMatrixTests.Matrix`.
//
// WHY NO EXISTING GATE COULD SEE IT
// ---------------------------------
// Checked, not assumed. `git grep ARCHITECTURE_LAYERS` over `tests/` shows the
// document is only ever CITED, never parsed — every hit is a `Because(...)`
// string or a comment. No rule reads a layer out of markdown.
// `tools/check-doc-cites.py`, the gate that exists precisely because a document
// can be confidently wrong, asks whether a `file:line` still points at a line
// and whether a named type is constructed. It has no layer rule, because a layer
// is not a citation: nothing in the tree says which layer a project is except the
// matrix. The document is therefore not wrong in a way a gate can see — the
// gates exist, and their subject is not what they inspect. That is #649's
// finding verbatim, one layer over.
//
// WHAT IS GRADED — TWO SHAPES, BOTH DERIVED
// -----------------------------------------
// 1. `ReadmeDeclarations_MatchTheMatrix` — every `src/<Project>/README.md` that
//    carries a `## Layer` section, which is this repository's own convention for
//    a project declaring its own layer, checked against `Matrix[<Project>]`.
//    The perimeter is `RepoPaths.EnumerateSrcProjects()` — the same walk
//    `EnforcerIntegrityTests.SrcProjects_AreAllClassified` uses — so a new
//    project selects ITSELF into the perimeter. Thirty-three projects carry the
//    section today; a typed list would be wrong the day a project was added.
//
// 2. `ProseAttributions_MatchTheMatrix` — the two ATTRIBUTIVE wordings a comment
//    uses to bind a layer to a project: `<Layer>-labelled`, and
//    `layer matrix calls|labels|says|names <Layer>`. Both are grammatical, not
//    merely co-occurring, which is the whole reason they are safe to scan and
//    the reason nothing broader is attempted.
//
// WHY NOT A SCAN FOR "PROJECT MENTION + LAYER WORD"
// -------------------------------------------------
// Tried against the corpus and rejected. `TuiEffectHost.cs:23` reads "Domain
// layer — it never needs to reference `Harbor.Application` for agent types": a
// layer word and a project name in one sentence, and no claim about either. A
// co-occurrence rule is either wrong on every legitimate sentence or narrowed
// until it matches nothing — the trap `DefaultModelDocClaimTests` describes for
// provider ids. So the selector is two specific CLAIM SHAPES.
//
// The same reasoning settles the second hazard. A claim whose SUBJECT is a
// pronoun ("this leaf is a published Domain-labelled package") is not gradeable,
// and a guard that guesses reports the wrong thing confidently — which is worse
// than reporting nothing. So a claim that names no project is UNRESOLVED, which
// is RED, and the message says so. The fix is one clause of prose: name the
// project. That is a ratchet, not a workaround — once named, the claim is
// checkable forever, and it can no longer be silenced by rewording, only by
// being right.
//
// WHY A MISSING `## Layer` SECTION IS NOT A VIOLATION
// ----------------------------------------------------
// This grades DECLARATIONS; it does not require them. Twenty matrix projects have
// no such section today (`Harbor.Application`, `Harbor.Registries`, `Harbor.Ipc.*`,
// `Harbor.Tui.*`, `Harbor.Desktop.*`, `Harbor.Lsp`, `Harbor.Terminal.Pty`,
// `Harbor.DesignSystem`). Requiring one would be a documentation project wearing
// a guard's clothes, and #555 freezes new axes. The floors in
// `Declarations_AreFound` close the softer hole instead: a section deleted to
// dodge a finding shrinks a count a reviewer can see.
//
// NON-VACUITY, AND A CONTROL THAT IS NOT CIRCULAR
// ----------------------------------------------
// Every mechanism here goes RED rather than quietly passing:
//   1. `Declarations_AreFound` — floors on the number of `## Layer` sections and
//      on the number GRADED, asserted separately so "matches nothing" and
//      "matches everything" cannot both hide behind one total.
//   2. `ProseClaims_AreFound` — floors PER SHAPE, because a combined total is
//      satisfied by one live shape while the other matches nothing. Rewording is
//      how a scanner dies, and wording is this rule's entire subject.
//   3. `Shapes_GradeTextTheyHaveNeverSeen` — the control. It hands the grader
//      four SYNTHETIC declarations built from four different real matrix rows,
//      two of them wrong, and requires the wrong pair to be reported AND the
//      right pair not to be. The grader cannot pass by recognising a memorised
//      string, and cannot pass by flagging everything: both directions are
//      required, which a planted-bad-input test alone does not give.
//   4. `RequireRepoRoot` THROWS. An early `return` on a missing checkout would
//      turn "the guard could not find what it polices" into a pass — the exact
//      vacuity #877 just spent an issue on.
//
// DELIBERATELY NOT GRADED, AND WHY
// -------------------------------
//   * Whole-tree layer TABLES: the root README's architecture block, the
//     `CLAUDE.md` layer table, §1's ASCII boxes. This file MEASURES the first
//     two rather than failing on them, and the reason is in the PR body — those
//     tables disagree with the matrix about roughly twenty projects each, almost
//     none of them the `Harbor.Desktop.Abstractions` claim, and turning them red
//     here would be a much larger change wearing this issue's number. That they
//     are wrong is not in doubt; §895's finding is that nobody could see it, and
//     this file's finding is that the class extends past the ten sites.
//   * `Harbor.Tui.Abstractions`, `Harbor.Scripting`, `Harbor.Desktop.CodeEditor`
//     and the other projects a stale plan names. They are not matrix rows, so
//     there is no layer to disagree with; a rule that judged a name the matrix
//     does not have would be inventing one.
//   * `docs/shell-recon-specs.md:227` — "Add `Harbor.Desktop.Abstractions` to
//     the Domain layer". A 2025 recon to-do list; out of both shapes by grammar
//     (an instruction, not an attribution), and a historical record is not made
//     current by editing it. Named so the omission is a decision with a reason
//     rather than a silence.
//   * `contrib/` — unmaintained, in no CI job.
//   * Projects the matrix does not classify. `OutOfScopeAssemblies`
//     (`Harbor.Plugins.Host`) and `SharedSourceFolders` (`Harbor.Providers.Shared`,
//     `Harbor.Storage.Shared`) have no layer, so a claim about one is not a
//     contradiction; `Harbor.CodeGen` is a source generator.
//
// STATUS: never compiled locally. Local dotnet is out of scope in the authoring
// environment, so the red run's CI log is the only execution of these matchers
// there has ever been, and the commit after this one transcribes it.

using System.Text;
using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     #895: a document or comment that attributes a LAYER to a Harbor project
///     must agree with <see cref="FullLayerMatrixTests.Matrix" />, the one place
///     the answer lives. See the file header for the mechanism, the two claim
///     shapes, the non-vacuity controls, and what is deliberately not graded.
/// </summary>
public sealed class LayerClaimMatchesMatrixRules
{
    // ---------------------------------------------------------------------
    // The claim shapes.
    // ---------------------------------------------------------------------

    /// <summary>
    ///     A <c>## Layer</c> heading, alone on its line. The <c>\r?</c> is not
    ///     decoration: in Multiline mode <c>$</c> matches before the <c>\n</c>,
    ///     so on a CRLF checkout a heading written without it would not match
    ///     at all — and a shape that stops matching on Windows is exactly the
    ///     quiet failure the floors below exist to catch.
    /// </summary>
    private static readonly Regex LayerSection = new(
        @"^[ \t]*#{2,6}[ \t]*Layer[ \t]*:?[ \t]*\r?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    /// <summary>
    ///     The word a declaration OPENS with: the first run of letters after any
    ///     leading <c>*</c> and whitespace. Anchored at the start on purpose —
    ///     "**Storage infrastructure (shared source).**" and "**Provider
    ///     infrastructure (shared source).**" both CONTAIN "infrastructure" and
    ///     declare no layer at all, because their first word names the family.
    ///     Only the leading word is a claim.
    /// </summary>
    private static readonly Regex LeadingWord = new(
        @"^[ \t*]*(?<word>[A-Za-z]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    ///     "&lt;Layer&gt;-labelled" — an adjective bound to the noun it follows,
    ///     so it cannot be a statement about some other project in the sentence.
    /// </summary>
    private static readonly Regex Labelled = new(
        @"\b(?<layer>Domain|Presentation|Application|Infrastructure|Composition[\s\-]?Root)-[Ll]abelled\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    ///     "the layer matrix calls|labels|says|names &lt;Layer&gt;" — an explicit
    ///     attribution to the very artifact this rule checks against, so the
    ///     subject is the project being discussed. Four verb spellings ship
    ///     today and all four are the same claim.
    /// </summary>
    private static readonly Regex MatrixSays = new(
        @"\blayer\s+matrix\s+(?:calls|labels|says|names)\s+(?<layer>Domain|Presentation|Application|Infrastructure|Composition[\s\-]?Root)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>A Harbor project name as it appears in prose, namespace-shaped included.</summary>
    private static readonly Regex ProjectName = new(
        @"\bHarbor\.[A-Za-z0-9]+(?:\.[A-Za-z0-9]+)*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    ///     How far from a prose claim its subject may sit. A claim naming no
    ///     project inside this window is UNRESOLVED and red, never guessed at.
    ///     600 characters is about a comment paragraph, which is the unit a
    ///     comment reasons in.
    /// </summary>
    private const int SubjectWindow = 600;

    /// <summary>
    ///     Floors, set below today's counts and meant as a ratchet: a
    ///     legitimate removal lowers them in the same diff, where a reviewer can
    ///     see it. All four are MINIMA, so the assertions are
    ///     <c>IsGreaterThan(min - 1)</c>.
    /// </summary>
    private const int MinLayerSections = 25;

    private const int MinGradedDeclarations = 20;
    private const int MinLabelledClaims = 3;
    private const int MinMatrixSaysClaims = 2;

    // ---------------------------------------------------------------------
    // Tests.
    // ---------------------------------------------------------------------

    /// <summary>
    ///     Shape 1 — a project README's <c>## Layer</c> section, against the
    ///     matrix. The perimeter is derived from the csproj walk, so a new
    ///     project is in scope the day its csproj exists.
    /// </summary>
    [Test]
    public async Task ReadmeDeclarations_MatchTheMatrix()
    {
        RequireRepoRoot();
        List<string> failures =
            [.. ReadmeClaims().Where(c => c.Outcome == Verdict.False).Select(Describe)];

        await Assert.That(failures.Count).IsEqualTo(0).Because(
            "A project README's `## Layer` section states which layer the project is in, and that "
            + "answer has one home: the `Layer` value on its row in FullLayerMatrixTests.Matrix. "
            + "The row is enforced — MatrixTable_RespectsLayerRules and "
            + "EverySrcAssembly_ReferenceSet_MatchesMatrix both fail the build on a row that lies "
            + "about dependencies — while the README is a comment. #751/#896 found nine such "
            + "placements wrong in docs/ARCHITECTURE_LAYERS.md and fixed that document; the same "
            + "false claim survived in the root README, in CLAUDE.md and in three per-project "
            + "READMEs, because each round of fixes opened the file the previous round had named. "
            + "One rule rather than one file. A project with NO matrix row — `Harbor.Plugins.Host`, "
            + "out of scope by design — is not graded here; it cannot be a contradiction with an "
            + "answer that does not exist. Offending declarations:\n"
            + string.Join("\n", failures));
    }

    /// <summary>
    ///     Shape 2 — the two attributive prose wordings, in <c>.cs</c> comments
    ///     and string literals and in markdown.
    /// </summary>
    /// <remarks>
    ///     Comments are read RAW here, never stripped.
    ///     <c>SourceCommentStripper</c> exists so a source scan grading a code
    ///     SHAPE does not grade the prose documenting it; this rule grades prose,
    ///     and stripping the prose would leave it nothing to read. The claims it
    ///     must find live in exactly the text the stripper would delete.
    /// </remarks>
    [Test]
    public async Task ProseAttributions_MatchTheMatrix()
    {
        RequireRepoRoot();
        List<string> failures =
        [
            .. ProseClaims()
                .Where(c => c.Outcome is Verdict.False or Verdict.Unresolved)
                .Select(Describe),
        ];

        await Assert.That(failures.Count).IsEqualTo(0).Because(
            "A comment or a document that says a project IS a layer is making a claim the matrix "
            + "already answers, and every one of #895's ten sites was that claim being wrong: "
            + "'Harbor.Desktop.Abstractions is a published, Domain-labelled package' — in the "
            + "guard's own header, in its XML doc, in its assertion message, and in six more "
            + "files. UNRESOLVED counts as a failure on purpose: the claim names no project within "
            + "the window, so its subject is a pronoun and no grader can check it. Naming the "
            + "project is the fix, and it is why this is a rule and not a word list. Offending "
            + "claims:\n" + string.Join("\n", failures));
    }

    /// <summary>
    ///     Floors, counted separately so that "matches nothing" and "matches
    ///     everything" cannot both hide behind one total.
    /// </summary>
    [Test]
    public async Task Declarations_AreFound()
    {
        RequireRepoRoot();
        IReadOnlyList<LayerClaim> claims = ReadmeClaims();
        int sections = claims.Count;
        int graded = claims.Count(static c => c.ClaimedLayer is not null);

        await Assert.That(sections).IsGreaterThan(MinLayerSections - 1).Because(
            "The perimeter is 'every src project README that carries a `## Layer` section', and a "
            + "README can stop carrying one. Thirty-three carry it today. A repository where that "
            + "number collapses has not been made more correct, it has been made quiet — and a "
            + "guard that reports a clean tree because it read less of it is the #877 failure. If "
            + "a refactor really removes sections, lower this floor in the same diff. Found "
            + sections + " `## Layer` sections.");

        await Assert.That(graded).IsGreaterThan(MinGradedDeclarations - 1).Because(
            "Sections exist, but one whose LEADING word is not a layer name grades nothing. That is "
            + "correct for '**Storage infrastructure (shared source).**' and wrong if it spreads: "
            + "the claim is the first word, so '**Infrastructure (helper).**' is graded while "
            + "'**Helper (infrastructure).**' is not. Found " + graded + " graded of " + sections
            + ".");
    }

    /// <summary>
    ///     A floor per prose shape, for the same reason. A combined total would
    ///     be satisfied by one live shape while the other matched nothing.
    /// </summary>
    [Test]
    public async Task ProseClaims_AreFound()
    {
        RequireRepoRoot();
        IReadOnlyList<LayerClaim> claims = ProseClaims();
        int labelled = claims.Count(static c => c.Shape == LabelledShape);
        int says = claims.Count(static c => c.Shape == MatrixSaysShape);

        await Assert.That(labelled).IsGreaterThan(MinLabelledClaims - 1).Because(
            "A rule that matches no wording reports nothing and is believed — the failure "
            + "docs.yml's selftest job exists to catch for the markdown gates. If the "
            + "'<Layer>-labelled' shape stops matching, the prose stating a layer is no longer "
            + "being checked at all and the only evidence would be this rule passing. Found "
            + labelled + ".");

        await Assert.That(says).IsGreaterThan(MinMatrixSaysClaims - 1).Because(
            "The second shape, floored separately for the reason above: a total is satisfied by one "
            + "live shape. Found " + says + " 'layer matrix <verb> <Layer>' claims.");
    }

    /// <summary>
    ///     The control, and the reason this file is not circular: four synthetic
    ///     declarations built from four different real matrix rows, two of them
    ///     wrong. The grader must report the wrong pair and pass the right pair,
    ///     so it cannot pass by recognising a memorised string, and cannot pass
    ///     by flagging everything either. A planted-bad-input test supplies only
    ///     the first half of that.
    /// </summary>
    [Test]
    public async Task Shapes_GradeTextTheyHaveNeverSeen()
    {
        // Two Presentation rows and two Infrastructure rows, none of them the
        // projects #895 was about, and the two layers differ so a grader that
        // only ever saw one layer cannot satisfy the set.
        (string Project, string Wrong, string Right)[] probes =
        [
            ("Harbor.Ui.Framework.Rendering", "Domain", "Presentation"),
            ("Harbor.Tui.Notifications", "Domain", "Presentation"),
            ("Harbor.Telemetry.Core", "Presentation", "Infrastructure"),
            ("Harbor.Transport.Remote", "Domain", "Infrastructure"),
        ];

        var missed = new List<string>();
        var overReported = new List<string>();

        foreach ((string project, string wrong, string right) in probes)
        {
            LayerClaim bad = GradeDeclaration(project, wrong);
            LayerClaim good = GradeDeclaration(project, right);

            if (bad.Outcome is not Verdict.False)
            {
                missed.Add($"{project} declared {wrong} -> {bad.Outcome}, expected False");
            }

            if (good.Outcome is not Verdict.True)
            {
                overReported.Add($"{project} declared {right} -> {good.Outcome}, expected True");
            }
        }

        await Assert.That(missed.Count).IsEqualTo(0).Because(
            "A stale declaration, in text this file has never read, must go red. If it does not, "
            + "the matcher is not reading the layer word — which is the only thing this rule "
            + "compares. Missed:\n" + string.Join("\n", missed));

        await Assert.That(overReported.Count).IsEqualTo(0).Because(
            "The same control in the other direction, and it is the half that matters. A matcher "
            + "that reports every declaration is not stricter than one that reports none; it is a "
            + "rule whose output nobody reads, and it would have turned this file's own red run "
            + "into a list of everything. Over-reported:\n" + string.Join("\n", overReported));
    }

    // ---------------------------------------------------------------------
    // Verdicts and rendering.
    // ---------------------------------------------------------------------

    /// <summary>Shape names, used as the per-shape floor keys.</summary>
    private const string LabelledShape = "-labelled";

    private const string MatrixSaysShape = "layer matrix <verb>";
    private const string ReadmeShape = "## Layer section";

    private enum Verdict
    {
        /// <summary>The claim names the layer the matrix assigns to its subject.</summary>
        True,

        /// <summary>The claim names a different layer than the matrix assigns.</summary>
        False,

        /// <summary>
        ///     The claim names no project the matrix classifies, so its subject
        ///     cannot be established. Red on purpose — see the header.
        /// </summary>
        Unresolved,

        /// <summary>
        ///     No layer word in the shape's grammar: the text declares no layer,
        ///     which is not a violation.
        /// </summary>
        NotAClaim,
    }

    /// <summary>
    ///     One graded claim, with enough context to fix it from the message
    ///     alone. The verdict member is named <c>Outcome</c> rather than
    ///     <c>Verdict</c> so that it does not share a name with the enum it
    ///     holds.
    /// </summary>
    private sealed record LayerClaim(
        string File,
        int Line,
        string Shape,
        string? Subject,
        string? ClaimedLayer,
        string? TrueLayer,
        Verdict Outcome,
        string Snippet);

    /// <summary>
    ///     The layer the matrix assigns, or <see langword="null" /> when the
    ///     project has no row — <c>OutOfScopeAssemblies</c>,
    ///     <c>SharedSourceFolders</c> and <c>Harbor.CodeGen</c> have none, and a
    ///     claim about one of them contradicts nothing.
    /// </summary>
    private static string? MatrixLayerOf(string project)
        => FullLayerMatrixTests.Matrix.TryGetValue(project, out FullLayerMatrixTests.Row row)
            ? Canonical(row.Layer.ToString())
            : null;

    /// <summary>
    ///     Normalise a layer spelling to the enum's own name, so "Composition
    ///     Root", "Composition-Root" and "CompositionRoot" are one layer and not
    ///     three.
    /// </summary>
    private static string? Canonical(string? word)
    {
        if (string.IsNullOrEmpty(word))
        {
            return null;
        }

        var key = new StringBuilder(word.Length);
        foreach (char c in word)
        {
            if (char.IsLetterOrDigit(c))
            {
                key.Append(char.ToLowerInvariant(c));
            }
        }

        return key.ToString() switch
        {
            "domain" => nameof(FullLayerMatrixTests.Layer.Domain),
            "presentation" => nameof(FullLayerMatrixTests.Layer.Presentation),
            "application" => nameof(FullLayerMatrixTests.Layer.Application),
            "infrastructure" => nameof(FullLayerMatrixTests.Layer.Infrastructure),
            "compositionroot" => nameof(FullLayerMatrixTests.Layer.CompositionRoot),
            _ => null,
        };
    }

    private static LayerClaim Build(
        string file,
        int line,
        string shape,
        string? subject,
        string? claimed,
        string snippet)
    {
        string? trueLayer = subject is null ? null : MatrixLayerOf(subject);
        Verdict outcome;

        if (claimed is null)
        {
            outcome = Verdict.NotAClaim;
        }
        else if (trueLayer is null)
        {
            // A claim about a project the matrix does not classify. It is not a
            // contradiction, and it is not graded as one — but it is not
            // silently fine either, so it stays a distinct outcome and the
            // caller decides. `Harbor.Plugins.Host`'s README claims
            // Infrastructure and the matrix has no row for it, because that
            // project is out of scope by design.
            outcome = Verdict.Unresolved;
        }
        else
        {
            outcome = string.Equals(claimed, trueLayer, StringComparison.Ordinal)
                ? Verdict.True
                : Verdict.False;
        }

        return new LayerClaim(file, line, shape, subject, claimed, trueLayer, outcome, snippet);
    }

    private static string Describe(LayerClaim claim)
    {
        string subject = claim.Subject ?? "(no project named — subject unresolvable)";
        string claimed = claim.ClaimedLayer ?? "(no layer word)";
        string actual = claim.TrueLayer ?? "(the matrix has no row for it)";
        return $"{claim.File}:{claim.Line}  [{claim.Shape}]  {subject} says {claimed}, "
               + $"matrix says {actual}\n      {claim.Snippet}";
    }

    /// <summary>
    ///     Grades a declaration whose subject is given, for the synthetic
    ///     control. The real <c>## Layer</c> path derives the subject from the
    ///     README's directory; this takes it as a parameter so the control can
    ///     exercise the same comparison on a project of its choosing.
    /// </summary>
    private static LayerClaim GradeDeclaration(string project, string claimedLayer)
        => Build("<synthetic>", 1, ReadmeShape, project, Canonical(claimedLayer), "synthetic");

    private static int LineOf(string text, int index)
    {
        int line = 1;
        int limit = Math.Min(index, text.Length);
        for (int i = 0; i < limit; i++)
        {
            if (text[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    private static string SnippetAt(string text, int index)
    {
        int from = Math.Max(0, index - 90);
        int to = Math.Min(text.Length, index + 90);
        string flat = text[from..to].Replace("\r", string.Empty).Replace('\n', ' ').Trim();
        return flat.Length <= 160 ? flat : flat[..157] + "...";
    }

    private static void RequireRepoRoot()
    {
        if (RepoPaths.RepoRoot is null)
        {
            throw new InvalidOperationException(
                "LayerClaimMatchesMatrixRules could not find the repository root (Harbor.slnx). "
                + "Every rule in this file reads documents and comments out of the checkout, so "
                + "without it there is nothing to grade — and returning quietly would report a "
                + "clean tree on the strength of reading nothing, which is the vacuity #877 exists "
                + "to end. Run from a test host built inside the repository.");
        }
    }

    // ---------------------------------------------------------------------
    // Shape 1 — a project README's `## Layer` section.
    // ---------------------------------------------------------------------

    private static IReadOnlyList<LayerClaim> ReadmeClaims()
    {
        var claims = new List<LayerClaim>();
        foreach ((string relative, string project) in ProjectReadmes())
        {
            string? text = SourceScan.TryReadAllText(relative);
            if (text is null)
            {
                continue;
            }

            Match heading = LayerSection.Match(text);
            if (!heading.Success)
            {
                continue;
            }

            // The declaration is the first line after the heading that is neither
            // blank nor another heading: `## Layer`, a blank line, one sentence.
            // Stopping at the next `#` is what keeps the section's BODY out of
            // the claim.
            int cursor = heading.Index + heading.Length;
            int lineNumber = LineOf(text, cursor);
            string? declaration = null;
            int declarationLine = 0;

            foreach (string raw in text[cursor..].Split('\n'))
            {
                lineNumber++;
                string trimmed = raw.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                if (trimmed.StartsWith('#'))
                {
                    break;
                }

                declaration = trimmed;
                declarationLine = lineNumber;
                break;
            }

            if (declaration is null)
            {
                continue;
            }

            Match word = LeadingWord.Match(declaration);
            claims.Add(Build(
                relative,
                declarationLine,
                ReadmeShape,
                project,
                word.Success ? Canonical(word.Groups["word"].Value) : null,
                Truncate(declaration)));
        }

        claims.Sort(static (a, b) => string.CompareOrdinal(a.File, b.File));
        return claims;
    }

    /// <summary>
    ///     Every <c>src/&lt;Project&gt;/README.md</c>, paired with the project
    ///     the directory is named after. Derived from the csproj walk rather
    ///     than a directory glob, so a folder without a project is not in the
    ///     perimeter and a project without a README is.
    /// </summary>
    private static IReadOnlyList<(string Path, string Project)> ProjectReadmes()
    {
        var found = new List<(string Path, string Project)>();
        if (RepoPaths.RepoRoot is not { } root)
        {
            return found;
        }

        foreach (string csproj in RepoPaths.EnumerateSrcProjects())
        {
            string? directory = Path.GetDirectoryName(csproj);
            if (directory is null)
            {
                continue;
            }

            string readme = Path.Combine(directory, "README.md");
            if (File.Exists(readme))
            {
                found.Add((Path.GetRelativePath(root, readme).Replace('\\', '/'), Path.GetFileName(directory)));
            }
        }

        found.Sort(static (a, b) => string.CompareOrdinal(a.Path, b.Path));
        return found;
    }

    // ---------------------------------------------------------------------
    // Shape 2 — attributive prose.
    // ---------------------------------------------------------------------

    private static IReadOnlyList<LayerClaim> ProseClaims()
    {
        if (RepoPaths.RepoRoot is not { } root)
        {
            return [];
        }

        var files = new List<string>();

        // `SourceScan.EnumerateCsFiles` is NOT usable here, and using it would be
        // the #877 bug a second time: `IsBuildOutput` rejects `/tests/`, so the
        // call returns an empty list BY CONSTRUCTION — and three of the claims
        // this rule grades live in tests/Harbor.Architecture.Tests, two of them
        // in the guard #895 is about. Hence the explicit walk.
        files.AddRange(WalkCs(root, "src"));
        files.AddRange(WalkCs(root, "apps"));
        files.AddRange(WalkCs(root, "tests/Harbor.Architecture.Tests"));

        foreach ((string readme, _) in ProjectReadmes())
        {
            files.Add(readme);
        }

        files.AddRange(TopLevelMarkdown(root));

        var claims = new List<LayerClaim>();
        foreach (string relative in files.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            string? text = SourceScan.TryReadAllText(relative);
            if (text is null)
            {
                continue;
            }

            claims.AddRange(GrammaticalClaims(relative, text, OwningProject(relative)));
        }

        return claims;
    }

    private static IEnumerable<string> WalkCs(string root, string relativeTree)
    {
        string directory = Path.Combine(root, relativeTree);
        if (!Directory.Exists(directory))
        {
            yield break;
        }

        foreach (string path in Directory
            .EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
        {
            string normalised = path.Replace('\\', '/');
            if (normalised.Contains("/obj/", StringComparison.Ordinal)
                || normalised.Contains("/bin/", StringComparison.Ordinal)
                || normalised.Contains("/contrib/", StringComparison.Ordinal))
            {
                continue;
            }

            yield return Path.GetRelativePath(root, path).Replace('\\', '/');
        }
    }

    /// <summary>
    ///     The root <c>*.md</c> files and the top level of <c>docs/</c> — a
    ///     bounded perimeter, stated rather than derived, because a recursive
    ///     walk of <c>docs/</c> reaches archived sprint notes that are records
    ///     of what was true when they were written. Named in the header as not
    ///     graded; named here so the boundary is visible in the code.
    /// </summary>
    private static IEnumerable<string> TopLevelMarkdown(string root)
    {
        foreach (string path in Directory.EnumerateFiles(root, "*.md", SearchOption.TopDirectoryOnly))
        {
            yield return Path.GetRelativePath(root, path).Replace('\\', '/');
        }

        string docs = Path.Combine(root, "docs");
        if (Directory.Exists(docs))
        {
            foreach (string path in Directory.EnumerateFiles(docs, "*.md", SearchOption.TopDirectoryOnly))
            {
                yield return Path.GetRelativePath(root, path).Replace('\\', '/');
            }
        }
    }

    /// <summary>
    ///     The project a file belongs to, or <see langword="null" />. The last
    ///     fallback for a prose claim that names no project: a claim in
    ///     <c>src/Harbor.Desktop.Abstractions/README.md</c> is about
    ///     <c>Harbor.Desktop.Abstractions</c> whether or not it says so, and the
    ///     alternative is a rule that reports nothing about the most-read prose
    ///     in the repository.
    /// </summary>
    private static string? OwningProject(string relative)
    {
        ReadOnlySpan<char> span = relative.AsSpan();
        if (!span.StartsWith("src/", StringComparison.Ordinal))
        {
            return null;
        }

        ReadOnlySpan<char> rest = span[4..];
        int slash = rest.IndexOf('/');
        return slash <= 0 ? null : rest[..slash].ToString();
    }

    /// <summary>
    ///     Both shapes over one file's text. A claim's SUBJECT is the project
    ///     named closest to it — searching backward first, then forward, inside
    ///     <see cref="SubjectWindow" /> characters. Backward first because a
    ///     claim states what the sentence has already introduced.
    /// </summary>
    private static IEnumerable<LayerClaim> GrammaticalClaims(
        string relative,
        string text,
        string? owning)
    {
        foreach ((Regex shape, string name) in
                 new[] { (Labelled, LabelledShape), (MatrixSays, MatrixSaysShape) })
        {
            foreach (Match match in shape.Matches(text))
            {
                string? claimed = Canonical(match.Groups["layer"].Value);
                if (claimed is null)
                {
                    continue;
                }

                string? subject = ResolveSubject(text, match.Index, owning);
                yield return Build(
                    relative,
                    LineOf(text, match.Index),
                    name,
                    subject,
                    claimed,
                    SnippetAt(text, match.Index));
            }
        }
    }

    private static string? ResolveSubject(string text, int claimIndex, string? owning)
    {
        int from = Math.Max(0, claimIndex - SubjectWindow);
        int to = Math.Min(text.Length, claimIndex + SubjectWindow);

        string? nearestBefore = null;
        int nearestBeforeEnd = int.MinValue;
        string? nearestAfter = null;
        int nearestAfterStart = int.MaxValue;

        foreach (Match match in ProjectName.Matches(text[from..to]))
        {
            int start = from + match.Index;
            int end = start + match.Length;

            // `Harbor.Ui.Framework.Configuration.ICommonConfigModelRefReader` is a
            // TYPE inside a namespace, not a project. Trim the match to the
            // longest prefix that IS a matrix row, so the subject is a project.
            string? project = LongestMatrixProject(match.Value);
            if (project is null)
            {
                continue;
            }

            if (end <= claimIndex && end > nearestBeforeEnd)
            {
                nearestBefore = project;
                nearestBeforeEnd = end;
            }
            else if (start >= claimIndex && start < nearestAfterStart)
            {
                nearestAfter = project;
                nearestAfterStart = start;
            }
        }

        if (nearestBefore is not null)
        {
            return nearestBefore;
        }

        if (nearestAfter is not null)
        {
            return nearestAfter;
        }

        // No project named anywhere near the claim. The owning directory is the
        // subject when the file lives in one; otherwise the claim is
        // unresolvable, and an unresolvable claim is red rather than skipped.
        return owning;
    }

    /// <summary>
    ///     The longest prefix of a dotted name that is a matrix row, or
    ///     <see langword="null" /> when no prefix is one.
    /// </summary>
    private static string? LongestMatrixProject(string candidate)
    {
        string name = candidate;
        while (name.StartsWith("Harbor.", StringComparison.Ordinal))
        {
            if (MatrixLayerOf(name) is not null)
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

    private static string Truncate(string value)
        => value.Length <= 160 ? value : value[..157] + "...";
}
