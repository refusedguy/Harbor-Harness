// DefaultModelDocClaimTests.cs — #649: the documentation named the free model
// as `kilocode/kilo-auto/free` in five places while providers/kilocode.json has
// declared `tencent/hy3:free` all along. Nobody noticed for a long time, and the
// reason is structural: ci.yml ignores `**.md` and `docs/**` on purpose, and the
// docs gate (docs.yml: selftest / links / lint) checks links, anchors, encoding,
// headings and fence balance — it never checks whether a stated FACT is still
// true. So the doc was not wrong in a way any existing gate could see: the gate
// exists, and its subject is not what the gate inspects. This file is that gate.
//
// The scoping is the whole design, so it is worth stating plainly. A provider
// directory legitimately contains non-default model ids: openrouter's default is
// `anthropic/claude-3.5-sonnet`, and fireworks' is a four-segment
// `accounts/fireworks/models/...` path. A guard that flagged "this file mentions
// some model id" would either be wrong on every such mention or have to be
// narrowed until it matched nothing. So this test does NOT scan for provider/model
// tokens. It scans for four specific SHAPES OF CLAIM — an export line, a
// documented status line, and two prose forms naming the free model — and checks
// only those against the declared default. Everything else in the markdown is out
// of scope on purpose.
//
// WHY THESE THREE FILES
// ---------------------
// AGENTS.md, CLAUDE.md and README.md are the root documents a reader opens, and
// between them they carry the commands a reader will actually run and the sample
// output they will diff a real run against. CHANGELOG.md is deliberately NOT
// included even though it also contains `kilo-auto/free`: a changelog is a record
// of what happened at a past commit, and demanding that history keep agreeing with
// today's default would make the file a lie to fix. Do not "fix" the changelog to
// make a guard green.
//
// NON-VACUITY, IN BOTH DIRECTIONS
// -------------------------------
// A guard that matches nothing is indistinguishable from a guard that is broken,
// and a broken guard is worse than no guard because it is believed. Five
// mechanisms below, all of which go RED rather than quietly passing:
//
//   1. `EveryClaimShape_IsStillPresentInTheRootDocs` requires each of the four
//      shapes to be found AT LEAST ONCE against the real root docs — counted per
//      shape, never as one combined total, because a combined total is satisfied
//      by a single shape matching everything while three shapes match nothing.
//   2. The same test puts a CEILING on each shape. A shape that fires on every
//      file is as useless as one that fires on none: it produces so much noise
//      that a real violation is lost in it. Both ends are asserted, so neither
//      "matches nothing" nor "matches everything" passes.
//   3. That test also asserts the three root docs still EXIST, because a
//      renamed or deleted root doc shrinks the scanned set and the guard would
//      then report the docs clean on the strength of reading fewer of them.
//   4. A claim is counted toward (1) only if the rule actually JUDGED it — i.e.
//      only if a `providers/<id>.json` exists for the id it names. A count
//      satisfied by claims the rule skipped would make (1) look green while
//      nothing is being compared to anything.
//   5. `Matcher_FiresOnAStaleClaim_InEveryShape` plants the stale text and
//      requires all four shapes to flag it, which is also the only thing that
//      exercises the prefix-tolerant comparison the prose shape needs.
//
// Also note `RequireRepoRoot` THROWS when the checkout cannot be found. An early
// `return` there would turn "the guard could not find the files it polices" into
// a silent pass — the same vacuity this file exists to prevent.
//
// STATUS: never compiled. Local dotnet builds are forbidden in this repository
// (8 concurrent agents on a 7 GB box), so CI is the only thing that has ever run
// this code, and the first CI run is the first build. Treat the C# as unverified
// until then; the first build to touch it may find a genuine compile error, and
// that is the point of sending it rather than a defect to reason about locally.
//
// WHAT A REVIEWER ALREADY CHANGED, AND WHY
// ----------------------------------------
// The first draft of this file carried three defects, all of which made it read
// stronger than it was. They are recorded here because a guard whose weaknesses
// are erased is a guard nobody can audit later:
//
//   1. Non-vacuity was incomplete. The draft required only that a combined
//      `shapeHits` list be non-empty, and nothing asserted that all FOUR shapes
//      occur in the real docs. `FreeModelProse` was the one that could silently
//      match nothing, because it demands a specific wording that no rule requires
//      anyone to use — and if it matched nothing, the guard was quietly weaker
//      than it read. FIXED: per-shape counts against the real docs, `>= 1` each.
//   2. `shapeHits` was appended on a CORRECT claim too, and only non-emptiness
//      was checked, so a shape firing on every file passed exactly as happily as
//      one firing once. Together with (1) the assertion proved much less than it
//      appeared to. FIXED: the count is per shape and bounded above as well as
//      below, and the message reports the breakdown so the numbers are visible.
//   3. The draft branched on `shape == FreeModelProse`, comparing Regex
//      INSTANCES BY REFERENCE. It worked only because both sides were the same
//      static field; reorder the array or add a shape and the branch silently
//      misroutes a claim to the wrong comparison. FIXED: the branch reads a
//      `ModelOmitsProviderPrefix` flag carried by the entry, so it cannot depend
//      on which object happens to be in the array.
//
// KNOWN LIMITATIONS — stated, not hidden
// --------------------------------------
// * This is a FLOOR, not a ceiling. A fifth prose shape invented in a later doc
//   edit ("the verified model for kilocode is X") is not covered until somebody
//   adds it to `ClaimShape`. The file header above is why the next person should.
// * `FreeModelProse` rests on ONE sentence in AGENTS.md and demands a specific
//   wording. If that sentence is reworded the guard goes RED, which is the
//   intended behaviour — the shape disappearing is a decision to make, not an
//   accident to absorb — but it does mean this is the shape most likely to need
//   attention.
// * `docs/` is out of scope, and measurably so: the export-line shape alone
//   matches 19 times under `docs/`, which is why widening the scan is a decision
//   about the ceiling rather than a one-word change. Known instances of the same
//   drift outside this guard's scope were reported to the owner rather than fixed
//   here.
//
// WHAT THIS GUARD CANNOT DO (#937) — and the one thing it now also checks
// ------------------------------------------------------------------------
// Everything above compares a document against `providers/<id>.json`. That
// makes the JSON the ORACLE, and on #937 the oracle is the thing under
// dispute: `tencent/hy3:free` is what the file declares, and the provider's own
// catalogue does not serve it (measured 2026-10-01, unauthenticated GET on the
// `modelsUrl` this very file reads `providers/*.json` from — see
// `docs/notes/kilocode-default-model-probe.md`). So this guard was green on a
// value that a single HTTP request disproves. Checking doc-vs-JSON answers
// "do the copies agree", never "is the shared value real", and no amount of
// widening the document set changes that. Liveness needs the network; this is
// an offline guard, so it does not pretend.
//
// What it CAN do without knowing the answer is check that the copies agree
// with EACH OTHER on the paths that are actually EXECUTED. The live evals
// runner reads `evals/profiles/*.json` — `model` and `harbor.env.HARBOR_MODEL` —
// and no gate in this repository compared either against anything. That is why
// the #937 fork survived twenty days: the two paths disagreed, and every guard
// in the tree read only one of them. `LiveEvalProfiles_AgreeWith_TheDeclaredDefault`
// below closes that, and it is deliberately value-AGNOSTIC: it does not say
// which model is correct, only that a shipped profile and the shipped provider
// file must not name different ones. It therefore passes whoever wins.

using System.Text.Json;
using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Every place the root markdown states which model a provider defaults to
///     must agree with <c>providers/&lt;id&gt;.json</c>.
/// </summary>
public sealed class DefaultModelDocClaimTests
{
    /// <summary>Docs at the repository root. These are what a reader actually opens.</summary>
    private static readonly string[] RootDocs = ["AGENTS.md", "CLAUDE.md", "README.md"];

    /// <summary>
    ///     Upper bound on how many judged claims a single shape may produce.
    ///     Currently the busiest shape finds 3 (the export line, across AGENTS.md
    ///     and README.md). Eight leaves room for ordinary doc growth while still
    ///     catching a shape that degenerated into "matches every line" — which is
    ///     not a working guard, it is noise.
    /// </summary>
    private const int MaxJudgedClaimsPerShape = 8;

    /// <summary>
    ///     One shape of documented claim.
    /// </summary>
    /// <param name="Name">How the shape is called in a failure message.</param>
    /// <param name="Pattern">
    ///     The matcher. Both capture groups are required: <c>id</c> is the
    ///     provider directory name, <c>model</c> is what the doc says to use.
    /// </param>
    /// <param name="ModelOmitsProviderPrefix">
    ///     True for a shape that, by its wording, quotes the model WITHOUT the
    ///     provider prefix — so the declared value and the documented one agree
    ///     when the documented one is a suffix of the declared one
    ///     (<c>tencent/hy3:free</c> vs <c>hy3:free</c>). This is a property OF
    ///     THE SHAPE, so it travels with the shape as data. It is deliberately
    ///     not a `shape == SomeStaticField` test: comparing Regex instances by
    ///     reference only works while the array happens to hold the same object
    ///     the other side names, and breaks silently the day it does not.
    /// </param>
    private sealed record ClaimShape(string Name, Regex Pattern, bool ModelOmitsProviderPrefix)
    {
        /// <summary>An instruction to run against a specific model.</summary>
        internal static ClaimShape Export { get; } = new(
            "export line",
            new(@"export\s+HARBOR_MODEL=(?<id>[a-z0-9\-]+)/(?<model>\S+)", RegexOptions.Compiled),
            ModelOmitsProviderPrefix: false);

        /// <summary>A documented expected-output line, which readers diff against a real run.</summary>
        internal static ClaimShape Status { get; } = new(
            "status line",
            new(@"^status:\s+(?<id>[a-z0-9\-]+)/(?<model>\S+)\s*\|",
                RegexOptions.Compiled | RegexOptions.Multiline),
            ModelOmitsProviderPrefix: false);

        /// <summary>Prose naming the free model of a provider, spelled <c>provider/model</c>.</summary>
        internal static ClaimShape FreeModel { get; } = new(
            "free-model prose",
            new(@"free model is\s+`(?<id>[a-z0-9\-]+)/(?<model>[^`]+)`",
                RegexOptions.Compiled | RegexOptions.IgnoreCase),
            ModelOmitsProviderPrefix: false);

        /// <summary>Prose of the form "the free <c>model</c> model" for a named provider.</summary>
        internal static ClaimShape FreeModelProse { get; } = new(
            "free-model prose (named provider)",
            new(@"provider is\s+\*\*(?<id>[A-Za-z0-9\-]+)\*\*\s+with the free\s+`(?<model>[^`]+)`\s+model",
                RegexOptions.Compiled),
            ModelOmitsProviderPrefix: true);
    }

    /// <summary>
    ///     The table the guard iterates. Named static members rather than a bare
    ///     list of regexes so that every use of a shape — including the positive
    ///     control — refers to it by identity-independent name.
    /// </summary>
    private static readonly ClaimShape[] Shapes =
    [
        ClaimShape.Export,
        ClaimShape.Status,
        ClaimShape.FreeModel,
        ClaimShape.FreeModelProse,
    ];

    /// <summary>A single claim the guard judged, and how it turned out.</summary>
    private sealed record ClaimSite(string Shape, string Doc, string ProviderId, string Model, string Declared);

    /// <summary>
    ///     The rule: no root doc may name a model for a provider we ship that
    ///     differs from that provider's declared <c>defaultModel</c>.
    /// </summary>
    [Test]
    public async Task DocumentedClaims_AgreeWith_TheProvidersDeclaredDefault()
    {
        string root = RequireRepoRoot();
        Dictionary<string, string> defaults = DeclaredDefaults(root);

        await Assert.That(defaults.Count).IsGreaterThan(10)
            .Because("the guard needs providers/*.json to compare against: with none, every claim "
                + "is unjudgeable and the rule below would be vacuously satisfied. Found "
                + defaults.Count + " provider file(s) carrying a defaultModel.");

        (List<ClaimSite> violations, _) = Judge(root, RootDocs);

        await Assert.That(violations).IsEmpty()
            .Because("a documented default model that disagrees with the provider file is a "
                + "command that does not match what actually runs. "
                + Describe(violations)
                + " Fix the DOC (it is the one that is stale) or change providers/<id>.json if the "
                + "default really moved — never both, and never leave them disagreeing.");
    }

    /// <summary>
    ///     The live evals profile must name the same model as the provider file.
    /// </summary>
    /// <remarks>
    ///     This is the check that was missing for #937, and it is the one that
    ///     could have caught the fork. <see cref="DocumentedClaims_AgreeWith_TheProvidersDeclaredDefault" />
    ///     reads the root markdown; this reads <c>evals/profiles/*.json</c>, which is what
    ///     <c>tools/Harbor.Evals</c> actually launches a run with — <c>EvalProfile.Model</c> and
    ///     <c>harbor.env.HARBOR_MODEL</c> both land in the child process's environment. A stale
    ///     id there is not a doc problem: the weekly evals run measures a different model
    ///     than the one every document promises, and reports green.
    ///     <para>
    ///     VALUE-AGNOSTIC BY CONSTRUCTION. Nothing here knows that <c>kilo-auto/free</c> is
    ///     right and <c>tencent/hy3:free</c> is wrong; it only knows they must not differ. So
    ///     this test goes green the moment the owner picks a winner and moves every site
    ///     together, and it stays green afterwards. A guard that encoded the current value
    ///     would instead have to be edited by the same commit that resolves the fork — which
    ///     is how the disputed value got pinned in the first place.
    ///     </para>
    ///     <para>
    ///     The two fields are checked separately on purpose. <c>model</c> and
    ///     <c>HARBOR_MODEL</c> are two independent copies of one claim in one file, and
    ///     <c>WorkspacePreparer</c> writes <c>model</c> into the attempt manifest while the
    ///     child process reads the env var. A profile where only one was updated would run one
    ///     model and report another.
    ///     </para>
    /// </remarks>
    [Test]
    public async Task LiveEvalProfiles_AgreeWith_TheDeclaredDefault()
    {
        string root = RequireRepoRoot();
        Dictionary<string, string> defaults = DeclaredDefaults(root);

        List<(string Profile, string Field, string Model)> claims = ReadLiveProfileClaims(root);

        // Non-vacuity, same property as the doc rule: a guard over zero claims is
        // indistinguishable from a guard over a path that moved.
        await Assert.That(claims.Count).IsGreaterThanOrEqualTo(2)
            .Because("evals/profiles/local.json declares a model in two places — 'model' and "
                + "'harbor.env.HARBOR_MODEL' — and both reach the runner. Reading fewer than two "
                + "means the profile moved, was deleted, or the reader stopped finding it, and "
                + "the rule below would then be satisfied by nothing. Found "
                + claims.Count + " claim(s).");

        List<string> violations =
        [
            .. claims.Where(c => !AgreesWithDeclared(c.Model, defaults))
                    .Select(c => c.Profile + " ['" + c.Field + "'] names '" + c.Model
                                 + "' but providers/" + SplitProvider(c.Model)
                                 + ".json declares defaultModel '"
                                 + defaults.GetValueOrDefault(SplitProvider(c.Model), "<no such provider>")
                                 + "'")
        ];

        await Assert.That(violations).IsEmpty()
            .Because("a shipped evals profile and the provider file disagree about which model a "
                + "run uses. That is not cosmetic: tools/Harbor.Evals passes this value to the CLI, "
                + "so the weekly baseline measures one model while every document promises another, "
                + "and the run stays green. " + string.Join(" | ", violations)
                + " Fix by moving BOTH the profile and providers/<id>.json in the same commit — "
                + "and re-read docs/notes/kilocode-default-model-probe.md first, which records why "
                + "the fork existed. Never make one side agree by editing only the other.");
    }

    /// <summary>
    ///     Every model claim the live evals runner will act on, from every shipped profile.
    /// </summary>
    /// <remarks>
    ///     Both copies are returned rather than judged here so the count assertion above can see
    ///     them, and so the failure message can name the field that drifted. An unparseable or
    ///     absent field is <em>not</em> reported by this method: a profile that stops declaring a
    ///     model is a different defect, and silently treating it as agreement here would let the
    ///     count assertion carry the weight alone.
    /// </remarks>
    private static List<(string Profile, string Field, string Model)> ReadLiveProfileClaims(string root)
    {
        var claims = new List<(string, string, string)>();
        string dir = Path.Combine(root, "evals", "profiles");
        if (!Directory.Exists(dir))
        {
            return claims;
        }

        foreach (string file in Directory.EnumerateFiles(dir, "*.json").OrderBy(x => x, StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));

            if (doc.RootElement.TryGetProperty("model", out JsonElement model)
                && model.ValueKind == JsonValueKind.String)
            {
                claims.Add((relative, "model", model.GetString()!));
            }

            if (doc.RootElement.TryGetProperty("harbor", out JsonElement harbor)
                && harbor.ValueKind == JsonValueKind.Object
                && harbor.TryGetProperty("env", out JsonElement env)
                && env.ValueKind == JsonValueKind.Object
                && env.TryGetProperty("HARBOR_MODEL", out JsonElement envModel)
                && envModel.ValueKind == JsonValueKind.String)
            {
                claims.Add((relative, "harbor.env.HARBOR_MODEL", envModel.GetString()!));
            }
        }

        return claims;
    }

    /// <summary>Provider half of a qualified <c>provider/model</c> reference.</summary>
    private static string SplitProvider(string qualified)
    {
        int slash = qualified.IndexOf('/');
        return slash < 0 ? qualified : qualified[..slash];
    }

    /// <summary>
    ///     Model half of a qualified reference, keeping every segment after the first slash.
    /// </summary>
    /// <remarks>
    ///     Deliberately the unlimited split #599 restored, NOT <c>Split('/')[1]</c>: a gateway
    ///     model id is routinely multi-segment (<c>kilo-auto/free</c>), and a bare index drops
    ///     everything after the first — the exact truncation that made a default install answer
    ///     <c>kilocode/hy3:free</c> while the constant promised <c>kilocode/tencent/hy3:free</c>.
    /// </remarks>
    private static string ModelHalf(string qualified)
    {
        int slash = qualified.IndexOf('/');
        return slash < 0 ? qualified : qualified[(slash + 1)..];
    }

    /// <summary>
    ///     Non-vacuity, part 1: every shape must still be found against the REAL
    ///     root docs, counted per shape.
    /// </summary>
    /// <remarks>
    ///     A combined "total claims found" figure is not this assertion. It is
    ///     satisfied by one shape matching everywhere while the other three match
    ///     nothing at all — which is precisely the state in which the guard stops
    ///     catching the bug it was written for and nobody notices, because the
    ///     test is still green. Each shape gets its own lower bound.
    /// </remarks>
    [Test]
    public async Task EveryClaimShape_IsStillPresentInTheRootDocs()
    {
        string root = RequireRepoRoot();

        // A root doc that is renamed or deleted shrinks the scanned set, and the
        // guard would report that the docs are clean because it read fewer of
        // them. The scan has to fail on the missing file, not on its absence.
        foreach (string doc in RootDocs)
        {
            await Assert.That(File.Exists(Path.Combine(root, doc))).IsTrue()
                .Because(doc + " is one of the documents this guard exists to police. If it was "
                    + "renamed, update RootDocs AND say so in the file header — a guard that quietly "
                    + "stopped reading a document is not a guard that got simpler.");
        }

        (List<ClaimSite> _, Dictionary<string, int> judged) = Judge(root, RootDocs);

        foreach (ClaimShape shape in Shapes)
        {
            int count = judged.GetValueOrDefault(shape.Name);

            await Assert.That(count).IsGreaterThan(0)
                .Because($"the {shape.Name} shape no longer occurs in any root doc, so the guard "
                    + "can no longer catch a stale value written in that shape. Either the docs were "
                    + "rewritten away from the shape, or the pattern no longer matches how they are "
                    + "written now. Re-anchor the pattern to the current wording, or drop the shape "
                    + "and say so in the header — do not leave it matching nothing.");

            // Non-vacuity, part 2: the other end. A shape that matches everywhere
            // is not a working guard either, it is a warning nobody reads.
            await Assert.That(count).IsLessThanOrEqualTo(MaxJudgedClaimsPerShape)
                .Because($"the {shape.Name} shape judged {count} claims in the root docs, above the "
                    + $"ceiling of {MaxJudgedClaimsPerShape}. That is the signature of a pattern that "
                    + "has stopped being specific — it will bury a real violation in noise until "
                    + "nobody runs it. Narrow the pattern.");
        }
    }

    /// <summary>
    ///     Non-vacuity, part 3, and the proof the guard is not theatre: plant the
    ///     stale value in ALL FOUR shapes and require all four to be flagged.
    /// </summary>
    /// <remarks>
    ///     The real docs are currently correct, so every assertion in the rule
    ///     above is satisfied by an empty violation list. That is what a correct
    ///     document looks like — it is also what a blind guard looks like, and
    ///     nothing in the rule itself tells the two apart. This test does: it
    ///     asserts each shape flags a value that is NOT the declared default, so
    ///     a green run of the rule means "the matcher fired and found nothing",
    ///     which is the only reading that carries information.
    /// </remarks>
    [Test]
    public async Task Matcher_FiresOnAStaleClaim_InEveryShape()
    {
        const string StaleModel = "kilo-auto/free";
        const string StaleModelHalf = "tencent/hy3:NOT-free";

        // One line per shape, all naming the same provider and all WRONG.
        // Written so that only the fourth line needs the prefix-tolerant
        // comparison — the shape that quotes the model without the provider
        // prefix. That is the branch the per-entry flag exists for, and this is
        // the only thing in the file that exercises it.
        string[] planted =
        [
            "export HARBOR_MODEL=kilocode/" + StaleModel,
            "status: kilocode/" + StaleModel + " | agent: code",
            "> The free model is `kilocode/" + StaleModel + "` — no card required.",
            "**E2E-verified** provider is **Kilocode** with the free `" + StaleModelHalf + "` model.",
        ];

        (List<ClaimSite> violations, Dictionary<string, int> judged) =
            Judge(RequireRepoRoot(), planted);

        // One per line: every shape caught the planted staleness.
        await Assert.That(violations.Count).IsEqualTo(planted.Length)
            .Because("every planted line names a model that is not providers/kilocode.json's "
                + "declared default, so every shape must flag it. Flagged: "
                + Describe(violations) + " A shape missing from that list is blind in the real docs "
                + "too, and the green rule above was not evidence of anything.");

        await Assert.That(judged.GetValueOrDefault(ClaimShape.FreeModelProse.Name)).IsEqualTo(1)
            .Because("the prefix-tolerant comparison is reached only through the shape's own "
                + "ModelOmitsProviderPrefix flag; if that flag stopped routing, the prose shape "
                + "would silently fall through to a strict equality it cannot satisfy");

        // The group values themselves, so a pattern that fires for the wrong
        // reason is distinguishable from one that fires correctly.
        Match prose = ClaimShape.FreeModelProse.Pattern.Match(planted[3]);
        await Assert.That(prose.Success).IsTrue();
        await Assert.That(prose.Groups["id"].Value).IsEqualTo("Kilocode");
        await Assert.That(prose.Groups["model"].Value).IsEqualTo(StaleModelHalf);
    }

    /// <summary>
    ///     The flag is load-bearing in the other direction too: a prose shape that
    ///     abbreviates the model to its last segment is still making the same
    ///     claim, and the guard must not report it.
    /// </summary>
    [Test]
    public async Task Matcher_AcceptsAProseShapeThatAbbreviatesTheModelPath()
    {
        const string Declared = "tencent/hy3:free";

        // "hy3:free" is the declared value's last segment: the same assertion,
        // written shorter. Only the shape that omits the provider prefix may be
        // compared this way.
        string[] abbreviated =
        [
            "**E2E-verified** provider is **Kilocode** with the free `hy3:free` model.",
        ];

        (List<ClaimSite> violations, _) = Judge(RequireRepoRoot(), abbreviated);

        await Assert.That(violations).IsEmpty()
            .Because("'" + Declared + "' ends with '/hy3:free', so a prose shape writing the short "
                + "form is stating the same model. Reported: " + Describe(violations));

        // And the same tolerance must NOT leak into a shape that spells the full
        // path, where a short form would be a different (and wrong) assertion.
        (List<ClaimSite> strict, _) = Judge(RequireRepoRoot(),
            ["export HARBOR_MODEL=kilocode/hy3:free"]);

        await Assert.That(strict).IsNotEmpty()
            .Because("the export line writes a path a reader would copy into HARBOR_MODEL, so "
                + "'kilocode/hy3:free' is a DIFFERENT model from the declared "
                + "'kilocode/tencent/hy3:free' and must be reported. If this is empty, the "
                + "prefix tolerance has leaked into a shape that is supposed to be strict.");
    }

    /// <summary>
    ///     The escape hatch is tested too. CLAUDE.md carries a placeholder example
    ///     naming a provider we do not ship, and it must not be reported: a guard
    ///     that fires on ids with no <c>providers/</c> directory is a guard whose
    ///     findings get suppressed, and then it catches nothing at all.
    /// </summary>
    [Test]
    public async Task Matcher_IgnoresClaimsAboutProvidersWeDoNotShip()
    {
        (List<ClaimSite> violations, Dictionary<string, int> judged) = Judge(RequireRepoRoot(),
            ["export HARBOR_MODEL=myllm/my-model-id"]);

        await Assert.That(violations).IsEmpty()
            .Because("'myllm' has no providers/myllm.json, so the doc is not contradicting "
                + "anything this repository ships. Reported: " + Describe(violations));

        // Unjudgeable is not the same as un-firing: the shape still matched.
        await Assert.That(judged.Count).IsEqualTo(0)
            .Because("a claim about an unknown provider must be skipped by the JUDGE, not "
                + "counted as evidence that the shape still works — otherwise the "
                + "per-shape non-vacuity count can be satisfied by claims the rule never examined");
    }

    /// <summary>
    ///     Non-vacuity for the live-profile rule: the same comparison, run against a
    ///     PLANTED fork, must flag it.
    /// </summary>
    /// <remarks>
    ///     <see cref="LiveEvalProfiles_AgreeWith_TheDeclaredDefault" /> reads a real file, so on a
    ///     consistent tree it returns an empty violation list — which is also what a broken
    ///     reader would return. This test is the difference between "found nothing" and "looked
    ///     and agreed", and it is also what keeps the rule from being pinned to today's value: it
    ///     plants a profile carrying a model the provider file does NOT declare, and requires the
    ///     comparison to catch it. The two helper functions are exercised directly rather than
    ///     through a temp directory, because writing a profile into the repository's own
    ///     <c>evals/profiles/</c> to make a test fail is the one thing this file must never do.
    /// </remarks>
    [Test]
    public async Task LiveProfileRule_FiresOnAPlantedFork()
    {
        string root = RequireRepoRoot();
        Dictionary<string, string> defaults = DeclaredDefaults(root);
        string declaredForKilo = defaults["kilocode"];
        string planted = "kilocode/definitely-not-the-declared-model";

        // A model the provider file does not declare is a violation ...
        await Assert.That(AgreesWithDeclared(planted, defaults)).IsFalse()
            .Because("'" + planted + "' is not providers/kilocode.json's declared default '"
                + declaredForKilo + "', so the live-profile rule must report it");

        // ... the declared value itself is not, which is the half that would break if this rule
        // were written to pin today's value instead of comparing.
        await Assert.That(AgreesWithDeclared("kilocode/" + declaredForKilo, defaults)).IsTrue()
            .Because("the declared default must satisfy the rule, or the rule is a constant "
                + "rather than a comparison");

        // And the multi-segment id that #599 is about splits correctly. A router-style
        // model id must not be truncated to its first segment on either side of the
        // comparison, or the two sides would agree for the wrong reason. The input is a
        // QUALIFIED reference: ModelHalf splits on the FIRST slash, which is the provider
        // boundary — handing it a bare model id would drop everything before that slash and
        // is the caller's error, not a property of the helper.
        await Assert.That(SplitProvider("kilocode/kilo-auto/free")).IsEqualTo("kilocode")
            .Because("the provider half is everything before the first slash");
        await Assert.That(ModelHalf("kilocode/kilo-auto/free")).IsEqualTo("kilo-auto/free")
            .Because("the model half is everything after the FIRST slash, kept whole — the "
                + "truncation DefaultModelSingleSourceTests exists to prevent. A bare "
                + "Split('/')[1] would have returned 'free' here and matched nothing.");

        // The failure mode this guards is a WRONG-SIDE match: provider and model halves
        // swapped still 'agree' if both sides are computed the same wrong way, so the two
        // helpers must disagree on the boundary rather than agree by construction.
        foreach (string qualified in new[] { "kilocode/kilo-auto/free", "openrouter/anthropic/claude-3.5-sonnet" })
        {
            await Assert.That(SplitProvider(qualified) + "/" + ModelHalf(qualified)).IsEqualTo(qualified)
                .Because("splitting '" + qualified + "' and rejoining must be the identity — "
                    + "otherwise the two halves are computed inconsistently and a match between "
                    + "them means nothing");
        }
    }

    /// <summary>
    ///     The comparison <see cref="LiveEvalProfiles_AgreeWith_TheDeclaredDefault" /> makes,
    ///     factored out so the planted control above exercises the SAME code path.
    /// </summary>
    private static bool AgreesWithDeclared(string qualified, Dictionary<string, string> defaults)
    {
        string id = SplitProvider(qualified);
        return defaults.TryGetValue(id, out string declared) && declared == ModelHalf(qualified);
    }

    /// <summary>
    ///     Runs every shape over every document, and splits the result into the
    ///     claims the guard CONDEMNED and the per-shape count of claims it
    ///     actually JUDGED.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="docs">
    ///     Either repo-relative paths under <paramref name="root" />, or literal
    ///     document text — the positive control plants text rather than naming a
    ///     file, so that it never depends on what the real docs happen to say. The
    ///     two are told apart by whitespace: a planted line always contains some
    ///     (they are sentences or commands), and a document name in
    ///     <see cref="RootDocs" /> never does. Guessing "path, or text if no such
    ///     file" instead would scan a missing file's NAME as if it were its
    ///     content, which is a way to stay green on a root doc that vanished.
    /// </param>
    private static (List<ClaimSite> Violations, Dictionary<string, int> Judged) Judge(
        string root,
        IReadOnlyList<string> docs)
    {
        Dictionary<string, string> defaults = DeclaredDefaults(root);
        List<ClaimSite> violations = [];
        var judged = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (string doc in docs)
        {
            bool isLiteralText = doc.Any(char.IsWhiteSpace);
            string text = isLiteralText
                ? doc
                : File.ReadAllText(Path.Combine(root, doc));

            foreach (ClaimShape shape in Shapes)
            {
                foreach (Match match in shape.Pattern.Matches(text))
                {
                    string id = match.Groups["id"].Value;
                    string model = match.Groups["model"].Value;

                    if (!TryResolveDeclared(defaults, id, out string canonicalId, out string declared))
                    {
                        // No providers/<id>.json, so nothing is being contradicted.
                        // Deliberately NOT counted as judged: the per-shape
                        // non-vacuity count must only be satisfied by claims the
                        // rule genuinely examined.
                        continue;
                    }

                    judged[shape.Name] = judged.GetValueOrDefault(shape.Name) + 1;

                    // The branch reads a flag carried by the entry, never the
                    // identity of a Regex instance.
                    bool agrees = model == declared
                        || (shape.ModelOmitsProviderPrefix
                            && declared.EndsWith("/" + model, StringComparison.Ordinal));

                    if (!agrees)
                    {
                        violations.Add(new ClaimSite(shape.Name, doc, canonicalId, model, declared));
                    }
                }
            }
        }

        return (violations, judged);
    }

    private static string RequireRepoRoot()
    {
        string? root = RepoPaths.RepoRoot;
        return root ?? throw new InvalidOperationException(
            "Harbor.slnx not found above " + AppContext.BaseDirectory
            + " — this guard walks the repository and cannot run from a published test host. "
            + "Returning quietly here would be worse than throwing: it would report 'no stale "
            + "claims' without having read a single document.");
    }

    private static string Describe(IReadOnlyList<ClaimSite> violations)
        => violations.Count == 0
            ? "(none)"
            : string.Join(" | ", violations.Select(v =>
                $"{v.Doc} [{v.Shape}] claims '{v.ProviderId}/{v.Model}' but providers/{v.ProviderId}.json "
                + $"declares defaultModel '{v.Declared}'"));

    /// <summary>
    ///     Resolves a provider id as the document spelled it against the ids
    ///     actually declared in <c>providers/*.json</c>.
    /// </summary>
    /// <remarks>
    ///     A document may capitalise the id — the prose shape writes
    ///     <c>**Kilocode**</c> — so the comparison is case-insensitive. But the
    ///     failure message has to name a file the reader can actually open, so
    ///     the CANONICAL id is returned alongside the declared model. Echoing
    ///     the document's own spelling prints <c>providers/Kilocode.json</c>,
    ///     which is not a file; the first red CI run of this guard did exactly
    ///     that.
    /// </remarks>
    private static bool TryResolveDeclared(
        Dictionary<string, string> defaults,
        string id,
        out string canonicalId,
        out string declaredModel)
    {
        foreach (KeyValuePair<string, string> entry in defaults)
        {
            if (string.Equals(entry.Key, id, StringComparison.OrdinalIgnoreCase))
            {
                canonicalId = entry.Key;
                declaredModel = entry.Value;
                return true;
            }
        }

        canonicalId = id;
        declaredModel = string.Empty;
        return false;
    }

    /// <summary>provider id → <c>defaultModel</c>, straight from the shipped JSON.</summary>
    private static Dictionary<string, string> DeclaredDefaults(string root)
    {
        // Ordinal, NOT OrdinalIgnoreCase: the key must stay exactly as the file
        // spells it, so TryResolveDeclared can hand back the real file name. The
        // case-insensitive part of the lookup belongs in the resolver, where it
        // is visible, rather than baked invisibly into the dictionary.
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        string dir = Path.Combine(root, "providers");
        if (!Directory.Exists(dir))
        {
            return result;
        }

        foreach (string file in Directory.EnumerateFiles(dir, "*.json").OrderBy(x => x, StringComparer.Ordinal))
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
            if (!doc.RootElement.TryGetProperty("defaultModel", out JsonElement model)
                || model.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            if (doc.RootElement.TryGetProperty("id", out JsonElement id) && id.ValueKind == JsonValueKind.String)
            {
                result[id.GetString()!] = model.GetString()!;
            }
        }

        return result;
    }
}
