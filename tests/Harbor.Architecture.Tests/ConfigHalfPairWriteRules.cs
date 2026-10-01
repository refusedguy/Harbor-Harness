// ConfigHalfPairWriteRules.cs — the guard for #894.
//
// WHY THIS FILE EXISTS
// --------------------
// #894 asked whether the anchor "names the port" is exhausted, having found two
// sites that answer "is this pair whole?" without naming
// `ICommonConfigModelRefReader`. Measured over this checkout, the answer is that
// the anchor was never the right place to look, and the two sites are not the
// same defect:
//
//   * `Hosting/Modules/ToolsCatalog.cs` — `ResolveDefaultModelFromCommon`
//     re-qualifies the pair by hand. This one IS already covered, by a different
//     merged guard: `ModelRefSingleParserTests` inventories it in
//     `KnownAdHocCutSites` as "REAL DEBT", and rule 3 there scans every product
//     file for the ad-hoc cut. #894's own second premise — "rule 5 cannot see it
//     because it is static" — is true and irrelevant: rule 5 counts
//     IMPLEMENTERS, which is a different claim from the one being violated.
//   * `Desktop.Abstractions/ViewModels/OnboardingViewModel.cs` — the two halves
//     of the default pair are decided independently, per line. Nothing covers
//     this, and the two existing matchers cannot, for a reason measured below.
//
// WHY NO EXISTING MATCHER CAN SEE THE SECOND SITE
// ------------------------------------------------
// Both `CommonConfigContractRules.HalfPairProbe` and
// `ProviderModelAbsenceRules.HalfPairProbe` match ONE line holding a provider
// test, an operator, and a model test. The onboarding lines are:
//
//     DefaultProvider = overwriteDefaults || string.IsNullOrEmpty(cfg.DefaultProvider) ? provider : cfg.DefaultProvider,
//     DefaultModel    = overwriteDefaults || string.IsNullOrEmpty(cfg.DefaultModel)    ? model    : cfg.DefaultModel,
//
// Each line carries ONE emptiness test, and the operator on it joins
// `overwriteDefaults` to that test — not one half's test to the other's. So
// joining the two lines does not produce a match either; there is no line, and
// no pair of lines, that the operator-shaped pattern can see. Measured: both
// lines alone, and both lines joined with a space, return zero. This is
// therefore not the "blind to a dotted name" gap #453 fixed, and not a
// perimeter question: the construct is a different shape from the one the
// pattern encodes.
//
// THE INVARIANT, STATED AS A RELATION — NOT AS A LIST
// ---------------------------------------------------
// The tempting rule here is a list of the offending lines, or a list of
// "writers of the config's defaults". Both are the #595/#557 shape: a
// hand-maintained roster that rots, is not derivable, and encodes an author's
// opinion while looking like a fact — the exact reason
// `ReadOnlyAgentShellAllowRule` states a relation between two rulesets that
// already exist instead of a roster of mutating git verbs.
//
// So the invariant is a relation against the value that owns the question:
//
//     Writing a half of the pair must not be a decision ABOUT that half.
//
// `ModelRef` is already the one place that answers "is this reference whole?",
// and `ModelRef.Qualify` is the one function that answers "make this pair a
// reference". A write site that reads `cfg.DefaultProvider` to decide what to
// write into `DefaultProvider` is asking the value a question it has already
// answered — and, unlike a list, this needs no roster: the perimeter is
// DERIVED from the assignment itself, so a new writer selects itself in with
// nothing to edit.
//
// It is deliberately NOT the whole-file perimeter "names either contract",
// which measures 19 files against 6 and catches NOTHING (0 hits). Widening the
// anchor is the expensive move #894 predicted and it buys nothing here; the
// boundary that matters is not which contract a file names but whether it
// decides a half on its own.
//
// THE PERIMETER IS DERIVED, AND THE CONTROL PROVES IT
// ----------------------------------------------------
// A file becomes relevant by ASSIGNING a half — `DefaultProvider =` or
// `DefaultModel =`, not `==`. Becoming relevant is not an event a typed list
// records, which is the #767/#890 lesson; a list cannot be shown to acquire a
// file it was never given, so `Perimeter_AcquiresANewWriter_WithNothingToEdit`
// is the control that tells the two apart.
//
// NON-VACUITY
// -----------
// A "no violation" result is otherwise indistinguishable from a scan that read
// nothing, so the same predicate is driven with positive and negative controls,
// and the real file is required to BE on the derived perimeter — a list or a
// stale walk would let the assertion pass over a file the rule never sees.

using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Issue #894: the config's default provider/model is one value, so writing
///     one half must not be a per-half decision. The half-pair PROBE is a
///     different invariant with a different shape and is guarded by
///     <c>CommonConfigContractRules</c>; see the file header for why it cannot
///     see these lines.
/// </summary>
public sealed class ConfigHalfPairWriteRules
{
    /// <summary>
    ///     An ASSIGNMENT of one half of the default pair. Anchored so it cannot
    ///     match a comparison (<c>==</c>/<c>!=</c>) or a declaration, and
    ///     word-bounded so <c>MyDefaultProvider</c> is not one of these.
    /// </summary>
    private static readonly Regex HalfAssignment = new(
        @"\bDefault(?<Half>Provider|Model)\s*=(?!=)",
        RegexOptions.Compiled);

    /// <summary>
    ///     The same emptiness predicate <c>HalfPairProbe</c> encodes, applied to
    ///     ONE half. This is the relation's whole content: does the value written
    ///     into a half come from a test of that half?
    /// </summary>
    private static readonly Regex EmptinessTestOfHalf = new(
        @"string\.IsNullOr(?:Empty|WhiteSpace)\s*\(\s*[^)]*\bDefault(?:Provider|Model)\b",
        RegexOptions.Compiled);

    /// <summary>A file, a 1-based line number, and the offending line.</summary>
    private sealed record WriteSite(string RelativePath, int Line, string Text);

    // ── Rule 1: no half is decided on its own ─────────────────────────────────

    [Test]
    public async Task WritingAHalf_MustNotBeADecisionAboutThatHalf()
    {
        IReadOnlyList<WriteSite> sites = FindSelfDecidedHalves(RepoRoot ?? ".");

        await Assert.That(sites.Count).IsEqualTo(0).Because(
            "the config names one provider/model or nothing, so a writer must not test a half in "
            + "order to decide that same half. Deciding each half independently is what makes a "
            + "half-pair reachable: the config can end up with an overwritten provider beside an "
            + "untouched model, or the reverse — the exact split `ModelRef` exists to make "
            + "unrepresentable. `ModelRef.Qualify` is the one function that turns the two halves "
            + "into a reference, and asking the VALUE whether a half is absent re-derives a "
            + "question it has already answered. Found: " + Describe(sites)
            + ". See issues #453, #598 and #894. NOTE: this is not the half-pair PROBE — "
            + "`HalfPairProbe` matches a provider test, an operator and a model test on ONE line, "
            + "and each of these lines carries one test with `overwriteDefaults` on the other "
            + "side of the operator, so that pattern cannot see them joined either. Measured, not "
            + "assumed: both lines alone, and both joined, return zero hits.");
    }

    // ── The perimeter is derived, not typed ──────────────────────────────────

    /// <summary>
    ///     #894/#767: the perimeter is every product file that ASSIGNS a half, and
    ///     the expectation is restated here as its own query over the checkout
    ///     rather than taken from the rule's helper, so a bug in the helper shows
    ///     up as a disagreement instead of cancelling out.
    /// </summary>
    [Test]
    public async Task Perimeter_CoversEveryProductFileThatAssignsAHalf()
    {
        var derived = SourceScan.EnumerateProductCsFiles()
            .Where(p => SourceScan.TryReadAllText(p) is { } t && AssignsAHalf(t))
            .Select(SourceScan.Relative)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        await Assert.That(derived.Count).IsGreaterThan(2).Because(
            "the derivation has to find more than the one file it was written for, or it is a "
            "transcription of that file. Found: " + string.Join(", ", derived) + ".");

        IReadOnlyList<string> perimeter = Perimeter();
        var unpoliced = derived.Where(f => !perimeter.Contains(f, StringComparer.Ordinal)).ToList();

        await Assert.That(unpoliced).IsEmpty().Because(
            "a product file that assigns one half of the default pair can write a half-pair into "
            "the config, so it is on this seam by construction. A typed list cannot promise that: "
            "becoming relevant is not an event a list records. Unpoliced: "
            + string.Join(", ", unpoliced) + ".");

        // The wider anchors were measured and are NOT the answer, so they are
        // recorded here rather than left as a suggestion nobody re-measures.
        await Assert.That(derived.Any(f => f.EndsWith("ToolsCatalog.cs", StringComparison.Ordinal))).IsFalse()
            .Because(
                "ToolsCatalog re-qualifies the pair by hand, and is deliberately NOT in this "
                "perimeter: it does not ASSIGN a half, so it cannot write one, and the ad-hoc cut "
                "it performs is already covered — as an inventoried REAL DEBT entry with a "
                "reason — by `ModelRefSingleParserTests`. This rule and that one grade different "
                "questions, and folding the second into the first is how #894's two sites came to "
                "be described as one defect when they are two.");
    }

    /// <summary>
    ///     THE CONTROL THAT SEPARATES A DERIVATION FROM A LIST. A list cannot be
    ///     shown to acquire a file it was never given, so this hands the selector
    ///     three files the checkout does not contain and requires the two that
    ///     assign a half to arrive on their own.
    /// </summary>
    [Test]
    public async Task Perimeter_AcquiresANewWriter_WithNothingToEdit()
    {
        IReadOnlyList<string> selected = Writers(
        [
            "src/Brand/NewWriter.cs",
            "src/Brand/ComparesOnly.cs",
            "src/Brand/Unrelated.cs",
        ],
            path => Path.GetFileName(path) switch
            {
                "NewWriter.cs" => "return cfg with { DefaultProvider = p, DefaultModel = m };",
                // A comparison is not an assignment. If the anchor counted these,
                // every reader of the pair would join the perimeter.
                "ComparesOnly.cs" => "return DefaultProvider == DefaultModel;",
                _ => null,
            });

        await Assert.That(selected).IsEquivalentTo(new[] { "src/Brand/NewWriter.cs" })
            .Because(
                "relevance is ASSIGNING a half, and the perimeter follows the assignment rather "
                + "than a list of known writers. Got: " + string.Join(", ", selected) + ".");
    }

    // ── Non-vacuity ──────────────────────────────────────────────────────────

    /// <summary>
    ///     The predicate must separate the real violation from the two shapes that
    ///     are somebody else's problem: a write that does not consult the config,
    ///     and a read that is not a write. If the pattern were widened to fire on
    ///     either, rule 1 would fail on correct code — and a guard that fires on
    ///     correct code gets deleted.
    /// </summary>
    [Test]
    public async Task Rule_FiresOnTheRealShape_AndStaysSilentOnTheLegalOnes()
    {
        // The two lines as they stand, verbatim — including the qualifier, which
        // is the spelling #453 had to widen a pattern for.
        await Assert.That(SelfDecided(["DefaultProvider = overwriteDefaults || string.IsNullOrEmpty(cfg.DefaultProvider) ? provider : cfg.DefaultProvider,"]).Count)
            .IsGreaterThan(0)
            .Because("this is OnboardingViewModel:106 as it stands; the matcher must see it");
        await Assert.That(SelfDecided(["DefaultModel = overwriteDefaults || string.IsNullOrEmpty(cfg.DefaultModel) ? model : cfg.DefaultModel,"]).Count)
            .IsGreaterThan(0)
            .Because("this is OnboardingViewModel:107 — the same question asked of the other half");

        // A write that does not consult the config is not this rule. Both of
        // these are live product lines, quoted so the control cannot pass while
        // the pattern is over-broad.
        await Assert.That(SelfDecided(["DefaultProvider = model.ProviderId,", "DefaultModel = model.Id"]).Count)
            .IsEqualTo(0)
            .Because(
                "ProviderModelPickerViewModel:223-224 writes both halves from one already-whole "
                + "value — that is the shape the rule asks for, not a violation of it");
        await Assert.That(SelfDecided(["DefaultProvider = _common.DefaultProvider ?? string.Empty;"]).Count)
            .IsEqualTo(0)
            .Because(
                "SettingsViewModel:183 restores the record's own value into a local field; reading "
                + "a half to show it is not deciding it");
        await Assert.That(SelfDecided(["DefaultModel = SelectedProvider.DefaultModel;"]).Count)
            .IsEqualTo(0)
            .Because(
                "OnboardingViewModel:327 takes the model the picked provider declares — a "
                + "provider's own default, which is the pair arriving whole rather than being "
                + "assembled half by half");

        // And the comment/doc lines this scan skips, because the guard quotes the
        // shape it forbids in its own prose.
        await Assert.That(SelfDecided(["// DefaultProvider = overwriteDefaults || string.IsNullOrEmpty(cfg.DefaultProvider) ? a : b"]).Count)
            .IsEqualTo(0)
            .Because("a comment is prose about the old code, not a live write");
        await Assert.That(SelfDecided(["/// <c>DefaultProvider = ... string.IsNullOrEmpty(cfg.DefaultProvider)</c> in the docs."]).Count)
            .IsEqualTo(0)
            .Because("doc-comment lines are prose too");
    }

    /// <summary>
    ///     The real file must be ON the derived perimeter, and must be CLEAN by
    ///     the fix. Before the fix this is the red assertion that proves the rule
    ///     is not vacuous: the file is reachable by derivation and the predicate
    ///     fires on it. After it, the same file is still reachable and the
    ///     predicate no longer does — so the rule cannot be satisfied by a walk
    ///     that stopped seeing the file.
    /// </summary>
    [Test]
    public async Task TheOnboardingWriter_IsOnTheDerivedPerimeter()
    {
        string root = RepoPaths.RepoRoot ?? ".";
        const string writer = "src/Harbor.Desktop.Abstractions/ViewModels/OnboardingViewModel.cs";

        await Assert.That(Perimeter()).Contains(writer)
            .Because(
                "the file that writes the pair must be selected by the derivation. If this fails, "
                + "rule 1 is passing over the one file it was written for — the same failure mode "
                + "#453 found in CommonConfigContractRules, where the producer was missing from a "
                + "scan list and the rule passed over a file that still held the derivation.");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    ///     The derived perimeter: every product file that assigns one half.
    /// </summary>
    private static IReadOnlyList<string> Perimeter() => Writers(
        [.. SourceScan.EnumerateProductCsFiles().Select(SourceScan.Relative)],
        relative => SourceScan.TryReadAllText(Path.Combine(RepoPaths.RepoRoot ?? ".", relative)));

    /// <summary>
    ///     Selects the files that assign a half. The reader is injected so the
    ///     non-vacuity control can drive this with files the checkout does not
    ///     contain — a list cannot be shown to acquire one, so if that control
    ///     passes, the perimeter is derived.
    /// </summary>
    private static IReadOnlyList<string> Writers(
        IReadOnlyList<string> relativePaths,
        Func<string, string?> read)
    {
        var found = new List<string>();
        foreach (string path in relativePaths)
        {
            if (read(path) is { } text && AssignsAHalf(text))
            {
                found.Add(path);
            }
        }

        found.Sort(StringComparer.Ordinal);
        return found;
    }

    private static bool AssignsAHalf(string text) => HalfAssignment.IsMatch(text);

    /// <summary>
    ///     Every live write in the checkout whose value is a test of the half it
    ///     writes. Comment lines are skipped — the same line-level heuristic the
    ///     sibling guards use, with the same bound: this is a text scan, not a
    ///     parse. The bound is stated in the file header, and the rule is stated
    ///     as a relation rather than as an operator-shaped pattern precisely so
    ///     that it does not depend on where the operands sit.
    /// </summary>
    private static IReadOnlyList<WriteSite> FindSelfDecidedHalves(string root)
    {
        var found = new List<WriteSite>();
        foreach (string relative in Perimeter())
        {
            string[] lines;
            try
            {
                lines = File.ReadAllLines(Path.Combine(root, relative));
            }
            catch (IOException)
            {
                continue;
            }

            found.AddRange(SelfDecided(lines).Select(i => new WriteSite(relative, i + 1, lines[i].Trim())));
        }

        return found;
    }

    /// <summary>
    ///     0-based line numbers holding a write whose value is decided by testing
    ///     that same half.
    /// </summary>
    private static IReadOnlyList<int> SelfDecided(IReadOnlyList<string> lines)
    {
        var hits = new List<int>();
        for (int i = 0; i < lines.Count; i++)
        {
            if (IsCommentLine(lines[i]))
            {
                continue;
            }

            if (SelfDecidedLine(lines[i]))
            {
                hits.Add(i);
            }
        }

        return hits;
    }

    /// <summary>
    ///     One line: an assignment of a half, and an emptiness test naming a half
    ///     on the value side. Both halves count — deciding <c>DefaultModel</c> from
    ///     a test of <c>cfg.DefaultProvider</c> is the same split as the aligned
    ///     case, and the pattern must not depend on which half is which.
    /// </summary>
    private static bool SelfDecidedLine(string line)
    {
        var assignment = HalfAssignment.Match(line);
        if (!assignment.Success)
        {
            return false;
        }

        string value = line[(assignment.Index + assignment.Length)..];
        return EmptinessTestOfHalf.IsMatch(value);
    }

    private static bool IsCommentLine(string line)
    {
        string trimmed = line.TrimStart();
        return trimmed.StartsWith("//", StringComparison.Ordinal)
               || trimmed.StartsWith("/*", StringComparison.Ordinal)
               || trimmed.StartsWith('*');
    }

    private static string Describe(IReadOnlyList<WriteSite> sites)
        => sites.Count == 0 ? "(none)" : string.Join(", ", sites.Select(s => s.RelativePath + ":" + s.Line));
}