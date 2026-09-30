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
    // fixed above and the pattern here. `Matcher_...` below now plants the real
    // dotted spelling so neither gap can reopen silently.
    private static readonly Regex HalfPairProbe = new(
        """
        string\.IsNullOr(?:Empty|WhiteSpace)\s*\(\s*[\w.]*\w*(?:[Pp]rovider|[Mm]odel)\w*\s*\)\s*(?:&&|\|\|)\s*(?:!\s*)?string\.IsNullOr(?:Empty|WhiteSpace)\s*\(\s*[\w.]*\w*(?:[Pp]rovider|[Mm]odel)\w*\s*\)
        """,
        RegexOptions.Compiled);

    /// <summary>A file, a 1-based line number, and the offending line.</summary>
    private sealed record ProbeSite(string RelativePath, int Line, string Text);

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
        string root = RequireRepoRoot();
        List<ProbeSite> probes = [.. ConsumerFiles.SelectMany(f => FindProbes(root, f))];

        await Assert.That(probes.Count).IsEqualTo(0)
            .Because(
                "a caller must not decide 'is this pair usable?' by testing the two halves "
                + "against each other — that is the rule #598 removed, and the two spellings "
                + "already disagreed with each other: " + Describe(probes)
                + ". Ask the value instead (`is { } reference` / `HasNoValue`); the halves of a "
                + "ModelRef cannot come apart. See issue #598.");
    }

    // ── Non-vacuity ──────────────────────────────────────────────────────────

    [Test]
    public async Task CallerRule_ReachesEveryFileItClaimsToPolice()
    {
        string root = RequireRepoRoot();
        var missing = new List<string>();

        foreach (string file in ConsumerFiles)
        {
            if (!File.Exists(Path.Combine(root, file)))
            {
                missing.Add(file);
            }
        }

        await Assert.That(missing).IsEmpty()
            .Because(
                "the scan is rooted at named files; if one was renamed or moved the rule above "
                + "silently polices nothing. Point these at the new homes: " + string.Join(", ", missing)
                + ". #453 is the proof that this check earns its place: the producer was missing from "
                + "the list, so the rule passed over a file that still held the derivation.");
    }

    /// <summary>
    ///     #453: the producer is genuinely scanned, which is the fact the fix
    ///     depends on. A "the file is listed" assertion would be satisfied by a
    ///     path that is listed and then excluded, so this plants the exact line
    ///     the pre-fix adapter carried and requires the SAME scanner to catch it.
    /// </summary>
    [Test]
    public async Task CallerRule_ScansTheProducer_AndTheProbeWouldFireThere()
    {
        string root = RequireRepoRoot();
        const string producer = "apps/Harbor.App.Avalonia/Services/CommonConfigReaderAdapter.cs";

        // The real file, today: no probe, because the qualification moved to
        // ModelRef.Qualify. This is the assertion that the fix landed.
        await Assert.That(FindProbes(root, producer).Count).IsEqualTo(0)
            .Because(
                "the adapter is where the half-pair test used to live; it must no longer decide "
                + "whether a provider/model pair is whole. ModelRef.Qualify is the single answer.");

        // And the scanner is live on that file, proven by planting the exact
        // pre-fix spelling. If the path were silently skipped this would read 0
        // and the assertion above would be free.
        List<ProbeSite> planted = FindProbes(root, producer)
            .Concat(Scan(["if (string.IsNullOrEmpty(cfg.DefaultProvider) || string.IsNullOrEmpty(cfg.DefaultModel))"])
                .Select(i => new ProbeSite(producer, i + 1, "(planted control)")))
            .ToList();

        await Assert.That(planted.Count).IsGreaterThan(0)
            .Because(
                "the matcher must still fire on the pre-fix adapter line, or the assertion above "
                + "cannot distinguish 'the derivation is gone' from 'the producer is not scanned'.");
    }

    [Test]
    public async Task Matcher_FiresOnTheKnownSpellings_AndStaysSilentOtherwise()
    {
        // The two real pre-fix spellings, in both operand orders.
        await Assert.That(Scan(["if (!string.IsNullOrEmpty(providerId) && !string.IsNullOrEmpty(modelId))"]).Count)
            .IsGreaterThan(0)
            .Because("the && spelling is CreateDefaultAsync's; the matcher must see it");
        await Assert.That(Scan(["if (string.IsNullOrEmpty(providerId) || string.IsNullOrEmpty(modelId))"]).Count)
            .IsGreaterThan(0)
            .Because("the || spelling is RebindFromCommonConfigAsync's; the matcher must see it");
        await Assert.That(Scan(["if (string.IsNullOrEmpty(model) || string.IsNullOrEmpty(provider))"]).Count)
            .IsGreaterThan(0)
            .Because("operand order is an implementation detail, not a distinction the rule draws");
        await Assert.That(Scan(["if (string.IsNullOrWhiteSpace(modelId) && string.IsNullOrWhiteSpace(providerId))"]).Count)
            .IsGreaterThan(0)
            .Because("the same rule with the other emptiness predicate is still this rule");

        // #453: the QUALIFIED spelling, verbatim as the adapter wrote it. The
        // pre-#453 pattern was `\w*[Pp]rovider\w*`, which cannot match across a
        // dot — so this line, the very one the rule exists to forbid, was
        // invisible to it. If the pattern is ever narrowed back, this fails.
        await Assert.That(Scan(["if (string.IsNullOrEmpty(cfg.DefaultProvider) || string.IsNullOrEmpty(cfg.DefaultModel))"]).Count)
            .IsGreaterThan(0)
            .Because(
                "this is CommonConfigReaderAdapter:48 as it stood — dotted member access, which the "
                + "original pattern could not match. A guard that cannot see the spelling that "
                + "actually exists is not a guard; the pattern now accepts a qualified name, and "
                + "this control is what holds it to that");

        // The post-fix spelling, and near-misses that are somebody else's problem.
        await Assert.That(Scan(["if (configured is { } fromConfig)"]).Count).IsEqualTo(0)
            .Because("a test on the whole value is the fix, not a violation");
        await Assert.That(Scan(["if (string.IsNullOrEmpty(providerId) && !string.IsNullOrEmpty(modelId))"]).Count)
            .IsGreaterThan(0)
            .Because("a provider half tested against a model half is this rule whichever way it is negated");
        await Assert.That(Scan(["if (string.IsNullOrEmpty(providerId)) return;"]).Count).IsEqualTo(0)
            .Because("a lone per-field guard is a different question and stays legal; the rule needs the operator");
        await Assert.That(Scan(["if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(title))"]).Count)
            .IsEqualTo(0)
            .Because("two unrelated optional strings are not a provider/model pair");
        await Assert.That(Scan(["// if (!string.IsNullOrEmpty(providerId) && !string.IsNullOrEmpty(modelId))"]).Count)
            .IsEqualTo(0)
            .Because("comment lines are prose about the old code, not a live probe");
        await Assert.That(Scan(["/// <c>string.IsNullOrEmpty(p) || string.IsNullOrEmpty(m)</c> in the docs."]).Count)
            .IsEqualTo(0)
            .Because("doc-comment lines are prose too");
        await Assert.That(Scan(["    if (!string.IsNullOrEmpty(providerId) && !string.IsNullOrEmpty(modelId)) // trailing"]).Count)
            .IsGreaterThan(0)
            .Because("a code line with a trailing comment is still a code line");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string RequireRepoRoot()
    {
        string? root = RepoPaths.RepoRoot;
        return root ?? throw new InvalidOperationException(
            "Harbor.slnx not found above " + AppContext.BaseDirectory
            + " — this guard walks the repository and cannot run from a published test host.");
    }

    private static string Describe(IReadOnlyList<ProbeSite> sites)
        => sites.Count == 0 ? "(none)" : string.Join(", ", sites.Select(s => s.RelativePath + ":" + s.Line));

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
    ///     skipped — the same line-level heuristic the sibling #678 guard uses, and
    ///     with the same bound: a probe split across a line break is missed, which
    ///     is why rule 1 (reflection) carries the load and rule 2 only narrows it.
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
