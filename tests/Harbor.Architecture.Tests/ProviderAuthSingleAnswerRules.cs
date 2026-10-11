// ProviderAuthSingleAnswerRules.cs — guard for #671.
//
// The user's question is "is this provider authorized?". One application
// answered it three ways, from three different data sources:
//
//   1. apps/Harbor.App.Avalonia/ViewModels/SettingsViewModel.cs:173 asked
// check-doc-cites: record-drift apps/Harbor.App.Avalonia/ViewModels/SettingsViewModel.cs:173 now="// Restore, not assignment: a saved value may be a PALETT…" [#947: written over `authenticated = _authResolver.ResolveApi`; repair deferred to the owner's symbol-rename decision] -->
//      IAuthResolver — the abstraction whose own doc comment
//      (src/Harbor.Abstractions/Providers/IAuthResolver.cs:9) declares it
//      "the single auth abstraction for ALL ILlmClient implementations",
//      reading config stores, OS keychains, CLI overrides AND conventional
//      environment variables. Correct.
//   2. src/Harbor.Desktop.Abstractions/ViewModels/ProviderModelPickerViewModel.cs:250
// check-doc-cites: record-drift src/Harbor.Desktop.Abstractions/ViewModels/ProviderModelPickerViewModel.cs:250 now="CurrentModelLabel = $'{active.ProviderId}/{active.Model}';" [#947: written over `CurrentModelLabel = $"{result.Value.Defa`; repair deferred to the owner's symbol-rename decision] -->
//      asked the CommonConfig.ApiKeys dictionary directly, so it could not see
//      an env-var key and painted "✗ No API key" on a provider the Settings tab
//      calls "Authenticated" — two answers to one question, in one app.
//   3. src/Harbor.Desktop.Abstractions/ViewModels/ProviderConfigViewModel.cs:167
//      used `count > 0` — how many models a /models call returned — as the
//      authorization signal. A model count is a reachability fact, not an
//      identity fact: an authorized provider with an empty catalogue reads as
//      unauthorized, and an unauthorized provider whose endpoint happens to
//      answer /models reads as authorized.
//
// The second half of the subject is the timeout. A provider probe had TWO
// budgets under one concept: the canonical IProviderHealthCheck.DefaultTimeout
// = 10s (IProviderHealthCheck.cs:15), which OnboardingViewModel.cs:459 already
// check-doc-cites: record-drift OnboardingViewModel.cs:459 now="ambiguous:2" [2 tracked files share this basename, so the citation does not identify one] -->
// spends, and a bare 5s literal inside ProviderConfigViewModel.TestConnectionAsync
// with a user-facing string promising "Timed out after 5s". Two numbers, one
// meaning — and the number shown to the user was not the canonical one.
//
// This file is a SOURCE-level rule because the defect is a source-level one: no
// single runtime call is wrong in isolation, the two view-models are simply
// answering from different stores. Reading the two files is what makes the
// disagreement visible. The user-visible half is covered behaviourally in
// tests/Harbor.App.Avalonia.Tests/ProviderAuthSingleAnswerTests.cs.
//
// SCOPE, deliberately narrow: the two view-models in
// src/Harbor.Desktop.Abstractions/ViewModels/ that render an auth verdict.
// The catalogue surfaces (ProviderBrowserViewModel and the unreferenced
// ProviderModelPickerViewModelBase) are outside this rule — they render no auth
// verdict — and widening the scan would make the gate red on files this change
// does not own. Their half of the subject is not abandoned, only moved:
// ProviderCatalogueBudgetRules (#685) governs the catalogue-wait budget across
// every surface that touches the registry fan-out, and it deliberately does not
// name IAuthResolver, because reachability is not authorization.

// MECHANISM (#1086, step 2, conveyor)
// -----------------------------------
// The bans below are one ScanRule over the two governed view-models: the
// auth-verdict shapes (dictionary / model-count) plus the probe-budget shape
// (hard-coded duration, reported only for the settings row — the only file the
// old check read for it). Enumeration, matching and the control/discovery
// verdicts are ScanRunner's; this file keeps the issue prose and the test names.
//
// Three deliberate carries, not re-decisions:
//   * The matchers run over the RAW source with no comment stripping — the old
//     code called `Regex.Matches(source)` on the file text as read. A comment
//     naming `TimeSpan.FromSeconds` trips this rule exactly as before; the
//     parsers replicate the call verbatim (whole-source match, index-mapped
//     line numbers) rather than re-deciding it.
//   * The scope is two NAMED files, not a tree: `InScope` admits exactly the
//     picker and the settings row, and `MustContain` names both — so a deleted
//     subject fails the discovery floor instead of vanishing, as the old
//     missing-file arm did.
//   * The positive-existence halves (names IAuthResolver / the canonical
//     timeout / UiFeedbackBudget, the live-subjects pins) are not
//     forbidden-shape scans and stay handwritten.

using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Asserts that every view-model rendering a provider's authorization
///     verdict reads it from <c>IAuthResolver</c> — never from a config-store
///     dictionary and never from a model count — and that a provider probe
///     spends the canonical <c>IProviderHealthCheck.DefaultTimeout</c>.
/// </summary>
public class ProviderAuthSingleAnswerRules
{
    /// <summary>The project whose view-models render provider auth verdicts.</summary>
    private const string ProjectDir = "Harbor.Desktop.Abstractions";

    /// <summary>The picker row: its verdict is what the user reads as "✗ No API key".</summary>
    private const string PickerFile = "ProviderModelPickerViewModel.cs";

    /// <summary>The settings row: its verdict is what the user reads as "Authenticated".</summary>
    private const string ConfigRowFile = "ProviderConfigViewModel.cs";

    /// <summary>
    ///     Reading a provider key straight out of the config-store dictionary.
    ///     This is the expression the picker shipped; it cannot see a key living
    ///     in the environment, a keychain, or a CLI override.
    /// </summary>
    private static readonly Regex AuthFromConfigDictionary =
        new(@"ApiKeys\s*\.\s*(?:GetValueOrDefault|TryGetValue)", RegexOptions.Compiled);

    /// <summary>
    ///     An assignment to the auth flag whose right-hand side mentions a model
    ///     count. <c>count &gt; 0</c> was the shipped form. Anchored on the
    ///     assignment, so an unrelated local named <c>count</c> cannot trip it.
    /// </summary>
    private static readonly Regex AuthFromModelCount =
        new(@"IsAuthenticated\s*=\s*[^;\r\n]*\bcount\b", RegexOptions.Compiled);

    /// <summary>A hard-coded duration literal — the shape that let a probe pick its own budget.</summary>
    private static readonly Regex HardCodedDuration =
        new(@"TimeSpan\s*\.\s*FromSeconds\s*\(", RegexOptions.Compiled);

    private const string AuthDictSubId = "AUTH-FROM-CONFIG-DICTIONARY";
    private const string AuthCountSubId = "AUTH-FROM-MODEL-COUNT";
    private const string ProbeBudgetSubId = "PROBE-HARDCODED-DURATION";

    /// <summary>The bans as data: two verdict shapes plus the probe-budget shape, over the two governed files.</summary>
    private static readonly ScanRule Rule = new()
    {
        Id = "ProviderAuthSingleAnswer",
        Trees = ["src/" + ProjectDir + "/ViewModels"],
        InScope = path =>
            path.EndsWith("/" + PickerFile, StringComparison.Ordinal)
            || path.EndsWith("/" + ConfigRowFile, StringComparison.Ordinal),
        Forbidden =
        [
            new ScanForbidden(
                AuthDictSubId,
                AuthFromConfigDictionary,
                "a config dictionary cannot see an env key — read the verdict from IAuthResolver. "
                + "See issue #671."),
            new ScanForbidden(
                AuthCountSubId,
                AuthFromModelCount,
                "a model count is reachability, not identity — read the verdict from IAuthResolver. "
                + "See issue #671."),
            new ScanForbidden(
                ProbeBudgetSubId,
                HardCodedDuration,
                "a provider probe spends IProviderHealthCheck.DefaultTimeout — the same 10s the "
                + "onboarding wizard spends — not a literal of its own. See issue #671."),
        ],
        Controls =
        [
            // The exact shape the picker shipped.
            new ScanControl("Picker/Dict.cs", "var key = ApiKeys.GetValueOrDefault(providerId);", AuthDictSubId),
            new ScanControl("Picker/Try.cs", "if (ApiKeys.TryGetValue(id, out var k)) { }", AuthDictSubId),
            // The exact shape the settings row shipped.
            new ScanControl("Row/Count.cs", "IsAuthenticated = count > 0;", AuthCountSubId),
            // The exact probe shape the settings row shipped: its own 5s with a
            // user-facing string promising "Timed out after 5s". The control file
            // carries the governed name, because the old check read only that file.
            new ScanControl(
                "Row/ProviderConfigViewModel.cs",
                "using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));",
                ProbeBudgetSubId),
            // The post-fix shapes: the verdict from the abstraction, the budget
            // from the canonical constant.
            new ScanControl("Fixed/Resolver.cs", "_authState.IsAuthenticated = resolved;", null),
            new ScanControl(
                "Fixed/ProviderConfigViewModel.cs",
                "using var cts = new CancellationTokenSource(IProviderHealthCheck.DefaultTimeout);",
                null),
            // A duration literal in the PICKER is not a probe budget — the old
            // check never read that file for this shape.
            new ScanControl(
                "Picker/ProviderModelPickerViewModel.cs",
                "using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));",
                null),
        ],
        MinHits = 2,
        MustContain =
        [
            "src/" + ProjectDir + "/ViewModels/" + PickerFile,
            "src/" + ProjectDir + "/ViewModels/" + ConfigRowFile,
        ],
        CustomParse = ParseAuthSubjects,
    };

    /// <summary>
    ///     The custom parser: the old whole-source `Matches` calls verbatim — raw
    ///     source, no stripping — with match indexes mapped back to 1-based lines.
    ///     The duration shape is reported only for the settings row, exactly as
    ///     the old check read only that file.
    /// </summary>
    private static IEnumerable<ScanHit> ParseAuthSubjects(string displayPath, string rawSource)
    {
        foreach (Match match in AuthFromConfigDictionary.Matches(rawSource))
        {
            yield return new ScanHit(AuthDictSubId, displayPath, LineOf(rawSource, match.Index), match.Value.Trim());
        }

        foreach (Match match in AuthFromModelCount.Matches(rawSource))
        {
            yield return new ScanHit(AuthCountSubId, displayPath, LineOf(rawSource, match.Index), match.Value.Trim());
        }

        if (displayPath.EndsWith("/" + ConfigRowFile, StringComparison.Ordinal))
        {
            foreach (Match match in HardCodedDuration.Matches(rawSource))
            {
                yield return new ScanHit(ProbeBudgetSubId, displayPath, LineOf(rawSource, match.Index), match.Value.Trim());
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

    /// <summary>Source of a named view-model under the governed project.</summary>
    private static string? ReadViewModel(string fileName)
    {
        if (RepoPaths.FindProjectDir(ProjectDir) is not { } dir)
        {
            return null;
        }

        string path = Path.Combine(dir, "ViewModels", fileName);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    /// <summary>
    ///     The auth-rendering view-models this rule governs. A missing file maps
    ///     to <c>null</c> rather than being skipped, so the anti-rot test below
    ///     fails loudly instead of the scan going quietly green.
    /// </summary>
    private static IReadOnlyDictionary<string, string?> AuthRenderingViewModels() =>
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [PickerFile] = ReadViewModel(PickerFile),
            [ConfigRowFile] = ReadViewModel(ConfigRowFile)
        };

    /// <summary>Joins a violation list for a failure message.</summary>
    private static string Offenders(IEnumerable<string> violations)
    {
        var list = violations.ToArray();
        return list.Length == 0 ? "(none)" : string.Join("; ", list);
    }

    [Test]
    public async Task NoAuthViewModel_DecidesAuthorizationFromAConfigDictionaryOrAModelCount()
    {
        // A missing subject fails the discovery floor (MustContain names both
        // files) rather than vanishing — the old missing-file arm, preserved as
        // a verdict rather than a message.
        List<string> violations = ScanRunner.Evaluate(Rule);

        await Assert.That(violations).IsEmpty()
            .Because(
                "A provider's authorization verdict must come from IAuthResolver — the one abstraction that "
                + "reads config stores, keychains, CLI overrides and environment variables. A config dictionary "
                + "cannot see an env key (#671: the picker said ✗ where Settings said Authenticated), and a model "
                + "count is reachability, not identity. Offenders: " + Offenders(violations));
    }

    [Test]
    public async Task EveryAuthViewModel_TakesTheSingleAuthAbstraction()
    {
        var offenders = AuthRenderingViewModels()
            .Where(kv => kv.Value is null || !kv.Value.Contains("IAuthResolver", StringComparison.Ordinal))
            .Select(kv => kv.Key);

        await Assert.That(offenders.ToArray()).IsEmpty()
            .Because(
                "A view-model that renders an auth verdict but never names IAuthResolver has no legitimate way to "
                + "obtain one — it is reading a store directly. Offenders: " + Offenders(offenders));
    }

    [Test]
    public async Task ProviderProbe_SpendsTheCanonicalHealthCheckBudget()
    {
        string? source = ReadViewModel(ConfigRowFile);

        await Assert.That(source).IsNotNull()
            .Because("The settings row must exist — it is the surface whose auth flag must not track a model count.");

        List<string> violations = ScanRunner.Evaluate(Rule);

        await Assert.That(violations).IsEmpty()
            .Because(
                "TestConnectionAsync probes a provider (it fetches /models), so its budget is "
                + "IProviderHealthCheck.DefaultTimeout — the same 10s the onboarding wizard spends — not a literal "
                + "of its own. Found: " + Offenders(violations));
    }

    [Test]
    public async Task ProviderProbe_UsesTheCanonicalHealthCheckTimeoutConstant()
    {
        string? source = ReadViewModel(ConfigRowFile);

        await Assert.That(source).IsNotNull()
            .Because("The settings row must exist — it is the surface that probes a provider.");
        await Assert.That(source!).Contains("IProviderHealthCheck.DefaultTimeout")
            .Because("Naming the canonical constant is what stops a second number from appearing under one concept.");
    }

    [Test]
    public async Task PickerCatalogBudget_IsNamedAsAFeedbackBudgetNotAProbe()
    {
        string? source = ReadViewModel(PickerFile);

        await Assert.That(source).IsNotNull()
            .Because("The picker must exist — it is the surface that showed ✗ to an env-authenticated provider.");

        // The picker is not probing a provider: it is bounding how long the user
        // waits for a catalogue refresh. Naming that number like a probe is what
        // let two different budgets share one concept — the name is the fix, not
        // a second shared literal.
        await Assert.That(source!).Contains("UiFeedbackBudget")
            .Because("A catalogue wait is a UI feedback budget and must not read as a provider-probe budget.");
        await Assert.That(source!).DoesNotContain("ModelFetchTimeout")
            .Because("'ModelFetchTimeout' is the probe-flavoured name that made the two budgets indistinguishable.");
    }

    [Test]
    public async Task TheRuleStillHasLiveSubjects()
    {
        // Anti-rot. A two-sided correspondence can decay into a rule that matches
        // nothing, and a rule that matches nothing is indistinguishable from a
        // rule that passes. Name the two types the rule was written for and
        // assert each still renders an auth verdict — if either stops, the scan
        // above has no subject left and is vacuous.
        var viewModels = AuthRenderingViewModels();

        await Assert.That(viewModels[PickerFile]).IsNotNull()
            .Because("The picker must stay a governed subject: it is the surface that showed ✗ to an env-authorized provider.");
        await Assert.That(viewModels[ConfigRowFile]).IsNotNull()
            .Because("The settings row must stay a governed subject: its auth flag must not track a model count.");

        await Assert.That(viewModels[PickerFile]!).Contains("IsAuthenticated")
            .Because("The picker still projects an auth verdict (ProviderGroupViewModel.IsAuthenticated drives the ✓/✗ glyph).");
        await Assert.That(viewModels[ConfigRowFile]!).Contains("IsAuthenticated")
            .Because("The settings row still exposes an auth verdict (AuthIcon/AuthText branch on it).");
    }

    /// <summary>
    ///     The walk really reaches both governed view-models. A deleted subject
    ///     fails here (MustContain) and in <see cref="TheRuleStillHasLiveSubjects" />,
    ///     never by silently leaving the scope.
    /// </summary>
    [Test]
    public async Task Scanner_SeesBothSubjects()
    {
        List<string> discovery = ScanRunner.CheckDiscovery(Rule);

        await Assert.That(discovery).IsEmpty()
            .Because(
                "Both auth-rendering view-models must stay inside the scanned set, or the "
                + "bans above police nothing. "
                + string.Join("; ", discovery));
    }

    /// <summary>
    ///     Every baseline row states why it is tolerated, in the row itself. Vacuous
    ///     while both tables are empty, and deliberately so: wired from the first
    ///     row so the first row cannot skip the argument.
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
