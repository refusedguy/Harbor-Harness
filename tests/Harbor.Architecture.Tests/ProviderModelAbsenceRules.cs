// ProviderModelAbsenceRules.cs — the guard for issue #598.
//
// WHY THIS FILE EXISTS
// --------------------
// `SessionFactory.ResolveProviderModelFromConfigAsync` returned
// `Task<(string? ProviderId, string? ModelId)>`. A two-element tuple of nullable
// strings makes THREE states spellable — both present, both absent, and exactly
// one present — while the type can tell none of them apart. The three callers
// then disagreed about what the third state means, one file apart:
//
//   * `ResolveAgentDefinitionAsync`  — per-ELEMENT: `providerId ?? configProvider
//     ?? agentDef.ProviderId`, i.e. "keep the config half, fall back per field".
//   * `CreateDefaultAsync`            — ATOMIC: `if (!IsNullOrEmpty(p) && !IsNullOrEmpty(m))`,
//     i.e. "a half pair is unusable, discard the whole thing".
//   * `RebindFromCommonConfigAsync`  — ATOMIC, inverted: `if (IsNullOrEmpty(p) ||
//     !IsNullOrEmpty(m)) return;`, i.e. the same rule as the line above.
//
// Same question, two opposite answers, one file apart. Nothing in the type
// system said so; a hand-written normalisation in the producer was the only
// thing holding them consistent, and a second producer would silently break it.
//
// WHY ModelRef AND NOT Maybe<(string, string)>
// -----------------------------------------
// The issue as filed prescribed `Maybe<(string ProviderId, string ModelId)>`.
// That is the right SHAPE and the wrong CARRIER, and it predates #690:
//
//   * `ModelRef` is the repo's declared type for a `provider/model` reference
//     and is atomic — both halves are non-null by construction, so a
//     half-populated pair is not merely discouraged, it is unrepresentable.
//   * A `(string, string)` tuple re-admits two RAW strings. #690 moved this very
//     method off hand-rolled prefix-stripping because the raw provider string
//     reached a running Session unnormalized and unvalidated; `Maybe<(string,
//     string)>` would reintroduce that at the signature. `ModelRef.Qualify`
//     validates and lower-cases the provider half, and cannot yield a blank
//     model half, so carrying `ModelRef` is what keeps #678's win.
//
// The issue's other suggestion, `Maybe<T>.ToResult(string)` for
// `RebindFromCommonConfigAsync`, is declined on purpose. "No provider/model in
// config" is the NORMAL first-run state of every install before onboarding, not
// an error with a message to report; the caller already answers it by keeping
// the current agent. That is exactly the "Result in code that cannot fail is a
// lie" case — note that the pre-fix body already built a `Result` from
// `ModelRef.Qualify` and threw it away on the next line via
// `GetValueOrDefault((null, null))`, which is the signature of a wrapper that
// never earned its keep.
//
// TWO RULES, TWO KINDS OF PROOF
// -----------------------------
//   1. SHAPE (reflection): the method returns `Task<Maybe<ModelRef>>`.
//      Reflection, because the shape is a property of the compiled type.
//   2. CALLERS (source): the consumer files no longer RE-DERIVE "is this pair
//      usable?" by null-checking the two halves against each other. A source
//      rule, because the derivation is a statement, not a signature.
//
// NON-VACUITY — the part that makes the guard worth having
// --------------------------------------------------------
// A source rule that matches nothing is indistinguishable from a source rule
// that is broken, and a broken guard is worse than no guard because it is
// believed. `Matcher_...` below runs the SAME matcher against synthetic
// positive and negative controls, and `CallerRule_ReachesBothConsumerFiles`
// requires discovery to locate both files it is supposed to police.
//
// MECHANISM (#1086, step 2, conveyor)
// -----------------------------------
// Rule 2 below is a ScanRule: one banned shape over the three named consumer
// files, twelve planted controls, a discovery floor of three with all three
// files named. Enumeration, stripping, matching and the control/discovery
// verdicts are ScanRunner's; this file keeps the issue prose and the test
// names. Rule 1 is reflection over the compiled type — not a source scan — and
// stays exactly as it was.
//
// The predicate moves as-is (the same HalfPairProbe over comment-stripped
// lines). The old scanner skipped comment-only lines without full stripping;
// the engine strips comments first, which can only remove hits, never add
// them — and every control below grades identically under both, including the
// trailing-comment line that must still fire and the comment lines that must
// stay silent.

using System.Text.RegularExpressions;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Ui.Framework.Sessions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Issue #598: the provider/model the config names is ONE atomic value with
///     ONE absence, not a pair of optional strings. See the file header for the
///     three-way disagreement this removed and why the carrier is
///     <see cref="ModelRef" /> rather than a validated-again string tuple.
/// </summary>
public sealed class ProviderModelAbsenceRules
{
    private const string SubId = "HALF-PAIR-PROBE";

    /// <summary>
    ///     The files that consume the resolved pair. A half-populated pair used to
    ///     mean "discard everything" in both of them, spelled once with
    ///     <c>&amp;&amp;</c> and once with <c>||</c>; the value type makes the
    ///     question disappear instead of documenting it.
    /// </summary>
    /// <remarks>
    ///     #453: the PRODUCER was missing from this list, and the gap is the whole
    ///     reason the rule could be satisfied while the derivation was still there.
    ///     `CommonConfigReaderAdapter` — not a consumer file — carried the last
    ///     hand-written half-pair test
    ///     (<c>IsNullOrEmpty(cfg.DefaultProvider) || IsNullOrEmpty(cfg.DefaultModel)</c>),
    ///     and this file did not scan it, so "the consumers no longer re-derive
    ///     it" was true and the seam still re-derived it.
    /// </remarks>
    private static readonly string[] ConsumerFiles =
    [
        "src/Harbor.Ui.Framework.Sessions/Sessions/SessionFactory.cs",
        "src/Harbor.Ui.Framework.Sessions/Sessions/SessionLifecycleService.cs",
        "apps/Harbor.App.Avalonia/Services/CommonConfigReaderAdapter.cs", // #453: the producer
    ];

    /// <summary>#453: the producer, named so the scan-liveness test can point at it.</summary>
    private const string ProducerFile = "apps/Harbor.App.Avalonia/Services/CommonConfigReaderAdapter.cs";

    /// <summary>
    ///     "Is this pair usable?" spelled as a two-element emptiness probe: an
    ///     emptiness test on a provider-or-model-named value, joined by
    ///     <c>&amp;&amp;</c>/<c>||</c> to the same kind of test on another one.
    ///     Requiring BOTH operands to be provider/model-named is what keeps this
    ///     from firing on every unrelated <c>IsNullOrEmpty(a) || IsNullOrEmpty(b)</c>;
    ///     requiring the OPERATOR is what keeps it from firing on a lone
    ///     per-field guard, which is a different question and stays legal.
    /// </summary>
    // Raw string literals, deliberately: a verbatim @"…" would need every double
    // quote in the pattern doubled, which is where the sibling guard shipped red
    // the first time. The pattern IS the specification, so it stays literal.
    //
    // #453: the name alternation is now `[\w.]*\w*(?:[Pp]rovider|[Mm]odel)\w*`
    // rather than the original `(?:\w*[Pp]rovider\w*|\w*[Mm]odel\w*)`, because the
    // original could not match a QUALIFIED name. The one line this rule exists to
    // forbid reads `IsNullOrEmpty(cfg.DefaultProvider)` — dotted — so the pattern
    // was blind to it, and the file that carried it was not on the scan list
    // either. Two independent reasons the pre-fix seam passed; the scan list is
    // fixed above and the pattern here.
    private static readonly Regex HalfPairProbe = new(
        """
        string\.IsNullOr(?:Empty|WhiteSpace)\s*\(\s*[\w.]*\w*(?:[Pp]rovider|[Mm]odel)\w*\s*\)\s*(?:&&|\|\|)\s*(?:!\s*)?string\.IsNullOr(?:Empty|WhiteSpace)\s*\(\s*[\w.]*\w*(?:[Pp]rovider|[Mm]odel)\w*\s*\)
        """,
        RegexOptions.Compiled);

    /// <summary>The caller rule as data: one banned shape, three named files, twelve controls.</summary>
    private static readonly ScanRule CallerRule = new()
    {
        Id = "ProviderModelAbsence.Callers",
        Trees = ["src", "apps"],
        InScope = static p => ConsumerFiles.Contains(p, StringComparer.Ordinal),
        Forbidden =
        [
            new ScanForbidden(
                SubId,
                HalfPairProbe,
                "ask the value instead (`is { } reference` / `HasNoValue`); the halves of a "
                + "ModelRef cannot come apart."),
        ],
        Controls =
        [
            // The two real pre-fix spellings, in both operand orders, plus the
            // emptiness-predicate and dotted variants.
            new ScanControl("Spelling/And.cs", "if (!string.IsNullOrEmpty(providerId) && !string.IsNullOrEmpty(modelId))", SubId),
            new ScanControl("Spelling/Or.cs", "if (string.IsNullOrEmpty(providerId) || string.IsNullOrEmpty(modelId))", SubId),
            new ScanControl("Spelling/Order.cs", "if (string.IsNullOrEmpty(model) || string.IsNullOrEmpty(provider))", SubId),
            new ScanControl("Spelling/WhiteSpace.cs", "if (string.IsNullOrWhiteSpace(modelId) && string.IsNullOrWhiteSpace(providerId))", SubId),
            new ScanControl("Spelling/Dotted.cs", "if (string.IsNullOrEmpty(cfg.DefaultProvider) || string.IsNullOrEmpty(cfg.DefaultModel))", SubId),
            new ScanControl("Spelling/Negated.cs", "if (string.IsNullOrEmpty(providerId) && !string.IsNullOrEmpty(modelId))", SubId),
            // A code line with a trailing comment is still a code line.
            new ScanControl("Spelling/Trailing.cs", "if (!string.IsNullOrEmpty(providerId) && !string.IsNullOrEmpty(modelId)) // trailing", SubId),
            // The post-fix spelling, and near-misses that are somebody else's problem.
            new ScanControl("Legal/Whole.cs", "if (configured is { } fromConfig)", null),
            new ScanControl("Legal/Lone.cs", "if (string.IsNullOrEmpty(providerId)) return;", null),
            new ScanControl("Legal/Unrelated.cs", "if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(title))", null),
            new ScanControl("Legal/Comment.cs", "// if (!string.IsNullOrEmpty(providerId) && !string.IsNullOrEmpty(modelId))", null),
            new ScanControl("Legal/Doc.cs", "/// <c>string.IsNullOrEmpty(p) || string.IsNullOrEmpty(m)</c> in the docs.", null),
        ],
        MinHits = 3,
        MustContain = [.. ConsumerFiles],
    };

    // ── Rule 1: shape ────────────────────────────────────────────────────────

    [Test]
    public async Task ResolveProviderModelFromConfigAsync_ReturnsMaybeOfModelRef()
    {
        // Explicit binding flags and an explicit (empty) parameter-type list, not
        // `GetMethod(name)`: the build is a zero-warnings gate and REFL008/REFL029
        // flag the loose overload. The `types` argument is the method's PARAMETER
        // types — this one takes none, so it is Type.EmptyTypes, not the declaring
        // type. Passing the declaring type there matches nothing and returns null.
        MethodInfo? method = typeof(SessionFactory).GetMethod(
            nameof(SessionFactory.ResolveProviderModelFromConfigAsync),
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly,
            binder: null,
            Type.EmptyTypes,
            modifiers: null);

        // Non-vacuity. Without this the `!` below turns a moved-or-renamed method
        // into a NullReferenceException, which fails for the wrong reason and tells
        // the next reader nothing about which check broke.
        await Assert.That(method).IsNotNull()
            .Because(
                "ResolveProviderModelFromConfigAsync must be a public, parameterless method declared "
                + "on SessionFactory. If it moved, gained a parameter, or was renamed, point this guard "
                + "at its new home rather than deleting the check — a lookup that silently finds "
                + "nothing is how a guard starts reporting green forever.");

        await Assert.That(method!.ReturnType).IsEqualTo(typeof(Task<Maybe<ModelRef>>))
            .Because(
                "the config names one provider/model or nothing; it never names half. "
                + "Task<(string?, string?)> makes three states spellable and lets two of them "
                + "mean different things to different callers. Maybe<ModelRef> has one absence, "
                + "and ModelRef is the only carrier in the repo whose two halves are non-null "
                + "by construction and whose provider half is normalized (#678/#690). "
                + "Not Result: 'nothing configured' is the normal pre-onboarding state, not a "
                + "failure — the pre-fix body built one and discarded it on the next line. "
                + "See issue #598.");
    }

    // ── Rule 2: callers ──────────────────────────────────────────────────────

    [Test]
    public async Task Callers_DoNotReDerivePairUsability()
    {
        List<string> probes = ScanRunner.Evaluate(CallerRule);

        await Assert.That(probes).IsEmpty()
            .Because(
                "a caller must not decide 'is this pair usable?' by testing the two halves "
                + "against each other — that is the rule #598 removed, and the two spellings "
                + "already disagreed with each other: " + string.Join(", ", probes)
                + ". Ask the value instead (`is { } reference` / `HasNoValue`); the halves of a "
                + "ModelRef cannot come apart. See issue #598.");
    }

    // ── Non-vacuity ──────────────────────────────────────────────────────────

    [Test]
    public async Task CallerRule_ReachesEveryFileItClaimsToPolice()
    {
        List<string> discovery = ScanRunner.CheckDiscovery(CallerRule);

        await Assert.That(discovery).IsEmpty()
            .Because(
                "the scan is rooted at named files; if one was renamed or moved the rule above "
                + "silently polices nothing. Point the scope at the new homes. "
                + "#453 is the proof that this check earns its place: the producer was missing from "
                + "the list, so the rule passed over a file that still held the derivation. "
                + string.Join("; ", discovery));
    }

    /// <summary>
    ///     #453: the producer is genuinely scanned, which is the fact the fix
    ///     depends on. A "the file is listed" assertion would be satisfied by a
    ///     path that is listed and then excluded, so the planted dotted spelling —
    ///     the exact line the pre-fix adapter carried — is required to fire through
    ///     the SAME rule.
    /// </summary>
    [Test]
    public async Task CallerRule_ScansTheProducer_AndTheProbeWouldFireThere()
    {
        // The real file, today: no probe, because the qualification moved to
        // ModelRef.Qualify. This is the assertion that the fix landed.
        List<string> live = ScanRunner.EvaluateOver(
            ScanRunner.ReadSources([ProducerFile]), CallerRule);

        await Assert.That(live).IsEmpty()
            .Because(
                "the adapter is where the half-pair test used to live; it must no longer decide "
                + "whether a provider/model pair is whole. ModelRef.Qualify is the single answer. "
                + string.Join(", ", live));

        // And the rule is live on that file, proven by the planted dotted control:
        // if the path were silently skipped this would read 0 and the assertion
        // above would be free.
        List<string> failures = ScanRunner.CheckControls(CallerRule);

        await Assert.That(failures).IsEmpty()
            .Because(
                "the matcher must still fire on the pre-fix adapter line, or the assertion above "
                + "cannot distinguish 'the derivation is gone' from 'the producer is not scanned'. "
                + string.Join("; ", failures));
    }

    [Test]
    public async Task Matcher_FiresOnTheKnownSpellings_AndStaysSilentOtherwise()
    {
        // The real pre-fix spellings must fire and the legal neighbours must stay
        // silent. The snippets live on CallerRule.Controls, so the control drives
        // the REAL matcher rather than a second implementation of it — including
        // the QUALIFIED spelling, verbatim as the adapter wrote it, which the
        // pre-#453 pattern could not match across a dot.
        List<string> failures = ScanRunner.CheckControls(CallerRule);

        await Assert.That(failures).IsEmpty()
            .Because(
                "the && and || spellings, both operand orders, the WhiteSpace variant, the dotted "
                + "member access and the trailing-comment line must all fire; the whole-value test, "
                + "the lone per-field guard, the unrelated strings and the comment lines must stay "
                + "silent. A guard that cannot see the spelling that actually exists is not a guard. "
                + string.Join("; ", failures));
    }

    /// <summary>
    ///     Every baseline row states why it is tolerated, in the row itself. Vacuous
    ///     while the table is empty, and deliberately so: it is wired from the first
    ///     row so the first row cannot skip the argument.
    /// </summary>
    [Test]
    public async Task Baseline_Rows_AllHaveReasons()
    {
        List<string> failures = ScanRunner.CheckReasons(CallerRule);

        await Assert.That(failures).IsEmpty().Because(string.Join("\n", failures));
    }

    /// <summary>
    ///     Every baseline row must still correspond to a real hit, so the table
    ///     cannot rot into a blanket permission: fix the code without deleting the
    ///     row and this fails.
    /// </summary>
    [Test]
    public async Task Baseline_Rows_Are_Not_Stale()
    {
        List<string> stale = ScanRunner.StaleBaselineKeys(
            CallerRule, ScanRunner.ReadSources(ScanRunner.ScopeFiles(CallerRule)));

        await Assert.That(stale).IsEmpty()
            .Because("a baseline row with no violation behind it is a permission for a "
                + "problem that no longer exists: " + string.Join(", ", stale));
    }
}
