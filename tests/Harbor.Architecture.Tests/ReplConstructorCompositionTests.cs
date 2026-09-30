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
// FACT 1 — five of the twenty-one are not dependencies of ReplRunner at all.
// Measured by occurrence count in the file, each of these appears EXACTLY
// TWICE: once in the parameter list, once on line 92. They are never stored in
// a field, never read, never passed on:
//
//     IToolRegistry                      tools
//     IPermissionService                permissions
//     Func<IReadOnlyList<SkillFreshnessEntry>>? skillRefresh
//     Func<IReadOnlyList<string>, Task<SkillUpdateReport>>? skillUpdate
//     ILoggerFactory                     loggerFactory   (one of its two uses)
//
// Their single use is as an argument to `new SlashCommandDispatcher(...)` in
// the constructor. They are not what ReplRunner needs; they are what
// ReplRunner was resolving FOR something else and dragging into its own
// signature to do it. `ILoggerFactory` is the same fact in half measure: it is
// also used for `_cellForgeLogger = loggerFactory.CreateLogger<…>()`, which is
// composition (building a collaborator) wearing the costume of an assignment.
//
// This is the distinction the issue's own prescription gets wrong, and getting
// it wrong is the expensive mistake: the proposed fix is to bundle the
// parameters into a `ReplContext` and inject that. Bundling them would MOVE
// five masked defaults one level down and make the mega-constructor a
// well-typed one — twenty-one names replaced by one, with the same twenty-one
// values, five of which had no business existing, still being supplied. A
// context is the right answer for a class that genuinely depends on twenty-one
// things. This class depends on sixteen.
//
// FACT 2 — the slash layer is wired by hand in TWO places, and one of them is
// dead. `ReplRunner.cs:91` builds a `SlashCommandDispatcher` from nine
// collaborators. `LegacySlashRunner.cs:52` builds a SECOND one, from the same
// nine, through `services.GetRequiredService<…>`. Nothing calls
// `LegacySlashRunner.FromServices` — it is unreachable product code whose
// body is a service locator, which is #470's shape in a third file. So the
// slash layer has two wirings, one of them unreached, and neither substitutable:
// a test that wants a different dispatcher must build nine real collaborators
// first, which is why all three ReplRunner test call sites pass
// `new FakeToolRegistry()` and `new PermissionService(…)` positionally today.
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
// 5 offenders. Paired with the fixed spelling (17 parameters, all retained →
// 0) so the rule cannot fire on the fix it asked for.
//
// `SiteScan_FlagsDuplicatedWiring` is the positive control for rule 2: two
// sites → flagged; one site → clean; and the case a naive `Contains` gets
// wrong — `new SlashCommandDispatcher(` appearing inside a `//` comment and
// inside a string literal must count for NOTHING. `SlashCommandDispatcher` is
// named in prose in five files under src/, and a gate that fires on a doc
// comment is a gate that gets switched off.
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
    ///     The composition root for the REPL: the one place allowed to build the
    ///     dispatcher's collaborators, because it is the method that has the
    ///     <c>IServiceProvider</c> in scope to resolve them from.
    /// </summary>
    private const string CompositionRootRelative = "apps/Harbor.App.Cli/Commands/CliInfrastructure.cs";

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

        var sites = new List<string>();
        foreach (string path in sources)
        {
            foreach (int _ in ConstructionSites(File.ReadAllText(path), DispatcherType).ToList())
            {
                sites.Add(Relative(path));
            }
        }

        var offenders = new List<string>();
        foreach (string site in sites)
        {
            if (site.Contains(ConsumerDirectory, StringComparison.Ordinal))
            {
                offenders.Add(
                    site + " constructs a " + DispatcherType + " from inside the layer it serves. That is "
                    + "composition away from the composition root: the container-owned collaborators are "
                    + "resolved here, so the type is unreachable to DI and cannot be substituted without "
                    + "building all nine of them by hand.");
            }
        }

        await Assert.That(sites.Count).IsEqualTo(1)
            .Because(
                DispatcherType + " is container-owned — its nine collaborators are all registered "
                + "services — so every `new` of it is a re-wiring of the same nine dependencies. Two "
                + "sites means two wirings, and two wirings drift: nothing at either call site says the "
                + "other exists. Sites found:" + Environment.NewLine + string.Join(Environment.NewLine, sites));

        await Assert.That(offenders).IsEmpty()
            .Because(
                "The single site must be the composition root, not a consumer. Consumers of the slash "
                + "layer take it as a parameter; the root builds it once, from the services it is "
                + "already resolving. Offenders:" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
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
        // dispatcher's, resolved at the root now), the two skill delegates are
        // gone with them, and the logger the class needs for a class it does not
        // own is injected as that logger. Every parameter is retained, so the rule
        // must be silent — a gate that fires on the fix is a gate that gets deleted.
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
                ILogger<CellForgeReplRunner> cellForgeLogger,
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
                _cellForgeLogger = cellForgeLogger;
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
            new[] { "tools", "permissions", "loggerFactory", "skillRefresh", "skillUpdate" })
            .Because(
                "The five masked defaults, by name, are the point of the whole guard. Four of them are "
                + "read exactly once in the product, as arguments to the dispatcher's constructor; the "
                + "fifth is used to build a second logger the class does not own. If this list changes, "
                + "the finding in #486 has changed shape and the rule's premise needs re-reading rather "
                + "than the test being adjusted to match the code.");

        IReadOnlyList<string> fixedParams = ReadConstructorParameters(Fixed, "ReplRunner");
        await Assert.That(MaskedDefaults(Fixed, "ReplRunner", fixedParams)).IsEmpty()
            .Because(
                "The fixed spelling must stay clean, and it is also the control that proves the rule is "
                + "about retention and not about length: this constructor has 18 parameters and is "
                + "legitimate, because every one of them is a value the class keeps. A rule that fired "
                + "here would be a length rule, which is the gate this issue explicitly must not ship.");
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

        foreach (string relative in new[] { RunnerRelative, CompositionRootRelative })
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
    ///     assignment in the body. Compared against the BODY only, so a parameter
    ///     that merely appears in the signature is never enough to look retained.
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
        ];
    }

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
