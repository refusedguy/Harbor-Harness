// ProviderIdDispatchRule.cs — the guard for issue #560.
//
// WHY THIS FILE EXISTS
// --------------------
// Issue #560 asked whether Harbor still edits provider-keyed tables when a new
// LLM provider is added. Taking the claim apart file by file:
//
//   * `ProviderPresets` — a 13-entry hand-written table. ALREADY GONE: #580
//     replaced it with `ProviderPresetCatalog`, which projects the same
//     `providers/*.json` the registration path reads. The issue text predates
//     that fix.
//   * `tests/Harbor.Architecture.Tests/*` — 21 allow-list entries. Those fail
//     LOUDLY: `Matrix_CoversExactlyTheSrcInventory` goes red the moment a new
//     `src/` project is unlisted. The issue's own second pass concedes this is a
//     property of the layer matrix, not a defect.
//   * `ProviderCompatFlags._all` — a two-entry registry, covered by its own
//     two-way guard in `tests/Harbor.Providers.Tests/ProviderCompatFlagTests.cs`.
//   * `JsonProviderDiscovery`'s `apiType != "openai-compatible"` — a CAPABILITY
//     check ("can this path build a client at all?"), not a provider-keyed
//     dispatch. The `anthropic`/`openai` skip beside it mirrors the composition
//     root in `ProviderFactories.CreateProviderRegistry`, which registers exactly
//     those three ids natively; the JSON path must not double-register them.
//
// What was left, and genuinely wrong, was one thing: `OnboardingViewModel` mapped
// a provider id to its picker glyph through a 13-arm switch expression ending in a
// generic-wrench default — one arm per bundled provider, each arm a string literal,
// the whole thing reachable only by editing that file. It was the §OOP-002 shape
// again, and SILENT: a 14th provider rendered the wrench, no test named a single
// arm, and nothing failed. `Icon` is now a field on the provider's own
// `providers/<id>.json`, projected through `ProviderPresetCatalog` exactly like
// `displayName` and `priority` — so adding a provider touches no existing C#.
//
// The same Open/Closed violation §OOP-002 removed from the request-payload path,
// reappearing one layer out. It was SILENT: a 14th provider renders the generic
// wrench, no test named a single arm, and nothing fails. `Icon` is now data — a
// field on the provider's own `providers/<id>.json`, projected through
// `ProviderPresetCatalog` exactly like `displayName` and `priority` — so adding a
// provider touches no existing C# at all.
//
// THE RULE
// --------
// No product file may dispatch on a bundled provider id in a switch or
// expression arm. The arm form is what makes this a *table*: `"kilocode" => …`
// is one row of a provider-keyed map, and editing it to add a provider is the
// Open/Closed violation. It is deliberately narrower than "the id appears
// anywhere" — a single incidental reference (`ProviderPresets.Find("ollama")`) is
// a lookup, not a table, and flagging it would make the rule noise.
//
// THE RULE IS VALUE-AGNOSTIC
// --------------------------
// The ids to hunt for are read out of `providers/*.json` at test time, so adding
// a provider re-points the guard instead of silently disarming it. A guard pinned
// to a hard-coded id list stops guarding the day a provider is added — which is
// precisely the bug this file exists to prevent.
//
// NON-VACUITY
// -----------
// A source scan that matches nothing is indistinguishable from a source scan that
// is broken, and a broken guard is worse than none because it is believed. Two
// tests close that: discovery must find a real file set containing the file that
// used to hold the table, and the same matcher must fire on a planted arm while
// staying silent on a comment, on a non-arm use of the same id, and on an arm
// keyed by something that is not a provider.
//
// KNOWN LIMITATION — stated, not hidden
// ------------------------------------
// The scan is a line-level regex over code lines; it is not a C# parser. An arm
// written across lines (`"kilocode"\n    => …`) is missed, and an id inside a
// multi-line block comment is prose this rule does not govern. The limitation is
// bounded by construction: the only way to bring the table back is to make the
// arm DO work, and arms are string literals on code lines.
//
// MECHANISM (#1086, step 2, conveyor)
// -----------------------------------
// The rule below is a ScanRule with a CustomParse: the verdict needs the
// catalogue ids (read out of `providers/*.json`, so the guard tracks the
// catalogue instead of a stale copy) plus the literal-then-arrow arm test, which
// no single line regex expresses. So the rule plugs the unchanged arm finder
// through the Func-overload; the `Forbidden` row documents the arm shape and
// carries the failure text. Enumeration, baseline and the control/discovery
// verdicts are ScanRunner's; this file keeps the issue prose and the test names.

using System.Collections.Frozen;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Issue #560: no product file may dispatch on a bundled provider id in a
///     switch or expression arm. Per-provider presentation and behaviour is
///     declared by <c>providers/&lt;id&gt;.json</c> or by a registered
///     <c>IProviderCompatFlag</c>, never by a hand-maintained table in C#.
/// </summary>
public sealed class ProviderIdDispatchRule
{
    private const string SubId = "PROVIDER-ID-DISPATCH-ARM";

    /// <summary>
    ///     Files allowed to carry a provider-keyed arm, each with the reason it
    ///     cannot be data instead. An exemption is a decision, not an oversight —
    ///     the reason is printed in the failure message, the same way
    ///     <c>DefaultModelSingleSourceTests</c> documents its exemptions.
    /// </summary>
    /// <remarks>
    ///     Empty on purpose. The one file that used to need an entry
    ///     (<c>OnboardingViewModel.cs</c>, the 13-arm icon table) now reads its
    ///     glyph from the provider's own JSON, so nothing is exempt today. The list
    ///     stays because the escape hatch must exist before someone needs it —
    ///     a rule with no documented way to except a file gets worked around in
    ///     worse ways.
    /// </remarks>
    private static readonly Dictionary<string, string> DispatchExemptions = new(StringComparer.Ordinal);

    /// <summary>
    ///     The file that used to hold the hand-written table, named so the
    ///     non-vacuity check can prove discovery still sees it. Not an exemption.
    /// </summary>
    private const string FormerTableFileRelativePath =
        "src/Harbor.Desktop.Abstractions/ViewModels/OnboardingViewModel.cs";

    /// <summary>
    ///     One C# <c>string</c> literal on a line, captured with its value. Matched
    ///     rather than searched as raw text so a provider id mentioned in prose
    ///     outside quotes is not mistaken for a table row.
    /// </summary>
    private static readonly Regex QuotedLiteral = new(
        @"""(?<value>(?:[^""\\]|\\.)*)""",
        RegexOptions.Compiled);

    /// <summary>
    ///     The <c>=&gt;</c> that turns a literal into a switch/expression arm. Anchored
    ///     at the start of the text following the literal, so the arrow must be the
    ///     next thing on the line.
    /// </summary>
    private static readonly Regex ArmArrow = new(@"^\s*=>", RegexOptions.Compiled);

    /// <summary>
    ///     The rule as data: one documented arm shape, a catalogue-aware parser,
    ///     planted controls, a floor. Built lazily because the ids — and the planted
    ///     control keyed on one of them — are read out of <c>providers/*.json</c>.
    /// </summary>
    private static readonly Lazy<ScanRule> LazyRule = new(BuildRule);

    private static ScanRule Rule => LazyRule.Value;

    private static ScanRule BuildRule()
    {
        string root = RequireRepoRoot();
        IReadOnlySet<string> ids = BundledProviderIds(root);
        // Deterministic pick: the guard must not depend on set iteration order for
        // which id it plants, so take the ordinal-first one explicitly. The set is
        // non-empty by construction (BundledProviderCatalogue_IsDiscoverable_
        // AndNonTrivial asserts the directory walk works), and Min on a non-empty
        // sequence cannot return null.
        string id = ids.Min(StringComparer.Ordinal)
            ?? throw new InvalidOperationException("no bundled provider ids were discovered");

        return new ScanRule
        {
            Id = "ProviderIdDispatch",
            Trees = ["src", "apps"],
            Forbidden =
            [
                new ScanForbidden(
                    SubId,
                    QuotedLiteral,
                    "a switch/expression arm keyed on a provider id is a row of a hand-maintained, "
                    + "provider-keyed table. Declare the value as data on the provider's own "
                    + "providers/<id>.json or as a registered IProviderCompatFlag. If this file is a "
                    + "genuine exception, add it to DispatchExemptions WITH the reason."),
            ],
            Baseline =
            [
                .. DispatchExemptions.Select(static kv => new ScanBaseline(
                    $"{SubId} {kv.Key}",
                    IsPrefix: false,
                    kv.Value)),
            ],
            Controls =
            [
                // A planted provider-keyed arm must be detected, or the guard is blind.
                new ScanControl("Planted.cs", string.Join("\n",
                    "internal static class Planted",
                    "{",
                    "    private static string Glyph(string id) => id switch",
                    "    {",
                    "        \"" + id + "\" => \"x\",",
                    "        _ => \"y\"",
                    "    };",
                    "}"), SubId),
                // Comment lines are prose, not a table row.
                new ScanControl("Comment.cs", "// prose: \"" + id + "\" => \"x\" in a comment", null),
                // Doc-comment lines are prose, not a table row.
                new ScanControl("Doc.cs", "/// <c>\"" + id + "\"</c> in a doc comment.", null),
                // Looking a provider up by id is a lookup, not a table — the rule
                // targets arms only, or it would flag every legitimate reference.
                new ScanControl("Lookup.cs", "    var m = ProviderPresets.Find(\"" + id + "\");", null),
                // An arm keyed by a string that is not a bundled provider id is not
                // this rule's business.
                new ScanControl("Other.cs", "        \"not-a-provider\" => \"x\",", null),
            ],
            MinHits = 200,
            MustContain = [FormerTableFileRelativePath],
            CustomParse = (path, raw) => ParseArms(path, raw, ids),
        };
    }

    /// <summary>
    ///     The custom parser: the arm verdict over one file's raw source. The same
    ///     finder the rule always used — comment lines skipped, every quoted
    ///     literal tested against the catalogue, the arrow required right after —
    ///     so the planted controls grade identically to product files.
    /// </summary>
    private static IEnumerable<ScanHit> ParseArms(string displayPath, string rawSource, IReadOnlySet<string> ids)
    {
        int number = 0;
        foreach (string line in SplitLines(rawSource))
        {
            number++;
            if (IsComment(line))
            {
                continue;
            }

            foreach (Match literal in QuotedLiteral.Matches(line))
            {
                string value = literal.Groups["value"].Value;
                if (!ids.Contains(value))
                {
                    continue;
                }

                // The literal must be the ARM KEY, i.e. an arrow follows it before
                // any other literal — that is what distinguishes a table row from a
                // lookup, a comparison, or a value.
                string tail = line[(literal.Index + literal.Length)..];
                if (ArmArrow.IsMatch(tail))
                {
                    yield return new ScanHit(SubId, displayPath, number, line.Trim());
                }
            }
        }
    }

    /// <summary>
    ///     The bundled provider ids, read from <c>providers/*.json</c> rather than
    ///     hard-coded, so the guard tracks the catalogue instead of a stale copy of
    ///     it.
    /// </summary>
    [Test]
    public async Task BundledProviderCatalogue_IsDiscoverable_AndNonTrivial()
    {
        string root = RequireRepoRoot();
        string[] files = Directory.GetFiles(Path.Combine(root, "providers"), "*.json");
        string[] ids = [.. files.Select(f => Path.GetFileNameWithoutExtension(f))];

        await Assert.That(ids.Length).IsGreaterThanOrEqualTo(10)
            .Because(
                "providers/ holds the whole bundled catalogue; fewer than 10 means the walk broke "
                + "and the arm scan would be hunting for an empty id set — a guard that matches "
                + "nothing passes for the wrong reason.");
    }

    /// <summary>
    ///     The rule itself: no product file outside <see cref="DispatchExemptions" />
    ///     dispatches on a bundled provider id in an arm.
    /// </summary>
    [Test]
    public async Task NoProductFile_DispatchesOnABundledProviderIdInAnArm()
    {
        List<string> sites = ScanRunner.Evaluate(Rule);

        await Assert.That(sites).IsEmpty()
            .Because(
                "a switch/expression arm keyed on a provider id is a row of a hand-maintained, "
                + "provider-keyed table — the Open/Closed violation #560 reported, and the one §OOP-002 "
                + "removed from the request-payload path. Offending arm(s): " + string.Join("; ", sites)
                + ". Declare the value as data on the provider's own providers/<id>.json (the "
                + "ProviderPresetCatalog projection is the precedent: displayName, description, "
                + "icon, priority) or as a registered IProviderCompatFlag. If this file is a genuine "
                + "exception, add it to DispatchExemptions WITH the reason.");
    }

    /// <summary>
    ///     The exemption list stays honest in both directions: a reason is
    ///     mandatory, an entry must name a file the scan still sees that still
    ///     carries an arm, and no row may rot into a blanket permission.
    /// </summary>
    [Test]
    public async Task EveryExemption_StillNamesAFileThatStillCarriesAnArm()
    {
        // Reasons are mandatory — an exemption without one is an oversight.
        List<string> reasons = ScanRunner.CheckReasons(Rule);

        await Assert.That(reasons).IsEmpty()
            .Because("every exemption must state why it cannot be data instead. " + string.Join("\n", reasons));

        // Every entry must still correspond to a real hit.
        List<string> stale = ScanRunner.StaleBaselineKeys(
            Rule, ScanRunner.ReadSources(ScanRunner.ScopeFiles(Rule)));

        await Assert.That(stale).IsEmpty()
            .Because(
                "an exemption that no longer carries a provider-keyed arm is no longer "
                + "an exception — delete the entry and let the rule apply. " + string.Join(", ", stale));

        // And every entry must name a file the scan still sees.
        IReadOnlyList<string> files = ScanRunner.ScopeFiles(Rule);
        foreach ((string path, string reason) in DispatchExemptions)
        {
            await Assert.That(reason.Length).IsGreaterThan(0)
                .Because("exemption " + path + " must state why it cannot be data instead");
            await Assert.That(files.Contains(path)).IsTrue()
                .Because("exemption " + path + " names a file the scan does not see — drop the entry");
        }
    }

    /// <summary>
    ///     Non-vacuity, part 1: discovery must find a real, non-trivial file set
    ///     that still includes the file which used to hold the table. A scan rooted
    ///     at a typo finds zero files and then "passes" everything.
    /// </summary>
    [Test]
    public async Task Discovery_FindsARealFileSet_IncludingTheFormerTableFile()
    {
        List<string> discovery = ScanRunner.CheckDiscovery(Rule);

        await Assert.That(discovery).IsEmpty()
            .Because(
                "src/ + apps/ hold an order of magnitude more than 200 C# files, including the file "
                + "that carried the 13-arm icon table; a smaller count means the glob broke and the "
                + "arm scan is looking at nothing. " + string.Join("; ", discovery));
    }

    /// <summary>
    ///     Non-vacuity, part 2: the SAME parser must fire on a planted arm, and
    ///     stay silent on the three things that are not provider-keyed dispatch —
    ///     a comment, a plain lookup of the same id, and an arm keyed by something
    ///     that is not a provider. This is what separates "the guard is green" from
    ///     "the guard is looking at nothing".
    /// </summary>
    [Test]
    public async Task Matcher_FiresOnAPlantedArm_AndStaysSilentOtherwise()
    {
        // The snippets live on Rule.Controls — including the planted arm keyed on
        // the ordinal-first catalogue id, the comment and doc-comment prose, the
        // incidental lookup, and the non-provider arm — so the control drives the
        // REAL parser rather than a second implementation of it.
        List<string> failures = ScanRunner.CheckControls(Rule);

        await Assert.That(failures).IsEmpty()
            .Because(
                "a planted provider-keyed arm must be detected, or the guard is blind; and the comment, "
                + "the lookup and the non-provider arm must stay silent, or the rule targets more than "
                + "arms and flags every legitimate reference to a provider id. "
                + string.Join("; ", failures));
    }

    /// <summary>Locate the repository root; fail loudly rather than scan nothing.</summary>
    private static string RequireRepoRoot()
    {
        string? root = RepoPaths.RepoRoot;
        return root ?? throw new InvalidOperationException(
            "Harbor.slnx not found above " + AppContext.BaseDirectory
            + " — this guard walks the repository and cannot run from a published test host.");
    }

    /// <summary>
    ///     The bundled provider ids, read from the <c>id</c> field of each
    ///     <c>providers/*.json</c>.
    /// </summary>
    /// <remarks>
    ///     The <c>id</c> FIELD, parsed as JSON — not "the first string literal in
    ///     the file". Several configs open with <c>"$schema"</c>, so a
    ///     first-literal heuristic reads <c>"$schema"</c> as the provider id and
    ///     silently reduces the id set to a size where the arm scan can never
    ///     match. Parsing also means a config whose <c>id</c> differs from its file
    ///     name still contributes its real id. A config that cannot be parsed, or
    ///     that declares no id, falls back to the file name so it is still hunted.
    /// </remarks>
    private static IReadOnlySet<string> BundledProviderIds(string root)
    {
        var ids = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string file in Directory.GetFiles(Path.Combine(root, "providers"), "*.json"))
        {
            string? id = null;
            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(file));
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("id", out JsonElement idElement)
                    && idElement.ValueKind == JsonValueKind.String)
                {
                    id = idElement.GetString();
                }
            }
            catch (JsonException)
            {
                // A malformed config is `ProviderPresetCatalog`'s problem to skip and
                // log; here it just falls back to the file name so the guard keeps
                // hunting rather than quietly dropping a provider.
            }

            ids.Add(string.IsNullOrWhiteSpace(id) ? Path.GetFileNameWithoutExtension(file) : id);
        }

        // Frozen: the scan asks "is this literal a bundled provider id?" once per
        // string literal in ~960 files, and a linear Contains over a list would make
        // the guard's own cost scale with the catalogue.
        return ids.ToFrozenSet(StringComparer.Ordinal);
    }

    /// <summary>Comment and doc-comment lines are prose, not code.</summary>
    private static bool IsComment(string line)
    {
        string trimmed = line.TrimStart();
        return trimmed.StartsWith("//", StringComparison.Ordinal)
            || trimmed.StartsWith("/*", StringComparison.Ordinal)
            || trimmed.StartsWith('*');
    }

    private static string[] SplitLines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
}
