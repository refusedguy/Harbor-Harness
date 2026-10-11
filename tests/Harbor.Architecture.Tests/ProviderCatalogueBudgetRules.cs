// ProviderCatalogueBudgetRules.cs — guard for #685.
//
// #671 already settled the two halves of this subject, and this file must not
// reopen either of them:
//
//   * A provider PROBE ("can this provider answer?") spends the canonical
//     IProviderHealthCheck.DefaultTimeout. ProviderConfigViewModel.TestConnectionAsync
//     does, and ProviderAuthSingleAnswerRules holds it there.
//
//   * A UI CATALOGUE WAIT ("what models does this provider list, and how long
//     may the user stare at an empty picker?") is a different question with a
//     deliberately different number. #671 renamed it from the probe-flavoured
//     "ModelFetchTimeout" to "UiFeedbackBudget" and said so in the picker's
//     doc comment.
//
// What #671 did NOT settle is the scope of the rename. Its guard reads exactly
// one file — ProviderModelPickerViewModel.cs — because widening the scan "would
// make the gate red on files this change does not own". So the concept is
// governed where someone remembered and unguarded everywhere else, and the two
// subjects that bound the IDENTICAL registry fan-out through the IDENTICAL
// AsyncFeed<T> kept the old name and their own copy of the number:
//
//   ProviderBrowserViewModel.cs:22          ModelFetchTimeout = 5s   (live surface)
//   ProviderModelPickerViewModelBase.cs:39  ModelFetchTimeout = 5s   (unreferenced)
//
// That is precisely the drift #685 predicts: when the budget moves, whoever
// remembers updates the picker and the browser keeps waiting its own five
// seconds, and the user reports it as "sometimes settings hang".
//
// This file therefore does not invent a home for the number. The budget already
// HAS an owner — ProviderModelPickerViewModel.UiFeedbackBudget, named and
// documented by #671 — so the rule is not "relocate the constant" but "there is
// exactly one declaration, and every other catalogue surface spends it". A new
// shared holder would be the same magic number in a different file, which is
// no better than what shipped.
//
// Subjects are DISCOVERED, not listed. A rule that enumerates its own subjects
// rots: the moment a fourth catalogue surface appears it is silently
// ungoverned, which is the exact failure mode this issue is about. Discovery is
// anchored on the fan-out itself (the AsyncFeed that carries the budget, the
// GetAllModelsAsync call it bounds, the two names the concept has worn), so a
// new surface joins the rule the moment it touches the registry.
//
// SOURCE-level rule, like its #671 sibling: no single runtime call is wrong in
// isolation — the surfaces are each self-consistent and mutually inconsistent.
// Reading the four files side by side is what makes the disagreement visible.

// MECHANISM (#1086, step 2, conveyor)
// -----------------------------------
// The two surface bans below are one ScanRule over the governed ViewModels
// directory: the duration-literal ban and the retired probe-name ban.
// Enumeration, matching and the control/discovery verdicts are ScanRunner's;
// this file keeps the issue prose and the test names.
//
// Three deliberate carries, not re-decisions:
//   * Subjects are DISCOVERED by marker, not listed: the parser admits only a
//     file naming the feed, the call, or either budget name — raw `Contains`,
//     exactly as `CatalogueSurfaces` did — so a fourth surface joins the rule
//     the moment it touches the fan-out, and a file that never did stays out.
//   * The owner file (declaring the canonical budget) is exempt IN the parser,
//     exactly as the old loop `continue`d past it. A second duration literal in
//     the owner's own file stays silent, as before.
//   * The matchers run over the RAW source with no comment stripping — the old
//     `Matches(source)`/`Contains` calls graded comments too. Preserved verbatim.
// The count/existence halves (exactly one owner, every spender names a budget,
// the live-subjects pins) are not forbidden-shape scans and stay handwritten
// over the unchanged `CatalogueSurfaces` discovery.

using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Asserts that a UI surface which loads the provider model catalogue spends
///     one declared budget — <c>UiFeedbackBudget</c> — rather than each surface
///     picking a duration of its own, and that no surface borrows the
///     probe-flavoured name that made the two budgets indistinguishable.
/// </summary>
public class ProviderCatalogueBudgetRules
{
    /// <summary>The project whose view-models load the provider catalogue.</summary>
    private const string ProjectDir = "Harbor.Desktop.Abstractions";

    /// <summary>The name #671 gave the catalogue wait. Exactly one file declares it.</summary>
    private const string CanonicalName = "UiFeedbackBudget";

    /// <summary>The probe-flavoured name #671 retired. No surface may reintroduce it.</summary>
    private const string RetiredName = "ModelFetchTimeout";

    /// <summary>
    ///     The retired name as a pattern. The parser grades it with a verbatim
    ///     <c>Contains</c> (as the old check did), so this row documents the same
    ///     literal substring — not a copy of a different shape — and the runner
    ///     never executes it (the rule grades through <see cref="ParseCatalogueSurface" />).
    /// </summary>
    private static readonly Regex RetiredNamePattern = new(
        Regex.Escape(RetiredName), RegexOptions.Compiled);

    /// <summary>The canonical probe budget, for surfaces that probe rather than list.</summary>
    private const string ProbeCanon = "IProviderHealthCheck.DefaultTimeout";

    /// <summary>
    ///     Any term that marks a file as participating in the catalogue wait:
    ///     the feed that carries the budget, the call it bounds, or either name
    ///     the concept has worn. This is the discovery predicate.
    /// </summary>
    private static readonly string[] CatalogueMarkers =
        ["AsyncFeed", "GetAllModelsAsync", RetiredName, CanonicalName];

    /// <summary>
    ///     A hard-coded duration literal of any unit — the shape that let each
    ///     surface answer "how long may I wait?" for itself.
    /// </summary>
    private static readonly Regex DurationLiteral =
        new(@"TimeSpan\s*\.\s*From(?:Seconds|Milliseconds|Minutes|Hours)\s*\(",
            RegexOptions.Compiled);

    /// <summary>A declaration of the canonical budget — the one owner this rule admits.</summary>
    private static readonly Regex CanonicalDeclaration =
        new(@"static\s+readonly\s+TimeSpan\s+" + CanonicalName, RegexOptions.Compiled);

    /// <summary>Actual code that spends a budget, as opposed to prose that mentions one.</summary>
    private static readonly Regex SpendsABudget =
        new(@"new\s+AsyncFeed|GetAllModelsAsync\s*\(", RegexOptions.Compiled);

    private const string DurationSubId = "CATALOGUE-DURATION-LITERAL";
    private const string ProbeNameSubId = "CATALOGUE-PROBE-NAME";

    /// <summary>The surface bans as data: duration literals and the retired probe name.</summary>
    private static readonly ScanRule Rule = new()
    {
        Id = "ProviderCatalogueBudget",
        Trees = ["src/" + ProjectDir + "/ViewModels"],
        Forbidden =
        [
            new ScanForbidden(
                DurationSubId,
                DurationLiteral,
                "only the budget's owner states a duration — spend the declared "
                + CanonicalName + ". See issue #685."),
            new ScanForbidden(
                ProbeNameSubId,
                RetiredNamePattern,
                "'" + RetiredName + "' is the probe-flavoured name #671 retired. See issue #685."),
        ],
        Controls =
        [
            // A second surface with its own number: discovered by the feed, not
            // the owner, carrying a duration literal.
            new ScanControl(
                "Surfaces/Browser.cs",
                """
                public async Task LoadAsync()
                {
                    var feed = new AsyncFeed<Model>();
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                }
                """,
                DurationSubId),
            // The retired name on a discovered surface.
            new ScanControl(
                "Surfaces/Legacy.cs",
                """
                public TimeSpan Wait => ModelFetchTimeout;
                public async Task LoadAsync() { await GetAllModelsAsync(); }
                """,
                ProbeNameSubId),
            // The owner states the number once — the one place it may be written down.
            new ScanControl(
                "Surfaces/Owner.cs",
                """
                private static readonly TimeSpan UiFeedbackBudget = TimeSpan.FromSeconds(8);
                public async Task LoadAsync() { await GetAllModelsAsync(); }
                """,
                null),
            // A duration literal in a file that never touches the fan-out is not
            // a catalogue wait at all — the discovery gate, proved through the parser.
            new ScanControl(
                "Elsewhere/Timer.cs",
                "using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));",
                null),
            // A discovered surface spending the declared budget.
            new ScanControl(
                "Surfaces/Spender.cs",
                """
                public async Task LoadAsync()
                {
                    var feed = new AsyncFeed<Model>(UiFeedbackBudget);
                    await GetAllModelsAsync();
                }
                """,
                null),
        ],
        MinHits = 10,
        MustContain =
        [
            "src/" + ProjectDir + "/ViewModels/ProviderBrowserViewModel.cs",
            "src/" + ProjectDir + "/ViewModels/ProviderConfigViewModel.cs",
            "src/" + ProjectDir + "/ViewModels/ProviderModelPickerViewModel.cs",
            "src/" + ProjectDir + "/ViewModels/ProviderModelPickerViewModelBase.cs",
        ],
        CustomParse = ParseCatalogueSurface,
    };

    /// <summary>
    ///     The custom parser: the old per-file verdict verbatim — undiscovered
    ///     files silent, the owner exempt, raw source throughout — with match
    ///     indexes mapped back to 1-based lines.
    /// </summary>
    private static IEnumerable<ScanHit> ParseCatalogueSurface(string displayPath, string rawSource)
    {
        if (!CatalogueMarkers.Any(marker => rawSource.Contains(marker, StringComparison.Ordinal)))
        {
            yield break;
        }

        if (CanonicalDeclaration.IsMatch(rawSource))
        {
            yield break;
        }

        foreach (Match match in DurationLiteral.Matches(rawSource))
        {
            yield return new ScanHit(DurationSubId, displayPath, LineOf(rawSource, match.Index), match.Value.Trim());
        }

        string[] lines = rawSource.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains(RetiredName, StringComparison.Ordinal))
            {
                yield return new ScanHit(ProbeNameSubId, displayPath, i + 1, lines[i].Trim());
            }
        }
    }

    private static int LineOf(string source, int index)
    {
        int line = 1;
        for (int i = 0; i < index && i < source.Length; i++)
        {
            if (source[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    /// <summary>
    ///     Every view-model in the governed project that participates in the
    ///     catalogue wait, discovered by <see cref="CatalogueMarkers" /> rather
    ///     than listed by hand. Sorted for a deterministic failure message.
    /// </summary>
    private static IReadOnlyDictionary<string, string> CatalogueSurfaces()
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        if (RepoPaths.FindProjectDir(ProjectDir) is not { } dir)
        {
            return found;
        }

        string viewModels = Path.Combine(dir, "ViewModels");
        if (!Directory.Exists(viewModels))
        {
            return found;
        }

        foreach (string path in Directory.GetFiles(viewModels, "*.cs").OrderBy(p => p, StringComparer.Ordinal))
        {
            string source = File.ReadAllText(path);
            if (CatalogueMarkers.Any(marker => source.Contains(marker, StringComparison.Ordinal)))
            {
                found[Path.GetFileName(path)] = source;
            }
        }

        return found;
    }

    /// <summary>Joins a violation list for a failure message.</summary>
    private static string Offenders(IEnumerable<string> violations)
    {
        string[] list = violations.ToArray();
        return list.Length == 0 ? "(none)" : string.Join("; ", list);
    }

    [Test]
    public async Task TheCatalogueWaitHasExactlyOneOwner()
    {
        string[] owners =
            [.. CatalogueSurfaces()
                .Where(kv => CanonicalDeclaration.IsMatch(kv.Value))
                .Select(kv => kv.Key)
                .OrderBy(name => name, StringComparer.Ordinal)];

        var violations = new List<string>();
        if (owners.Length != 1)
        {
            violations.Add(
                $"expected exactly one declaration of {CanonicalName}, found {owners.Length} ({Offenders(owners)})");
        }

        await Assert.That(violations).IsEmpty()
            .Because(
                "One question gets one answer. A UI surface that lists the provider catalogue must learn how long it "
                + "may wait from a single declared budget, and that budget must be declared once. Two owners is two "
                + "numbers under one concept again — the exact shape #685 reports as 'sometimes settings hang'. "
                + "Offenders: " + Offenders(violations));
    }

    [Test]
    public async Task NoCatalogueSurfaceDeclaresADurationOfItsOwn()
    {
        // The owner exemption and the discovery gate live in the parser now; this
        // test reads the duration-sub-id verdict out of the shared evaluation.
        List<string> violations =
        [
            .. ScanRunner.Evaluate(Rule).Where(line => line.Contains("[" + DurationSubId + "]", StringComparison.Ordinal)),
        ];

        await Assert.That(violations).IsEmpty()
            .Because(
                "Only the budget's owner states a duration. Every other catalogue surface must spend the declared "
                + CanonicalName + " — a surface that picks its own number is a surface that will keep waiting its own "
                + "five seconds after the budget moves, which is the drift #685 is about. Offenders: "
                + Offenders(violations));
    }

    [Test]
    public async Task NoCatalogueSurfaceNamesItsBudgetLikeAProbe()
    {
        List<string> violations =
        [
            .. ScanRunner.Evaluate(Rule).Where(line => line.Contains("[" + ProbeNameSubId + "]", StringComparison.Ordinal)),
        ];

        await Assert.That(violations).IsEmpty()
            .Because(
                "'" + RetiredName + "' is the probe-flavoured name #671 retired: it made a catalogue wait "
                + "indistinguishable from a provider probe, and the two budgets are deliberately different numbers. "
                + "A surface may describe a catalogue wait only as " + CanonicalName + " — or as the probe canon ("
                + ProbeCanon + ") when it is genuinely probing. Offenders: " + Offenders(violations));
    }

    [Test]
    public async Task EveryCatalogueSpenderNamesARealBudget()
    {
        var violations = new List<string>();

        foreach ((string file, string source) in CatalogueSurfaces())
        {
            if (!SpendsABudget.IsMatch(source))
            {
                continue;
            }

            bool namesOwner = source.Contains(CanonicalName, StringComparison.Ordinal);
            bool namesProbeCanon = source.Contains(ProbeCanon, StringComparison.Ordinal);
            if (!namesOwner && !namesProbeCanon)
            {
                violations.Add(
                    $"{file}: loads the catalogue but names neither {CanonicalName} nor {ProbeCanon}");
            }
        }

        await Assert.That(violations).IsEmpty()
            .Because(
                "A surface that actually runs a catalogue fetch must say whose budget it is spending: "
                + CanonicalName + " for a UI catalogue wait, or " + ProbeCanon + " for a probe. A surface that names "
                + "neither has an unnamed private timeout, which is how four views came to hold four answers. "
                + "Offenders: " + Offenders(violations));
    }

    [Test]
    public async Task TheRuleStillHasLiveSubjects()
    {
        // Anti-rot. A discovered rule can decay into one that matches nothing,
        // and a rule that matches nothing is indistinguishable from a rule that
        // passes. Pin both halves of the discovery: the four subjects that exist
        // today must still be found, and the rule must still have real spenders
        // to judge. If a catalogue surface is renamed or removed, this fails
        // loudly instead of the scan quietly going green.
        string[] expected =
        [
            "ProviderBrowserViewModel.cs",
            "ProviderConfigViewModel.cs",
            "ProviderModelPickerViewModel.cs",
            "ProviderModelPickerViewModelBase.cs"
        ];

        var surfaces = CatalogueSurfaces();
        var violations = new List<string>();

        foreach (string file in expected.Where(file => !surfaces.ContainsKey(file)))
        {
            violations.Add($"{file}: no longer discovered as a catalogue surface — the rule lost a subject");
        }

        int spenders = surfaces.Count(kv => SpendsABudget.IsMatch(kv.Value));
        if (spenders < 2)
        {
            violations.Add(
                $"expected at least 2 catalogue surfaces that actually spend a budget, found {spenders} — "
                + "the discovery predicate has stopped matching real spenders");
        }

        await Assert.That(violations).IsEmpty()
            .Because(
                "The subjects are discovered, so they can silently drift out of scope. Today four view-models in "
                + ProjectDir + " load the provider catalogue; if that set changes, this rule must be re-pointed "
                + "deliberately rather than left governing nothing. Offenders: " + Offenders(violations));
    }

    /// <summary>
    ///     Non-vacuity for both surface bans: the parser fires on a second
    ///     surface's own number and on the retired name, stays silent for the
    ///     owner, for files outside the discovery, and for spenders of the
    ///     declared budget. The shapes live on the rule as data and drive the
    ///     REAL parser.
    /// </summary>
    [Test]
    public async Task Detector_FiresOnRogueSurfaces_AndStaysQuietOnOwnerAndOutsiders()
    {
        List<string> failures = ScanRunner.CheckControls(Rule);

        await Assert.That(failures).IsEmpty()
            .Because(
                "a surface that picks its own number, or that still speaks the retired "
                + "probe-flavoured name, must be detected — and the owner, an undiscovered "
                + "file, and a spender of the declared budget must stay silent. "
                + string.Join("; ", failures));
    }

    /// <summary>
    ///     The walk really reaches the governed directory with all four
    ///     discovered surfaces inside it.
    /// </summary>
    [Test]
    public async Task Scanner_SeesTheViewModels()
    {
        List<string> discovery = ScanRunner.CheckDiscovery(Rule);

        await Assert.That(discovery).IsEmpty()
            .Because(
                "The subjects are discovered, so the scope must provably contain them — "
                + "a scan rooted at a typo finds zero files and then passes everything. "
                + string.Join("; ", discovery));
    }

    /// <summary>
    ///     Every baseline row states why it is tolerated, in the row itself. Vacuous
    ///     while the table is empty, and deliberately so: wired from the first row
    ///     so the first row cannot skip the argument.
    /// </summary>
    [Test]
    public async Task Baseline_Rows_AllHaveReasons()
    {
        List<string> failures = ScanRunner.CheckReasons(Rule);

        await Assert.That(failures).IsEmpty().Because(string.Join("\n", failures));
    }

    /// <summary>
    ///     Every baseline row must still correspond to a real hit, so the table
    ///     cannot rot into a blanket permission.
    /// </summary>
    [Test]
    public async Task Baseline_Rows_Are_Not_Stale()
    {
        List<string> stale = ScanRunner.StaleBaselineKeys(
            Rule, ScanRunner.ReadSources(ScanRunner.ScopeFiles(Rule)));

        await Assert.That(stale).IsEmpty()
            .Because("a baseline row with no violation behind it is a permission for a "
                + "problem that no longer exists: " + string.Join(", ", stale));
    }
}
