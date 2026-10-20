// CellForgeEngineAtomicityRules.cs — issue #795, step 1; CLOSED by #436.
//
// THE HOLE THIS FILE EXISTS TO CLOSE
// ----------------------------------
// `Harbor.Tui.CellForge.Engine` describes ITSELF in its csproj:
//
//     <Description>… BCL-only, AOT-compatible. …</Description>
//
// and used to declare FOUR ProjectReferences while saying that:
//
//     Harbor.Abstractions
//     Harbor.Ui.Framework.Rendering
//     Harbor.Ui.Framework.State
//     Harbor.DesignSystem
//
// Every layering gate in this project reads `<ProjectReference>` —
// `LayerDependencyTests`, `NetArchLayerRules`, `FullLayerMatrixTests`,
// `DeclaredButUnboundProjectReferences`. They found four edges that were
// declared on purpose, and all four were PERMITTED by the matrix. So the layer
// gates were green, and they were right to be: the edges were declared, legal,
// and the gates answered the question they were built to answer.
//
// THE LIST ABOVE IS HISTORY, NOT INVENTORY. #435 removed two of the four —
// `Harbor.Abstractions` (dead by fact) and `Harbor.Ui.Framework.State`
// (ported onto the #162 BCL-only vocabulary) — and #436 removed the last two
// (`Harbor.Ui.Framework.Rendering`, `Harbor.DesignSystem`) by porting the
// cell/input/probe vocabulary into the engine and moving the chat-owned
// rendering to Harbor.Tui.CellForge/Chat/Rendering. The paragraphs above are
// left as written because they are what the guard was built to catch.
// Read `ReviewedReferences` (now empty) for what is actually declared.
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
// #435 paid down part of this debt. It removed two edges (Harbor.Abstractions,
// Harbor.Ui.Framework.State) and the two State import rows that were their only
// justification, so both tables below are two rows and one row smaller than the
// merged text described. What remains is #436's: the cell vocabulary in
// Harbor.Ui.Framework.Rendering and the palette constants in
// Harbor.DesignSystem. #436 paid the rest: the tables below are EMPTY, and the
// tests that read them are now zero-tolerance guards rather than a baseline.
// A row appearing here again is a new borrow — red until it carries an
// allowance and a tracking issue, exactly as before.
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

// MECHANISM (#1086, step 2, conveyor)
// -----------------------------------
// The two source-scan halves below are ScanRules: the ambient-channel ban (no
// `global using` — a plain line scan over the shared stripper) and the
// UI-vocabulary import ban (a CustomParse, because the verdict needs the
// using-directive's captured namespace plus the reviewed-rows exemption, which
// no line regex expresses). Enumeration and the control/discovery verdicts are
// ScanRunner's; this file keeps the issue prose and the test names.
//
// Deliberately NOT moved, and why:
//   * The reviewed tables (`ReviewedImports` / `ReviewedVocabulary` /
//     `ReviewedReferences`) stay as the exemption vehicle with their own
//     reasons check (`ReviewedRowsStateWhyTheyAreTolerated`) and liveness
//     checks (`ReviewedImportRowsAreStillReal`,
//     `ReviewedReferenceRowsAreStillDeclared`) — that is where a future borrow
//     is declared, not in a ScanRule baseline, so no baseline rows exist here.
//   * Scope parity was verified, not assumed: this csproj declares no
//     `<Compile Include>` outside its directory, so the shared tree walk sees
//     exactly the file set `RepoPaths.EnumerateCsFiles` (directory + linked
//     sources) saw — the linked arm is empty here.
//   * The csproj half (`TheBclOnlyClaimIsCheckedAgainstRealReferencesAndImports`,
//     `ANonAtomicEngineNamesItsTrackingIssueInItsOwnDescription`, the Description
//     pins in the discovery test) reads XML, not source — a different flavor,
//     out of this conveyor's scope per #1086.

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
        // #436: empty — the engine imports no Harbor namespace anymore. A row
        // appearing here again is a new borrow and must carry its allowance.
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
        // #436: empty — the vocabulary lives in the engine now (ports) or moved
        // to Chat with the chat-owned files. Same rule as above: a new row is a
        // new borrow, red until it carries an allowance and a tracking issue.
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
    ///     The `<ProjectReference>` edges the csproj declares while calling itself
    ///     BCL-only. Permitted by the layer matrix, and therefore invisible to
    ///     every gate that reads references — which is why the Description claim is
    ///     checked against them here. Two rows after #435 (Harbor.Abstractions and
    ///     Harbor.Ui.Framework.State were removed with the code that justified
    ///     them); both survivors are #436's.
    /// </summary>
    /// <param name="Project">The referenced project directory name.</param>
    /// <param name="Allowance">Why this edge may stay, and what removes it.</param>
    private static readonly (string Project, ExemptionReason.Row Allowance)[] ReviewedReferences =
    [
        // #436: empty — the engine declares zero Harbor references (standalone
        // leaf). A re-added edge is red here AND in the layer matrix.
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
    ///     An ambient <c>using</c>, in any of its spellings
    ///     (<c>global using X;</c>, <c>global using static X;</c>).
    ///     Comment-stripped input only, so a doc comment mentioning the phrase
    ///     cannot be reported — and so cannot be the thing that keeps this rule
    ///     from ever going red.
    /// </summary>
    private static readonly Regex GlobalUsingDirective = new(
        @"^\s*global\s+using\s+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private const string GlobalUsingSubId = "ENGINE-AMBIENT-USING";
    private const string ImportSubId = "ENGINE-UI-VOCABULARY-IMPORT";

    /// <summary>
    ///     The ambient-channel ban as data: no `global using` in any engine file.
    ///     A plain line scan — the shape is line-anchored and needs no file facts.
    /// </summary>
    private static readonly ScanRule GlobalUsingRule = new()
    {
        Id = "CellForgeEngineAtomicity.GlobalUsing",
        Trees = ["src/" + ProjectDir],
        Forbidden =
        [
            new ScanForbidden(
                GlobalUsingSubId,
                GlobalUsingDirective,
                "spell the borrow in the file that needs it — an ambient namespace makes "
                + "every borrower invisible to the import rule below. See issue #795."),
        ],
        Controls =
        [
            new ScanControl("Ambient/Plain.cs", "global using Harbor.Ui.Framework.Rendering;", GlobalUsingSubId),
            new ScanControl("Ambient/Static.cs", "global using static Harbor.Ui.Framework.Rendering.Widgets;", GlobalUsingSubId),
            new ScanControl("Ambient/Indented.cs", "    global using Harbor.Abstractions.Models;", GlobalUsingSubId),
            new ScanControl(
                "Ambient/SecondInFile.cs",
                "namespace N;\nglobal using Harbor.Ui.Framework.Rendering;",
                GlobalUsingSubId),
            new ScanControl("Ambient/NoSemicolon.cs", "global using System.Text", GlobalUsingSubId),
            // A per-file using is one of the rows this project requires — not ambient.
            new ScanControl("Ambient/PerFile.cs", "using Harbor.Ui.Framework.Rendering;", null),
            // Prose about the deleted block is documentation, not a channel.
            new ScanControl("Ambient/Comment.cs", "// GlobalUsings.cs used to carry global using of the UI namespaces.", null),
            new ScanControl(
                "Ambient/Trailing.cs",
                "public sealed class C { } // global using X;",
                null),
            new ScanControl("Ambient/Doc.cs", "/// <c>global using</c> is what this project deleted.", null),
            // A string literal is data, and a type merely named GlobalUsing is not a directive.
            new ScanControl("Ambient/String.cs", "var s = \"global using Harbor.Ui.Framework.Rendering;\";", null),
            new ScanControl("Ambient/Type.cs", "public sealed class GlobalUsing {}", null),
        ],
        MinHits = 20,
    };

    /// <summary>
    ///     The import ban as data: no `Harbor.Ui.Framework.*` /
    ///     `Harbor.Abstractions.*` import outside the reviewed rows (empty today —
    ///     fully armed). The verdict needs the directive's captured namespace, so
    ///     the rule grades through `ParseVocabularyImport`.
    /// </summary>
    private static readonly ScanRule ImportRule = new()
    {
        Id = "CellForgeEngineAtomicity.Import",
        Trees = ["src/" + ProjectDir],
        Forbidden =
        [
            // The directive shape the parser grades — not a copy of it. The ban
            // itself (the two forbidden prefixes minus the reviewed rows) is
            // applied by `ParseVocabularyImport`, which this row documents.
            new ScanForbidden(
                ImportSubId,
                UsingDirective,
                "the engine's csproj calls itself BCL-only: a new borrow is red until it "
                + "carries a reviewed row with an allowance and a tracking issue. See issue #795."),
        ],
        Controls =
        [
            new ScanControl("Imports/Plain.cs", "using Harbor.Ui.Framework.State;", ImportSubId),
            new ScanControl("Imports/Global.cs", "global using Harbor.Ui.Framework.Rendering;", ImportSubId),
            new ScanControl("Imports/Sub.cs", "using Harbor.Ui.Framework.Rendering.Input;", ImportSubId),
            new ScanControl("Imports/Abstractions.cs", "using Harbor.Abstractions.Models;", ImportSubId),
            new ScanControl("Imports/Indented.cs", "    using Harbor.Abstractions.Contracts;", ImportSubId),
            new ScanControl("Imports/Static.cs", "using static Harbor.Ui.Framework.Rendering.Widgets;", ImportSubId),
            new ScanControl("Imports/Alias.cs", "using Cells = Harbor.Ui.Framework.Rendering;", ImportSubId),
            // Prose and string literals naming a namespace are documentation.
            new ScanControl("Imports/Comment.cs", "// IFocusTarget moved to Harbor.Ui.Framework.Rendering.Input.", null),
            new ScanControl(
                "Imports/Doc.cs",
                "/// <see cref=\"Harbor.Ui.Framework.State.AppMsg\" /> is a message.",
                null),
            new ScanControl("Imports/String.cs", "var ns = \"using Harbor.Ui.Framework.State;\";", null),
            // The engine's own namespace, a mere prefix-sharer, #436's edge, the
            // #626-exempt plugin family, the BCL, and a non-directive mention.
            new ScanControl("Imports/Own.cs", "using Harbor.Tui.CellForge.Rendering;", null),
            new ScanControl("Imports/Prefix.cs", "using Harbor.Ui.Frameworking;", null),
            new ScanControl("Imports/DesignSystem.cs", "using Harbor.DesignSystem;", null),
            new ScanControl("Imports/Plugins.cs", "using Harbor.Plugins.Abstractions;", null),
            new ScanControl("Imports/System.cs", "using System.Text;", null),
            new ScanControl("Imports/NotUsing.cs", "public sealed class HarborUiFrameworkState { }", null),
        ],
        MinHits = 20,
        CustomParse = ParseVocabularyImport,
    };

    /// <summary>
    ///     The custom parser: the `AllForbiddenImports` inner loop over one file's
    ///     raw source — the shared stripper, the directive's captured namespace,
    ///     the forbidden prefixes, minus the reviewed rows. Same fields, same
    ///     order, same verdict; the synthetic controls below grade this parser
    ///     while `NonVacuityTheImportMatcherFiresOnPlantedOffendersOnly` keeps
    ///     pinning `ForbiddenImportsIn` directly, so the two stay honest about
    ///     each other. Reviewed rows are keyed by the PROJECT-relative file name,
    ///     derived here by trimming this rule's own tree prefix.
    /// </summary>
    private static IEnumerable<ScanHit> ParseVocabularyImport(string displayPath, string rawSource)
    {
        string prefix = "src/" + ProjectDir + "/";
        string fileName = displayPath.StartsWith(prefix, StringComparison.Ordinal)
            ? displayPath[prefix.Length..]
            : displayPath;

        string[] lines = SourceScan.StripComments(rawSource).Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            Match match = UsingDirective.Match(lines[i]);
            if (match.Success && IsForbidden(match.Groups["ns"].Value)
                && !IsReviewed(fileName, match.Groups["ns"].Value))
            {
                yield return new ScanHit(ImportSubId, displayPath, i + 1, lines[i].Trim());
            }
        }
    }

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
        List<string> violations = ScanRunner.Evaluate(GlobalUsingRule);

        await Assert.That(violations).IsEmpty()
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
                + (violations.Count == 0 ? "(none)" : string.Join("; ", violations)));
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
        // The reviewed-rows exemption lives in the parser now; this test reads the
        // shared evaluation.
        List<string> violations = ScanRunner.Evaluate(ImportRule);

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
                "these edges are listed so the 'BCL-only' claim can be checked against them, not so "
                + "they may live forever. #435 already removed two of them (Harbor.Abstractions and "
                + "Harbor.Ui.Framework.State) and #436 removes the rest; when one goes its row must "
                + "go with it — otherwise the table becomes a menu for re-adding an edge the layer "
                + "matrix already permits. Rows naming edges no longer declared: " + Offenders(stale));
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

        List<string> globalDiscovery = ScanRunner.CheckDiscovery(GlobalUsingRule);
        List<string> importDiscovery = ScanRunner.CheckDiscovery(ImportRule);

        await Assert.That(globalDiscovery).IsEmpty()
            .Because($"{ProjectDir} is a large project directory. A much smaller number means the "
                   + "walk stopped finding files and the channel rule above is grading an empty set. "
                   + string.Join("; ", globalDiscovery));
        await Assert.That(importDiscovery).IsEmpty()
            .Because($"{ProjectDir} is a large project directory. A much smaller number means the "
                   + "walk stopped finding files and the import rule above is grading an empty set. "
                   + string.Join("; ", importDiscovery));

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
        // The ambient spellings (five) and the must-stay-silent cases (six) live
        // as controls on the rule and drive the REAL line scan. The silent cases
        // are the ones that matter: a doc comment saying `global using` is what
        // `FocusRouter.cs` used to carry, and a plain per-file `using` is among
        // the reviewed rows this project requires — if the matcher fired on
        // either, the rule would be red for a tree that is doing exactly the
        // right thing.
        List<string> failures = ScanRunner.CheckControls(GlobalUsingRule);

        await Assert.That(failures).IsEmpty()
            .Because(
                "the ambient channel must be visible in every spelling and invisible "
                + "everywhere else; zero on an offender means the rule cannot fire at all, "
                + "and a hit on prose, a literal or a per-file using means the rule would "
                + "be deleted by the first person it annoyed. "
                + string.Join("; ", failures));
    }

    [Test]
    public async Task NonVacuityTheImportMatcherFiresOnPlantedOffendersOnly()
    {
        // Every forbidden spelling (seven) and every near-miss (nine) live as
        // controls on the rule and drive the REAL parser: prose that names the
        // namespace, a string literal, the engine's own namespace, a namespace
        // that merely shares a prefix, `Harbor.DesignSystem` (#436's edge, not
        // an import-rule target), and `Harbor.Plugins.*` (exempt everywhere,
        // #626). `ForbiddenImportsIn` itself stays as the matcher core the
        // parser mirrors.
        List<string> failures = ScanRunner.CheckControls(ImportRule);

        await Assert.That(failures).IsEmpty()
            .Because(
                "every forbidden spelling must be caught exactly once, and every near-miss "
                + "must stay silent. A matcher that fires on prose is deleted by the first "
                + "person it annoys; one that fires on nothing enforces nothing. "
                + string.Join("; ", failures));
    }

    // =====================================================================
    // 5. The #1620 batch-A verbatim leaf set, pinned where it landed.
    // =====================================================================

    /// <summary>
    ///     The #1620 batch-A verbatim leaf moves, as (engine path, chat path)
    ///     relative to each project directory. Each of these files resolves
    ///     with BCL and the engine's own namespace family alone — that is the
    ///     property that made the move verbatim: bodies untouched, namespaces
    ///     unchanged, import rule above still grading them where they now
    ///     live. The table is the assertion: a row whose engine file is gone,
    ///     or whose chat file is back, names the drift.
    /// </summary>
    private static readonly (string EngineFile, string ChatFile)[] VerbatimLeafMoves =
    [
        ("Widgets/SpinnerStrip.cs", "Chat/Widgets/SpinnerStrip.cs"),
        ("Widgets/RetryCountdown.cs", "Chat/Widgets/RetryCountdown.cs"),
        ("Widgets/JpegProbe.cs", "Chat/Widgets/JpegProbe.cs"),
        ("Widgets/ScrollableViewport.cs", "Chat/Widgets/ScrollableViewport.cs"),
        ("Widgets/ScrollInfo.cs", "Chat/Widgets/ScrollInfo.cs"),
        ("Widgets/MascotMode.cs", "Chat/Widgets/MascotMode.cs"),
        ("Widgets/ToolOutputCollapse.cs", "Chat/Widgets/ToolOutputCollapse.cs"),
        ("Widgets/ThinkingSummary.cs", "Chat/Widgets/ThinkingSummary.cs"),
        ("Streaming/CommitTickPacer.cs", "Chat/Streaming/CommitTickPacer.cs"),
    ];

    /// <summary>
    ///     The verbatim leaf set lives in the engine, and no chat copy is back
    ///     beside it. The reverse half is the load-bearing one: a Chat file
    ///     shadowing the engine copy in the same namespace wins resolution for
    ///     chat callers, so the two bodies would drift apart while every rule
    ///     here grades only the engine side — the silent-fork failure this
    ///     batch exists to move away from.
    /// </summary>
    [Test]
    public async Task TheVerbatimLeafSetLivesInTheEngineAndItsChatCopiesAreGone()
    {
        if (RepoPaths.RepoRoot is not { } root)
        {
            return;
        }

        var drift = new List<string>();
        foreach ((string engineFile, string chatFile) in VerbatimLeafMoves)
        {
            if (!File.Exists(Path.Combine(root, "src", ProjectDir, engineFile.Replace('/', Path.DirectorySeparatorChar))))
            {
                drift.Add($"src/{ProjectDir}/{engineFile} — row in VerbatimLeafMoves names a file that is gone; "
                          + "either the leaf moved again (update the row in the same commit) or the engine lost it");
            }

            if (File.Exists(Path.Combine(root, "src", "Harbor.Tui.CellForge", chatFile.Replace('/', Path.DirectorySeparatorChar))))
            {
                drift.Add($"src/Harbor.Tui.CellForge/{chatFile} — a Chat copy came back and now shadows the "
                          + "engine type for chat callers; delete the copy or fold it into the engine file");
            }
        }

        await Assert.That(drift).IsEmpty()
            .Because(
                "#1620 moved nine pure-BCL chat-widget leaves into the engine verbatim (no adaptation: "
                + "bodies byte-equal, namespaces unchanged, zero Harbor imports in the bodies outside doc "
                + "crefs). This file's import rule keeps grading them in their new home, and rows in "
                + "VerbatimLeafMoves keep grading that the home and the origin do not disagree — which is "
                + "what 'дословный переезд' means as a checkable fact: same body, one home, no shadow. "
                + "Drift: " + Offenders(drift));
    }

    // ── #1627 batch B (adapted) ─────────────────────────────────────────────
    //
    // Batch A moved leaves whose bodies were byte-equal chat↔engine. Batch B
    // (#1627) moves leaves that still bind ONE small vocabulary — and adapts
    // that binding instead of dragging it along:
    //
    //   * the `Ensure` guard alias (Harbor.Abstractions.Extensions, carried by
    //     Harbor.Extensions) is not BCL, so the moved body retypes each
    //     `Ensure.NotNull(value, name)` to `ArgumentNullException.ThrowIfNull(
    //     value)` — the alias's own delegate implementation, with the
    //     CallerArgumentExpression name preserved; behavior-identical by
    //     construction, and FuzzyMatcher/AutocompletePalette gain no new
    //     vocabulary in their new home;
    //   * `AutocompleteMode` re-homes out of AutocompleteTrigger.cs beside the
    //     engine-hosted filter family it parameterizes (AutocompleteItem.Mode,
    //     AutocompleteFilter.Filter). The trigger record, the trigger scan and
    //     the PromptBuffer extensions stay in that chat file untouched —
    //     KeyGateFamilyRules pins AutocompleteTriggerScan's chat path, and the
    //     file keeps its Rendering / UiKey vocabulary.
    //
    // The painting widgets named in the issue (ColumnsBlock, GaugeBar,
    // CalendarView, RuleView, SelectionEngine) do NOT follow: they paint the
    // UIR painter grid (global `Cell`/`ScreenBuffer` aliases →
    // Harbor.Ui.Framework.Rendering — a different buffer type from the engine
    // grid, see ScreenSession's painter-side BACK) and three of them bind
    // ChatPalette (DesignSystem) / PanelChrome / IChatBlock. Hosted here they
    // would need exactly the imports the empty Reviewed* tables above forbid.
    //
    // The chat half of a whole-file row is a LEGAL deletion (the guard retype
    // is the only body change), so the verdicts mirror VerbatimLeafMoves: the
    // engine home exists, no chat copy shadows it, and the re-homed enum no
    // longer lives in the chat file it left.

    /// <summary>
    ///     Whole-file adapted leaves (guard retype, body otherwise equal):
    ///     the engine home and the deleted chat leaf.
    /// </summary>
    private static readonly (string EngineFile, string ChatFile)[] AdaptedLeafMoves =
    [
        ("Widgets/FuzzyMatcher.cs", "Chat/Widgets/FuzzyMatcher.cs"),
        ("Widgets/AutocompletePalette.cs", "Chat/Widgets/AutocompletePalette.cs"),
    ];

    /// <summary>
    ///     The re-homed enum: engine file that must exist, and the chat file it
    ///     left — which must itself stay (KeyGateFamilyRules pins its path)
    ///     while no longer declaring the moved type.
    /// </summary>
    private const string RehomedEnumEngineFile = "Widgets/AutocompleteMode.cs";
    private const string RehomedEnumChatFile = "Chat/Widgets/AutocompleteTrigger.cs";
    private const string RehomedEnumChatDeclaration = "public enum AutocompleteMode";

    /// <summary>
    ///     The adapted leaf set lives in the engine, and no chat copy is back
    ///     beside it. The shadow half is the load-bearing one, exactly as for
    ///     VerbatimLeafMoves: the moved types keep the namespace
    ///     <c>Harbor.Tui.CellForge.Widgets</c>, so a chat copy in the same
    ///     namespace wins resolution for chat callers while engine callers see
    ///     the engine one — a silent fork across assemblies. The re-homed
    ///     enum's chat declaration is the same hazard in miniature: two
    ///     <c>AutocompleteMode</c> types under one name.
    /// </summary>
    [Test]
    public async Task TheAdaptedLeafSetLivesInTheEngineAndItsChatCopiesAreGone()
    {
        if (RepoPaths.RepoRoot is not { } root)
        {
            return;
        }

        var drift = new List<string>();
        foreach ((string engineFile, string chatFile) in AdaptedLeafMoves)
        {
            if (!File.Exists(Path.Combine(root, "src", ProjectDir, engineFile.Replace('/', Path.DirectorySeparatorChar))))
            {
                drift.Add($"src/{ProjectDir}/{engineFile} — row in AdaptedLeafMoves names a file that is gone; "
                          + "either the leaf moved again (update the row in the same commit) or the engine lost it");
            }

            if (File.Exists(Path.Combine(root, "src", "Harbor.Tui.CellForge", chatFile.Replace('/', Path.DirectorySeparatorChar))))
            {
                drift.Add($"src/Harbor.Tui.CellForge/{chatFile} — a Chat copy came back and now shadows the "
                          + "engine type for chat callers; delete the copy or fold it into the engine file");
            }
        }

        string rehomedEngine = Path.Combine(root, "src", ProjectDir, RehomedEnumEngineFile.Replace('/', Path.DirectorySeparatorChar));
        string rehomedChat = Path.Combine(root, "src", "Harbor.Tui.CellForge", RehomedEnumChatFile.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(rehomedEngine))
        {
            drift.Add($"src/{ProjectDir}/{RehomedEnumEngineFile} — the re-homed AutocompleteMode engine home is gone; "
                      + "update the row in the same commit or the chat scan loses the type its trigger record names");
        }

        if (!File.Exists(rehomedChat))
        {
            drift.Add($"src/Harbor.Tui.CellForge/{RehomedEnumChatFile} — AutocompleteTriggerScan's pinned home is gone; "
                      + "KeyGateFamilyRules names this exact path");
        }
        else
        {
            string chatText = File.ReadAllText(rehomedChat);
            if (chatText.Contains(RehomedEnumChatDeclaration, StringComparison.Ordinal))
            {
                drift.Add($"src/Harbor.Tui.CellForge/{RehomedEnumChatFile} — declares AutocompleteMode again; "
                          + "the re-homed engine enum is now forked under one name across the two assemblies");
            }
        }

        await Assert.That(drift).IsEmpty()
            .Because(
                "#1627 batch B moved the adapted engine leaves (guard retype, re-homed enum) behind the same "
                + "one-home-no-shadow contract as #1620's verbatim rows: AdaptedLeafMoves grades the two "
                + "whole-file leaves, and the re-home check grades AutocompleteMode's move out of "
                + "AutocompleteTrigger.cs without retyping any chat caller. Drift: " + Offenders(drift));
    }

    // ── #1621 engine C1: the vocabulary's source of truth ───────────────────
    //
    // #436 made the engine a standalone leaf (zero Harbor references), and the
    // price of that leaf was a SECOND COPY of the cell/screen/input vocabulary:
    // Cell, Rect, ScreenBuffer, CellStyle, KeyEvent and friends live in the
    // engine AND in the shared rendering layer, and the two copies are kept in
    // sync BY HAND. #1621 names the two ways out:
    //
    //   * merge the vocabularies — the shared layer would have to reference
    //     the engine, which inverts the layering the engine's isolation bought
    //     (Harbor.Ui.Framework.Rendering is the renderer-agnostic shared layer;
    //     a CellForge-specific engine under it is a backend-specific dependency
    //     in the wrong direction), and it would collapse the dual-vocabulary
    //     boundary ScreenSession is the documented crossing point of;
    //   * make one side the source of truth and GUARD the other side against
    //     drift — which is what this table and
    //     CellForgeEngineVocabularyParityRules do.
    //
    // The table below is the second way made checkable. The ENGINE side is the
    // source of truth (it is the leaf; it is the side that cannot borrow), and
    // each row names the shared twin that must stay a verbatim copy of it, the
    // type names the pair declares, and the spelling differences that are
    // SANCTIONED — today exactly one, the guard retype #1627 batch B already
    // used: the engine is BCL-only, so `Ensure.NotNull` (Harbor.Extensions) is
    // spelled `ArgumentNullException.ThrowIfNull` there, and the alias's own
    // delegate makes the two behavior-identical by construction.
    //
    // A row appearing here is a new pair: the engine file, the shared twin and
    // the type names are all spelled out, so a new copy of the vocabulary
    // cannot appear beside the old one without this table naming it.

    /// <summary>
    ///     One sanctioned spelling difference between an engine vocabulary file
    ///     and its shared twin: the shared side's spelling, and the engine's.
    /// </summary>
    /// <param name="Shared">How the shared twin spells the line.</param>
    /// <param name="Engine">How the engine spells the same line.</param>
    internal readonly record struct VocabularyRewrite(string Shared, string Engine);

    /// <summary>
    ///     One vocabulary pair: the engine's copy (the source of truth), the
    ///     shared twin that must stay identical to it, the type names the pair
    ///     declares, and the sanctioned spelling differences between them.
    /// </summary>
    /// <param name="EngineFile">The engine file, relative to the engine project directory.</param>
    /// <param name="SharedProject">The shared project directory that holds the twin.</param>
    /// <param name="SharedFile">The twin file, relative to the shared project directory.</param>
    /// <param name="TypeNames">Every public type name the pair declares.</param>
    /// <param name="Rewrites">The sanctioned spelling differences, if any.</param>
    internal sealed record VocabularyPair(
        string EngineFile,
        string SharedProject,
        string SharedFile,
        string[] TypeNames,
        VocabularyRewrite[] Rewrites);

    /// <summary>
    ///     The vocabulary pairs, engine side first. The engine file is the source
    ///     of truth; the shared twin is the copy that must not drift from it.
    /// </summary>
    internal static readonly VocabularyPair[] VocabularyPairs =
    [
        new("Rendering/Cell.cs", "Harbor.Ui.Framework.Rendering", "Cell.cs", ["Cell"], []),
        new("Rendering/Rect.cs", "Harbor.Ui.Framework.Rendering", "Rect.cs", ["Rect"], []),
        new("Rendering/CellDiffOption.cs", "Harbor.Ui.Framework.Rendering", "CellDiffOption.cs",
            ["CellDiffOption"], []),
        new("Rendering/UnicodeWidth.cs", "Harbor.Ui.Framework.Rendering", "UnicodeWidth.cs",
            ["UnicodeWidth"],
            [
                // #1627 batch B's guard retype: the engine is BCL-only, so the
                // alias (Harbor.Abstractions.Extensions.Ensure.NotNull, carried by
                // Harbor.Extensions) is spelled as its own delegate there.
                // Ensure.NotNull forwards to ArgumentNullException.ThrowIfNull,
                // so the two spellings throw the same exception with the same
                // parameter name — behavior-identical by construction.
                new("Harbor.Abstractions.Extensions.Ensure.NotNull(text);",
                    "ArgumentNullException.ThrowIfNull(text);"),
            ]),
        new("Rendering/ScreenBuffer.cs", "Harbor.Ui.Framework.Rendering", "ScreenBuffer.cs",
            ["ScreenBuffer"], []),
        // CellStyle and RgbColor live in the DesignSystem package under their
        // historical Harbor.Ui.Framework.* namespaces, so the twin is there and
        // not in the rendering project.
        new("Rendering/CellStyle.cs", "Harbor.DesignSystem", "DesignSystem/CellStyle.cs",
            ["StyleAttr", "PackedColor", "CellStyle"], []),
        new("Capabilities/RgbColor.cs", "Harbor.DesignSystem", "DesignSystem/RgbColor.cs",
            ["RgbColor"], []),
        new("Input/KeyEvent.cs", "Harbor.Ui.Framework.Rendering", "Input/KeyEvent.cs", ["KeyEvent"], []),
        new("Input/KeyCode.cs", "Harbor.Ui.Framework.Rendering", "Input/KeyCode.cs", ["KeyCode"], []),
        new("Input/KeyEventType.cs", "Harbor.Ui.Framework.Rendering", "Input/KeyEventType.cs",
            ["KeyEventType"], []),
        new("Input/KeyModifiers.cs", "Harbor.Ui.Framework.Rendering", "Input/KeyModifiers.cs",
            ["KeyModifiers"], []),
        new("Input/UiKeyDto.cs", "Harbor.Ui.Framework.Rendering", "Input/UiKeyDto.cs",
            ["UiKeyKind", "UiKeyMods", "UiKeyDto"], []),
        new("Input/IFocusTarget.cs", "Harbor.Ui.Framework.Rendering", "Input/IFocusTarget.cs",
            ["IFocusTarget"], []),
    ];

    /// <summary>
    ///     A public type declaration, by simple name. Modifiers are consumed in
    ///     any order and any number, so <c>public readonly record struct</c> and
    ///     <c>public sealed partial class</c> are both matched, and a field
    ///     (<c>public static readonly UiKeyDto Unknown</c>) is not — the shape
    ///     after the modifiers must be a type keyword.
    /// </summary>
    private static readonly Regex PublicTypeDeclaration = new(
        @"^\s*public\s+(?:(?:sealed|static|readonly|partial|ref|record|abstract|unsafe|new)\s+)*(?:struct|class|enum|interface)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    /// <summary>
    ///     Every public type name the vocabulary declares, across all pairs.
    /// </summary>
    private static readonly HashSet<string> VocabularyTypeNames =
        new(VocabularyPairs.SelectMany(p => p.TypeNames), StringComparer.Ordinal);

    /// <summary>
    ///     The vocabulary is declared in exactly two assemblies — the engine and
    ///     the shared twin the table names — and in no third one. This is the
    ///     inventory half of #1621: the parity rule
    ///     (<see cref="CellForgeEngineVocabularyParityRules" />) grades the two
    ///     copies against each other, and this one grades that there are only
    ///     two of them. A third copy in a third assembly is the silent fork the
    ///     issue names: a fix lands in whichever copy the author had open, and
    ///     every rule here grades only the two it knows about.
    /// </summary>
    [Test]
    public async Task TheEngineVocabularyIsDeclaredInTheEngineAndItsSharedTwinAndNowhereElse()
    {
        if (RepoPaths.RepoRoot is not { } root)
        {
            return;
        }

        var drift = new List<string>();

        foreach (VocabularyPair pair in VocabularyPairs)
        {
            string enginePath = Path.Combine(root, "src", ProjectDir, pair.EngineFile.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(enginePath))
            {
                drift.Add($"src/{ProjectDir}/{pair.EngineFile} — the engine side of the vocabulary is gone; "
                          + "update the row in the same commit or the twin has no source of truth left");
            }

            string sharedPath = Path.Combine(root, "src", pair.SharedProject, pair.SharedFile.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(sharedPath))
            {
                drift.Add($"src/{pair.SharedProject}/{pair.SharedFile} — the shared twin is gone; "
                          + "the vocabulary now exists in one assembly only, which is a different architecture");
            }
        }

        // Which assemblies declare a vocabulary type name at all.
        var declaringProjects = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (string csproj in RepoPaths.EnumerateSrcProjects())
        {
            string project = Path.GetFileName(Path.GetDirectoryName(csproj)!) ?? string.Empty;
            foreach (string file in RepoPaths.EnumerateCsFiles(project))
            {
                if (SourceScan.TryReadAllText(file) is not { } text)
                {
                    continue;
                }

                foreach (Match match in PublicTypeDeclaration.Matches(text))
                {
                    string name = match.Groups["name"].Value;
                    if (!VocabularyTypeNames.Contains(name))
                    {
                        continue;
                    }

                    if (!declaringProjects.TryGetValue(name, out SortedSet<string> projects))
                    {
                        projects = new SortedSet<string>(StringComparer.Ordinal);
                        declaringProjects[name] = projects;
                    }

                    projects.Add(project);
                }
            }
        }

        foreach (VocabularyPair pair in VocabularyPairs)
        {
            foreach (string name in pair.TypeNames)
            {
                var expected = new SortedSet<string>(StringComparer.Ordinal) { ProjectDir, pair.SharedProject };
                var actual = declaringProjects.TryGetValue(name, out SortedSet<string>? found)
                    ? found
                    : new SortedSet<string>(StringComparer.Ordinal);

                if (!expected.SetEquals(actual))
                {
                    drift.Add($"{name} — declared in [{string.Join(", ", actual)}], expected exactly "
                              + $"[{string.Join(", ", expected)}] (the engine and its shared twin, and nothing else)");
                }
            }
        }

        await Assert.That(drift).IsEmpty()
            .Because(
                "#1621: the engine's cell/screen/input vocabulary is copied into the shared rendering layer "
                + "because the engine must stay a zero-Harbor-reference leaf, and the two copies were kept in "
                + "sync by hand. The engine side is the source of truth, the shared twin is the copy, and this "
                + "table names both — so a third copy of a vocabulary type in a third assembly is a fork that "
                + "every other rule here would grade only one side of. Drift: " + Offenders(drift));
    }
}
