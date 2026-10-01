// CellForgeEngineAtomicityRules.cs — issue #795, step 1.
//
// THE HOLE THIS FILE EXISTS TO CLOSE
// ----------------------------------
// `Harbor.Tui.CellForge.Engine` describes ITSELF in its csproj:
//
//     <Description>… BCL-only, AOT-compatible. …</Description>
//
// and declares FOUR ProjectReferences while saying that:
//
//     Harbor.Abstractions
//     Harbor.Ui.Framework.Rendering
//     Harbor.Ui.Framework.State
//     Harbor.DesignSystem
//
// Every layering gate in this project reads `<ProjectReference>` —
// `LayerDependencyTests`, `NetArchLayerRules`, `FullLayerMatrixTests`,
// `DeclaredButUnboundProjectReferences`. They find four edges that were
// declared on purpose (the csproj says so: "CF-A-001: прямые референсы вместо
// транзитивных"), and all four are PERMITTED by the matrix. So the layer gates
// are green, and they are right to be: the edges are declared, they are legal,
// and the gates answer the question they were built to answer.
//
// The hole was that NOBODY READS THE IMPORTS. `GlobalUsings.cs` pre-imported
//
//     global using Harbor.Ui.Framework.Rendering;
//     global using Harbor.Ui.Framework.Rendering.Input;
//     global using Harbor.Ui.Framework.Rendering.Markdown;
//     global using Harbor.Ui.Framework.Rendering.Widgets;
//
// and the files that spelled `UiMsg` / `UiKeyDto` looked CLEAN — no `using`
// line, nothing for a reference-scanner to find. The vocabulary was borrowed,
// the declarations were honest, and the engine was not atomic. That was the
// whole defect, and it was invisible to every gate that existed.
//
// #795 has now deleted that block, so the borrow is spelled per file and the
// rule below has something real to grade:
//
//     no file the engine compiles imports Harbor.Ui.Framework.* or
//     Harbor.Abstractions.* except a reviewed row, the Description's
//     'BCL-only' claim is checked against both imports and references, and
//     NO FILE DECLARES A `global using` AT ALL.
//
// The last clause is the one that closes the residual hole. The rule in this
// file reads imports, and an import-reading rule is blind to a borrower that
// arrives through an ALREADY-BASELINED ambient namespace: by the time the
// borrow exists, every import line involved is already in the table. That was
// demonstrated against this file in its merged form — a synthetic file binding
// six ambient types with no `using` line of its own left both decisive
// predicates GREEN at 9 rows. So the channel is now forbidden outright instead
// of merely accounted for: with zero ambient namespaces, a borrow must be
// spelled in the file that needs it, and spelling it is a line this file can
// read.
//
// WHY THE ORDERING IS "GUARD IMPORTS FIRST" AND NOT "DELETE REFERENCES FIRST"
// ---------------------------------------------------------------------------
// Because the references ARE deletable today and the guard would still pass.
// `GlobalUsings.cs` would survive, the borrow would survive, and the engine
// would be non-atomic with an EMPTY reference list — which looks like progress
// and is worse than the honest four, because it destroys the evidence.
// Guarding the imports first is what makes the later reference deletion honest:
// once this file is green, an empty reference list is a fact rather than a
// claim.
//
// WHAT IS BASELINED, AND WHY IT IS NOT A PERMISSION
// -------------------------------------------------
// The 34 borrowing import pairs and the four references exist, and this PR does
// not remove them: moving `Input/` out is step 3 of #795 (deliberately a
// separate change so it cannot collide with #435/#436), and the references are
// #435's and #436's to delete. A permanently-red test is a comment with extra
// steps, so both tables below list today's violations with a reason and the
// issue that will delete them.
//
// The 34 pairs are 3 rows in `ReviewedImports` (the State translator pair and
// the Protocol constant) plus 5 groups in `ReviewedVocabulary` expanded to one
// row per file. Grouping is by namespace because the debt is per namespace;
// the file list is spelled out because the old shape — one row for
// `GlobalUsings.cs` serving 24 files — could not name a 25th borrower and so
// could not fail when one appeared.
//
// They are a TO-DO LIST, not an amnesty, and three things keep them that way:
//
//   * `ReviewedRowsStateWhyTheyAreTolerated` runs every row through the shared
//     `ExemptionReason` check, so a row cannot be added blank, and cannot be a
//     bare URL — a URL says where the debt is tracked, not why it is tolerated
//     here. Same contract as `PresentationCapabilityRules` and
//     `EnforcerIntegrityTests`.
//   * `ReviewedImportRowsAreStillReal` and `ReviewedReferenceRowsAreStill
//     Declared` fail when a row OUTLIVES its violation. That is the half that
//     stops a baseline decaying into a standing permission: when #435 drops
//     `Harbor.Abstractions`, the row must go in the same commit, and the leak
//     cannot come back silently.
//   * `ANonAtomicEngineNamesItsTrackingIssueInItsOwnDescription` closes the
//     cheapest way to silence all of this — deleting the words `BCL-only` from
//     the csproj. The promise is NOT withdrawn here; it stays in the
//     Description and this file keeps checking it. The rule is that an engine
//     which is not yet atomic must SAY which issue it owes, so the aspiration
//     stays greppable and the promise cannot die quietly with the project.
//
// THE SECOND HALF: THE DESCRIPTION IS A PROMISE, AND A PROMISE IS CHECKABLE
// -------------------------------------------------------------------------
// `BCL-only` is the engine's own statement about itself, and nothing verified
// it, which is why it outlived the four references it contradicts. While the
// csproj makes the claim, every reference and every forbidden import must be in
// a reviewed row — so the claim is enforced against the project it describes
// rather than remembered alongside it.
//
// SCOPE — AND WHAT IS DELIBERATELY NOT IN IT
// ------------------------------------------
//   * `Harbor.DesignSystem` is NOT an import-rule target.
//     `Parsing/EscapeSequenceParser.cs` imports it and this guard says nothing
//     about that: the issue's rule names `Harbor.Ui.Framework.*` and
//     `Harbor.Abstractions.*`, and widening it here would pre-empt #436, which
//     owns the DesignSystem edge. The Description rule still covers it, because
//     a DesignSystem reference is equally not-BCL and contradicts the same
//     promise.
//   * `Harbor.Tui.CellForge.*` is the engine's own namespace family and is not
//     forbidden. `Harbor.Plugins.*` is exempt everywhere (#626) and appears in
//     the planted control as a must-stay-silent case.
//   * CONTRIBUTED CODE IS NOT SCANNED. The walk is one project directory, which
//     excludes `contrib/`, `tests/`, `obj/`, `bin/` and `.worktrees/` by
//     construction.
//   * This is a SOURCE rule. Whether a type actually binds is a compiler
//     question, and this run is a demonstration of why that distinction must not
//     be blurred: a text scan used to plan the deletion of the ambient block
//     reported `UnicodeWidth` and `TerminalBackgroundProbe` as absent from two
//     files, and the build disagreed with CS0103. The scan's own comment
//     stripper desynchronised on the char literal `'\n'` and blanked the rest of
//     the file. What a text scan can answer honestly — and what is exactly the
//     leak — is which names the project ASKS FOR; whether the request resolves
//     is the compiler's job, and this file's rows are therefore written from
//     the compiler's own error list rather than from a scan's silence.
//   * "Asks for" is deliberately not "binds": a reviewed row is a declaration
//     that may stand, and the two `Harbor.Abstractions.Models` lines that named
//     no type at all were deleted as drive-bys rather than baselined.
//
// NON-VACUITY — FOUR PLACES, ALL OF WHICH CAN GO RED
// ---------------------------------------------------
//   1. `NonVacuityDiscoverySeesTheEngineProjectAndItsDescription` — the
//      directory resolves, contributes a non-trivial number of files, and the
//      csproj still carries BOTH the promise being graded and the tracking
//      issue it owes. A rule pointed at a path nobody occupies is green forever
//      — the NetArchTest trap this project has been bitten by — and a promise
//      quietly deleted is the same failure wearing a different hat.
//   2. `NonVacuityTheImportMatcherFiresOnPlantedOffendersOnly` — synthetic
//      source. Every forbidden spelling must be reported exactly once, and every
//      near-miss must stay silent: a comment naming the namespace, a string
//      literal, the engine's own namespace, a namespace that merely shares a
//      prefix, `Harbor.DesignSystem`, and `Harbor.Plugins.*`. A matcher that
//      fires on prose is deleted by the first person it annoys; one that fires
//      on nothing enforces nothing.
//   2b. `NonVacuityTheGlobalUsingMatcherFiresOnPlantedAmbientUsingsOnly` — the
//      same contract for the ambient channel, because a channel rule that
//      cannot see `global using` is the merged guard's blind spot rebuilt one
//      clause later.
//   3. `TheBclOnlyClaimIsCheckedAgainstRealProjectReferences` reads a real
//      csproj through the same helper the planted control uses.
//   4. The liveness tests above — a stale row is red.
//
// STATUS: written without a local build. Local dotnet is forbidden in this
// repository (concurrent agents on one machine), so the first CI run is the
// first compile. The guard landing RED on that first run is the intent: the
// baseline tables start empty, so the two claim rules report the real borrows
// rather than a curated list of them.

using System.Text.RegularExpressions;
using System.Xml.Linq;
using TUnit.Assertions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Issue #795: <c>Harbor.Tui.CellForge.Engine</c> promises <c>BCL-only</c>
///     in its own csproj while borrowing the UI vocabulary — through its
///     IMPORTS, which no reference-reading gate in this project inspects.
/// </summary>
public sealed class CellForgeEngineAtomicityRules
{
    /// <summary>The project whose self-description this file grades.</summary>
    private const string ProjectDir = "Harbor.Tui.CellForge.Engine";

    /// <summary>The self-description phrase under guard.</summary>
    private const string BclOnlyClaim = "BCL-only";

    /// <summary>
    ///     The issue that owns the remaining work. Required in the csproj's own
    ///     Description while the engine is not yet atomic, so the promise can
    ///     only be withdrawn by SAYING it was withdrawn.
    /// </summary>
    private const string TrackingIssue = "#795";

    /// <summary>
    ///     Namespace prefixes the engine may not import. A match is the prefix
    ///     itself or anything beneath it, so
    ///     <c>Harbor.Ui.Framework.Rendering.Input</c> is covered by
    ///     <c>Harbor.Ui.Framework</c> without the rule enumerating sub-namespaces
    ///     that do not exist yet.
    /// </summary>
    private static readonly string[] ForbiddenImportPrefixes =
    [
        "Harbor.Ui.Framework",
        "Harbor.Abstractions",
    ];

    /// <summary>One forbidden import found in one file.</summary>
    /// <param name="FileName">File name relative to the project directory.</param>
    /// <param name="Namespace">The imported namespace.</param>
    private readonly record struct ImportHit(string FileName, string Namespace)
    {
        /// <summary>The <c>file:line: namespace</c> form used in failure messages.</summary>
        public string Describe(int line) => $"{FileName}:{line}: {Namespace}";
    }

    /// <summary>
    ///     One C# using directive. Handles all four spellings — plain, <c>global</c>,
    ///     <c>static</c> and alias (<c>using A = B;</c>) — because the rule is
    ///     about the NAME BEING ASKED FOR, and each form asks for one. Applied
    ///     per line to comment-stripped text, so it needs no
    ///     <see cref="RegexOptions.Multiline" /> and cannot match across lines.
    /// </summary>
    private static readonly Regex UsingDirective = new(
        @"^\s*(?:global\s+)?using\s+(?:static\s+)?(?:[A-Za-z_][A-Za-z0-9_.]*\s*=\s*)?(?<ns>[A-Za-z_][A-Za-z0-9_.]*)\s*;",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    ///     Today's forbidden imports, as (file name, namespace). A pair is
    ///     tolerated only while listed here; anything else is red on first touch.
    /// </summary>
    /// <param name="FileName">File name relative to the project directory.</param>
    /// <param name="Namespace">The imported namespace.</param>
    /// <param name="Allowance">
    ///     Why this row may stay — checked by the shared <see cref="ExemptionReason" />,
    ///     so it cannot be blank and cannot be a bare URL.
    /// </param>
    private static readonly (string FileName, string Namespace, ExemptionReason.Row Allowance)[] ReviewedImports =
    [
        (
            "Input/MouseRouter.cs",
            "Harbor.Ui.Framework.State",
            new ExemptionReason.Row(
                "WheelToMessage turns a wheel tick into AppMsg.KeyInput(ChatAction, UiKey) — a "
                + "state-layer message — and TimelineWheelTarget dispatches it. That is the "
                + "translator half of Input/, which #795 step 3 relocates to "
                + "Harbor.Tui.CellForge/Chat; a translator between the terminal protocol and the UI "
                + "vocabulary has two languages by definition and cannot be BCL. All six names it "
                + "binds are declared in Harbor.Ui.Framework.State, not in .ViewModels — measured "
                + "by reading the declaration file of each, because 'which State project' was the "
                + "open question this row had to answer.",
                TrackedBy: "https://github.com/refusedguy/Harbor-Harness/issues/795")
        ),
        (
            "Rendering/ComposerController.cs",
            "Harbor.Ui.Framework.State",
            new ExemptionReason.Row(
                "EnterKeyPolicy and InputMsg are the store-owned key decisions the composer applies "
                + "its buffer effects to — deliberately, since #359, so key behaviour cannot diverge "
                + "per renderer. The composer is a translator over the store's vocabulary; #795 step 3 "
                + "moves that translation out of the engine.",
                TrackedBy: "https://github.com/refusedguy/Harbor-Harness/issues/795")
        ),
        (
            "Rendering/DiffEngine.cs",
            "Harbor.Ui.Framework.Rendering.Protocol",
            new ExemptionReason.Row(
                "CellDiffHints.HintAreaThreshold is the single canonical 25% damage-area threshold, "
                + "re-exposed as HintAreaThreshold so the engine's cell differ and the portable "
                + "encoder cannot drift apart. It is a cell-level constant rather than a UI concept, "
                + "and its eventual home is #436's decision.",
                TrackedBy: "https://github.com/refusedguy/Harbor-Harness/issues/795")
        ),
    ];

    /// <summary>
    ///     The cell vocabulary the engine still borrows, one row per namespace
    ///     with the CLOSED list of files that may import it. This is the row
    ///     shape the ambient block used to hide: before #795 a single
    ///     <c>global using</c> line in <c>GlobalUsings.cs</c> served 24 files,
    ///     so the table had one row per NAMESPACE and no file could be named.
    ///     Grouping is still by namespace, because the debt (#436 decides where
    ///     the vocabulary lives) is per namespace, but the file list is spelled
    ///     out, so a 25th borrower is a red row rather than an invisible one.
    /// </summary>
    /// <param name="Namespace">The borrowed namespace.</param>
    /// <param name="Files">
    ///     Every engine file allowed to import it. A file outside this list is
    ///     reported by <see cref="NoEngineFileImportsTheUiVocabularyOutsideTheReviewedRows" />.
    /// </param>
    /// <param name="Allowance">Why this vocabulary may stay, and what removes it.</param>
    private static readonly (string Namespace, string[] Files, ExemptionReason.Row Allowance)[] ReviewedVocabulary =
    [
        (
            // #409. Placed first because it is the newest borrow and the one a
            // reader is most likely to mistake for a stray: AnsiWriter spells it
            // on its own line, which is exactly what #795's deletion of the
            // global block was for.
            "Harbor.Ui.Framework.Rendering.PerformanceContracts",
            ["Rendering/AnsiWriter.cs"],
            new ExemptionReason.Row(
                "#409's per-stage counters name four pipeline stages. Three sit in "
                + "Harbor.Ui.Framework.Rendering and one sits here, because AnsiWriter is the only type "
                + "in the engine that reaches a terminal backend — so the write stage is counted at this "
                + "site or not at all. The row exists rather than an engine-local counter because a "
                + "second tally is the failure mode: four stage numbers read off two types can drift "
                + "apart silently, which is the #970 shape (an instrument matching more than reality). "
                + "UiStageCounters is a counter and nothing else — nothing is constructed here, no "
                + "engine logic reads a value, and the call sits behind a guard that is off by default, "
                + "so the engine is no less BCL in behaviour than before, only one file longer in its "
                + "list of spelled borrows. Same shape as the ReviewedImports Protocol row below, where "
                + "a shared constant exists precisely so two differs cannot disagree.",
                TrackedBy: "https://github.com/refusedguy/Harbor-Harness/issues/795")
        ),
        (
            "Harbor.Ui.Framework.Rendering",
            [
                "Input/MouseRouter.cs",
                "Rendering/AnsiWriter.cs",
                "Rendering/BufferSwapChain.cs",
                "Rendering/CodeHighlightPalette.cs",
                "Rendering/CodeSyntaxTokenizer.cs",
                "Rendering/CodeTokenizer.cs",
                "Rendering/DiffEngine.cs",
                "Rendering/DirtyRect.cs",
                "Rendering/FlexLayout.cs",
                "Rendering/FrameDiff.cs",
                "Rendering/InlineImageLayer.cs",
                "Rendering/InlineSession.cs",
                "Rendering/LayoutTree.cs",
                "Rendering/OverlayStack.cs",
                "Rendering/PostFx.cs",
                "Rendering/PromptBuffer.cs",
                "Rendering/PromptRenderer.cs",
                "Rendering/PromptViewport.cs",
            ],
            new ExemptionReason.Row(
                "The renderer-agnostic cell vocabulary — Cell, Rect, ScreenBuffer, CellStyle, "
                + "PackedColor, StyleAttr, UnicodeWidth, TextWrap, IInlineImageSink — has no BCL "
                + "equivalent. All 18 files now spell the import themselves; what they must not do "
                + "is borrow it invisibly, which is what the deleted global block did. Where the "
                + "vocabulary eventually lives is #436's decision.",
                TrackedBy: "https://github.com/refusedguy/Harbor-Harness/issues/436")
        ),
        (
            "Harbor.Ui.Framework.Rendering.Input",
            [
                "Input/FocusRouter.cs",
                "Input/InputEvent.cs",
                "Input/MouseEvent.cs",
                "Parsing/EscapeSequenceParser.cs",
                "Rendering/ComposerController.cs",
                "Rendering/OverlayStack.cs",
                "Rendering/VimComposerMode.cs",
            ],
            new ExemptionReason.Row(
                "KeyEvent, KeyCode, KeyModifiers, KeyEventType and IFocusTarget are the shared "
                + "input vocabulary the byte state-machine parser, FocusRouter, OverlayStack and "
                + "ComposerController all speak. Parsing/ is the engine proper and needs it, so this "
                + "is NOT the #795 step 3 translator move — that is the State row above.",
                TrackedBy: "https://github.com/refusedguy/Harbor-Harness/issues/436")
        ),
        (
            "Harbor.Ui.Framework.Rendering.Markdown",
            [
                "Rendering/CodeSyntaxTokenizer.cs",
                "Rendering/CodeTokenizer.cs",
            ],
            new ExemptionReason.Row(
                "MdLine and MdStyle are read by the two code tokenizers to classify spans. NOTE: the "
                + "merged version of this row claimed no engine file names anything in this namespace "
                + "and that the import 'buys nothing today' — false, and the build found it when the "
                + "ambient block was deleted and these two files stopped compiling. Corrected here "
                + "because a baseline row that misstates its own debt cannot be reviewed.",
                TrackedBy: "https://github.com/refusedguy/Harbor-Harness/issues/436")
        ),
        (
            "Harbor.Ui.Framework.Rendering.Widgets",
            [
                "Rendering/CodeHighlightPalette.cs",
                "Rendering/PostFx.cs",
            ],
            new ExemptionReason.Row(
                "ChatPalette (CodeHighlightPalette, four call sites) and PanelFx.Lerp (PostFx) are "
                + "read here. The merged version of this row called the line 'unused vocabulary "
                + "parked in a global block' on the grounds that LineDiff and WordDiff are "
                + "referenced by neither — true of those two types and beside the point, since the "
                + "namespace is imported for these. Corrected for the same reason as the row above.",
                TrackedBy: "https://github.com/refusedguy/Harbor-Harness/issues/436")
        ),
    ];

    /// <summary>
    ///     Every reviewed import row, vocabulary groups expanded to one row per
    ///     file. Both tables are read through this so a rule cannot accidentally
    ///     check one and ignore the other.
    /// </summary>
    private static readonly (string FileName, string Namespace, ExemptionReason.Row Allowance)[] ReviewedAll =
        ReviewedImports
            .Concat(ReviewedVocabulary.SelectMany(v =>
                v.Files.Select(f => (f, v.Namespace, v.Allowance))))
            .ToArray();

    /// <summary>
    ///     The four `<ProjectReference>` edges the csproj declares while calling
    ///     itself BCL-only. Permitted by the layer matrix, and therefore invisible
    ///     to every gate that reads references — which is why the Description claim
    ///     is checked against them here.
    /// </summary>
    /// <param name="Project">The referenced project directory name.</param>
    /// <param name="Allowance">Why this edge may stay, and what removes it.</param>
    private static readonly (string Project, ExemptionReason.Row Allowance)[] ReviewedReferences =
    [
        (
            "Harbor.Abstractions",
            new ExemptionReason.Row(
                "ALREADY KNOWN DEAD: EnforcerIntegrityTests.DeclaredButUnboundProjectReferences "
                + "carries this exact edge with the note that the IL gate proved the assembly emits no "
                + "AssemblyRef for it. Measured cause: the namespace the two importing files name, "
                + "Harbor.Abstractions.Models, is declared in Harbor.Abstractions.Contracts, which "
                + "this project already reaches through Harbor.Ui.Framework.Rendering — and neither "
                + "file binds a type from it. The edge is #435's to delete.",
                TrackedBy: "https://github.com/refusedguy/Harbor-Harness/issues/435")
        ),
        (
            "Harbor.Ui.Framework.Rendering",
            new ExemptionReason.Row(
                "Carries the cell primitives 20 engine files name — ScreenBuffer, Cell, Rect, "
                + "UnicodeWidth, TextWrap, the Key* family and IFocusTarget — plus CellDiffHints. "
                + "Most reach it with no using line of their own, through GlobalUsings.cs. Removing "
                + "it is not a deletion but a re-homing decision, and #436 owns that decision.",
                TrackedBy: "https://github.com/refusedguy/Harbor-Harness/issues/436")
        ),
        (
            "Harbor.Ui.Framework.State",
            new ExemptionReason.Row(
                "Carries AppMsg, InputMsg, EnterKeyPolicy and ChatAction — the store vocabulary that "
                + "MouseRouter.WheelToMessage and ComposerController translate into. Once step 3 "
                + "moves the translators, nothing in the engine binds it and the edge becomes "
                + "deletable.",
                TrackedBy: "https://github.com/refusedguy/Harbor-Harness/issues/435")
        ),
        (
            "Harbor.DesignSystem",
            new ExemptionReason.Row(
                "Imported by Parsing/EscapeSequenceParser.cs for the palette constants the escape "
                + "parser emits. It is a cell/palette concern rather than a UI-view concern, so the "
                + "import rule deliberately does not name it; the edge is listed here because a "
                + "DesignSystem reference is still not-BCL and contradicts the same promise. #436 "
                + "decides whether the constants move or the edge is grandfathered for good.",
                TrackedBy: "https://github.com/refusedguy/Harbor-Harness/issues/436")
        ),
    ];

    /// <summary>
    ///     Joins violations for a failure message; <c>(none)</c> when empty, so a
    ///     message never reads as if it listed something.
    /// </summary>
    private static string Offenders(IEnumerable<string> violations)
    {
        string[] list = violations.ToArray();
        return list.Length == 0 ? "(none)" : string.Join("; ", list);
    }

    /// <summary>
    ///     Absolute path of the engine's csproj, or <c>null</c> outside a
    ///     checkout.
    /// </summary>
    private static string? CsprojPath() => RepoPaths.FindSrcProject(ProjectDir);

    /// <summary>
    ///     The csproj's <c>&lt;Description&gt;</c> value, or <see langword="null" />
    ///     when the csproj has none or cannot be read. Degrading rather than
    ///     throwing is what <see cref="RepoPaths" /> does everywhere, and is why
    ///     every test here also pins that the checkout exists.
    /// </summary>
    private static string? ReadDescription(string csprojPath)
    {
        try
        {
            return XDocument.Load(csprojPath).Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "Description")?.Value;
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException)
        {
            return null;
        }
    }

    /// <summary>Whether a description makes the <c>BCL-only</c> promise.</summary>
    private static bool ClaimsBclOnly(string? description) =>
        description is not null && description.Contains(BclOnlyClaim, StringComparison.Ordinal);

    /// <summary>Whether a namespace is one the engine may not ask for.</summary>
    private static bool IsForbidden(string ns) =>
        ForbiddenImportPrefixes.Any(p =>
            ns.Equals(p, StringComparison.Ordinal)
            || ns.StartsWith(p + ".", StringComparison.Ordinal));

    /// <summary>
    ///     Every forbidden import in one source text, in line order. Comments are
    ///     stripped first: <c>Input/FocusRouter.cs</c> carries a doc comment naming
    ///     <c>Harbor.Ui.Framework.Rendering.Input</c>, and a rule that grades its
    ///     own documentation is a rule that gets deleted.
    /// </summary>
    private static IReadOnlyList<ImportHit> ForbiddenImportsIn(string fileName, string source)
    {
        var found = new List<ImportHit>();
        string[] lines = SourceScan.StripComments(source).Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            Match match = UsingDirective.Match(lines[i]);
            if (match.Success && IsForbidden(match.Groups["ns"].Value))
            {
                found.Add(new ImportHit(fileName, match.Groups["ns"].Value));
            }
        }

        return found;
    }

    /// <summary>
    ///     Every forbidden import across every file the engine compiles, with
    ///     1-based line numbers, sorted for a stable message. Linked source files
    ///     are included by <see cref="RepoPaths.EnumerateCsFiles" />, so a violation
    ///     cannot hide in shared source.
    /// </summary>
    private static IReadOnlyList<(ImportHit Hit, int Line)> AllForbiddenImports()
    {
        var found = new List<(ImportHit Hit, int Line)>();

        if (RepoPaths.FindProjectDir(ProjectDir) is not { } projectRoot)
        {
            return found;
        }

        foreach (string path in RepoPaths.EnumerateCsFiles(ProjectDir))
        {
            string? source = SourceScan.TryReadAllText(path);
            if (source is null)
            {
                continue;
            }

            string fileName = Path.GetRelativePath(projectRoot, path).Replace('\\', '/');
            string[] lines = SourceScan.StripComments(source).Split('\n');

            for (int i = 0; i < lines.Length; i++)
            {
                Match match = UsingDirective.Match(lines[i]);
                if (match.Success && IsForbidden(match.Groups["ns"].Value))
                {
                    found.Add((new ImportHit(fileName, match.Groups["ns"].Value), i + 1));
                }
            }
        }

        found.Sort(static (a, b) => string.CompareOrdinal(a.Hit.Describe(a.Line), b.Hit.Describe(b.Line)));
        return found;
    }

    /// <summary>Whether an import is covered by a reviewed row.</summary>
    private static bool IsReviewed(string fileName, string ns) =>
        ReviewedAll.Any(r =>
            string.Equals(r.FileName, fileName, StringComparison.Ordinal)
            && string.Equals(r.Namespace, ns, StringComparison.Ordinal));

    /// <summary>The reviewed import rows as <c>file: namespace</c>, for messages.</summary>
    private static string ReviewedImportKeys() =>
        string.Join(
            " | ",
            ReviewedAll.Select(r => $"{r.FileName}: {r.Namespace}").OrderBy(k => k, StringComparer.Ordinal));

    // =====================================================================
    // 1b. The channel itself. A `global using` is what made the borrow
    //     invisible, so the channel is now forbidden outright.
    // =====================================================================

    /// <summary>
    ///     Every <c>global using</c> in every file the engine compiles. The
    ///     merged guard could not see a borrower that arrives through an
    ///     already-baselined ambient namespace: by the time the borrow exists,
    ///     every import line involved is already in the table, so no
    ///     import-reading rule and no reference-reading rule can catch it. That
    ///     was demonstrated on the merged guard with a synthetic file binding
    ///     six ambient types and no <c>using</c> line of its own — 9 rows found,
    ///     both decisive predicates green. Deleting the block closes the hole
    ///     for today; this rule is what stops it being reopened, because with
    ///     zero ambient namespaces there is no channel left to borrow through.
    /// </summary>
    private static IReadOnlyList<(string FileName, int Line)> AllGlobalUsings()
    {
        var found = new List<(string, int)>();

        if (RepoPaths.FindProjectDir(ProjectDir) is not { } projectRoot)
        {
            return found;
        }

        foreach (string path in RepoPaths.EnumerateCsFiles(ProjectDir))
        {
            string? source = SourceScan.TryReadAllText(path);
            if (source is null)
            {
                continue;
            }

            string fileName = Path.GetRelativePath(projectRoot, path).Replace('\\', '/');
            string[] lines = SourceScan.StripComments(source).Split('\n');

            for (int i = 0; i < lines.Length; i++)
            {
                if (GlobalUsingDirective.IsMatch(lines[i]))
                {
                    found.Add((fileName, i + 1));
                }
            }
        }

        return found;
    }

    /// <summary>
    ///     An ambient <c>using</c>, in any of its spellings
    ///     (<c>global using X;</c>, <c>global using static X;</c>).
    ///     Comment-stripped input only, so a doc comment mentioning the phrase
    ///     cannot be reported — and so cannot be the thing that keeps this rule
    ///     from ever going red.
    /// </summary>
    private static readonly Regex GlobalUsingDirective = new(
        @"^\s*global\s+using\s+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    ///     No file the engine compiles may declare a <c>global using</c>. This is
    ///     the predicate the merged guard was missing: it is the one question
    ///     whose answer makes every borrow in this project necessarily visible,
    ///     because a file with no ambient namespace can only reach a type it
    ///     asked for by name, and asking is a line this project can read.
    /// </summary>
    [Test]
    public async Task NoEngineFileDeclaresAGlobalUsing()
    {
        IReadOnlyList<(string FileName, int Line)> globals = AllGlobalUsings();

        var offenders = globals
            .Select(g => $"{g.FileName}:{g.Line}")
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        await Assert.That(offenders).IsEmpty()
            .Because(
                "a global using is the one construct that makes a borrow invisible to every rule in "
                + "this file. When Harbor.Tui.CellForge.Engine/GlobalUsings.cs pre-imported four "
                + "Harbor.Ui.Framework.Rendering.* namespaces, 24 files reached Cell, Rect, "
                + "ScreenBuffer, KeyEvent, MdLine, ChatPalette and PanelFx with no import line of "
                + "their own, and the import rule below was green the whole time. A synthetic file "
                + "doing the same on the merged guard left both decisive predicates green at 9 rows, "
                + "because by then every import involved was already baselined. No import-reading "
                + "rule and no reference-reading rule can close that; removing the channel can. So "
                + "the borrow must be spelled in the file that needs it — which is exactly what the "
                + "34 reviewed rows above now enumerate, one per file. Ambient usings found: "
                + Offenders(offenders));
    }

    // =====================================================================
    // 1. The rule this issue asked for: imports, not references.
    // =====================================================================

    /// <summary>
    ///     No file the engine compiles may import <c>Harbor.Ui.Framework.*</c> or
    ///     <c>Harbor.Abstractions.*</c> beyond the reviewed rows above.
    /// </summary>
    [Test]
    public async Task NoEngineFileImportsTheUiVocabularyOutsideTheReviewedRows()
    {
        var violations = new List<string>();

        foreach ((ImportHit hit, int line) in AllForbiddenImports())
        {
            if (!IsReviewed(hit.FileName, hit.Namespace))
            {
                violations.Add(hit.Describe(line));
            }
        }

        await Assert.That(violations).IsEmpty()
            .Because(
                "The engine's csproj calls itself BCL-only, and no layer gate can see this because "
                + "the borrow arrives through imports: GlobalUsings.cs pre-imports the UI namespaces, "
                + "so a file spelling UiMsg has no using line for a reference-scanner to find. Every "
                + "reference in the project is already declared and permitted, so the import is the "
                + "only place the violation is visible. A new one is not a style preference — it is "
                + "another file keeping the engine non-atomic after #435 and #436 remove the "
                + "references and leave an empty list that only LOOKS atomic. Reviewed rows: "
                + ReviewedImportKeys() + ". Offenders: " + Offenders(violations));
    }

    // =====================================================================
    // 2. The Description is a promise; make it checkable.
    // =====================================================================

    /// <summary>
    ///     While the csproj says <c>BCL-only</c>, every declared
    ///     <c>ProjectReference</c> and every forbidden import must be in a reviewed
    ///     row — so the claim is enforced against the project it describes.
    /// </summary>
    [Test]
    public async Task TheBclOnlyClaimIsCheckedAgainstRealReferencesAndImports()
    {
        string? csproj = CsprojPath();
        await Assert.That(csproj).IsNotNull()
            .Because("the engine's csproj is what this rule grades. Without it there is no claim to "
                   + "check, and a rule with no subject passes for the wrong reason.");

        string? description = ReadDescription(csproj!);
        await Assert.That(description).IsNotNull()
            .Because("the engine csproj declares a <Description>; without one the self-description "
                   + "half of this guard has nothing to read and the other rules would pass "
                   + "vacuously.");

        if (!ClaimsBclOnly(description))
        {
            // The promise has been withdrawn, which
            // ANonAtomicEngineNamesItsTrackingIssueInItsOwnDescription permits only when the
            // withdrawal is declared. Returning keeps this test's message about the contradiction
            // it exists to catch, rather than about a promise nobody makes any more.
            return;
        }

        (string[] references, _) = RepoPaths.ReadProjectReferences(csproj!);

        var contradictions = new List<string>();

        foreach (string reference in references)
        {
            if (!ReviewedReferences.Any(r => string.Equals(r.Project, reference, StringComparison.Ordinal)))
            {
                contradictions.Add($"undeclared-in-review reference: {reference}");
            }
        }

        foreach ((ImportHit hit, int line) in AllForbiddenImports())
        {
            if (!IsReviewed(hit.FileName, hit.Namespace))
            {
                contradictions.Add($"undeclared-in-review import: {hit.Describe(line)}");
            }
        }

        await Assert.That(contradictions).IsEmpty()
            .Because(
                $"the csproj describes this project as '{BclOnlyClaim}', and a reference to another "
                + "Harbor project is by definition not the BCL — so the file contradicts itself. "
                + "Nothing else checks that phrase, which is exactly how it outlived the four "
                + "references it denies: a promise no gate reads decays into a decoration and dies "
                + "with the project. Reviewed references: "
                + string.Join(" | ", ReviewedReferences.Select(r => r.Project).OrderBy(p => p, StringComparer.Ordinal))
                + ". Contradictions: " + Offenders(contradictions));
    }

    /// <summary>
    ///     An engine that is not yet atomic must NAME the issue it owes in its own
    ///     Description — so <c>BCL-only</c> cannot be deleted to make the rule above
    ///     green.
    /// </summary>
    [Test]
    public async Task ANonAtomicEngineNamesItsTrackingIssueInItsOwnDescription()
    {
        string? csproj = CsprojPath();
        await Assert.That(csproj).IsNotNull()
            .Because("without the csproj there is no self-description to read, and this assertion is "
                   + "the only thing standing between the promise and a silent deletion.");

        string? description = ReadDescription(csproj!);
        (string[] references, _) = RepoPaths.ReadProjectReferences(csproj!);
        int imports = AllForbiddenImports().Count;

        if (references.Length == 0 && imports == 0)
        {
            // Once the engine really is atomic the debt is paid and naming it is
            // no longer required — the promise is simply true.
            return;
        }

        await Assert.That(description!).Contains(TrackingIssue)
            .Because(
                "this project is not atomic yet — it declares " + references.Length
                + " reference(s) and " + imports + " forbidden import(s) — so its self-description "
                + "must say which issue still owes the work (" + TrackingIssue + "). Otherwise the "
                + "cheapest way to silence the contradiction check is to delete the words '"
                + BclOnlyClaim + "', and a promise withdrawn by deletion is indistinguishable from a "
                + "promise that was never made. Declaring the debt keeps the aspiration greppable "
                + "while it is unpaid, and the file header says the promise is NOT being withdrawn.");
    }

    // =====================================================================
    // 3. Baseline integrity — a to-do list, not an amnesty.
    // =====================================================================

    /// <summary>
    ///     Every reviewed import row must STILL be violated. A row whose import is
    ///     gone has outlived its reason and must be deleted in the same commit, so
    ///     the baseline shrinks as the work lands instead of becoming a standing
    ///     permission.
    /// </summary>
    [Test]
    public async Task ReviewedImportRowsAreStillReal()
    {
        IReadOnlyList<(ImportHit Hit, int Line)> actual = AllForbiddenImports();
        var stale = new List<string>();

        foreach ((string fileName, string ns, _) in ReviewedAll)
        {
            bool stillViolated = actual.Any(a =>
                string.Equals(a.Hit.FileName, fileName, StringComparison.Ordinal)
                && string.Equals(a.Hit.Namespace, ns, StringComparison.Ordinal));

            if (!stillViolated)
            {
                stale.Add($"{fileName}: {ns}");
            }
        }

        await Assert.That(stale).IsEmpty()
            .Because(
                "a baseline row is a promise TO FIX, not a statement that the violation is fine. "
                + "When the import is gone the row has outlived its reason, and leaving it lets the "
                + "same import return silently. Delete the row in the commit that removes the "
                + "violation. Stale rows: " + Offenders(stale));
    }

    /// <summary>
    ///     Every reviewed reference row must STILL be declared in the csproj, for
    ///     the same liveness reason as the import rows.
    /// </summary>
    [Test]
    public async Task ReviewedReferenceRowsAreStillDeclared()
    {
        string? csproj = CsprojPath();
        await Assert.That(csproj).IsNotNull()
            .Because("the reviewed reference rows are checked against the csproj's real "
                   + "ProjectReference list, which requires the csproj.");

        (string[] declared, _) = RepoPaths.ReadProjectReferences(csproj!);
        var stale = new List<string>();

        foreach ((string project, _) in ReviewedReferences)
        {
            if (!declared.Contains(project, StringComparer.Ordinal))
            {
                stale.Add(project);
            }
        }

        await Assert.That(stale).IsEmpty()
            .Because(
                "these four edges are listed so the 'BCL-only' claim can be checked against them, "
                + "not so they may live forever. #435 and #436 remove them, and when one goes its "
                + "row must go with it — otherwise the table becomes a menu for re-adding an edge "
                + "the layer matrix already permits. Rows naming edges no longer declared: "
                + Offenders(stale));
    }

    /// <summary>
    ///     Every row in both tables states WHY it is tolerated here. The shared
    ///     <see cref="ExemptionReason" /> check makes that a value rather than a
    ///     comment above the row, which no tool reads.
    /// </summary>
    [Test]
    public async Task ReviewedRowsStateWhyTheyAreTolerated()
    {
        var rows = new List<(string Key, ExemptionReason.Row Row)>(ReviewedAll.Length + ReviewedReferences.Length);

        foreach ((string fileName, string ns, ExemptionReason.Row allowance) in ReviewedAll)
        {
            rows.Add(($"ReviewedImports[{fileName}: {ns}]", allowance));
        }

        foreach ((string project, ExemptionReason.Row allowance) in ReviewedReferences)
        {
            rows.Add(($"ReviewedReferences[{project}]", allowance));
        }

        IReadOnlyList<string> failures = ExemptionReason.RowsWithoutAReason("ReviewedImports/ReviewedReferences", rows);

        await Assert.That(failures).IsEmpty()
            .Because(
                "each row grants a permission, so it owes an argument. A row that cannot say why the "
                + "import or the edge may stay is indistinguishable from an accident, and the reason "
                + "belongs in the row because a comment above it is invisible to every tool that "
                + "reads this table. Failures: " + Offenders(failures));
    }

    // =====================================================================
    // 4. Non-vacuity.
    // =====================================================================

    /// <summary>
    ///     The scan must SEE the engine, and the promise must still be written down.
    ///     A rule pointed at a path nobody occupies is green forever — the
    ///     NetArchTest trap this project has been bitten by — and a promise quietly
    ///     deleted is the same failure wearing a different hat.
    /// </summary>
    [Test]
    public async Task NonVacuityDiscoverySeesTheEngineProjectAndItsDescription()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("without a repository root the scan finds no files and every rule in this file "
                   + "passes for the wrong reason. RepoPaths degrades to empty rather than throwing, "
                   + "so this is the one assertion standing between a green run and a blind one.");

        IReadOnlyList<string> files = RepoPaths.EnumerateCsFiles(ProjectDir);

        await Assert.That(files.Count).IsGreaterThan(20)
            .Because($"{ProjectDir} is a large project directory. A much smaller number means the "
                   + "walk stopped finding files and the import rule above is grading an empty set. "
                   + "Found: " + files.Count);

        string? csproj = CsprojPath();
        await Assert.That(csproj).IsNotNull()
            .Because($"the engine's csproj must exist under src/{ProjectDir} for the Description "
                   + "promise to be checkable at all.");

        string? description = ReadDescription(csproj!);
        await Assert.That(description).IsNotNull()
            .Because("the engine csproj declares a <Description>. If it ever stops doing so, the "
                   + "self-description half of this guard has no subject, and this test says so "
                   + "instead of letting the other rules pass vacuously.");

        await Assert.That(description!).Contains(BclOnlyClaim)
            .Because(
                "this file exists to grade the '" + BclOnlyClaim + "' promise, and the engine csproj "
                + "still makes it. That promise is deliberately NOT withdrawn here — the issue's point "
                + "is that it is currently false, not that it should stop being made. If the phrase "
                + "disappears, that is a decision for a human to record by editing this file, not a "
                + "side effect of an unrelated csproj change.");
    }

    /// <summary>
    ///     The ambient-channel matcher must fire on every spelling of
    ///     <c>global using</c> and stay silent on the two things that are most
    ///     likely to make it look like it works. The silent cases are the ones
    ///     that matter: a doc comment saying <c>global using</c> is what
    ///     <c>FocusRouter.cs</c> used to carry, and a plain per-file
    ///     <c>using</c> is among the 34 rows above — if this matcher fired on either,
    ///     the rule would be red for a tree that is doing exactly the right
    ///     thing.
    /// </summary>
    [Test]
    public async Task NonVacuityTheGlobalUsingMatcherFiresOnPlantedAmbientUsingsOnly()
    {
        (string Name, string Source)[] ambient =
        [
            ("plain.cs", "global using Harbor.Ui.Framework.Rendering;"),
            ("static.cs", "global using static Harbor.Ui.Framework.Rendering.Widgets;"),
            ("indented.cs", "    global using Harbor.Abstractions.Models;"),
            ("second-in-file.cs", "namespace N;\nglobal using Harbor.Ui.Framework.Rendering;"),
            ("no-trailing-semicolon-needed.cs", "global using System.Text"),
        ];

        (string Name, string Source)[] quiet =
        [
            ("per-file.cs", "using Harbor.Ui.Framework.Rendering;"),
            ("comment.cs", "// GlobalUsings.cs used to carry global using of the UI namespaces."),
            ("trailing-comment.cs", "public sealed class C { } // global using X;"),
            ("doc.cs", "/// <c>global using</c> is what this project deleted."),
            ("string.cs", "var s = \"global using Harbor.Ui.Framework.Rendering;\";"),
            ("notusing.cs", "public sealed class GlobalUsing {}"),
        ];

        foreach ((string name, string source) in ambient)
        {
            bool fired = SourceScan.StripComments(source).Split('\n')
                .Any(l => GlobalUsingDirective.IsMatch(l));

            await Assert.That(fired).IsTrue()
                .Because($"'{name}' declares an ambient using, so the matcher must see it. Zero here "
                       + "means the rule cannot fire at all, and a rule that cannot fire is the "
                       + "merged guard's blind spot rebuilt one clause later.");
        }

        foreach ((string name, string source) in quiet)
        {
            bool fired = SourceScan.StripComments(source).Split('\n')
                .Any(l => GlobalUsingDirective.IsMatch(l));

            await Assert.That(fired).IsFalse()
                .Because($"'{name}' is NOT an ambient using. A per-file using is one of the 34 reviewed "
                       + "rows this project now requires, prose is documentation, and a string "
                       + "literal is data. A rule that fires on any of them would be deleted by the "
                       + "first person it annoyed — and this rule exists precisely because the "
                       + "previous shape let a borrower through unseen.");
        }
    }

    /// <summary>
    ///     Every forbidden spelling must be caught exactly once, and every near-miss
    ///     must stay silent: prose that names the namespace, a string literal, the
    ///     engine's own namespace, a namespace that merely shares a prefix,
    ///     <c>Harbor.DesignSystem</c> (#436's edge, not an import-rule target), and
    ///     <c>Harbor.Plugins.*</c> (exempt everywhere, #626).
    /// </summary>
    [Test]
    public async Task NonVacuityTheImportMatcherFiresOnPlantedOffendersOnly()
    {
        (string Name, string Source)[] offenders =
        [
            ("plain.cs", "using Harbor.Ui.Framework.State;"),
            ("global.cs", "global using Harbor.Ui.Framework.Rendering;"),
            ("sub.cs", "using Harbor.Ui.Framework.Rendering.Input;"),
            ("abstractions.cs", "using Harbor.Abstractions.Models;"),
            ("indented.cs", "    using Harbor.Abstractions.Contracts;"),
            ("static.cs", "using static Harbor.Ui.Framework.Rendering.Widgets;"),
            ("alias.cs", "using Cells = Harbor.Ui.Framework.Rendering;"),
        ];

        (string Name, string Source)[] quiet =
        [
            ("comment.cs", "// IFocusTarget moved to Harbor.Ui.Framework.Rendering.Input."),
            ("doc.cs", "/// <see cref=\"Harbor.Ui.Framework.State.AppMsg\" /> is a message."),
            ("string.cs", "var ns = \"using Harbor.Ui.Framework.State;\";"),
            ("own.cs", "using Harbor.Tui.CellForge.Rendering;"),
            ("prefix.cs", "using Harbor.Ui.Frameworking;"),
            ("designsystem.cs", "using Harbor.DesignSystem;"),
            ("plugins.cs", "using Harbor.Plugins.Abstractions;"),
            ("system.cs", "using System.Text;"),
            ("notusing.cs", "public sealed class HarborUiFrameworkState { }"),
        ];

        foreach ((string name, string source) in offenders)
        {
            IReadOnlyList<ImportHit> hits = ForbiddenImportsIn(name, source);
            await Assert.That(hits.Count).IsEqualTo(1)
                .Because($"'{name}' is one of the forbidden shapes, so exactly one import must be "
                       + "reported for it. Zero means the matcher has a hole in that spelling; more "
                       + "than one means it is matching something that is not a using directive.");
        }

        foreach ((string name, string source) in quiet)
        {
            IReadOnlyList<ImportHit> hits = ForbiddenImportsIn(name, source);
            await Assert.That(hits).IsEmpty()
                .Because(
                    $"'{name}' is NOT a forbidden import and the rule must stay silent on it. Prose "
                    + "and string literals naming a namespace are documentation; Harbor.Tui.CellForge.* "
                    + "is the engine's own namespace; 'Harbor.Ui.Frameworking' merely shares a "
                    + "prefix; Harbor.DesignSystem is #436's edge and deliberately not an "
                    + "import-rule target; Harbor.Plugins.* is exempt everywhere (#626). A guard "
                    + "that fires on any of these is deleted by the first person it annoys. "
                    + "Reported: " + Offenders(hits.Select(h => h.Namespace)));
        }
    }
}