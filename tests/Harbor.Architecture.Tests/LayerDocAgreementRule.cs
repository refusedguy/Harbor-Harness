// LayerDocAgreementRule.cs — GUARD for issue #888.
//
// WHAT #888 NAMED
// ---------------
// `docs/ARCHITECTURE_LAYERS.md` §1 assigned all four IPC assemblies to APPLICATION.
// `FullLayerMatrixTests.Matrix` says Domain for `Harbor.Ipc.Abstractions` and
// Infrastructure for `Harbor.Ipc.Client` / `.InProcess` / `.Server`. The doc was
// the wrong side, and it was not a stale line: §1 had never agreed with the
// matrix on this family.
//
// WHY A DOC FIX ALONE IS NOT THE ANSWER
// -------------------------------------
// This is the third documented instance of one behaviour, and the pattern is
// what makes it worth a guard rather than twelve edits:
//
//   #879  three false rows in the §5.6 capability table
//   #896  nine false places in §1, two of which had been false SINCE THE MATRIX
//         ROW WAS CREATED (2026-08-25, 5d2df19f) — not out of date, wrong
//   #888  twelve false places in §1's APPLICATION box, plus one the issue did
//         not name (Harbor.Desktop.Abstractions is not involved; Harbor.DesignSystem
//         is — see R1 below)
//
// Three separate doc edits, three separate merges, and the file still lies. The
// reason is structural and is the same one `DefaultModelDocClaimTests` (#649) and
// `check-doc-cites.py` (#664) each had to be written for: `ci.yml` ignores `**.md`
// and `docs/**` on purpose (#509), so the docs gate checks links, anchors,
// encoding, headings, fences and `file:line` fences — never whether a stated FACT
// is still true. `check-doc-cites.py` is the sharpest existing statement of the
// gap and it is worth reading the two together: that tool proves a cited line
// still EXISTS, and says so itself — "the fence rules below are necessary but NOT
// sufficient". A `file:line` fence that resolves to a real line of a real file is
// a citation with no meaning in it. Layer assignment is prose, so no existing gate
// sees it at all: it is not even a fence.
//
// So: twelve edits would have left the twelfth place one refactor away from
// breaking again, with nothing red. This file is the thing that makes the
// correction hold.
//
// WHAT IS AUTHORITY, AND WHY IT IS NOT A COINCIDENTAL PREFERENCE
// --------------------------------------------------------------
// The gate. Not because it wins ties, but because of what it is made of:
//
//   * The Allowed sets are MEASURED, not declared. `EverySrcAssembly_ReferenceSet_-
//     MatchesMatrix` reads `Assembly.GetReferencedAssemblies()`, so a row's contents
//     are the compiler's output. §1's layer lists are hand-typed.
//   * The layer CLASS is then forced, not chosen, for the plugin family. Marking
//     `Harbor.Plugins.Runtime` Application would fail
//     `MatrixTable_RespectsLayerRules` if it referenced the other six; the gate
//     permits Infrastructure→Infrastructure only via the `Family()` carve-out, and
//     two other places in the same file (the `SharedSourceFolders` reasons) exist
//     precisely because the matrix "forbids Infrastructure→Infrastructure project
//     references". You cannot read the gate's plugin rows as Application without
//     contradicting its own prose four times.
//   * The `Layer` enum's own XML doc already says it: Infrastructure is
//     "Implementations: providers, storage, tools, IPC endpoints, telemetry,
//     plugin machinery" (`FullLayerMatrixTests.cs:37`). "IPC endpoints" is the
//     gate's own name for `Harbor.Ipc.{Client,InProcess,Server}`.
//
// HONEST LIMIT OF THAT ARGUMENT — the layer class is a judgement, not a derivation
// ------------------------------------------------------------------------------------
// For the IPC rows the edges do NOT force the class. `Harbor.Ipc.Server`'s Allowed
// set is `{Harbor.Ipc.Abstractions, Harbor.Abstractions}` — both Domain — and
// `MatrixTable_RespectsLayerRules` would accept that row as Domain, Application or
// Infrastructure alike. So this file cannot claim the IPC classification is
// derived. What it claims is narrower and checkable: the ALLOWED SETS are
// measurements and are not in dispute, the class agrees with the enum's own
// vocabulary and with the capability content (MessagePack framing in
// `Harbor.Ipc.Client/Protocol/`, named pipes and broadcast fanout in
// `Harbor.Ipc.Server`, pure `[Union]` request/response records and no I/O in
// `Harbor.Ipc.Abstractions`), and no edge is wrong. The doc is the artifact with
// no measurement behind it.
//
// THE THIRD POSSIBILITY, RULED OUT EXPLICITLY
// -------------------------------------------
// The rule does allow exceptions, so "the matrix says as-intended and
// `DocumentedExceptions` says as-obtained — both are true" had to be checked
// rather than assumed. It is false here: `DocumentedExceptions` has exactly four
// keys (`Harbor.Desktop.Abstractions`, `Harbor.Plugins.Abstractions`,
// `Harbor.Plugins.Compilation`, `Harbor.Plugins.Registration`) and NOT ONE
// `Harbor.Ipc.*` or `Harbor.Logging` entry. The IPC classification is a plain
// matrix row, not an exemption. `Harbor.Plugins.Host` is the one genuinely
// different case — the gate declines to classify it at all
// (`OutOfScopeAssemblies`: "a composition root") while §1 assigns it
// Application — and R3 reports exactly that shape rather than forcing it into R1.
//
// RULES
// -----
//   R1  Every assembly §1 places in one of the four layer boxes the gate also
//       classifies must be in the layer the matrix says. A name the matrix does
//       not have is not a finding here — see DECLARED HOLES.
//   R2  No assembly may be claimed for two different gate-classified layers in
//       §1. This is not hypothetical: #896 moved `Harbor.Ipc.Abstractions` into
//       the DOMAIN box and left it in the APPLICATION box, so §1 currently
//       contradicts itself about the same project on the same screen.
//   R3  A name in a classified box that the gate puts in `OutOfScopeAssemblies`
//       is a finding: §1 asserts a layer for an assembly the matrix deliberately
//       declines to classify, and the reason it gives belongs in the doc.
//
// NON-VACUITY
// -----------
// The failure mode of every guard in this family is a matcher that stopped
// matching while the test stayed green, so none of the four tests below can be
// satisfied by reading nothing:
//
//   1. `Section_One_Diagram_Still_Yields_The_Five_Boxes` — the fence, the heading
//      and all five box headers must be found. A missing section THROWS rather
//      than returning an empty claim set, because "no claims" and "no diagram"
//      are otherwise the same green.
//   2. `The_Compared_Set_Is_Non_Vacuous_In_Both_Directions` — a floor and a
//      ceiling on the number of names actually compared, and the count of
//      unjudged names reported in the same message, so a project rename that
//      quietly moves an assembly out of the compared set cannot pass.
//   3. `The_Comparison_Fires_On_A_Planted_Mislayering` — a synthetic diagram with
//      a known-wrong layer must produce exactly that finding, and the same
//      diagram with the name moved to the right box must produce none. This runs
//      the real parser and the real comparison, so it is the only test here that
//      distinguishes "the doc is clean" from "the guard is blind".
//   4. `The_Box_Header_Mapping_Covers_Every_Layer_The_Matrix_Uses` — a new
//      `Layer` value with no box to compare against would silently stop being
//      checked, so the mapping is itself held to the enum.
//
// DECLARED HOLES — what this file does NOT check, on purpose
// ---------------------------------------------------------
// A guard that pretends to be total is worse than one that names its edges,
// because the pretence is what a later reader trusts. These are the edges:
//
//   * ABBREVIATIONS. The diagram writes `Harbor.Storage.Jsonl / Memory / Sqlite`,
//     `Harbor.Providers.OpenAiCompatible / Anthropic / OpenAI / Ollama`,
//     `Tui.AnsiPlain`, `Harbor.Tui.CellForge (+ .Engine)`, `Harbor.Ui.Framework
//     (+ split projects State/, ViewModels/, …)`. Only the fully-spelled leading
//     name is read. Expanding the suffixes would mean guessing that `Memory` means
//     `Harbor.Storage.Memory` and not something else, in a diagram that also
//     contains `contrib/tui` renderers nobody ships. So `Harbor.Storage.Memory`,
//     `.Sqlite`, `.Anthropic`, `.OpenAI`, `.Ollama`, `Harbor.Tui.CellForge.Engine`
//     and the eight `Harbor.Ui.Framework.*` modules are UNCHECKED today. Fixing
//     this means spelling the names out in the diagram, which is a documentation
//     decision, not a parsing one.
//   * THE `UI FRAMEWORK` BOX. The gate's `Layer` enum has five values and none of
//     them is a UI-framework layer; the matrix puts `Harbor.Ui.Framework` and
//     every `Harbor.Ui.Framework.*` module except `.Abstractions` in
//     Presentation. The diagram's sixth box is therefore not comparable, and its
//     contents are skipped rather than folded into Presentation — folding would
//     be this file inventing a layer the gate does not have, on top of a guess
//     about what the box means. Test 1 asserts the box is still found so the hole
//     is visible, and test 2 reports how many names sit in it.
//   * `contrib/`. `Harbor.Scripting.*` is a glob over six real but
//     non-CI-compiled projects outside `Harbor.slnx`; nothing in the matrix can
//     speak for them and `contrib/` is out of scope by owner decision. Names with
//     no matrix row and no out-of-scope entry are reported as unjudged, not
//     passed.
//   * INVENTORY COVERAGE. This file does NOT require §1 to name every matrix
//     assembly — roughly a dozen (Lsp, Terminal.Pty, Transport.Remote,
//     Telemetry.*, Desktop.Shared, Desktop.Animations, Hosting, Ipc.Client,
//     Ipc.InProcess, Ipc.Server, Logging) are absent from the diagram today. That
//     is an omission to close, but a coverage requirement would make this PR a
//     diagram rewrite, and it would fire on assemblies nobody has mislayered. The
//     direction that matters — a name that IS in the diagram must be in the
//     right box — is R1.
//
// STATUS: written without being compiled. Local dotnet is forbidden in this
// repository and CI is the first build, exactly as recorded in
// `DefaultModelDocClaimTests.cs:60-64`. Treat the C# as unverified until that run.
//
// Author: refusedguy (#888).

using System.Text;
using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Keeps <c>docs/ARCHITECTURE_LAYERS.md</c> §1 — the hand-typed per-layer assembly
///     lists — in agreement with <see cref="FullLayerMatrixTests.Matrix" />, which is the
///     IL-measured authority for the same fact (#888).
/// </summary>
public sealed class LayerDocAgreementRule
{
    /// <summary>Repo-relative path of the document this rule polices.</summary>
    private const string DocRelativePath = "docs/ARCHITECTURE_LAYERS.md";

    /// <summary>
    ///     The §1 heading. Anchoring on it — rather than on "the first fenced block in the
    ///     file" — is what keeps the rule pointed at the layer diagram if the document grows
    ///     a diagram earlier in its text.
    /// </summary>
    private const string SectionOneHeading = "## 1. The layering model";

    /// <summary>Every box header §1 draws, mapped to the gate layer it claims.</summary>
    /// <remarks>
    ///     The keys are compared after the header's parenthetical is stripped, so
    ///     <c>DOMAIN / ABSTRACTIONS (the hexagon core)</c> matches
    ///     <c>DOMAIN / ABSTRACTIONS</c>.
    ///     <para>
    ///         <c>UI FRAMEWORK</c> is deliberately NOT in this table and that is the point,
    ///         not an oversight: the gate has no such layer. See DECLARED HOLES in the file
    ///         header — folding it into Presentation would be this file inventing a layer.
    ///     </para>
    /// </remarks>
    private static readonly (string BoxHeader, string GateLayer)[] ClassifiedBoxes =
    [
        ("PRESENTATION", nameof(FullLayerMatrixTests.Layer.Presentation)),
        ("APPLICATION", nameof(FullLayerMatrixTests.Layer.Application)),
        ("INFRASTRUCTURE", nameof(FullLayerMatrixTests.Layer.Infrastructure)),
        ("DOMAIN / ABSTRACTIONS", nameof(FullLayerMatrixTests.Layer.Domain)),
    ];

    /// <summary>All five headers, so test 1 can prove the sixth box was found and skipped, not missed.</summary>
    private static readonly string[] AllBoxHeaders =
        [.. ClassifiedBoxes.Select(b => b.BoxHeader), "UI FRAMEWORK"];

    /// <summary>
    ///     A layer the matrix uses for which §1 draws no box. Not a hole in this rule:
    ///     <c>Harbor.Hosting</c> is a composition root and is not in the diagram at all.
    /// </summary>
    private static readonly string[] LayersWithNoBox = [nameof(FullLayerMatrixTests.Layer.CompositionRoot)];

    /// <summary>Fewer compared names than this means the diagram stopped naming assemblies.</summary>
    private const int MinComparedNames = 20;

    /// <summary>
    ///     More compared names than this means the matcher degraded into "any token that
    ///     starts with Harbor", which is noise that buries a real finding.
    /// </summary>
    private const int MaxComparedNames = 60;

    /// <summary>Regexes are given a timeout; a document is untrusted input like any other.</summary>
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    ///     <c>Harbor.Plugins.{Abstractions, Runtime, …}</c> — the brace form. Matched first
    ///     and removed before the plain matcher runs, so the same text is not also read as
    ///     the bare prefix <c>Harbor.Plugins</c>.
    /// </summary>
    private static readonly Regex BraceForm = new(
        @"Harbor\.[A-Za-z0-9_]+\.\{(?<items>[^}]*)\}",
        RegexOptions.Compiled,
        RegexTimeout);

    /// <summary>
    ///     A fully-spelled assembly name. The trailing <c>[A-Za-z0-9_]</c> is what stops the
    ///     greedy middle from swallowing a following dot, so <c>Harbor.Scripting.*</c> reads
    ///     as <c>Harbor.Scripting</c> and <c>src/Harbor.DesignSystem/</c> reads as
    ///     <c>Harbor.DesignSystem</c>.
    /// </summary>
    private static readonly Regex PlainName = new(
        @"Harbor\.[A-Za-z0-9_.]*[A-Za-z0-9_]",
        RegexOptions.Compiled,
        RegexTimeout);

    /// <summary>
    ///     The marker that ends a box's member list. A box's trailing
    ///     <c>Depends on:</c> clause names assemblies too, and those are REFERENCES, not
    ///     members: §1's APPLICATION box says <c>Depends on: Domain ONLY (Harbor.Abstractions
    ///     + Harbor.Abstractions.Contracts)</c>, and reading that as "both are Application"
    ///     would manufacture two false findings out of the one line that is most correct in
    ///     the diagram.
    /// </summary>
    private const string DependsMarker = "Depends on:";

    // ---- R1 / R3: every name §1 places must be in the layer the matrix says ----

    [Test]
    public async Task Section_One_Layer_Lists_Agree_With_The_Matrix()
    {
        IReadOnlyList<string> diagram = SectionOneDiagram();

        var findings = new List<string>();
        foreach (DiagramBox box in ParseBoxes(diagram))
        {
            if (box.GateLayer is null)
            {
                continue;
            }

            foreach (string name in MemberAssemblyNames(box.Body))
            {
                if (FullLayerMatrixTests.Matrix.TryGetValue(name, out FullLayerMatrixTests.Row? row))
                {
                    string actual = row.Layer.ToString();
                    if (!string.Equals(actual, box.GateLayer, StringComparison.Ordinal))
                    {
                        findings.Add(
                            $"{name}: §1 box '{box.BoxHeader}' places it in {box.GateLayer}, "
                            + $"FullLayerMatrixTests.Matrix says {actual}.");
                    }
                }
                else if (FullLayerMatrixTests.OutOfScopeAssemblies.TryGetValue(name, out string? why))
                {
                    // R3. The matrix has an opinion about this project and the opinion is
                    // "not a layer at all", so §1 asserting one is a disagreement about
                    // which kind of thing this project is.
                    findings.Add(
                        $"{name}: §1 box '{box.BoxHeader}' places it in {box.GateLayer}, but the "
                        + $"matrix does not classify it as a layer at all — {why} Move it out of a "
                        + "layer box or give it a matrix row; do not invent a third answer.");
                }
            }
        }

        await Assert.That(findings.Count).IsEqualTo(0)
            .Because(
                "§1's per-layer assembly lists are the document an agent or a contributor reads "
                + "before adding a <ProjectReference>, and they are hand-typed while the matrix is "
                + "measured from Assembly.GetReferencedAssemblies(). A disagreement means the "
                + "diagram licenses an edge the gate would reject — or forbids one it would "
                + "accept. Fix the DOC (it is the stale side); changing a matrix row to match "
                + "prose would trade a measurement for a guess. Findings: "
                + (findings.Count == 0 ? "(none)" : string.Join("\n  ", findings)));
    }

    // ---- R2: §1 must not claim one assembly for two layers ----

    [Test]
    public async Task No_Assembly_Is_Claimed_For_Two_Layers()
    {
        var claimsByAssembly = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);

        foreach (DiagramBox box in ParseBoxes(SectionOneDiagram()))
        {
            if (box.GateLayer is null)
            {
                continue;
            }

            foreach (string name in MemberAssemblyNames(box.Body))
            {
                if (!FullLayerMatrixTests.Matrix.ContainsKey(name))
                {
                    continue;
                }

                if (!claimsByAssembly.TryGetValue(name, out SortedSet<string>? layers))
                {
                    layers = new SortedSet<string>(StringComparer.Ordinal);
                    claimsByAssembly[name] = layers;
                }

                layers.Add(box.GateLayer);
            }
        }

        var contradictions = claimsByAssembly
            .Where(kv => kv.Value.Count > 1)
            .Select(kv => $"{kv.Key}: §1 claims it for {string.Join(" and ", kv.Value)}.")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        await Assert.That(contradictions.Count).IsEqualTo(0)
            .Because(
                "An assembly in two layer boxes is not a partial error, it is a diagram that "
                + "disagrees with itself on one screen — and a reader has no way to tell which "
                + "box is the intended one. This is not hypothetical: #896 corrected "
                + "Harbor.Ipc.Abstractions into the DOMAIN box and left the same project in the "
                + "APPLICATION box, where it still is. Findings: "
                + (contradictions.Count == 0 ? "(none)" : string.Join("\n  ", contradictions)));
    }

    // ---- Non-vacuity 1: the diagram was found and parsed ----

    [Test]
    public async Task Section_One_Diagram_Still_Yields_Its_Five_Boxes()
    {
        IReadOnlyList<DiagramBox> boxes = ParseBoxes(SectionOneDiagram());

        var headers = boxes.Select(b => b.BoxHeader).ToList();

        foreach (string expected in AllBoxHeaders)
        {
            await Assert.That(headers.Contains(expected, StringComparer.Ordinal)).IsTrue()
                .Because(
                    $"§1 no longer contains a '{expected}' box. This rule compares boxes to matrix "
                    + "rows, so a box that is renamed, merged or dropped stops being checked and "
                    + "the rule goes quiet rather than green-on-merit. Read DECLARED HOLES in this "
                    + "file's header before adding a layer, and update ClassifiedBoxes in the "
                    + "same edit. Headers found: " + string.Join(", ", headers));
        }

        await Assert.That(boxes.Count).IsEqualTo(AllBoxHeaders.Length)
            .Because(
                "§1 drew exactly five boxes. A sixth is either a new layer — which the matrix "
                + "must classify before this rule can compare it — or a drawing artefact this "
                + "parser is mis-splitting. Headers found: " + string.Join(", ", headers));
    }

    // ---- Non-vacuity 2: the compared set is neither empty nor everything ----

    [Test]
    public async Task The_Compared_Set_Is_Non_Vacuous_In_Both_Directions()
    {
        var compared = new List<string>();
        var unjudged = new List<string>();
        int inUncomparedBox = 0;

        foreach (DiagramBox box in ParseBoxes(SectionOneDiagram()))
        {
            foreach (string name in MemberAssemblyNames(box.Body))
            {
                if (box.GateLayer is null)
                {
                    inUncomparedBox++;
                    continue;
                }

                if (FullLayerMatrixTests.Matrix.ContainsKey(name))
                {
                    compared.Add(name);
                }
                else
                {
                    unjudged.Add(name);
                }
            }
        }

        await Assert.That(compared.Count).IsGreaterThanOrEqualTo(MinComparedNames)
            .Because(
                $"§1 yielded {compared.Count} assembly name(s) in a gate-classified box, below the "
                + $"floor of {MinComparedNames}. A guard over an empty claim set is green for no "
                + "reason. Either the diagram stopped naming assemblies, or the matcher stopped "
                + "reading the shape it used to read. "
                + $"{inUncomparedBox} name(s) sit in the UI FRAMEWORK box, which is deliberately "
                + "not compared — see DECLARED HOLES. Compared: " + string.Join(", ", compared));

        await Assert.That(compared.Count).IsLessThanOrEqualTo(MaxComparedNames)
            .Because(
                $"§1 yielded {compared.Count} compared name(s), above the ceiling of "
                + $"{MaxComparedNames}. That is the signature of a pattern that has stopped being "
                + "specific — it will bury a real mislayering in noise until nobody reads the "
                + "failure. Narrow the matcher.");

        // The unjudged set is reported rather than asserted, because it is a declared hole and
        // a hole that is invisible is the hole this whole file exists to stop being invisible.
        // It IS bounded, because "everything became unjudged" is the same blindness wearing a
        // different hat — and the floor above already rules that out for the compared set.
        await Assert.That(unjudged.Count).IsLessThanOrEqualTo(AllBoxHeaders.Length * 2)
            .Because(
                $"§1 names {unjudged.Count} assemblies that no matrix row claims, above the "
                + $"ceiling of {AllBoxHeaders.Length * 2}: " + string.Join(", ", unjudged)
                + ". A name can leave the compared set by being renamed, by being abbreviated "
                + "(see DECLARED HOLES), or by the matcher narrowing. The first and third are "
                + "defects; the second is a documentation decision. Either way it must be noticed.");
    }

    // ---- Non-vacuity 3: the comparison fires on planted input ----

    [Test]
    public async Task The_Comparison_Fires_On_A_Planted_Mislayering()
    {
        // The real document may be correct, and then rule 1 is satisfied by an empty list —
        // which is also exactly what a blind parser produces. Nothing in the rule itself tells
        // those two apart, so this does: it feeds the real parser and the real comparison a
        // diagram with a known-wrong layer and requires the finding.

        // `Harbor.Ipc.Server` is Infrastructure in the matrix. Put it in the APPLICATION box.
        string[] plantedWrong =
        [
            Top(),
            Row("APPLICATION (use cases, orchestration)"),
            Row("- Harbor.Application"),
            Row("- Harbor.Ipc.Server"),
            Bottom(),
        ];

        var wrong = Findings(plantedWrong);

        await Assert.That(wrong.Count).IsEqualTo(1)
            .Because(
                "the planted diagram puts Harbor.Ipc.Server in the APPLICATION box and the matrix "
                + "has it as Infrastructure, so the comparison must report exactly that one "
                + "finding — no more (the matcher is over-broad) and no fewer (it is blind). "
                + "Reported: " + (wrong.Count == 0 ? "(none)" : string.Join(" | ", wrong)));

        await Assert.That(wrong[0]).Contains("Harbor.Ipc.Server")
            .Because("the single finding must name the offending assembly, or a reviewer cannot "
                + "find the line to fix. Reported: " + wrong[0]);

        // And the same name in the box the matrix agrees with must produce nothing, so the
        // positive control above cannot be satisfied by a rule that reports everything.
        string[] plantedRight =
        [
            Top(),
            Row("INFRASTRUCTURE (adapters, I/O, external services)"),
            Row("- Harbor.Ipc.Server"),
            Bottom(),
        ];

        await Assert.That(Findings(plantedRight).Count).IsEqualTo(0)
            .Because(
                "Harbor.Ipc.Server in the INFRASTRUCTURE box agrees with the matrix, so the rule "
                + "must be silent. A guard that reports this too is reporting its own parser "
                + "rather than the document, and its findings get suppressed until it catches "
                + "nothing at all.");
    }

    // ---- Non-vacuity 3b: the EXTRACTOR, asserted on the shape that broke it ----
    //
    // The control above plants ONE name, and it passed while the extractor was dropping a name
    // from a real box — so "exactly one finding" was never a strong enough claim to notice.
    // This one asserts the extracted SET, exactly, on the two shapes §1 actually uses and that
    // no other test here covered: a brace list WRAPPED ACROSS A LINE BOUNDARY, and more than
    // one brace form in a single box. Both were live in §1's APPLICATION box, which is the
    // reason this test exists rather than a nice-to-have.

    [Test]
    public async Task The_Extractor_Reads_Wrapped_And_Repeated_Brace_Forms()
    {
        // The real rows, box chrome included, fed through the REAL ParseBoxes — because the
        // first version of this control passed a hand-cleaned string and so missed the bug it
        // exists to catch. `│` is not whitespace: hand-cleaning the input is precisely what hid
        // it. A control must run the code on the shape the code actually receives.
        //
        // The plugins brace form is split mid-brace across a line boundary, which is the case
        // that put a box-drawing character inside the extracted name.
        string[] diagram =
        [
            Top(),
            Row("APPLICATION (use cases, orchestration)"),
            Row("- Harbor.Application (AgentLoop, Sessions, Agents,"),
            Wrap("Configuration, Permissions, Onboarding)"),
            Row("- Harbor.Registries"),
            Row("- Harbor.Plugins.{Abstractions, Runtime, Hosting, Registration,"),
            Wrap("Instantiation, Compilation, Storage, Host} (8 projects)"),
            Row("- contrib/scripting: Harbor.Scripting.* (ScriptHost, Bridge)"),
            Row("- Harbor.Ipc.{Abstractions, InProcess, Server, Client}"),
            Row("- Harbor.Logging (Serilog per-run timestamped files)"),
            Row("Depends on: Domain ONLY (Harbor.Abstractions +"),
            Wrap("Harbor.Abstractions.Contracts)"),
            Bottom(),
        ];

        IReadOnlyList<DiagramBox> boxes = ParseBoxes(diagram);

        await Assert.That(boxes.Count).IsEqualTo(1)
            .Because("the planted diagram is one box; the extractor cannot be judged on a body it "
                + "never received. Rows fed: " + diagram.Length);

        string[] actual = [.. MemberAssemblyNames(boxes[0].Body)];

        // Every fully-spelled name in the member list, sorted. Note `Harbor.Scripting` — the
        // diagram writes the glob `Harbor.Scripting.*`, and only the leading name is read.
        string[] expected =
        [
            "Harbor.Application",
            "Harbor.Ipc.Abstractions",
            "Harbor.Ipc.Client",
            "Harbor.Ipc.InProcess",
            "Harbor.Ipc.Server",
            "Harbor.Logging",
            "Harbor.Plugins.Abstractions",
            "Harbor.Plugins.Compilation",
            "Harbor.Plugins.Host",
            "Harbor.Plugins.Hosting",
            "Harbor.Plugins.Instantiation",
            "Harbor.Plugins.Registration",
            "Harbor.Plugins.Runtime",
            "Harbor.Plugins.Storage",
            "Harbor.Registries",
            "Harbor.Scripting",
        ];

        var missing = expected.Except(actual, StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList();
        var unexpected = actual.Except(expected, StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList();

        await Assert.That(missing.Count).IsEqualTo(0)
            .Because(
                "the extractor dropped name(s) that §1's APPLICATION box really contains. A dropped "
                + "name is a silently unchecked assembly: it never becomes a finding, so the rule "
                + "reports a document as clean on the strength of having read less of it than it "
                + "claims. The wrapped brace form is the usual cause — a reader that works line by "
                + "line cannot see a `Harbor.Family.{A, B,` that continues on the next line, and "
                + "degrades it to the bare prefix. Missing: "
                + (missing.Count == 0 ? "(none)" : string.Join(", ", missing)));

        await Assert.That(unexpected.Count).IsEqualTo(0)
            .Because(
                "the extractor invented name(s) that §1 does not contain, which is the over-broad "
                + "half of the same defect: findings nobody can act on, and a ceiling that fires on "
                + "noise. Unexpected: "
                + (unexpected.Count == 0 ? "(none)" : string.Join(", ", unexpected)));
    }

    // ---- Non-vacuity 4: the header mapping is held to the enum ----

    [Test]
    public async Task The_Box_Header_Mapping_Covers_Every_Layer_The_Matrix_Uses()
    {
        var mapped = ClassifiedBoxes.Select(b => b.GateLayer).ToHashSet(StringComparer.Ordinal);
        foreach (string noBox in LayersWithNoBox)
        {
            mapped.Add(noBox);
        }

        var unmapped = FullLayerMatrixTests.Matrix.Values
            .Select(row => row.Layer.ToString())
            .Where(layer => !mapped.Contains(layer))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        await Assert.That(unmapped.Count).IsEqualTo(0)
            .Because(
                "FullLayerMatrixTests.Layer has a value this file has no §1 box for and does not "
                + "list in LayersWithNoBox. Every assembly in that layer stops being compared "
                + "against the document, silently, and the guard keeps reporting green for the "
                + "layers it does know. Add the box header to ClassifiedBoxes, or the layer to "
                + "LayersWithNoBox with a reason in this file's header. Unmapped: "
                + string.Join(", ", unmapped));
    }

    // ---- findings, shared by the rule and the positive control ----

    /// <summary>R1 + R3 over an arbitrary diagram. The positive control uses this, not a copy.</summary>
    private static List<string> Findings(IReadOnlyList<string> diagram)
    {
        var findings = new List<string>();
        foreach (DiagramBox box in ParseBoxes(diagram))
        {
            if (box.GateLayer is null)
            {
                continue;
            }

            foreach (string name in MemberAssemblyNames(box.Body))
            {
                if (FullLayerMatrixTests.Matrix.TryGetValue(name, out FullLayerMatrixTests.Row? row))
                {
                    string actual = row.Layer.ToString();
                    if (!string.Equals(actual, box.GateLayer, StringComparison.Ordinal))
                    {
                        findings.Add($"{name}: {box.BoxHeader} says {box.GateLayer}, matrix says {actual}");
                    }
                }
                else if (FullLayerMatrixTests.OutOfScopeAssemblies.ContainsKey(name))
                {
                    findings.Add($"{name}: {box.BoxHeader} says {box.GateLayer}, matrix declines to classify it");
                }
            }
        }

        return findings;
    }

    // ---- parsing ----

    /// <summary>One box of the §1 diagram: its header, the gate layer it claims, and its body.</summary>
    /// <param name="BoxHeader">Header text with the parenthetical stripped, e.g. <c>APPLICATION</c>.</param>
    /// <param name="GateLayer">
    ///     The <c>FullLayerMatrixTests.Layer</c> the box claims, or <c>null</c> for a box the
    ///     gate has no layer for.
    /// </param>
    /// <param name="Body">The box's content lines, joined by <c>\n</c>, trailing spaces trimmed.</param>
    private sealed record DiagramBox(string BoxHeader, string? GateLayer, string Body);

    /// <summary>
    ///     Reads §1's fenced diagram out of the document.
    /// </summary>
    /// <remarks>
    ///     Every failure mode here THROWS. Returning an empty list instead would make "the
    ///     document no longer has a §1 diagram" and "the document's §1 diagram agrees with the
    ///     matrix" the same green result, and the second is the only one of the two that means
    ///     anything. The same reasoning is recorded on <c>RequireRepoRoot</c> in
    ///     <c>DefaultModelDocClaimTests</c>.
    /// </remarks>
    private static IReadOnlyList<string> SectionOneDiagram()
    {
        string? root = RepoPaths.RepoRoot;
        if (root is null)
        {
            throw new InvalidOperationException(
                "Harbor.slnx not found above " + AppContext.BaseDirectory + " — this rule reads "
                + DocRelativePath + " and cannot run from a published test host.");
        }

        string path = Path.Combine(root, DocRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                DocRelativePath + " does not exist. It is the document this rule exists to keep "
                + "in agreement with the matrix; a deleted or renamed document is not a passing "
                + "guard, it is a guard with nothing left to read.");
        }

        string text = File.ReadAllText(path);

        int heading = text.IndexOf(SectionOneHeading, StringComparison.Ordinal);
        if (heading < 0)
        {
            throw new InvalidOperationException(
                "'" + SectionOneHeading + "' not found in " + DocRelativePath + ". This rule anchors "
                + "on that heading to find the layer diagram; re-anchor it if the section was "
                + "renumbered, and say so in this file's header.");
        }

        int openFence = text.IndexOf("```", heading, StringComparison.Ordinal);
        if (openFence < 0)
        {
            throw new InvalidOperationException(
                "no fenced block after '" + SectionOneHeading + "' in " + DocRelativePath
                + " — §1's layer diagram is a fenced block and this rule reads only that shape.");
        }

        int bodyStart = text.IndexOf('\n', openFence) + 1;
        int closeFence = text.IndexOf("```", bodyStart, StringComparison.Ordinal);
        if (closeFence < 0)
        {
            throw new InvalidOperationException(
                "unterminated fenced block after '" + SectionOneHeading + "' in " + DocRelativePath);
        }

        return text[bodyStart..closeFence].Split('\n');
    }

    /// <summary>
    ///     Box-drawing characters, in one place so the parser and the synthetic rows in the
    ///     positive control cannot disagree about what a box looks like. Literal rather than
    ///     escaped because U+2500-block characters are already used this way across the C#
    ///     tree (<c>src/Harbor.Ui.Framework.Sessions/Sessions/SessionManager.cs</c> and others),
    ///     and the file is UTF-8 without BOM by <c>.editorconfig</c>. A mis-decoded source
    ///     file would turn every match below into a no-op and the rule would report a clean
    ///     document without ever having read one — which is why test 3 exists.
    /// </summary>
    private const char BoxTopLeft = '┌';

    private const char BoxBottomLeft = '└';
    private const char BoxSide = '│';
    private const char Horizontal = '─';
    private const char TopRight = '┐';
    private const char BottomRight = '┘';

    private static IReadOnlyList<DiagramBox> ParseBoxes(IReadOnlyList<string> lines)
    {
        var boxes = new List<DiagramBox>();
        string? header = null;
        var body = new StringBuilder();

        foreach (string raw in lines)
        {
            string line = raw.TrimEnd();
            if (line.Length == 0)
            {
                continue;
            }

            if (line[0] == BoxTopLeft)
            {
                header = null;
                body.Clear();
                continue;
            }

            if (line[0] == BoxBottomLeft)
            {
                if (header is not null)
                {
                    boxes.Add(new DiagramBox(header, GateLayerFor(header), body.ToString()));
                }

                header = null;
                body.Clear();
                continue;
            }

            // The `▲` / `│ uses` / `▼` connectors between boxes are indented, so they do not
            // start with a side character and land here. Anything else that is not box chrome
            // is skipped for the same reason: a diagram that grows a legend must not derail it.
            if (line[0] != BoxSide)
            {
                continue;
            }

            string content = InnerText(line);
            if (header is null)
            {
                if (content.Length > 0)
                {
                    header = StripParenthetical(content);
                }
            }
            else
            {
                body.Append(content).Append('\n');
            }
        }

        return boxes;
    }

    /// <summary>The gate layer a box header claims, or <c>null</c> when the gate has no such layer.</summary>
    private static string? GateLayerFor(string boxHeader)
    {
        foreach ((string header, string layer) in ClassifiedBoxes)
        {
            if (string.Equals(header, boxHeader, StringComparison.Ordinal))
            {
                return layer;
            }
        }

        return null;
    }

    /// <summary>
    ///     The text between a box row's two side characters.
    /// </summary>
    /// <remarks>
    ///     The TRAILING bar has to come off, and the first CI build is what proved it. Taking
    ///     <c>line[1..]</c> and trimming leaves the closing <c>│</c> in place — it is not
    ///     whitespace, so <c>Trim()</c> keeps it — and on a brace form wrapped across two lines
    ///     that bar lands INSIDE the braces, immediately before the newline:
    ///     <c>…Registration,│\nInstantiation, …</c>. The item then trims to
    ///     <c>│Instantiation</c> and the extracted name is
    ///     <c>Harbor.Plugins.│Instantiation</c> — a key the matrix does not have, so the
    ///     assembly was reported as unjudged and produced no finding. `Harbor.Plugins.Instantiation`
    ///     was the one name in §1 that no finding mentioned, and the reason was a character of
    ///     box-drawing in the wrong place. Only a name that begins a WRAPPED line is affected,
    ///     which is why five sibling projects in the same brace list were flagged correctly and
    ///     the sixth was not.
    /// </remarks>
    private static string InnerText(string line)
    {
        string inner = line[1..].TrimEnd();
        if (inner.Length > 0 && inner[^1] == BoxSide)
        {
            inner = inner[..^1];
        }

        return inner.Trim();
    }

    /// <summary>
    ///     <c>DOMAIN / ABSTRACTIONS (the hexagon core)</c> → <c>DOMAIN / ABSTRACTIONS</c>.
    /// </summary>
    private static string StripParenthetical(string header)
    {
        int paren = header.IndexOf('(');
        string key = paren < 0 ? header : header[..paren];
        return key.Trim();
    }

    /// <summary>
    ///     Every fully-spelled <c>Harbor.*</c> name in a box's MEMBER list, with the
    ///     <c>Harbor.Family.{A, B, C}</c> brace form expanded.
    /// </summary>
    /// <remarks>
    ///     The whole body is scanned as ONE string, not line by line, and that is load-bearing:
    ///     a wrapped bullet splits a brace form across two lines
    ///     (<c>Harbor.Plugins.{Abstractions, … Registration,</c> / <c>Instantiation, …}</c>), and
    ///     <c>[^}]</c> matches the newline, so the form survives. Line-by-line it would not, and
    ///     the plugin family — the largest brace list in the diagram — would silently degrade to
    ///     the bare prefix <c>Harbor.Plugins</c>, which is not an assembly and would be
    ///     reported as unjudged rather than as the seven mislayerings it is.
    /// </remarks>
    private static IReadOnlyList<string> MemberAssemblyNames(string body)
    {
        int dependsAt = body.IndexOf(DependsMarker, StringComparison.Ordinal);
        string region = dependsAt < 0 ? body : body[..dependsAt];

        var names = new List<string>();
        var remainder = new StringBuilder(region);

        foreach (Match match in BraceForm.Matches(region))
        {
            string prefix = match.Value[..match.Value.IndexOf('{')];
            foreach (string item in match.Groups["items"].Value.Split(','))
            {
                string trimmed = item.Trim();
                if (trimmed.Length > 0)
                {
                    names.Add(prefix + trimmed);
                }
            }

            // Remove the brace form so the plain matcher cannot also read its prefix.
            //
            // Two-arg Replace on purpose: the StringComparison overload that would silence a
            // CA1307 is `string.Replace`, not `StringBuilder.Replace`, and asking for it here is
            // a compile error (CS1501) rather than a warning. The first CI build of this file
            // is what found that out — see the PR body.
            _ = remainder.Replace(match.Value, string.Empty);
        }

        foreach (Match match in PlainName.Matches(remainder.ToString()))
        {
            names.Add(match.Value);
        }

        return [.. names.Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal)];
    }

    // ---- synthetic diagram rows for the positive control ----
    //
    // Built from the SAME character constants the parser keys on, rather than pasted as
    // literals, so the control cannot accidentally disagree with the parser about what a box
    // looks like — and so a change to the box shape breaks the control loudly instead of
    // quietly making it test a diagram the parser would never accept.

    private static string Top() => BoxTopLeft + new string(Horizontal, 65) + TopRight;

    private static string Bottom() => BoxBottomLeft + new string(Horizontal, 65) + BottomRight;

    private static string Row(string content) => BoxSide + "  " + content.PadRight(63) + BoxSide;

    /// <summary>
    ///     A continuation row, indented one step further than <see cref="Row" /> — the shape a
    ///     wrapped bullet or a wrapped brace form takes in the real diagram.
    /// </summary>
    private static string Wrap(string content) => BoxSide + "    " + content.PadRight(61) + BoxSide;
}
