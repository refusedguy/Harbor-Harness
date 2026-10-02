// ReplConstructorCompositionTests.cs — guard for the REPL's own wiring
// (issue #486).
//
// THE DEFECT
// ----------
// `apps/Harbor.App.Cli/Repl/ReplRunner.cs` declared TWENTY-ONE constructor
// parameters. The issue filed it as "20/22" and, as of this guard, the count is
// 21 (and CellForgeReplRunner's is 23) — the number has only gone up, so the
// finding has not gone stale, it has compounded.
//
// But the count is the SYMPTOM, and a gate on it would be worthless twice over:
// "no constructor longer than N" blocks the first legitimate dependency added
// (it measures length, not cause) and it cannot say WHICH of the 21 are at
// fault. So the count is never asserted anywhere in this file. What is asserted
// is the two facts the count is made of.
//
// FACT 1 — four of the twenty-one are not dependencies of ReplRunner at all.
// Measured by occurrence count in the file, each of these appears EXACTLY
// TWICE: once in the parameter list, once on line 92. They are never stored in
// a field, never read, never passed on:
//
//     IToolRegistry                      tools
//     IPermissionService                permissions
//     Func<IReadOnlyList<SkillFreshnessEntry>>? skillRefresh
//     Func<IReadOnlyList<string>, Task<SkillUpdateReport>>? skillUpdate
//
// Their single use is as an argument to `new SlashCommandDispatcher(...)` in
// the constructor. They are not what ReplRunner needs; they are what
// ReplRunner was resolving FOR something else and dragging into its own
// signature to do it. A FIFTH parameter, `ILoggerFactory`, is composition in
// the same constructor — `loggerFactory.CreateLogger<CellForgeReplRunner>()` —
// and is the single case the rule allows, for a reason that is an analyzer's:
// see ProducesATypedLogger.
//
// This is the distinction the issue's own prescription gets wrong, and getting
// it wrong is the expensive mistake: the proposed fix is to bundle the
// parameters into a `ReplContext` and inject that. Bundling them would MOVE
// four masked defaults one level down and make the mega-constructor a
// well-typed one — twenty-one names replaced by one, with the same twenty-one
// values, four of which had no business existing, still being supplied. A
// context is the right answer for a class that genuinely depends on twenty-one
// things. This class depends on seventeen.
//
// FACT 2 — AS FILED, the slash layer was wired by hand in TWO places, and one
// of them was dead: `ReplRunner` built a `SlashCommandDispatcher` from nine
// collaborators inside its own constructor, and `LegacySlashRunner.FromServices`
// built a SECOND one, from the same nine, through
// `services.GetRequiredService<…>`. Nothing called `FromServices` — it was
// unreachable product code whose body was a service locator, which is #470's
// shape in a third file. #776 deleted both, leaving exactly one construction
// site, at the root (`CliInfrastructure.cs:73`), still fed by those same nine
// registered services. So neither wiring was substitutable: a test that wants a
// different dispatcher must build nine real collaborators first, which is why
// all three ReplRunner test call sites pass `new FakeToolRegistry()` and
// `new PermissionService(…)` positionally today.
//
// WHY THE RULES ARE DERIVED, NOT REMEMBERED
// -----------------------------------------
// Rule 1 reads the constructor's own text: a parameter that is never the
// right-hand side of a field assignment in the constructor body was not
// retained, and a parameter a class does not retain is a value the class
// resolved for its own convenience. Nothing here lists `tools` by name — rename
// the parameter, add a fifth constructor-feed, or move the class to another
// file, and the rule still holds on the same evidence.
//
// Rule 2 counts construction sites for one named type in the product. A
// container-owned type constructed from two places has two wirings by
// definition, and they will drift: the two sites already disagree about
// nothing today only because the dead one was copied and never run.
//
// SECOND MEASUREMENT — the OTHER mega-constructor, and what the 24 are
// ------------------------------------------------------------------
// Rule 1 and rule 2 above were written against `ReplRunner`, which the issue
// filed as "20/22 parameters". That class is down to 18 and the finding there
// is CLOSED (PR #776). The issue's finding 3 — `CellForgeReplRunner`,
// "22 parameters, same dependency shape spread across two runners" — was never
// measured by anyone, so it is measured here. From the tree:
//
//   ReplRunner           18 parameters   (was 21 when #776 measured it)
//   CellForgeReplRunner  24 parameters   (the issue says 22 — it has grown
//                                         by two: IPanelRegistry, and
//                                         IGitQuery from #857/#929)
//
// And the 24 are NOT twenty-four dependencies. Derived, by type, from the
// primary constructor's own text:
//
//   12  plain container services (IConfigStore, IProviderRegistry,
//       IAgentRegistry, AuthStore, ISessionStore, IRendererPipeline,
//       IEventBus, ITokenTracker, IAgent, ILogger<>, PluginReloadService,
//       IProviderHealthCheck)
//    6  MEMBERS OF ONE AGGREGATE THE ROOT ALREADY BUILDS — `CellForgeScreens`
//       (ReplRunner.cs:556), which the composition root constructs as a single
//       value and then hands over PIECE BY PIECE: ScreenSession, ChatScreen,
//       ChatScreenBridge, TerminalInputSource, ITerminalBackend,
//       IApprovalCoordinator. Six of the twenty-four parameters are one
//       dependency spelled six times.
//    3  optional ports the CONSUMER resolves for itself, through
//       `_rendererHost.GetService<…>()`: IPanelRegistry,
//       DiagnosticsAggregator, IGitQuery — #470's service-locator shape, in
//       the composition root's own consumer
//    2  per-run values, not services at all: the `Session` that
//       `RunCellForgeAsync` just created, and the `ITerminalModeController`
//       `CreateModeController()` picked for this OS
//    1  container-owned adapter, built by the ROOT from five registered
//       services (`CliInfrastructure.cs:92`) and handed in — it used to be
//       `new LegacySlashRunner(_slashes, _agentRegistry, _configStore,
//       _authStore, _providers)` written out by hand inside the consumer that
//       serves it, which is finding 2's shape one level down
//
// So the god-object reading is false, and the "bundle them in a ReplContext"
// prescription is not affordable either: the six loose aggregate members are
// already bundled (in `CellForgeScreens`), so a context would add a SECOND
// bundle beside the first rather than remove one. #776 reached the same verdict
// for the other runner and this guard is where that verdict is kept.
//
// WHAT THE SECOND HALF OF RULE 2 IS FOR
// ------------------------------------
// #776 moved `SlashCommandDispatcher` to the composition root and deleted the
// dead second wiring. It did not touch the adapter that dispatcher arrived in:
// `LegacySlashRunner` was still built BY HAND inside the consumer that serves
// it, from five collaborators the container already owns — the identical shape,
// one level down, in the same method. Rule 2 was written against one hard-coded
// type name, so it could not see this: it asked "where is `SlashCommandDispatcher`
// constructed?" and the answer was a clean single site at the root. The rule is
// now stated over the slash LAYER — both types, named once — which is what made
// it red on the tree, and the adapter's half is what this branch then removed.
//
// WHY A SOURCE SCAN
// -----------------
// "Is this parameter retained by its constructor" and "how many times is this
// type constructed" are both textual facts about code that never ships as an
// inspectable API — `SlashCommandDispatcher` is `internal sealed`, so a test
// double is not reachable without an axis the feature freeze (#555) does not
// permit. Reading the text is also the only way to see the dead wiring, which
// by definition no test executes.
//
// NON-VACUITY
// -----------
// `Rule_FlagsTheMaskedDefaults` is the positive control: issue #486's
// constructor verbatim, from which the walker must recover 21 parameters and
// name 4 offenders, plus a separate assertion that the fifth candidate
// (`loggerFactory`) is the allowed typed-logger production. Paired with the
// fixed spelling (18 parameters, every one retained or allowed → 0) so the
// rule cannot fire on the fix it asked for.
//
// `LoggerFactory_BuildingSomethingOtherThanALogger_IsStillFlagged` keeps that
// single exception from becoming a general amnesty for composition, which is
// the defect itself.
//
// `SiteScan_FlagsDuplicatedWiring` is the positive control for rule 2: two
// sites → flagged; one site → clean; and the case a naive `Contains` gets
// wrong — `new SlashCommandDispatcher(` appearing inside a `//` comment and
// inside a string literal must count for NOTHING. `SlashCommandDispatcher` is
// named in prose in five files under src/, and a gate that fires on a doc
// comment is a gate that gets switched off.
//
// `SiteScan_FlagsTheAdapterWiredByAConsumer` is the control for the SECOND
// type rule 2 now grades: the adapter's site inside a consumer directory must
// be reported, and the same spelling at the root must not be. Without it,
// widening rule 2 from one name to a set is indistinguishable from widening it
// to "any type", which would fire on `new CellForgeReplRunner(` — a type that
// legitimately cannot be container-resolved, because it needs the `Session`
// that was created one statement earlier.
//
// `CellForgeRunnerParameters_AreMeasured` re-derives the table in the header
// and asserts its two load-bearing numbers: that the primary-constructor walk
// recovers the whole signature, and that every member of the root-built
// aggregate reaches the runner — either as loose parameters (today) or as the
// aggregate itself (the split this issue still owes). It is the measurement
// half of the finding, and it is written to stay green through that split.
//
// `Perimeter_Files_StillExist` keeps both rules from passing over a tree they
// no longer describe.

using System.Text;
using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Source-level guards for issue #486: a REPL consumer's constructor may not
///     be a second composition root, and a container-owned type is constructed in
///     exactly one place.
/// </summary>
public sealed class ReplConstructorCompositionTests
{
    /// <summary>
    ///     The consumer whose signature is the finding. One file: the defect is one
    ///     constructor declaration in it.
    /// </summary>
    private const string RunnerRelative = "apps/Harbor.App.Cli/Repl/ReplRunner.cs";

    /// <summary>
    ///     The type with two hand-built wirings. Named once so both the scan and the
    ///     positive control quote the same spelling the product uses.
    /// </summary>
    private const string DispatcherType = "SlashCommandDispatcher";

    /// <summary>
    ///     The adapter the dispatcher arrives in, and the second half of the slash
    ///     layer rule 2 grades. It is container-owned in the same sense as the
    ///     dispatcher: every one of its five collaborators is a registered service,
    ///     and the composition root builds it from them. Building it inside
    ///     <c>ReplRunner.RunCellForgeAsync</c> is #486 finding 2's shape one level
    ///     down — the same defect #776 fixed for the dispatcher, which this rule
    ///     could not see while it was written against one type name.
    /// </summary>
    private const string AdapterType = "LegacySlashRunner";

    /// <summary>
    ///     The slash layer, in the two types it is spelled in. A named SET and not a
    ///     general "is this type container-owned" walk, on purpose: the walk is
    ///     textual, and "container-owned" has no textual definition. Guessing would
    ///     grade <c>new CellForgeReplRunner(</c> too — a type that cannot be
    ///     resolved from a container at all, because it is handed the <c>Session</c>
    ///     that <c>RunCellForgeAsync</c> created one statement earlier, plus the
    ///     <c>ITerminalModeController</c> this OS needs. Two names, each justified
    ///     where it is declared, is the honest shape.
    /// </summary>
    private static readonly string[] SlashLayerTypes = [DispatcherType, AdapterType];

    /// <summary>
    ///     The composition root for the REPL: the one place allowed to build the
    ///     dispatcher's collaborators, because it is the method that has the
    ///     <c>IServiceProvider</c> in scope to resolve them from.
    /// </summary>
    private const string CompositionRootRelative = "apps/Harbor.App.Cli/Commands/CliInfrastructure.cs";

    /// <summary>
    ///     The runner the issue's finding 3 is about — "22 parameters, same
    ///     dependency shape spread across two runners", never measured until now.
    ///     Primary constructor, so it needs its own walk (see
    ///     <see cref="ReadPrimaryConstructorParameters" />).
    /// </summary>
    private const string CellForgeRunnerRelative = "apps/Harbor.App.Cli/Repl/CellForgeReplRunner.cs";

    /// <summary>
    ///     The aggregate the root ALREADY builds for that runner and then hands over
    ///     one member at a time. Six of the runner's twenty-four parameters are this
    ///     one record.
    /// </summary>
    private const string AggregateType = "CellForgeScreens";

    /// <summary>
    ///     A consumer directory: a file under <c>Repl/</c> is a CONSUMER of the slash
    ///     layer by construction (it is the layer's own home directory), so a
    ///     construction site there is composition happening away from the root.
    /// </summary>
    private const string ConsumerDirectory = "/Repl/";

    // ── Rule 1: the constructor may not resolve anything for itself ───────────

    [Test]
    public async Task Constructor_RetainsEveryParameterItDeclares()
    {
        if (RepoPaths.RepoRoot is null)
        {
            return;
        }

        string full = Path.Combine(RepoPaths.RepoRoot, RunnerRelative);
        string text = File.ReadAllText(full);

        IReadOnlyList<string> parameters = ReadConstructorParameters(text, "ReplRunner");
        IReadOnlyList<string> offenders = MaskedDefaults(text, "ReplRunner", parameters);

        await Assert.That(parameters.Count).IsGreaterThan(8)
            .Because(
                "Non-vacuity, and the premise of the rule: ReplRunner is a class with a long, "
                + "order-sensitive constructor, and the rule is about which of those parameters are "
                + "dependencies. " + parameters.Count + " parameters were read from " + RunnerRelative
                + " — if the walk stopped matching (a primary-constructor rewrite, a moved file) the "
                + "offender list below is empty and this gate is decorative. RepoPaths.RepoRoot was "
                + (RepoPaths.RepoRoot is null ? "null" : "found") + ".");

        await Assert.That(offenders).IsEmpty()
            .Because(
                "A constructor parameter the class does not keep is not a dependency of the class — it "
                + "is a value the class resolved for someone else and then carried in its own signature. "
                + "Concretely: these parameters are read exactly once each, as arguments to a "
                + "`new " + DispatcherType + "(…)` in the constructor body, and are never stored in a "
                + "field. That makes ReplRunner a second composition root: it is why the signature is 21 "
                + "long, why adding one parameter in the middle silently rebinds arguments at every call "
                + "site, and why the three test call sites must each construct real collaborators "
                + "positionally instead of injecting a dispatcher. Fix the CAUSE — build the dispatcher at "
                + "the composition root, where the services are resolved anyway, and take it as a "
                + "parameter — rather than bundling these into a context object, which would MOVE five "
                + "masked defaults one level down and leave their count unchanged. "
                + "A parameter that is composed rather than assigned (`loggerFactory.CreateLogger<T>()`) "
                + "is the same defect: building a collaborator is not retaining a dependency. "
                + "Offenders:" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    // ── Rule 2: one container-owned type, one construction site ──────────────

    [Test]
    public async Task ContainerOwnedType_IsConstructedInTheCompositionRootOnly()
    {
        if (RepoPaths.RepoRoot is null)
        {
            return;
        }

        IReadOnlyList<string> sources = ProductSources();
        await Assert.That(sources.Count).IsGreaterThan(50)
            .Because(
                "Non-vacuity: the scan is over the product tree, and a walk that returned nothing would "
                + "satisfy 'exactly one site' trivially. " + sources.Count + " .cs files were enumerated "
                + "under apps/ and src/." );

        // One pass over the tree, both types graded from the same evidence. Reading
        // each file once and matching both names in it keeps this a single scan —
        // and keeps the two verdicts comparable, which is the point of asking about
        // the layer rather than about one class.
        var sitesByType = SlashLayerTypes.ToDictionary(type => type, type => new List<string>());
        foreach (string path in sources)
        {
            string text = File.ReadAllText(path);
            foreach (string type in SlashLayerTypes)
            {
                foreach (int _ in ConstructionSites(text, type).ToList())
                {
                    sitesByType[type].Add(Relative(path));
                }
            }
        }

        foreach (string type in SlashLayerTypes)
        {
            List<string> sites = sitesByType[type];
            List<string> offenders =
            [
                .. sites
                    .Where(site => site.Contains(ConsumerDirectory, StringComparison.Ordinal))
                    .Select(site =>
                        site + " constructs a " + type + " from inside the layer it serves. That is "
                        + "composition away from the composition root: the container-owned collaborators are "
                        + "resolved here, so the type is unreachable to DI and cannot be substituted without "
                        + "building all of them by hand.")
            ];

            await Assert.That(sites.Count).IsEqualTo(1)
                .Because(
                    type + " is container-owned — its collaborators are all registered services — so every "
                    + "`new` of it is a re-wiring of the same dependencies. Two sites means two wirings, and "
                    + "two wirings drift: nothing at either call site says the other exists. One site and zero "
                    + "sites are both wrong here — zero means this scan stopped recognising the spelling, "
                    + "which would make the consumer check below pass vacuously. Sites found:"
                    + Environment.NewLine + string.Join(Environment.NewLine, sites));

            await Assert.That(offenders).IsEmpty()
                .Because(
                    "The single site must be the composition root, not a consumer. Consumers of the slash "
                    + "layer take it as a parameter; the root builds it once, from the services it is "
                    + "already resolving. Offenders:" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
        }
    }

    // ── The measurement half: what the other 24 parameters actually are ────

    [Test]
    public async Task CellForgeRunnerParameters_AreMeasured()
    {
        if (RepoPaths.RepoRoot is null)
        {
            return;
        }

        string runnerText = File.ReadAllText(Path.Combine(RepoPaths.RepoRoot, CellForgeRunnerRelative));
        string aggregateText = File.ReadAllText(Path.Combine(RepoPaths.RepoRoot, RunnerRelative));

        IReadOnlyList<string> parameters = ReadPrimaryConstructorParameters(runnerText, "CellForgeReplRunner");
        IReadOnlyList<string> parameterTypes = ReadPrimaryConstructorParameterTypes(runnerText, "CellForgeReplRunner");
        IReadOnlyList<string> aggregate = ReadRecordMemberTypes(aggregateText, AggregateType);

        await Assert.That(parameters.Count).IsGreaterThan(8)
            .Because(
                "Non-vacuity, and the premise of the measurement: this is the constructor issue #486's "
                + "finding 3 calls \"22 parameters\". The walk below recovered " + parameters.Count
                + " — if a primary-constructor rewrite, a moved file or a stop matching mid-list silently "
                + "shortened the signature, the arithmetic in the header of this file would be wrong and "
                + "the composition conclusion drawn from it would be an artefact of the matcher, not of "
                + "the code.");

        await Assert.That(parameterTypes.Count).IsEqualTo(parameters.Count)
            .Because(
                "The two walks must agree on the same signature: names are used to report, types are used "
                + "to classify, and a classification computed over a shorter list than the count it "
                + "accompanies is a silent subset — which is how a rule ends up passing on half a "
                + "constructor. Names:" + string.Join(", ", parameters) + " / types:"
                + string.Join(", ", parameterTypes));

        // Every member of the one aggregate the root already builds must reach the
        // runner — today as six loose parameters, after the split this issue still
        // owes as the aggregate itself. Either shape is the finding; a third one
        // (a member quietly dropped, or the aggregate invented locally) is not, and
        // is what this catches.
        var unreached = aggregate.Where(t => !parameterTypes.Contains(t) && !parameterTypes.Contains(AggregateType)).ToList();

        await Assert.That(unreached).IsEmpty()
            .Because(
                AggregateType + " is built ONCE by the composition root and handed to the runner; its "
                + "members are therefore one dependency, not six. If a member type reaches the runner "
                + "neither as a loose parameter nor through the aggregate itself, the wiring has forked and "
                + "the table in this file's header no longer describes the tree. Aggregate members:"
                + string.Join(", ", aggregate) + "; runner parameter types:" + string.Join(", ", parameterTypes)
                + "; unreached:" + string.Join(", ", unreached));

        await Assert.That(aggregate.Count).IsGreaterThan(1)
            .Because(
                "Non-vacuity for the classification itself: an aggregate of one member is not an aggregate, "
                + "and the whole reading of the twenty-four parameters as \"twelve services plus one bundle "
                + "spelled six times\" rests on there being several. Members recovered: " + aggregate.Count);
    }

    // ── Control for the second type rule 2 grades ─────────────────────────

    [Test]
    public async Task SiteScan_FlagsTheAdapterWiredByAConsumer()
    {
        // The shape the fix removes: the adapter built inside the consumer that
        // serves it. This is the positive control for the adapter half of rule 2 —
        // without it, widening the rule from one type name to a set cannot be
        // distinguished from widening it to "any type".
        const string WiredByConsumer = """
            // ReplRunner.cs — RunCellForgeAsync
            var runner = new CellForgeReplRunner(
                _configStore,
                new LegacySlashRunner(
                    _slashes,
                    _agentRegistry,
                    _configStore,
                    _authStore,
                    _providers),
                sessionResult.Value);
            """;

        // The shape the fix produces: the same spelling, at the root.
        const string WiredAtRoot = """
            // CliInfrastructure.cs
            var slashes = new SlashCommandDispatcher(
                services.GetRequiredService<ILogger<SlashCommandDispatcher>>(),
                services.GetRequiredService<IToolRegistry>());
            var legacySlash = new LegacySlashRunner(
                slashes,
                services.GetRequiredService<IAgentRegistry>(),
                services.GetRequiredService<IConfigStore>(),
                services.GetRequiredService<AuthStore>(),
                services.GetRequiredService<IProviderRegistry>());
            """;

        // And the cry-wolf control for the set: a type that is genuinely NOT
        // container-owned and IS legitimately built where it is used, because it
        // needs the value the previous statement produced. Rule 2 names its types;
        // it must not be a "any `new` in a consumer" rule wearing a name.
        const string NotSlashLayer = """
            // ReplRunner.cs — RunCellForgeAsync
            var runner = new CellForgeReplRunner(
                configStore,
                new PromptPipeline(this, _catalog, logger, Tokens, new Lazy<LegacySlashRunner>(static () => null!)),
                sessionResult.Value);
            """;

        var byConsumer = ConstructionSites(WiredByConsumer, AdapterType).Count();
        await Assert.That(byConsumer).IsEqualTo(1)
            .Because(
                "The positive control for the adapter rule: the product built the adapter inside the "
                + "consumer, and that spelling must still be recognised. A count of zero means the walk no "
                + "longer matches a `new` nested inside another `new`'s argument list — the exact shape "
                + "this site has — and the gate would go green with the defect in place.");

        await Assert.That(ConstructionSites(WiredAtRoot, AdapterType).Count()).IsEqualTo(1)
            .Because(
                "The single site at the root is the shape the fix produces, at both types' spelling. "
                + "Flagging it would make the gate unsatisfiable.");

        await Assert.That(ConstructionSites(NotSlashLayer, AdapterType).Count()).IsEqualTo(0)
            .Because(
                "Rule 2 grades the slash layer by name, and nothing else. A type that cannot be resolved from "
                + "a container — one that takes the Session created one statement earlier, or the mode "
                + "controller this OS needs — is built where it is used by design, and a rule that flagged it "
                + "would be a \"no `new` in a consumer\" rule, which is neither this finding nor #486's.");
    }

    // ── Self-checks: neither rule may run vacuously ─────────────────────────

    [Test]
    public async Task Rule_FlagsTheMaskedDefaults()
    {
        // Issue #486's constructor verbatim, and the five parameters of it that
        // exist only to feed the `new` on the last line. The control asserts a
        // count, not a boolean: a matcher that reported the first offender and
        // stopped would satisfy a weaker rule and under-report forever.
        const string Current = """
            public ReplRunner(
                ILogger<ReplRunner> logger,
                IConfigStore configStore,
                AuthStore authStore,
                OnboardingWizard wizard,
                ITuiRenderer renderer,
                IEventBus eventBus,
                IAgent agent,
                ISessionStore sessionStore,
                IAgentRegistry agentRegistry,
                IProviderRegistry providers,
                IToolRegistry tools,
                IPermissionService permissions,
                ILoggerFactory loggerFactory,
                Harbor.Hosting.PluginReloadService? pluginReload,
                Harbor.Hosting.Rendering.IRendererPipeline? rendererPipeline,
                ITokenTracker? tokens,
                Func<CellForgeScreens> cellForgeScreens,
                IServiceProvider rendererHost,
                Func<IReadOnlyList<SkillFreshnessEntry>>? skillRefresh = null,
                Func<IReadOnlyList<string>, Task<SkillUpdateReport>>? skillUpdate = null,
                IProviderHealthCheck? healthCheck = null)
            {
                _logger = logger;
                _configStore = configStore;
                _authStore = authStore;
                _wizard = wizard;
                _renderer = renderer;
                _eventBus = eventBus;
                _agent = agent;
                _sessionStore = sessionStore;
                _agentRegistry = agentRegistry;
                _providers = providers;
                _cellForgeLogger = loggerFactory.CreateLogger<CellForgeReplRunner>();
                _slashes = new SlashCommandDispatcher(
                    loggerFactory.CreateLogger<SlashCommandDispatcher>(), tools, sessionStore, wizard, permissions, pluginReload, rendererPipeline, skillRefresh, skillUpdate);
                _rendererPipeline = rendererPipeline;
                _pluginReload = pluginReload;
                _healthCheck = healthCheck;
                _tokens = tokens;
                _cellForgeScreens = cellForgeScreens;
                _rendererHost = rendererHost;
            }
            """;

        // The fixed spelling. `tools` and `permissions` are gone (they were the
        // dispatcher's, resolved at the root now) and so are the two skill
        // delegates. Every remaining parameter is either retained outright or is
        // the one allowed typed-logger production, so the rule must be silent — a
        // gate that fires on the fix is a gate that gets deleted.
        const string Fixed = """
            public ReplRunner(
                ILogger<ReplRunner> logger,
                IConfigStore configStore,
                AuthStore authStore,
                OnboardingWizard wizard,
                ITuiRenderer renderer,
                IEventBus eventBus,
                IAgent agent,
                ISessionStore sessionStore,
                IAgentRegistry agentRegistry,
                IProviderRegistry providers,
                SlashCommandDispatcher slashes,
                ILoggerFactory loggerFactory,
                Harbor.Hosting.PluginReloadService? pluginReload,
                Harbor.Hosting.Rendering.IRendererPipeline? rendererPipeline,
                ITokenTracker? tokens,
                Func<CellForgeScreens> cellForgeScreens,
                IServiceProvider rendererHost,
                IProviderHealthCheck? healthCheck = null)
            {
                _logger = logger;
                _configStore = configStore;
                _authStore = authStore;
                _wizard = wizard;
                _renderer = renderer;
                _eventBus = eventBus;
                _agent = agent;
                _sessionStore = sessionStore;
                _agentRegistry = agentRegistry;
                _providers = providers;
                _slashes = slashes;
                _cellForgeLogger = loggerFactory.CreateLogger<CellForgeReplRunner>();
                _rendererPipeline = rendererPipeline;
                _pluginReload = pluginReload;
                _healthCheck = healthCheck;
                _tokens = tokens;
                _cellForgeScreens = cellForgeScreens;
                _rendererHost = rendererHost;
            }
            """;

        IReadOnlyList<string> currentParams = ReadConstructorParameters(Current, "ReplRunner");
        await Assert.That(currentParams.Count).IsEqualTo(21)
            .Because(
                "The positive control quotes issue #486's signature, which declared 21 parameters. If "
                + "the parameter walk under-counts — a default value, a fully-qualified type with dots "
                + "in it, a generic argument list holding a comma — every count below is wrong and the "
                + "rule is measuring a subset of the signature. Read: " + string.Join(", ", currentParams));

        await Assert.That(MaskedDefaults(Current, "ReplRunner", currentParams)).IsEquivalentTo(
            new[] { "tools", "permissions", "skillRefresh", "skillUpdate" })
            .Because(
                "The four masked defaults, by name, are the point of the whole guard. Each is read "
                + "exactly once in the product, as an argument to the dispatcher's constructor. If this "
                + "list changes, the finding in #486 has changed shape and the rule's premise needs "
                + "re-reading rather than the test being adjusted to match the code.");

        // `loggerFactory` is the fifth, and the reason it is NOT in the list above is
        // recorded in ProducesATypedLogger. Asserted separately so that narrowing is
        // a visible change to this test rather than a silent one.
        await Assert.That(MaskedDefaults(Current, "ReplRunner", currentParams).Contains("loggerFactory")).IsFalse()
            .Because(
                "loggerFactory creates the typed logger of CellForgeReplRunner — a class this one "
                + "CONSTRUCTS, and therefore cannot be injected (S6672 forbids a class from holding "
                + "ILogger<T> for a T it does not own; the repo already works around S6672 twice on "
                + "purpose, in ToolDispatcher and in AgentLoop). One typed-logger production is the "
                + "rule's single allowed composition. It is allowed by TYPE and use, not because "
                + "composition is acceptable here.");

        IReadOnlyList<string> fixedParams = ReadConstructorParameters(Fixed, "ReplRunner");
        await Assert.That(MaskedDefaults(Fixed, "ReplRunner", fixedParams)).IsEmpty()
            .Because(
                "The fixed spelling must stay clean, and it is also the control that proves the rule is "
                + "about retention and not about length: this constructor has 18 parameters and is "
                + "legitimate, because every one of them is either kept outright or is the one allowed "
                + "typed-logger production. A rule that fired here would be a length rule, which is the "
                + "gate this issue explicitly must not ship.");
    }

    [Test]
    public async Task LoggerFactory_BuildingSomethingOtherThanALogger_IsStillFlagged()
    {
        // The exception in ProducesATypedLogger is the one way composition is allowed
        // in a consumer's constructor, and the control that keeps it from becoming a
        // general amnesty for composition — which is the whole defect this guard
        // exists for. A logger factory spent on anything other than a logger is the
        // same shape as the four masked defaults: a value the class resolved for a
        // collaborator and did not keep.
        const string NotALogger = """
            public ReplRunner(
                ILogger<ReplRunner> logger,
                IToolRegistry tools,
                ILoggerFactory loggerFactory)
            {
                _logger = logger;
                _tools = loggerFactory.CreateSomethingElse<ToolRegistry>();
            }
            """;

        // And the case a bare "is the name loggerFactory" check would wrongly pass:
        // the type is right, the USE is not. If the exception is ever widened to
        // "the parameter is called loggerFactory", this stops flagging and the guard
        // has quietly become decorative.
        IReadOnlyList<string> offenders = MaskedDefaults(
            NotALogger, "ReplRunner", ReadConstructorParameters(NotALogger, "ReplRunner"));

        await Assert.That(offenders).IsEquivalentTo(new[] { "tools", "loggerFactory" })
            .Because(
                "A factory spent on anything but a typed logger is a masked default, and so is the "
                + "service dragged in beside it. The single allowed composition is "
                + "ILoggerFactory.CreateLogger<T>() — and it is allowed because S6672 makes injecting "
                + "ILogger<T> for a T the class does not own impossible, not because composing here is "
                + "acceptable.");
    }

    [Test]
    public async Task SiteScan_FlagsDuplicatedWiring()
    {
        // Both live wirings, at the shapes the product has them.
        const string TwoSites = """
            // ReplRunner.cs
            _slashes = new SlashCommandDispatcher(
                loggerFactory.CreateLogger<SlashCommandDispatcher>(), tools, sessionStore, wizard, permissions);
            // LegacySlashRunner.cs
            internal static LegacySlashRunner FromServices(IServiceProvider services) => new(
                new SlashCommandDispatcher(
                    services.GetRequiredService<ILogger<SlashCommandDispatcher>>(),
                    services.GetRequiredService<IToolRegistry>()),
                services.GetRequiredService<IAgentRegistry>());
            """;

        const string OneSite = """
            var slashes = new SlashCommandDispatcher(
                services.GetRequiredService<ILogger<SlashCommandDispatcher>>(),
                services.GetRequiredService<IToolRegistry>());
            """;

        // The cry-wolf control. The dispatcher's own type is named in prose across
        // src/ (ITuiRenderer.cs, SlashCommandCatalog.cs) and a fully-qualified
        // spelling must match too, so the control pins the qualifier handling as
        // well as the comment handling.
        const string ProseOnly = """
            /// <summary>
            ///     A <c>SlashCommandDispatcher</c> owns that and hands back an outcome.
            ///     Legacy wiring spelled `new SlashCommandDispatcher(...)` used to live here.
            /// </summary>
            public sealed class Note
            {
                public const string Example = "var s = new SlashCommandDispatcher(logger, tools);";
            }
            """;

        await Assert.That(ConstructionSites(TwoSites, DispatcherType).Count()).IsEqualTo(2)
            .Because(
                "The positive control for rule 2: the product had two wirings of one container-owned "
                + "type. A miss here means the walk no longer recognises the shape and duplicated "
                + "wiring could be reintroduced with the gate green.");

        await Assert.That(ConstructionSites(OneSite, DispatcherType).Count()).IsEqualTo(1)
            .Because(
                "The single site at the root is the shape the fix produces. Flagging it would make the "
                + "gate unsatisfiable.");

        await Assert.That(ConstructionSites(ProseOnly, DispatcherType).Count()).IsEqualTo(0)
            .Because(
                "A `new T(` inside a doc comment or a string literal is not a construction site, and "
                + "the type name appears in prose in several files under src/. A scan that counted those "
                + "would fail on documentation edits and teach everyone to ignore it.");
    }

    [Test]
    public async Task Perimeter_Files_StillExist()
    {
        if (RepoPaths.RepoRoot is null)
        {
            return;
        }

        foreach (string relative in new[] { RunnerRelative, CompositionRootRelative, CellForgeRunnerRelative })
        {
            await Assert.That(File.Exists(Path.Combine(RepoPaths.RepoRoot, relative))).IsTrue()
                .Because(
                    "The guard's perimeter moved. " + relative + " is named by these rules; if it is "
                    + "renamed or moved, the gate is scanning a tree it no longer describes and passes "
                    + "vacuously. Update the constant in the same commit that moves the file.");
        }
    }

    // ── scanning ────────────────────────────────────────────────────────────

    /// <summary>
    ///     The parameter names of a PRIMARY constructor — <c>class C(A a, B b)</c> —
    ///     which <see cref="ReadConstructorParameters" /> cannot see: that one looks
    ///     for <c>public C(</c>, and the class under measurement here has no explicit
    ///     constructor at all. Comments are stripped BEFORE the comma split, because
    ///     the primary constructor is the one place in this tree where the parameter
    ///     list carries block comments (the #49 and #857 rationales), and an
    ///     unbalanced parenthesis inside one would desynchronise the depth counter
    ///     and tear a parameter in half.
    /// </summary>
    private static IReadOnlyList<string> ReadPrimaryConstructorParameters(string text, string typeName) =>
        [.. PrimaryConstructorEntries(text, typeName).Select(entry => entry.Name)];

    /// <summary>
    ///     The same walk, keeping each parameter's declared type — the classification
    ///     the finding is about is a comparison of TYPES against the aggregate's, not
    ///     of names (the runner calls its <c>ChatScreen</c> parameter <c>screen</c>).
    /// </summary>
    private static IReadOnlyList<string> ReadPrimaryConstructorParameterTypes(string text, string typeName) =>
        [.. PrimaryConstructorEntries(text, typeName).Select(entry => entry.Type)];

    /// <summary>
    ///     The declared types of a positional record's members — the other half of the
    ///     comparison. A record's parameter list is spelled like a primary
    ///     constructor's, so the same walk reads it.
    /// </summary>
    private static IReadOnlyList<string> ReadRecordMemberTypes(string text, string typeName)
    {
        string clean = StripCommentsAndLiterals(text);
        Match declaration = RecordDeclaration(typeName).Match(clean);
        if (!declaration.Success)
        {
            return [];
        }

        int open = clean.IndexOf('(', declaration.Index);
        int close = MatchParen(clean, open);
        return close < 0 ? [] : [.. SplitTypeAndName(clean[(open + 1)..close]).Select(pair => pair.Type)];
    }

    private static IReadOnlyList<(string Type, string Name)> PrimaryConstructorEntries(string text, string typeName)
    {
        string clean = StripCommentsAndLiterals(text);
        Match declaration = PrimaryConstructorDeclaration(typeName).Match(clean);
        if (!declaration.Success)
        {
            return [];
        }

        int open = clean.IndexOf('(', declaration.Index);
        int close = MatchParen(clean, open);
        return close < 0 ? [] : SplitTypeAndName(clean[(open + 1)..close]);
    }

    private static Regex PrimaryConstructorDeclaration(string typeName) => new(
        $@"class\s+{Regex.Escape(typeName)}\s*\(",
        RegexOptions.Compiled);

    private static Regex RecordDeclaration(string typeName) => new(
        $@"record\s+{Regex.Escape(typeName)}\s*\(",
        RegexOptions.Compiled);

    /// <summary>
    ///     Splits a parameter list into (type, name) pairs, dropping the default value
    ///     at depth 0 first. <c>TrimEnd</c> before the split is load-bearing for the
    ///     same reason it is in <see cref="ReadConstructorParameters" />: cutting at
    ///     '=' leaves a trailing space, the depth-0 space search then finds THAT
    ///     space, and every defaulted parameter is silently reported with an empty
    ///     name — which is five of the twenty-four measured here.
    /// </summary>
    private static IReadOnlyList<(string Type, string Name)> SplitTypeAndName(string list)
    {
        var entries = new List<(string Type, string Name)>();
        foreach (string parameter in SplitTopLevel(list))
        {
            string head = CutAtDepthZero(parameter, '=').TrimEnd();
            if (string.IsNullOrWhiteSpace(head))
            {
                continue;
            }

            int split = LastSpaceAtDepthZero(head);
            if (split <= 0 || split >= head.Length - 1)
            {
                continue;
            }

            string name = head[(split + 1)..].Trim();
            if (name.Length > 0)
            {
                entries.Add((head[..split].Trim(), name));
            }
        }

        return entries;
    }

    /// <summary>
    ///     The parameters of <paramref name="typeName" />'s explicit constructor, in
    ///     declaration order. Reads the RAW text because a default value may contain
    ///     a comma.
    /// </summary>
    private static IReadOnlyList<string> ReadConstructorParameters(string text, string typeName)
    {
        var names = new List<string>();
        Match declaration = ConstructorDeclaration(typeName).Match(text);
        if (!declaration.Success)
        {
            return names;
        }

        int open = text.IndexOf('(', declaration.Index);
        int close = MatchParen(text, open);
        if (close < 0)
        {
            return names;
        }

        foreach (string parameter in SplitTopLevel(text[(open + 1)..close]))
        {
            // Drop the default value at depth 0, then cut at the last space at
            // depth 0 — the boundary between type and name. TrimEnd before the
            // split is load-bearing: cutting at '=' leaves the space in front of
            // it, and the space search would then pick THAT space and report an
            // empty name, silently dropping every defaulted parameter — which is
            // three of the five the rule is about.
            string head = CutAtDepthZero(parameter, '=').TrimEnd();
            if (string.IsNullOrWhiteSpace(head))
            {
                continue;
            }

            int split = LastSpaceAtDepthZero(head);
            if (split <= 0 || split >= head.Length - 1)
            {
                continue;
            }

            string name = head[(split + 1)..].Trim();
            if (name.Length > 0)
            {
                names.Add(name);
            }
        }

        return names;
    }

    /// <summary>
    ///     The constructor body of <paramref name="typeName" />, comments and string
    ///     literals blanked so a snippet quoted in prose is not read as code.
    /// </summary>
    private static string ConstructorBody(string text, string typeName)
    {
        Match declaration = ConstructorDeclaration(typeName).Match(text);
        if (!declaration.Success)
        {
            return string.Empty;
        }

        int open = text.IndexOf('(', declaration.Index);
        int close = MatchParen(text, open);
        if (close < 0)
        {
            return string.Empty;
        }

        int bodyOpen = text.IndexOf('{', close);
        if (bodyOpen < 0)
        {
            return string.Empty;
        }

        int bodyClose = MatchBrace(text, bodyOpen);
        return bodyClose < 0
            ? string.Empty
            : StripCommentsAndLiterals(text[bodyOpen..(bodyClose + 1)]);
    }

    private static Regex ConstructorDeclaration(string typeName) => new(
        $@"public\s+{Regex.Escape(typeName)}\s*\(",
        RegexOptions.Compiled);

    /// <summary>
    ///     A construction site of <paramref name="typeName" />, with the namespace
    ///     qualifier allowed: <c>new SlashCommandDispatcher(</c> and
    ///     <c>new Harbor.App.Cli.Repl.SlashCommandDispatcher(</c> are the same site.
    /// </summary>
    private static IEnumerable<int> ConstructionSites(string source, string typeName)
    {
        foreach (Match match in ConstructionSite(typeName).Matches(StripCommentsAndLiterals(source)))
        {
            yield return match.Index;
        }
    }

    private static Regex ConstructionSite(string typeName) => new(
        $@"new\s+(?:[A-Za-z_]\w*\.)*{Regex.Escape(typeName)}\s*\(",
        RegexOptions.Compiled);

    /// <summary>
    ///     The parameters of <paramref name="parameters" /> that the constructor
    ///     never retains — that is, never the direct right-hand side of a field
    ///     assignment in the body, and never spent producing a typed logger.
    /// </summary>
    private static IReadOnlyList<string> MaskedDefaults(string text, string typeName, IReadOnlyList<string> parameters)
    {
        string body = ConstructorBody(text, typeName);
        return
        [
            .. parameters
                .Where(name => !Regex.IsMatch(
                    body,
                    @"=\s*" + Regex.Escape(name) + @"\s*;",
                    RegexOptions.CultureInvariant))
                .Where(name => !ProducesATypedLogger(name, body))
        ];
    }

    /// <summary>
    ///     The one composition a consumer is allowed to perform for itself: creating
    ///     the typed logger of a class it CONSTRUCTS.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This exception is narrow on purpose, and it exists because the alternative
    ///         is not available. A class cannot inject <c>ILogger&lt;T&gt;</c> for a
    ///         <c>T</c> it does not own — S6672 forbids exactly that, and the repo already
    ///         works around S6672 twice on purpose (<c>ToolDispatcher</c> takes its own
    ///         <c>ILogger&lt;ToolDispatcher&gt;</c> with the comment "own category instead of
    ///         the borrowed ILogger&lt;AgentLoop&gt;", and <c>AgentLoop</c> hands its
    ///         fallback a <c>NullLogger&lt;ToolDispatcher&gt;</c> for the same reason). So
    ///         the parameter is retained by USE and its type is checked, not its call
    ///         site: <c>loggerFactory</c> is allowed exactly when it is spent on
    ///         <c>CreateLogger&lt;</c>.
    ///     </para>
    ///     <para>
    ///         What this must NOT become is a general escape for composition — that is the
    ///         whole defect. <c>LoggerFactory_BuildingSomethingOtherThanALogger_IsStillFlagged</c>
    ///         is the control that keeps the door shut.
    ///     </para>
    /// </remarks>
    private static bool ProducesATypedLogger(string parameter, string body) =>
        Regex.IsMatch(body, @"\b" + Regex.Escape(parameter) + @"\s*\.\s*CreateLogger\s*<", RegexOptions.CultureInvariant);

    /// <summary>Every <c>.cs</c> file in the product tree, sorted, obj/bin excluded.</summary>
    private static IReadOnlyList<string> ProductSources()
    {
        if (RepoPaths.RepoRoot is null)
        {
            return [];
        }

        var found = new List<string>();
        foreach (string top in new[] { "apps", "src" })
        {
            string dir = Path.Combine(RepoPaths.RepoRoot, top);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            found.AddRange(Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories)
                .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                            && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)));
        }

        return [.. found.OrderBy(p => p, StringComparer.Ordinal)];
    }

    /// <summary>Repo-relative, forward-slashed, so a failure reads the same on every runner.</summary>
    private static string Relative(string path)
    {
        string root = RepoPaths.RepoRoot ?? string.Empty;
        string relative = path.StartsWith(root, StringComparison.Ordinal) ? path[root.Length..] : path;
        return relative.Replace('\\', '/').TrimStart('/');
    }

    private static int MatchParen(string text, int open)
    {
        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            if (text[i] == '(')
            {
                depth++;
            }
            else if (text[i] == ')' && --depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    private static int MatchBrace(string text, int open)
    {
        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            if (text[i] == '{')
            {
                depth++;
            }
            else if (text[i] == '}' && --depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    ///     Splits on commas that are not inside <c>&lt;&gt;</c> or <c>()</c> — a bare
    ///     split would tear <c>Func&lt;IReadOnlyList&lt;string&gt;, Task&lt;…&gt;&gt;</c> in half.
    /// </summary>
    private static IReadOnlyList<string> SplitTopLevel(string list)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        int depth = 0;

        foreach (char c in list)
        {
            if (c is '<' or '(')
            {
                depth++;
            }
            else if (c is '>' or ')')
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                parts.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        if (current.ToString().Trim().Length > 0)
        {
            parts.Add(current.ToString());
        }

        return parts;
    }

    private static string CutAtDepthZero(string text, char target)
    {
        int depth = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is '<' or '(')
            {
                depth++;
            }
            else if (c is '>' or ')')
            {
                depth--;
            }
            else if (c == target && depth == 0)
            {
                return text[..i];
            }
        }

        return text;
    }

    /// <summary>
    ///     Index of the last space at angle/paren depth 0 — the boundary between a
    ///     parameter's type and its name. Spaces INSIDE a generic argument list are
    ///     at depth 1 and must not win.
    /// </summary>
    private static int LastSpaceAtDepthZero(string text)
    {
        int depth = 0;
        int found = -1;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is '<' or '(')
            {
                depth++;
            }
            else if (c is '>' or ')')
            {
                depth--;
            }
            else if (c == ' ' && depth == 0)
            {
                found = i;
            }
        }

        return found;
    }

    /// <summary>
    ///     Blanks comments and string literals, preserving line structure, so a
    ///     snippet quoted in prose — including in the guards' own headers, which live
    ///     in the same tree — is never read as a declaration.
    /// </summary>
    private static string StripCommentsAndLiterals(string text)
    {
        var builder = new StringBuilder(text.Length);
        int i = 0;

        while (i < text.Length)
        {
            if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n')
                {
                    builder.Append(' ');
                    i++;
                }

                continue;
            }

            if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                builder.Append("  ");
                i += 2;
                while (i + 1 < text.Length && !(text[i] == '*' && text[i + 1] == '/'))
                {
                    builder.Append(text[i] == '\n' ? '\n' : ' ');
                    i++;
                }

                builder.Append("  ");
                i = Math.Min(i + 2, text.Length);
                continue;
            }

            if (text[i] is '"' or '\'')
            {
                char quote = text[i];
                builder.Append(' ');
                i++;
                while (i < text.Length && text[i] != quote)
                {
                    builder.Append(text[i] == '\n' ? '\n' : ' ');
                    i++;
                }

                i = Math.Min(i + 1, text.Length);
                continue;
            }

            builder.Append(text[i]);
            i++;
        }

        return builder.ToString();
    }
}
