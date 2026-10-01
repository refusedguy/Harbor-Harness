// CommonConfigContractRules.cs — the guard for #453.
//
// WHY THIS FILE EXISTS
// --------------------
// #453 was filed as a duplicate: two interfaces modelling the same
// ~/.harbor/config.json read, one in Desktop.Abstractions and one in
// Ui.Framework.Abstractions. Reading both ends says it is NOT a duplicate, and
// that is the finding this file has to hold true:
//
//     ICommonConfigStore        3 members — Load / Save / Update, the WHOLE
//                               CommonConfig, every failure a Result.
//     ICommonConfigReader       1 member  — read only, two fields, absence as a
//                               nullable tuple.
//
// One is a repository you write through; the other is a read-only projection of
// one value out of it. Same subject, different capability — the same shape #574
// found between two `CollapseWhitespace` methods that took text and returned text
// and did opposite things, and which #717 resolved by making them DIFFER IN NAME
// rather than by merging them. Merging is not available here anyway:
// Harbor.Desktop.Abstractions declares a direct ProjectReference to
// Harbor.Ui.Framework (plus six more Ui.Framework.* projects), so the reverse edge
// would be a cycle. The split is load-bearing and both contracts stay.
//
// WHAT #453 FOUND ANYWAY, AND WHY THIS IS NOT ONLY A RENAME
// ----------------------------------------------------------
// The audit's own parenthetical — "one implementation registers twice" — is the
// cheap half. The expensive half is that the narrow contract still returns the
// exact type #729 removed one commit ago:
//
//     Task<(string? ProviderId, string? ModelId)?>
//
// #729 fixed the CONSUMER end of this seam. `SessionFactory.
// ResolveProviderModelFromConfigAsync` now returns `Task<Maybe<ModelRef>>`, and
// ProviderModelAbsenceRules guards its shape plus the two consumer files. The
// PRODUCER end was left alone: the interface still hands the pair over, and the
// half-pair normalisation that fix's own doc calls the only thing holding the
// callers together has simply MOVED to the adapter —
//
//     CommonConfigReaderAdapter:48
//     if (string.IsNullOrEmpty(cfg.DefaultProvider) || string.IsNullOrEmpty(cfg.DefaultModel))
//         return null;
//
// — which is not one of the two files ProviderModelAbsenceRules scans. So the
// rule as landed cannot see the derivation it was written to forbid, and the
// seam still declares four states while the domain has one.
//
// There were in fact TWO independent reasons that rule passed over it, and the
// first CI run of this guard is what exposed the second: the file was not on the
// scan list, AND the pattern could not match a dotted name. `HalfPairProbe`
// required `\w*[Pp]rovider\w*`, and the line above reads
// `string.IsNullOrEmpty(cfg.DefaultProvider)` — so even with the file listed, the
// matcher would have gone green over the exact spelling that existed. Both are
// fixed, and the non-vacuity controls below plant the real dotted line in both
// this file and `ProviderModelAbsenceRules` so neither blind spot can reopen.
//
// A tuple of two optional strings, wrapped in another optional, spells FOUR
// states: both, neither, provider-only, model-only. `Maybe<ModelRef>` spells
// two, because `ModelRef` cannot hold half a reference. That is the whole of
// #729's argument, and it applies to the interface verbatim.
//
// Note the states are not even reachable by accident: `CommonConfig.DefaultProvider`
// and `.DefaultModel` are non-nullable `string` with non-empty initialisers
// ("anthropic" / "claude-sonnet-4"), so "half a config" only exists in a
// hand-edited file. The guard does not rely on that — it just means the
// normalisation is guarding a state the writer never produces.
//
// WHY THE NAMES, SPECIFICALLY
// ---------------------------
// "Store" and "Reader" told a new author that one was writable and one was not,
// which is the axis that matters — but not WHAT the reader reads, so the choice
// between them still required opening both. `ICommonConfigModelRefReader` names
// the value, which is what the #717 divergence is for: read the two names side
// by side and the difference is legible without a body. `ICommonConfigStore`
// keeps its name — "store" is the established read/write term, its docs already
// state it reads and writes atomically, and 58 references would churn without
// adding one bit of information. What both get instead is the mutual
// cross-reference, so a reader of either lands on the other and learns why there
// are two.
//
// THE RATIONALE IN THE OLD DOC COMMENT WAS ALSO WRONG
// --------------------------------------------------
// It said Desktop.Abstractions reaches Ui.Framework "via Harbor.Terminal.
// Abstractions", and that Terminal.Abstractions is what closes the cycle. By
// fact the edge is a DIRECT ProjectReference to Harbor.Ui.Framework, with
// Terminal.Abstractions one redundant edge among seven. The cycle is real; the
// path given for it was not. The rewrite names the real edge, because a stated
// reason that is wrong is worse than no reason — the next reader trusts it.
//
// FIVE RULES, FOUR KINDS OF PROOF
// ------------------------------
//   1. NAME (reflection by name, not a type reference): the narrow port IS
//      `ICommonConfigModelRefReader`, and `ICommonConfigReader` is gone. Resolved
//      by string so this file compiles before the rename — a guard for a name
//      that does not exist yet cannot be landed red-first otherwise.
//   2. SHAPE (reflection): its one member returns `Task<Maybe<ModelRef>>`.
//   3. CAPABILITY SPLIT (reflection): the narrow port exposes no write member
//      and the store owns the writes. This is the rule that keeps #453 from
//      being "fixed" by merging, and the rule that catches the opposite
//      regression — the narrow port quietly growing a second copy of the store.
//   4. NO RE-DERIVATION (source): the producer side no longer tests a provider
//      half against a model half. #729's matcher, applied to the files it did
//      not cover.
//   5. SOLE IMPLEMENTER (source): one production implementer. A second producer
//      is a second hand-written answer to "is this pair whole?", which is the
//      exact failure #729's doc warns a second producer would introduce. By
//      SOURCE and not by reflection, because the one implementer lives in
//      apps/Harbor.App.Avalonia — a composition root this test project does not
//      reference, so a reflection sweep counts zero and the rule could only ever
//      be satisfied by an unwired seam. The first CI run of this file proved it.
//
// NON-VACUITY
// -----------
// Rules 3 and 4 are predicates that can pass on a broken matcher or a moved
// file, and rule 3 in particular is a "there is no violation" result that is
// otherwise indistinguishable from "the scan read nothing". So the same
// predicates are driven with synthetic positive and negative controls, and the
// file list is required to resolve.
//
// #767: RULE 4's PERIMETER IS DERIVED, AND THE LIST IT REPLACED WAS NOT A RULE
// -----------------------------------------------------------------------------
// Rule 4 used to walk `SeamFiles` — three typed paths. #729 seeded it from the
// two consumer files it was already scanning, and #453 added the producer after
// finding the derivation had moved there. That is a transcription of what each
// round of findings happened to touch, and the measurement in this checkout says
// it never was a statement of where the contract applies:
//
//   * every one of its three entries is selected by the derivation, so the list
//     carried no information the derivation does not already produce. A list
//     that is a strict SUBSET of a shape cannot be where the shape applies — if
//     the contract were mandatory exactly there, the list would be complete.
//   * two product files name the port and were on it in neither: SessionFactory
//     (a constructor parameter) and ICommonConfigStore.cs (the cross-reference
//     #744 added). SessionFactory is on `ProviderModelAbsenceRules.ConsumerFiles`,
//     whose `HalfPairProbe` is BYTE-IDENTICAL to this file's — so the same
//     forbidden construct was policed in one file and permitted in the next
//     purely by list membership, and neither list can observe the other's
//     omission. That is why both lists stayed green over a hole.
//   * rule 5, in this same file, already derived its implementers structurally
//     over `src/` and `apps/`. Rule 4 typing what rule 5 derived is the
//     asymmetry #767 names, and it is why a new implementer would be CAUGHT by
//     rule 5 and yet never SCANNED by rule 4.
//
// So the perimeter is now "every product file that names the port". A file that
// reaches this seam necessarily spells the port's name, so it selects itself in;
// becoming relevant is not an event a typed list can record. That is the whole
// difference between the two, and `Perimeter_AcquiresANewFileThatNamesThePort_
// WithNothingToEdit` is the control that tells them apart — a list cannot be
// shown to acquire a file it was never given.
//
// The companion `SeamScanner_ReachesEverySeamFile` is deleted, not repaired. It
// iterated the same constant the rule scanned, so it was green by construction:
// it could only fail if a listed file was deleted, and was incapable of noticing
// a file that had fallen OUT of the list — which is the only direction this
// defect ever moved. Once the perimeter is derived from a directory walk it also
// cannot report a missing entry, so it had become pure ceremony. A derived
// perimeter needs the opposite check, and
// `SeamPerimeter_CoversEveryProductFileThatNamesThePort` is it: it restates the
// expectation as its own query and compares, so the two cannot agree by
// construction.
//
// KNOWN BOUND, STATED NOT FIXED HERE — RESOLVED BY #894, AND THE ANCHOR WAS
// NEVER THE PLACE TO LOOK
// ----------------------------------------------------------------------
// This section used to record two sites outside the port perimeter as a known
// bound. #894 measured both and the bound did not survive the measurement:
//
//   * `Hosting/Modules/ToolsCatalog.cs` — `ResolveDefaultModelFromCommon`
//     re-qualifies the pair by hand. It was never uncovered: rule 5 counts
//     IMPLEMENTERS, which is a different claim, and the ad-hoc cut it performs is
//     inventoried with a stated reason by `ModelRefSingleParserTests` rule 3,
//     which scans every product file. Describing it as unseen was reading one
//     rule's count as if it were the seam's.
//   * `Desktop.Abstractions/ViewModels/OnboardingViewModel.cs` — the onboarding
//     persister decided each half independently. That one was real, and it is
//     now `ConfigHalfPairWriteRules`, which grades the WRITE side against
//     `CommonConfig.HasDefaultPair`.
//
// Two measurements from #894 are worth keeping here, because they are what
// makes the remaining bound safe rather than merely stated:
//
//   * Widening THIS anchor is not the fix. "Names either contract" takes the
//     perimeter from 6 files to 19 and still catches nothing — 0 hits — because
//     the onboarding lines carry one emptiness test each with `overwriteDefaults`
//     on the other side of the operator. `HalfPairProbe` cannot see them alone or
//     joined; the construct is a different shape from the one the pattern encodes.
//   * `CommonConfig.HasDefaultPair` is deliberately spelled as two property
//     patterns, NOT as `string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b)`. This
//     file would join the perimeter the day anything widens it, so writing the
//     question the `HalfPairProbe` way would plant the forbidden shape in the one
//     place meant to own it.
//
// One bound genuinely remains, and it is upstream of every rule here:
// `ProviderModelAbsenceRules.ConsumerFiles` is still a typed three-path list while
// this file's perimeter is derived, and `SessionLifecycleService.cs` — one of its
// three — does not name the port, so the two perimeters disagree over the same
// seam with a byte-identical matcher. That is a #860-class defect (a rule carried
// by a closed file list), not a #894 one, and it is filed as such.

using System.Text.RegularExpressions;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models.Identifiers;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Issue #453: the shared common config is read through two contracts, and
///     they are two capabilities rather than one duplicated contract. The narrow
///     one hands out ONE atomic reference with ONE absence. See the file header
///     for the evidence that the split is load-bearing and for the
///     <c>(string?, string?)</c> carrier #729 removed from the receiving end and
///     this file removes from the producing end.
/// </summary>
public sealed class CommonConfigContractRules
{
    /// <summary>The whole-config, read/write repository. Name unchanged by #453.</summary>
    private const string StoreName = "ICommonConfigStore";

    /// <summary>
    ///     The read-only projection of one value out of it, after the rename. The
    ///     post-fix name on purpose: <see cref="NarrowSeamName" /> is what this
    ///     file asserts, and asserting it by string is the only way the guard can
    ///     be committed red before the rename lands.
    /// </summary>
    private const string NarrowSeamName = "ICommonConfigModelRefReader";

    /// <summary>The pre-rename name, which #453 retires: "Reader" said what it could not do, not what it read.</summary>
    private const string RetiredSeamName = "ICommonConfigReader";

    /// <summary>The narrow port's one member, after the rename.</summary>
    private const string NarrowSeamMethod = "ReadModelRefAsync";

    /// <summary>
    ///     The files rule 4 scans: every product file that <b>names the port</b>.
    ///     Derived, never typed — see the file header for the measurement that
    ///     says the typed list it replaces was a transcription rather than a
    ///     statement of where this contract applies.
    /// </summary>
    /// <remarks>
    ///     Paths come back repo-relative and forward-slashed, which is what
    ///     <see cref="FindProbes" /> combines with the repo root.
    /// </remarks>
    private static IReadOnlyList<string> SeamPerimeter()
        => PortNamingFiles(
            [.. SourceScan.EnumerateProductCsFiles().Select(SourceScan.Relative)],
            relative => SourceScan.TryReadAllText(Path.Combine(RepoPaths.RepoRoot ?? ".", relative)));

    /// <summary>
    ///     Selects the files that name the port out of <paramref name="relativePaths" />.
    /// </summary>
    /// <remarks>
    ///     Reading the whole text rather than stripping comments is deliberate and
    ///     the width is harmless: the port's own file names itself, and so does
    ///     the store's declaration through the cross-reference #744 added. Those
    ///     two files hold no live probe and <see cref="Scan" /> skips comment
    ///     lines, so a doc reference widens the perimeter without ever producing
    ///     a finding. The alternative — matching only code positions — would need
    ///     a parser to be correct and would silently drop a caller that spells the
    ///     port inside an attribute or a <c>nameof</c>.
    ///     <para>
    ///         The reader is injected so the non-vacuity control can drive this
    ///         with files the checkout does not contain. That control is the
    ///         whole point: a list cannot be shown to acquire a file it was never
    ///         given, so if the control passes, the perimeter is derived.
    ///     </para>
    /// </remarks>
    private static IReadOnlyList<string> PortNamingFiles(
        IReadOnlyList<string> relativePaths,
        Func<string, string?> read)
    {
        var found = new List<string>();
        foreach (string path in relativePaths)
        {
            if (read(path) is { } text && text.Contains(NarrowSeamName, StringComparison.Ordinal))
            {
                found.Add(path);
            }
        }

        found.Sort(StringComparer.Ordinal);
        return found;
    }

    /// <summary>
    ///     #729's matcher, extended in one place and only one: the name of the
    ///     tested value may now be QUALIFIED (<c>cfg.DefaultProvider</c>), not
    ///     just bare (<c>providerId</c>). The inherited pattern was
    ///     <c>\w*[Pp]rovider\w*</c>, which cannot match a dotted name — so it was
    ///     blind to the one spelling that actually existed on this seam. That is
    ///     not a hypothetical: the line it needed to catch reads
    ///     <c>IsNullOrEmpty(cfg.DefaultProvider)</c>, so #729's own rule would
    ///     have gone green over it even had the producer been on its file list.
    ///     Caught by this file's non-vacuity control, which plants the real line.
    /// </summary>
    private static readonly Regex HalfPairProbe = new(
        """
        string\.IsNullOr(?:Empty|WhiteSpace)\s*\(\s*[\w.]*\w*(?:[Pp]rovider|[Mm]odel)\w*\s*\)\s*(?:&&|\|\|)\s*(?:!\s*)?string\.IsNullOr(?:Empty|WhiteSpace)\s*\(\s*[\w.]*\w*(?:[Pp]rovider|[Mm]odel)\w*\s*\)
        """,
        RegexOptions.Compiled);

    /// <summary>
    ///     A member whose name opens with one of these verbs is a write. Matched
    ///     against the name with a trailing <c>Async</c> removed, anchored at the
    ///     start, and — this is the part the first version of this control got
    ///     wrong — the verb must be followed by a word boundary that is NOT a
    ///     lowercase letter. A bare <c>^Add</c> matches <c>AddressOf</c>, which is
    ///     a read; <c>(?![a-z])</c> is what keeps the two apart, and the control
    ///     below plants <c>AddressOfAsync</c> to hold it to that.
    /// </summary>
    private static readonly Regex WriteMember = new(
        """
        ^(?:Save|Update|Write|Set|Delete|Put|Post|Remove|Add|Insert|Erase|Purge|Clear|Mutate|Patch|Apply|Upsert|Commit|Edit|Persist|Configure|Store|Flush|Replace)(?![a-z])
        """,
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>A file, a 1-based line number, and the offending line.</summary>
    private sealed record ProbeSite(string RelativePath, int Line, string Text);

    // ── Rule 1: the names ────────────────────────────────────────────────────

    [Test]
    public async Task NarrowSeam_IsNamedForTheValue_And_TheOldNameIsGone()
    {
        IReadOnlyList<string> narrow = FindDeclarations(NarrowSeamName);
        IReadOnlyList<string> retired = FindDeclarations(RetiredSeamName);

        await Assert.That(narrow.Count).IsEqualTo(1).Because(
            "there is one narrow read-only port over the shared common config, and after #453 it is "
            + "named for the value it hands out: " + NarrowSeamName + ". \"ICommonConfigReader\" said "
            + "only what the member cannot do, so choosing between it and " + StoreName + " still "
            + "required opening both — the reader turns out to read one ModelRef, not the config. "
            + "Found: " + Describe(narrow) + ". If it moved, re-point this at the new home; a lookup "
            + "that finds nothing is how a guard reports green forever.");

        await Assert.That(retired.Count).IsEqualTo(0).Because(
            RetiredSeamName + " is the pre-#453 name and must not come back: it is the name that "
            + "hides the difference. Found: " + Describe(retired) + ". This is the #717 rule — two "
            + "different things under one name are made to differ in the name, and the name is not "
            + "re-added afterwards.");
    }

    // ── Rule 2: the carrier ──────────────────────────────────────────────────

    [Test]
    public async Task NarrowSeam_ReadsOneModelRef_WithOneAbsence()
    {
        Type? port = FindType(NarrowSeamName);

        // Non-vacuity. A null here would make every line below throw and fail for
        // the wrong reason, telling the next reader nothing about which check broke.
        await Assert.That(port).IsNotNull().Because(
            NarrowSeamName + " must be a public interface in a loaded production assembly. Rule 1 "
            + "already asserts that; this is the shape half, and it needs a non-null type to ask.");

        MethodInfo? method = port!.GetMethod(
            NarrowSeamMethod,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly,
            binder: null,
            [typeof(CancellationToken)],
            modifiers: null);

        await Assert.That(method).IsNotNull().Because(
            "the narrow port declares exactly one member, " + NarrowSeamMethod
            + ", and it must be a public instance method taking a CancellationToken. It was "
            + "TryReadProviderModelAsync before #453 — the \"Try\" was a second, redundant way of "
            + "saying \"Maybe\": a Maybe-returning method is asked, not tried.");

        await Assert.That(method!.ReturnType).IsEqualTo(typeof(Task<Maybe<ModelRef>>)).Because(
            "the config names one provider/model or nothing; it never names half. "
            + "Task<(string? ProviderId, string? ModelId)?> is a pair of optional strings inside "
            + "another optional, so it spells FOUR states — both, neither, provider-only, "
            + "model-only — and the type can tell none of them apart. #729 already removed that "
            + "carrier from the receiving end of this very seam and ProviderModelAbsenceRules guards "
            + "it there; Maybe<ModelRef> has one absence, and ModelRef is the only carrier in the "
            + "repo whose halves are non-null by construction and whose provider half is normalized "
            + "(#678/#690). Not Result: \"nothing configured yet\" is the normal pre-onboarding "
            + "state of every install, not a failure. See issues #453 and #598.");
    }

    // ── Rule 3: the split is real ────────────────────────────────────────────

    [Test]
    public async Task NarrowSeam_ExposesNoWrite_AndTheStoreOwnsTheWrites()
    {
        Type? narrow = FindType(NarrowSeamName);
        Type? store = FindType(StoreName);

        await Assert.That(narrow).IsNotNull().Because(NarrowSeamName + " must exist; see rule 1.");
        await Assert.That(store).IsNotNull().Because(
            StoreName + " must exist. It is the whole-config repository: the narrow port is a "
            + "read-only projection of it, and #453 keeps both, so neither may be deleted to make "
            + "the other look tidy.");

        IReadOnlyList<string> narrowWrites = FindWriteMembers(narrow!);
        IReadOnlyList<string> storeWrites = FindWriteMembers(store!);

        await Assert.That(narrowWrites).IsEmpty().Because(
            "the narrow port is the READ half of the split. A write member on it is a second "
            + "answer to \"how does the shared config change?\", next to " + StoreName + " — the "
            + "duplicate #453 was filed about, entering from the other direction. Found: "
            + string.Join(", ", narrowWrites) + ".");

        await Assert.That(storeWrites.Count).IsGreaterThan(0).Because(
            "and the split has to point somewhere: the writes live on " + StoreName + ". If BOTH "
            + "contracts were read-only the pair would be one contract wearing two names, which is "
            + "the duplicate, and the fix would be to merge them. The reason they are not merged is "
            + "a cycle — Harbor.Desktop.Abstractions declares a direct ProjectReference to "
            + "Harbor.Ui.Framework, so the reverse edge cannot be added.");
    }

    // ── Rule 4: the producer does not re-derive it ───────────────────────────

    [Test]
    public async Task Seam_DoesNotReDeriveHalfPairUsability()
    {
        string root = RequireRepoRoot();
        List<ProbeSite> probes = [.. SeamPerimeter().SelectMany(f => FindProbes(root, f))];

        await Assert.That(probes.Count).IsEqualTo(0).Because(
            "nobody on this seam may decide \"is this pair whole?\" by testing a provider half "
            + "against a model half. #729 removed that rule from the two consumer files and left the "
            + "derivation itself, which had moved to CommonConfigReaderAdapter:48 — a file its "
            + "scan's file list does not contain, so the guard as landed could not see it. A "
            + "hand-written normalisation is the wrong place for the answer because a second "
            + "producer need not know it exists: the value type states it, so the question cannot "
            + "be re-derived, forgotten, or answered the other way. Found: " + Describe(probes)
            + ". See issues #453 and #598.");
    }

    // ── Rule 5: one producer ─────────────────────────────────────────────────

    [Test]
    public async Task NarrowSeam_HasExactlyOneProductionImplementer()
    {
        string root = RequireRepoRoot();
        IReadOnlyList<string> implementers = FindSourceImplementers(root);

        await Assert.That(implementers.Count).IsEqualTo(1).Because(
            "exactly one production type implements the narrow port — CommonConfigReaderAdapter, "
            + "which forwards to " + StoreName + ". A second implementer is a second hand-written "
            + "answer to \"is this reference whole?\", which is the hazard #729's doc names: the "
            + "consumers agreed only because one producer normalised the halves, and nothing "
            + "stopped a second one from forgetting. Test doubles are excluded — a fake proves the "
            + "port is injectable and reads nobody's config. Found: "
            + (implementers.Count == 0 ? "(none — the seam is unwired)" : string.Join(", ", implementers))
            + ".");
    }

    /// <summary>
    ///     Implementers found by SOURCE, over <c>src/</c> and <c>apps/</c>.
    /// </summary>
    /// <remarks>
    ///     This started as a reflection sweep over loaded production assemblies,
    ///     which is the shape <c>ThemeStoreSeamRules</c> uses — and the first CI run
    ///     showed why it cannot work here: the sole implementer,
    ///     <c>CommonConfigReaderAdapter</c>, lives in <c>apps/Harbor.App.Avalonia</c>,
    ///     and apps are composition roots that this test project does not reference.
    ///     The count came back zero and the rule could never be satisfied by correct
    ///     code — the failure mode a guard must not have. <c>ServiceLocatorBoundaryRules</c>
    ///     records the same boundary in its own file header.
    ///     <para>
    ///         The scan is over the two product trees, and <c>tests/</c> is not one of
    ///         them, so fakes are excluded by the walk rather than by a name filter.
    ///         <c>src/</c> and <c>apps/</c> contain the port declaration itself, which
    ///         is skipped: an interface does not implement itself.
    ///     </para>
    /// </remarks>
    private static IReadOnlyList<string> FindSourceImplementers(string root)
    {
        var found = new List<string>();
        foreach (string tree in new[] { "src", "apps" })
        {
            string dir = Path.Combine(root, tree);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (string path in Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                if (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    continue;
                }

                string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (IsDeclaration(relative))
                {
                    continue;
                }

                if (ImplementsPort(path))
                {
                    found.Add(relative);
                }
            }
        }

        found.Sort(StringComparer.Ordinal);
        return found;
    }

    private static bool IsDeclaration(string relativePath)
        => string.Equals(relativePath, $"src/Harbor.Ui.Framework.Abstractions/Configuration/{NarrowSeamName}.cs", StringComparison.Ordinal);

    /// <summary>
    ///     Whether a file <b>implements</b> the port, as opposed to naming it.
    /// </summary>
    /// <remarks>
    ///     The distinction is the base list, and the first CI run of this rule
    ///     showed why it has to be made: a plain "does the file mention the name"
    ///     scan returned THREE files — the adapter, plus
    ///     <c>ConfigRegistration</c> (which registers it in DI) and
    ///     <c>SessionFactory</c> (which consumes it). Neither of those two is a
    ///     second implementation; one wires the port up and one declares it as a
    ///     constructor parameter, and both are exactly what the rule is supposed to
    ///     allow. A guard that cannot tell an implementer from a caller is a guard
    ///     that gets satisfied by deleting the only real implementer.
    ///     <para>
    ///         So the pattern matches the port name inside a type's base list:
    ///         <c>: … {port}</c>, <c>, {port}</c> on a declaration line, or
    ///         <c>where T : {port}</c>. Base lists wrap across lines, so a line that
    ///         ends in a separator is joined with the next before matching. This is
    ///         textual, not a parse, and the bound is stated here: a base list broken
    ///         with an intervening comment, or a type aliased onto the port, is
    ///         missed. Both would be visible in review, and rule 2 (the port's shape)
    ///         is what actually carries the invariant.
    ///     </para>
    /// </remarks>
    private static bool ImplementsPort(string path)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (IOException)
        {
            return false;
        }

        for (int i = 0; i < lines.Length; i++)
        {
            if (IsCommentLine(lines[i]))
            {
                continue;
            }

            // Join a wrapped base list: a line ending in a separator continues.
            string line = lines[i];
            int guard = 0;
            while ((line.TrimEnd().EndsWith(",", StringComparison.Ordinal)
                    || line.TrimEnd().EndsWith("|", StringComparison.Ordinal))
                   && i + 1 < lines.Length
                   && guard++ < 4)
            {
                line = line.TrimEnd() + " " + lines[++i].Trim();
            }

            if (!line.Contains(NarrowSeamName, StringComparison.Ordinal))
            {
                continue;
            }

            // ": <name>" immediately before it, or a generic constraint.
            int at = line.IndexOf(NarrowSeamName, StringComparison.Ordinal);
            string before = line[..at].TrimEnd();
            string after = line[(at + NarrowSeamName.Length)..].TrimStart();
            bool isBaseList = before.EndsWith(":", StringComparison.Ordinal)
                              || before.EndsWith(",", StringComparison.Ordinal);
            bool isConstraintTail = after.Length == 0
                                    || after.StartsWith(")", StringComparison.Ordinal)
                                    || after.StartsWith(",", StringComparison.Ordinal);

            if (isBaseList && isConstraintTail)
            {
                return true;
            }
        }

        return false;
    }

    // ── Non-vacuity ──────────────────────────────────────────────────────────

    [Test]
    public async Task WriteVerbRule_FiresOnAWrite_AndStaysSilentOnARead()
    {
        IReadOnlyList<string> writes = FindWriteMembersIn(
        [
            "ReadModelRefAsync", "LoadAsync", "TryReadAsync", "GetAsync", "FetchAsync",
            "TryReadProviderModelAsync",
        ]);

        await Assert.That(writes).IsEmpty().Because(
            "every name above is a read, and reads are what the narrow port is for. If one is "
            + "reported as a write the capability rule is over-broad and would fail on the correct "
            + "code. Got: " + string.Join(", ", writes) + ".");

        IReadOnlyList<string> caught = FindWriteMembersIn(["SaveAsync", "UpdateAsync", "LoadAsync"]);

        await Assert.That(caught.Count).IsEqualTo(2).Because(
            "the matcher must actually fire, or rule 3 is a \"there is no violation\" result that "
            + "cannot be told apart from a scan that read nothing. SaveAsync and UpdateAsync are "
            + StoreName + "'s two writes and must be reported; LoadAsync must not. Got: "
            + string.Join(", ", caught) + ".");

        await Assert.That(FindWriteMembersIn(["ResetAsync", "SettingAsync", "AddressOfAsync"])).IsEmpty().Because(
            "the verb is matched at the START of the name, so a read that merely contains a verb as "
            + "a substring (Reset, Setting, AddressOf) is not a write.");
    }

    /// <summary>
    ///     The name lookups this file depends on must be able to FIND something.
    ///     Rules 1–3 resolve the port by name across loaded assemblies, which is
    ///     what lets this file be committed red before the port is renamed — but it
    ///     also means that if the port ever moved out of a referenced assembly, every
    ///     one of them would report "not found" and read as a verdict.
    /// </summary>
    /// <summary>
    ///     Rule 5's implementer predicate must separate a real implementation from
    ///     the two legal ways a file merely NAMES the port — registering it in DI
    ///     and declaring it as a constructor parameter. The first CI run of this
    ///     file returned three files for a count of one, and the fix must not become
    ///     a matcher that reports zero instead: both controls below are the reason
    ///     the pattern can be trusted, and the negative one is the one that failed.
    /// </summary>
    [Test]
    public async Task ImplementerRule_SeesABaseList_AndIgnoresACaller()
    {
        string root = RequireRepoRoot();
        const string adapter = "apps/Harbor.App.Avalonia/Services/CommonConfigReaderAdapter.cs";
        const string registration = "apps/Harbor.App.Avalonia/Hosting/ConfigRegistration.cs";
        const string consumer = "src/Harbor.Ui.Framework.Sessions/Sessions/SessionFactory.cs";

        // Positive: the one real implementer, named in a base list.
        await Assert.That(FindSourceImplementers(root).Contains(adapter)).IsTrue()
            .Because(
                "CommonConfigReaderAdapter implements the port and must be counted. If this fails "
                + "the scan is blind to base lists, and rule 5 is satisfied by an UNWIRED seam.");

        // Negative: the two files that name the port without implementing it. These
        // are what the first run over-counted, and a count of three is the same
        // failure as a count of zero in a different costume.
        await Assert.That(FindSourceImplementers(root).Contains(registration)).IsFalse()
            .Because(
                "ConfigRegistration mentions the port in AddSingleton<ICommonConfigModelRefReader> "
                + "— that is wiring, not a second implementation, and a rule that cannot tell "
                + "them apart gets satisfied by deleting the real one");
        await Assert.That(FindSourceImplementers(root).Contains(consumer)).IsFalse()
            .Because(
                "SessionFactory declares the port as an optional constructor parameter — that is a "
                + "caller, which is exactly what a read-only seam exists to be");
    }

    [Test]
    public async Task PortLookup_CanSeeThePortAssembly()
    {
        await Assert.That(PortAssemblyIsVisible()).IsTrue().Because(
            NarrowSeamName + " must be resolvable by name from this test project's loaded "
            + "assemblies — it is declared in Harbor.Ui.Framework.Abstractions, which this "
            + "project references. If this fails, the by-name rules in this file are reporting "
            + "absence rather than a verdict, and the file's non-vacuity claims are void. Note the "
            + "asymmetry that forces the split: the port is visible, its IMPLEMENTER is in "
            + "apps/Harbor.App.Avalonia, which is a composition root this project does not "
            + "reference — so rule 5 counts implementers by source.");
    }

    /// <summary>
    ///     #767: the perimeter rule 4 scans is DERIVED — every product file that
    ///     names the port — and not typed. The expectation is restated here as
    ///     its own query over the checkout rather than obtained from the rule's
    ///     helper, so a bug in the helper shows up as a disagreement instead of
    ///     cancelling out.
    /// </summary>
    [Test]
    public async Task SeamPerimeter_CoversEveryProductFileThatNamesThePort()
    {
        var derived = SourceScan.EnumerateProductCsFiles()
            .Where(p => SourceScan.TryReadAllText(p)?.Contains(NarrowSeamName, StringComparison.Ordinal) == true)
            .Select(SourceScan.Relative)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        IReadOnlyList<string> scanned = SeamPerimeter();

        await Assert.That(derived.Count).IsGreaterThan(2).Because(
            "the derivation has to find more than the files the pre-#767 list happened to police, "
            + "or it is not a perimeter but a transcription of that list. Found: " + Describe(derived) + ".");

        var unpoliced = derived.Where(f => !scanned.Contains(f, StringComparer.Ordinal)).ToList();

        await Assert.That(unpoliced).IsEmpty().Because(
            "any product file that names " + NarrowSeamName + " is on this seam by construction — it "
            + "knows the port, so the forbidden derivation can be written in it — and rule 4 must "
            + "therefore scan it. A typed list cannot promise that, because becoming relevant is not "
            + "an event a list records. Unpoliced: " + Describe(unpoliced) + ". Two of these were the "
            + "concrete proof that the list was not a rule: SessionFactory names the port as a "
            + "constructor parameter and is listed in ProviderModelAbsenceRules.ConsumerFiles, whose "
            + "HalfPairProbe is byte-identical to this file's, so the same construct was forbidden in "
            + "one file and permitted in the next purely by list membership; and ICommonConfigStore.cs "
            + "names the port in the cross-reference #744 added and was on neither list. See #767.");

        await Assert.That(scanned.Count).IsGreaterThanOrEqualTo(derived.Count).Because(
            "the perimeter may be wider than the port-naming set — it is not a filter, it IS the set — "
            + "but it must never be narrower, or files outside it are unpoliced. Derived "
            + derived.Count + ", scanned " + scanned.Count + ".");
    }

    /// <summary>
    ///     THE CONTROL THAT SEPARATES A DERIVATION FROM A LIST. A list cannot be
    ///     shown to acquire a file it was never given, so this hands the selector
    ///     three files the checkout does not contain and requires the two that
    ///     name the port to arrive on their own. It is the acceptance criterion
    ///     #767 states: a structural change must not be indistinguishable from a
    ///     return to the enumeration.
    /// </summary>
    [Test]
    public async Task Perimeter_AcquiresANewFileThatNamesThePort_WithNothingToEdit()
    {
        IReadOnlyList<string> selected = PortNamingFiles(
            [
                "src/Brand/NewCaller.cs",
                "src/Brand/NewImplementer.cs",
                "src/Brand/Unrelated.cs",
            ],
            path => Path.GetFileName(path) switch
            {
                "NewCaller.cs" => "public sealed class C(ICommonConfigModelRefReader reader) { }",
                "NewImplementer.cs" => "public sealed class D : ICommonConfigModelRefReader { }",
                _ => null,
            });

        await Assert.That(selected).IsEquivalentTo(new[] { "src/Brand/NewCaller.cs", "src/Brand/NewImplementer.cs" })
            .Because(
                "a file that reaches this seam spells " + NarrowSeamName + ", whether it implements the "
                + "port or merely receives it, so both must select themselves into the perimeter with "
                + "no list to edit — and a file that never names the port must stay out, or the rule "
                + "grades the whole product tree. Got: " + Describe(selected) + ".");
    }

    [Test]
    public async Task HalfPairMatcher_StillSeesTheRealSpelling()
    {
        // The line this file exists for — dotted member access, which is how the
        // adapter actually wrote it, and which the pattern inherited from #729
        // could not match. Planted verbatim, because a control that only ever
        // used the bare spelling would have kept a blind pattern green.
        await Assert.That(Scan(["if (string.IsNullOrEmpty(cfg.DefaultProvider) || string.IsNullOrEmpty(cfg.DefaultModel))"]).Count)
            .IsGreaterThan(0)
            .Because("this is CommonConfigReaderAdapter:48 as it stood — the matcher must see it");
        await Assert.That(Scan(["if (!string.IsNullOrEmpty(modelId) && !string.IsNullOrEmpty(providerId))"]).Count)
            .IsGreaterThan(0)
            .Because("operand order is an implementation detail, not a distinction the rule draws");

        // And the near-misses that are somebody else's problem.
        await Assert.That(Scan(["if (configured is { } fromConfig)"]).Count).IsEqualTo(0)
            .Because("a test on the whole value is the fix, not a violation");
        await Assert.That(Scan(["if (string.IsNullOrEmpty(providerId)) return;"]).Count).IsEqualTo(0)
            .Because("a lone per-field guard is a different question and stays legal; the rule needs the operator");
        await Assert.That(Scan(["if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(title))"]).Count).IsEqualTo(0)
            .Because("two unrelated optional strings are not a provider/model pair");
        await Assert.That(Scan(["// if (!string.IsNullOrEmpty(providerId) && !string.IsNullOrEmpty(modelId))"]).Count)
            .IsEqualTo(0)
            .Because("comment lines are prose about the old code, not a live probe — and the "
                     + "post-fix docs on this seam deliberately quote the old spelling");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string RequireRepoRoot()
        => RepoPaths.RepoRoot ?? throw new InvalidOperationException(
            "Harbor.slnx not found above " + AppContext.BaseDirectory
            + " — this guard walks the repository and cannot run from a published test host.");

    private static string Describe(IReadOnlyList<string> names)
        => names.Count == 0 ? "(none)" : string.Join(", ", names);

    private static string Describe(IReadOnlyList<ProbeSite> sites)
        => sites.Count == 0 ? "(none)" : string.Join(", ", sites.Select(s => s.RelativePath + ":" + s.Line));

    /// <summary>Every public interface named <paramref name="name" />, as assembly-qualified names.</summary>
    private static IReadOnlyList<string> FindDeclarations(string name)
    {
        var found = new List<string>();
        foreach (Type type in LoadableTypes(ProductionAssemblies()))
        {
            if (type.IsInterface && type.IsPublic && string.Equals(type.Name, name, StringComparison.Ordinal))
            {
                found.Add(type.AssemblyQualifiedName ?? type.FullName ?? type.Name);
            }
        }

        found.Sort(StringComparer.Ordinal);
        return found;
    }

    /// <summary>A production type by simple name, or <c>null</c>.</summary>
    private static Type? FindType(string name)
    {
        foreach (Assembly assembly in ProductionAssemblies())
        {
            foreach (Type type in LoadableTypes([assembly]))
            {
                if (type.IsInterface && type.IsPublic && string.Equals(type.Name, name, StringComparison.Ordinal))
                {
                    return type;
                }
            }
        }

        return null;
    }

    /// <summary>
    ///     Whether the loaded-assembly sweep can see the port at all — a non-vacuity
    ///     backstop for the rules that resolve it BY NAME, and the reason rule 5 is
    ///     a source scan instead. The port is declared in
    ///     <c>Harbor.Ui.Framework.Abstractions</c>, which this project references, so
    ///     the name lookups work; the port's IMPLEMENTER is in an app, which it does
    ///     not. Asserted rather than assumed, because if the port ever moves into an
    ///     app the name lookups go quiet and every rule in this file reports
    ///     "not found" rather than a real verdict.
    /// </summary>
    private static bool PortAssemblyIsVisible() => FindType(NarrowSeamName) is not null;

    private static IReadOnlyList<string> FindWriteMembers(Type contract)
        => FindWriteMembersIn([.. contract.GetMethods().Select(m => m.Name).Distinct(StringComparer.Ordinal)]);

    /// <summary>
    ///     The write-verb predicate over bare member names, separated out so the
    ///     non-vacuity controls can drive the exact matcher rule 3 uses instead of
    ///     a second, near-identical one that could pass while the real one is
    ///     broken.
    /// </summary>
    private static IReadOnlyList<string> FindWriteMembersIn(IEnumerable<string> memberNames)
    {
        var found = new List<string>();
        foreach (string name in memberNames.Distinct(StringComparer.Ordinal))
        {
            string stem = name.EndsWith("Async", StringComparison.Ordinal) ? name[..^"Async".Length] : name;
            if (WriteMember.IsMatch(stem))
            {
                found.Add(name);
            }
        }

        found.Sort(StringComparer.Ordinal);
        return found;
    }

    private static IEnumerable<Assembly> ProductionAssemblies()
    {
        Assembly self = typeof(CommonConfigContractRules).Assembly;
        return ArchitectureTestHelpers.LoadHarborAssemblies()
            .Values
            .Where(a => !ReferenceEquals(a, self) && !IsTestAssembly(a));
    }

    private static bool IsTestAssembly(Assembly assembly)
    {
        string name = assembly.GetName().Name ?? string.Empty;
        return name.EndsWith("Tests", StringComparison.Ordinal)
               || name.EndsWith(".TestKit", StringComparison.Ordinal)
               || string.Equals(name, "Harbor.TestKit", StringComparison.Ordinal)
               || name.EndsWith(".Benchmarks", StringComparison.Ordinal)
               || name.StartsWith("testhost", StringComparison.Ordinal)
               || name.StartsWith("Microsoft.Testing", StringComparison.Ordinal);
    }

    private static IEnumerable<Type> LoadableTypes(IEnumerable<Assembly> assemblies)
    {
        foreach (Assembly assembly in assemblies)
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = [.. ex.Types.OfType<Type>()];
            }
            catch (Exception)
            {
                continue;
            }

            foreach (Type type in types)
            {
                yield return type;
            }
        }
    }

    private static List<ProbeSite> FindProbes(string root, string relativePath)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(Path.Combine(root, relativePath));
        }
        catch (IOException)
        {
            return [];
        }

        return
        [
            .. Scan(lines)
                .Select(index => new ProbeSite(relativePath, index + 1, lines[index].Trim()))
        ];
    }

    /// <summary>
    ///     0-based line numbers holding a half-pair probe. Comment-only lines are
    ///     skipped, with the same bound as #729's rule: a probe split across a
    ///     line break is missed, which is why rule 2 (reflection) carries the load
    ///     and this rule only narrows it.
    /// </summary>
    private static IReadOnlyList<int> Scan(IReadOnlyList<string> lines)
    {
        var hits = new List<int>();
        for (int i = 0; i < lines.Count; i++)
        {
            if (IsCommentLine(lines[i]))
            {
                continue;
            }

            if (HalfPairProbe.IsMatch(lines[i]))
            {
                hits.Add(i);
            }
        }

        return hits;
    }

    private static bool IsCommentLine(string line)
    {
        string trimmed = line.TrimStart();
        return trimmed.StartsWith("//", StringComparison.Ordinal)
               || trimmed.StartsWith("/*", StringComparison.Ordinal)
               || trimmed.StartsWith('*');
    }
}
