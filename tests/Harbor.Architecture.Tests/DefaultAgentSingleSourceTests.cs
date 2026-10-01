// DefaultAgentSingleSourceTests.cs — the guard for issue #683.
//
// THE DEFECT
// ----------
// "Which agent is the default?" was answered in three places inside
// src/Harbor.Ui.Framework.Sessions, and one of the three did not answer it at all:
//
//   SessionFactory.ResolveAgentDefinitionAsync   `a.Name.Value == (agentName ?? "code")`
//   SessionLifecycleService.RebindFromCommonConfigAsync   `a.Name.Value == "code"`
//   SessionFactory.CreateDefaultAsync            `GetAllAgents().FirstOrDefault()`   ← no policy
//
// The third is the one that runs at startup on every host. It takes whichever agent
// the registry happens to list first, so it agrees with the other two only by
// coincidence. And it is not even a coincidence that holds: AgentRegistry is backed
// by a ConcurrentDictionary (src/Harbor.Registries/Agents/AgentRegistry.cs:8), whose
// enumeration order is unspecified — so "first" is not "the one registered first",
// it is whatever the bucket layout yields on that run. The three sites agreeing is
// an accident with a name, and the accident is invisible in review because all
// three read the same way.
//
// THE RULE
// --------
// Two halves, one per defect:
//
//   1. BEHAVIOUR. The default session must be created around the agent the CORE
//      names (IdentityConfig.FallbackAgent), even when a registry lists a
//      different agent first. The value is read out of the core constant at test
//      time rather than written as a literal here, so rolling the fallback agent
//      over re-points the guard instead of quietly disarming it.
//
//   2. SOURCE. Inside src/Harbor.Ui.Framework.Sessions there is no code line that
//      spells the fallback agent's name at all. The name has exactly one home
//      (AgentName.Fallback, in Harbor.Abstractions.Contracts — the innermost
//      layer both Harbor.Application and Harbor.Ui.Framework.Sessions can see),
//      and the policy that APPLIES it has exactly one home
//      (SessionFactory.ResolveDefaultAgentDefinition).
//
//      The two product files that spelled it are the three sites above, less the
//      one that never did.
//
// WHY A SOURCE RULE AND NOT A BANNED SYMBOL
// -----------------------------------------
// The string "code" is a legitimate value in a dozen unrelated places (JSON-RPC
// error codes, Avalonia navigation ids, a demo payload). `BannedSymbols.txt`
// cannot express "this literal, in this project, on this kind of line". The rule is
// therefore scoped to the slice that owns the policy, which is the same trade
// MaybeAbsenceTests and TuiReadLineContractRules make for the same reason.
//
// NON-VACUITY
// -----------
// A source scan that matches nothing is indistinguishable from a source scan that is
// broken, and a broken guard is worse than none because it is believed. Four things
// close that here, and they are what make the behavioural test above trustworthy:
//
//   * `Scenario_PutsTheNonDefaultAgentFirst` asserts the fixture's own precondition
//     — that the registry's first entry is NOT the fallback agent. Without it the
//     behavioural test would still pass on a registry that happened to list code
//     first, i.e. it would be green for a reason unrelated to the policy.
//   * `Matcher_FiresOnPlantedLiteral_AndStaysSilentOnProse` runs the SAME matcher
//     against planted positive and negative controls.
//   * `Discovery_FindsTheSlicesItClaimsToGovern` fails when either scanned tree
//     comes back empty, which is what a renamed project directory looks like.
//   * the source rule prints the offending file:line rather than a count, so a green
//     run and a blind run are distinguishable in the log.
//
// KNOWN LIMITATION — stated, not hidden
// ------------------------------------
// The scan skips LINES whose first non-whitespace characters are `//`, `/*` or `*`.
// A fallback-agent name quoted inside a comment is prose, not a second policy. That
// is a line-level heuristic, not a C# parser: a name written inside a multi-line
// block comment, or on a line that opens inside a verbatim string, can be missed.
// The limitation is bounded by construction — the only way to bring the bug back is
// to make the second copy DO work, and work lives in string literals on code lines.
// The same limitation, and the same argument, is documented in
// DefaultModelSingleSourceTests.cs (issue #599), whose shape this file follows.

using System.Text.RegularExpressions;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Application.Configuration;
using Harbor.Storage.Memory;
using Harbor.Ui.Framework.Sessions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Issue #683: "the default agent is <c>code</c>" is decided in exactly one place,
///     and that place is reached by the default session as well as by the named
///     overrides. Registration order is not part of the contract.
/// </summary>
public sealed class DefaultAgentSingleSourceTests
{
    /// <summary>
    ///     The project that owns session orchestration, and therefore the only place the
    ///     default-agent policy may live. Named, not globbed, because the rule is about
    ///     this specific project.
    /// </summary>
    private const string SessionsProjectDir = "Harbor.Ui.Framework.Sessions";

    /// <summary>
    ///     The file that owns <see cref="AgentName" />, and therefore the one place the
    ///     default agent's name may be declared.
    /// </summary>
    private const string AgentNameSourceRelativePath =
        "src/Harbor.Abstractions.Contracts/Models/Identifiers/Identifiers.cs";

    /// <summary>One C# <c>string</c> literal on a line.</summary>
    private static readonly Regex QuotedLiteral = new(
        @"""(?<value>(?:[^""\\]|\\.)*)""",
        RegexOptions.Compiled);

    // ── 1. Behaviour: the policy is applied, not merely spelled ──────────────

    /// <summary>
    ///     The default session must be created around the agent the core names, even
    ///     when a registry lists a different agent first.
    /// </summary>
    [Test]
    public async Task CreateDefaultAsync_ResolvesTheCoreFallbackAgent_NotWhicheverIsListedFirst()
    {
        FixedOrderAgentRegistry registry = new(
            AgentDefinition.PlanDefault("plan-model", "test-provider"),
            AgentDefinition.CodeDefault("code-model", "test-provider"));

        Result<Session> result = await NewFactory(registry).CreateDefaultAsync();

        await Assert.That(result.IsSuccess).IsTrue()
            .Because("The stub store always succeeds, so the only way this fails is the agent pick.");

        // Asserted through the store boundary rather than through the resolved
        // definition: `CreateAsync` is handed `agentDef.Name.Value`, so the session's
        // Agent field IS the pick, and reading it back rules out an off-by-one
        // between "resolved the right definition" and "persisted the right name".
        await Assert.That(result.Value.Agent).IsEqualTo(IdentityConfig.FallbackAgent)
            .Because(
                "This is the defect. CreateDefaultAsync runs at startup on every host and used to take "
                + "GetAllAgents().FirstOrDefault() — whichever agent the registry listed first, with no "
                + "reference to the fallback name at all. The two sibling sites that DID name the fallback "
                + "agreed with it only because AgentRegistry is a ConcurrentDictionary whose enumeration "
                + "order is unspecified. A default agent chosen by bucket layout is not a policy. The session "
                + "must be created around " + IdentityConfig.FallbackAgent + ", the agent Harbor.Application "
                + "declares in IdentityConfig.");
    }

    /// <summary>
    ///     Non-vacuity, part 1: the fixture really does put a NON-default agent first.
    ///     Without this the test above would also pass on a registry that happened to
    ///     list the fallback first — green for a reason that has nothing to do with
    ///     the policy.
    /// </summary>
    [Test]
    public async Task Scenario_PutsTheNonDefaultAgentFirst()
    {
        FixedOrderAgentRegistry registry = new(
            AgentDefinition.PlanDefault("plan-model", "test-provider"),
            AgentDefinition.CodeDefault("code-model", "test-provider"));

        await Assert.That(registry.GetAllAgents().Count).IsEqualTo(2);
        await Assert.That(registry.GetAllAgents()[0].Name.Value).IsNotEqualTo(IdentityConfig.FallbackAgent)
            .Because(
                "The scenario is only meaningful if list order and policy disagree. If the first entry "
                + "already IS " + IdentityConfig.FallbackAgent + ", then 'takes the first entry' and "
                + "'applies the fallback policy' are indistinguishable and the test above proves nothing.");
    }

    /// <summary>
    ///     The named-override path still honours an explicit name, and the unnamed
    ///     path lands on the same agent the default session does. Without this the fix
    ///     could be "ignore the caller" and the first test would stay green.
    /// </summary>
    [Test]
    public async Task ResolveAgentDefinitionAsync_ExplicitNameWins_AndNoNameLandsOnTheFallback()
    {
        SessionFactory factory = NewFactory(new FixedOrderAgentRegistry(
            AgentDefinition.PlanDefault("plan-model", "test-provider"),
            AgentDefinition.CodeDefault("code-model", "test-provider")));

        AgentDefinition byName = await factory.ResolveAgentDefinitionAsync("plan", null, null);
        await Assert.That(byName.Name.Value).IsEqualTo("plan")
            .Because("An explicit agent name is an override. The fallback is the DEFAULT, not a constant.");

        AgentDefinition unnamed = await factory.ResolveAgentDefinitionAsync(null, null, null);
        await Assert.That(unnamed.Name.Value).IsEqualTo(IdentityConfig.FallbackAgent)
            .Because(
                "ResolveAgentDefinitionAsync with no name is the same question as CreateDefaultAsync and "
                + "must have the same answer. Two sites answering one question is what this issue is about.");
    }

    // ── 2. Source: the policy has one home ───────────────────────────────────

    /// <summary>
    ///     No code line in the sessions project spells the fallback agent's name. The
    ///     name lives in <c>AgentName.Fallback</c>; the slice reads it.
    /// </summary>
    [Test]
    public async Task SessionsSlice_NeverSpellsTheFallbackAgentName()
    {
        string root = RequireRepoRoot();
        string fallback = IdentityConfig.FallbackAgent;

        IReadOnlyList<string> files = EnumerateProjectCsFiles(root, SessionsProjectDir);
        List<string> sites = [];
        foreach (string relative in files)
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

            for (int i = 0; i < lines.Length; i++)
            {
                if (FindLiteralLines([lines[i]], fallback).Count > 0)
                {
                    sites.Add(relative + ":" + (i + 1));
                }
            }
        }

        await Assert.That(sites.Count).IsEqualTo(0)
            .Because(
                "the default agent's name (" + fallback + ") is hand-spelled on " + Describe(sites)
                + " inside " + SessionsProjectDir + ". Read AgentName.Fallback instead, and decide WHICH "
                + "agent is the default in exactly one place — SessionFactory.ResolveDefaultAgentDefinition. "
                + "Every extra spelling is a place where registration order can quietly become the policy. "
                + "See issue #683.");
    }

    /// <summary>
    ///     The core constant is derived from the one low-layer constant, not spelled
    ///     beside it. Two spellings of the same policy, both in product code, is the
    ///     shape the slice guard above cannot see across a project boundary.
    /// </summary>
    [Test]
    public async Task CoreFallbackConstant_IsDerivedFromTheOneAgentNameConstant()
    {
        string? declaration = ReadAgentNameFallbackDeclaration(RequireRepoRoot());

        await Assert.That(declaration).IsNotNull()
            .Because(
                "No `AgentName.Fallback = \"…\"` declaration was found in "
                + AgentNameSourceRelativePath + ". The default agent's name has to have ONE home, and the "
                + "innermost layer both Harbor.Application and Harbor.Ui.Framework.Sessions can see is the "
                + "only place that qualifies — the UI slice cannot reference Harbor.Application without "
                + "inverting the layering documented in docs/ARCHITECTURE_LAYERS.md. See issue #683.");

        await Assert.That(declaration).IsEqualTo(IdentityConfig.FallbackAgent)
            .Because(
                "AgentName.Fallback (" + declaration + ") and IdentityConfig.FallbackAgent ("
                + IdentityConfig.FallbackAgent + ") are the same policy declared twice, in two layers. They "
                + "agree today only because both are the same literal; the day one is renamed and the other "
                + "is not, the UI slice and the core disagree about the default agent and nothing says so. "
                + "IdentityConfig.FallbackAgent must be spelled FROM AgentName.Fallback. See issue #683.");
    }

    // ── Non-vacuity: discovery and matcher controls ──────────────────────────

    /// <summary>
    ///     Non-vacuity, part 2: the walk must find the project it claims to govern.
    ///     A scan rooted at a renamed directory finds zero files and then passes
    ///     everything.
    /// </summary>
    [Test]
    public async Task Discovery_FindsTheSlicesItClaimsToGovern()
    {
        IReadOnlyList<string> files = EnumerateProjectCsFiles(RequireRepoRoot(), SessionsProjectDir);

        await Assert.That(files.Count).IsGreaterThan(5)
            .Because(
                SessionsProjectDir + " holds well over five C# files; a smaller count means the project "
                + "directory was renamed or the walk stopped matching, and SessionsSlice_NeverSpellsThe "
                + "FallbackAgentName becomes vacuous.");

        await Assert.That(files.Any(f => f.EndsWith("SessionFactory.cs", StringComparison.Ordinal))).IsTrue()
            .Because("SessionFactory.cs is one of the two files the rule exists for; it must be in the set.");
        await Assert.That(files.Any(f => f.EndsWith("SessionLifecycleService.cs", StringComparison.Ordinal))).IsTrue()
            .Because("SessionLifecycleService.cs is the other one.");
    }

    /// <summary>
    ///     Non-vacuity, part 3: the SAME matcher must fire on a planted literal, stay
    ///     silent on a literal naming a different agent, and stay silent on comment
    ///     prose. This is what separates "the guard is green" from "the guard is
    ///     looking at nothing".
    /// </summary>
    [Test]
    public async Task Matcher_FiresOnPlantedLiteral_AndStaysSilentOtherwise()
    {
        string fallback = IdentityConfig.FallbackAgent;

        await Assert.That(FindLiteralLines(
            ["        var def = _agents.GetAllAgents().FirstOrDefault(a => a.Name.Value == \"" + fallback + "\");"],
            fallback).Count).IsGreaterThan(0)
            .Because("a planted copy of the default agent's name must be detected, or the guard is blind");

        await Assert.That(FindLiteralLines(
            ["        var def = _agents.GetAllAgents().FirstOrDefault(a => a.Name.Value == \"plan\");"],
            fallback).Count).IsEqualTo(0)
            .Because("a literal naming a DIFFERENT agent must not trip the guard, or the guard is noise");

        await Assert.That(FindLiteralLines(
            ["// the default agent is \"" + fallback + "\" in prose"], fallback).Count).IsEqualTo(0)
            .Because("comment lines are prose, not a second policy");

        await Assert.That(FindLiteralLines(
            ["/// <param name=\"agentName\">Optional override (defaults to \"" + fallback + "\").</param>"],
            fallback).Count).IsEqualTo(0)
            .Because("doc-comment lines are prose, not a second policy");

        await Assert.That(FindLiteralLines(
            ["        var s = \"" + fallback + "\"; // trailing comment"], fallback).Count).IsGreaterThan(0)
            .Because("a code line with a trailing comment is still a code line");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    ///     A <see cref="SessionFactory" /> over the given registry. <c>IAgent</c> is
    ///     <c>null</c> because none of the create/resolve paths read it, and
    ///     <c>ICommonConfigModelRefReader</c> is <c>null</c> because it is an optional
    ///     dependency (#63) — so the provider/model override path is inert here and
    ///     the agent pick is the only thing under test. The fork port (#670) is a
    ///     required constructor parameter and is wired to the real core fork over the
    ///     same store, though nothing on the agent-resolution path calls it.
    /// </summary>
    private static SessionFactory NewFactory(FixedOrderAgentRegistry registry)
    {
        var store = new MemorySessionStore();
        return new(registry, null!, store, new CoreSessionForker(store),
            NullLogger<SessionFactory>.Instance, configReader: null);
    }

    private static string RequireRepoRoot()
        => RepoPaths.RepoRoot ?? throw new InvalidOperationException(
            "Harbor.slnx not found above " + AppContext.BaseDirectory
            + " — this guard walks the repository and cannot run from a published test host.");

    /// <summary>
    ///     Reads the value out of <c>AgentName.Fallback</c>, or <c>null</c> when the
    ///     declaration is absent. Returns <c>null</c> rather than throwing so a missing
    ///     constant surfaces as a failed ASSERTION with a readable reason, not as an
    ///     unhandled exception.
    /// </summary>
    private static string? ReadAgentNameFallbackDeclaration(string root)
    {
        string text = File.ReadAllText(Path.Combine(root, AgentNameSourceRelativePath));

        // Anchor on the class declaration and read forward, so a `Fallback` belonging
        // to some LATER type in the same file cannot be mistaken for AgentName's.
        int classStart = text.IndexOf("class AgentName", StringComparison.Ordinal);
        if (classStart < 0)
        {
            return null;
        }

        Match match = Regex.Match(
            text[classStart..],
            @"Fallback\s*=\s*""(?<value>[^""]+)""",
            RegexOptions.None,
            TimeSpan.FromSeconds(5));

        return match.Success ? match.Groups["value"].Value : null;
    }

    private static string Describe(IReadOnlyList<string> sites)
        => sites.Count == 0 ? "(none)" : string.Join(", ", sites);

    /// <summary>Repo-relative <c>*.cs</c> paths of one <c>src/</c> project, sorted.</summary>
    private static IReadOnlyList<string> EnumerateProjectCsFiles(string root, string projectDir)
    {
        string dir = Path.Combine(root, "src", projectDir);
        if (!Directory.Exists(dir))
        {
            return [];
        }

        return
        [
            .. Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories)
                .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                            && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/'))
                .OrderBy(p => p, StringComparer.Ordinal)
        ];
    }

    /// <summary>
    ///     0-based line numbers whose string literals contain the agent name.
    ///     Comment-only lines are skipped (see the file header for why, and for the
    ///     limit that comes with it).
    /// </summary>
    private static IReadOnlyList<int> FindLiteralLines(IReadOnlyList<string> lines, string agentName)
    {
        var hits = new List<int>();
        for (int i = 0; i < lines.Count; i++)
        {
            string trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("//", StringComparison.Ordinal)
                || trimmed.StartsWith("/*", StringComparison.Ordinal)
                || trimmed.StartsWith('*'))
            {
                continue;
            }

            foreach (Match match in QuotedLiteral.Matches(lines[i]))
            {
                if (match.Groups["value"].Value.Contains(agentName, StringComparison.Ordinal))
                {
                    hits.Add(i);
                    break;
                }
            }
        }

        return hits;
    }
}

/// <summary>
///     An <see cref="IAgentRegistry" /> that enumerates in a FIXED order, chosen by the
///     test rather than by insertion. The production registry is backed by a
///     <c>ConcurrentDictionary</c>, whose order is unspecified — so a test that relied
///     on insertion order would be testing the hash layout, not the policy.
/// </summary>
internal sealed class FixedOrderAgentRegistry(params AgentDefinition[] agents) : IAgentRegistry
{
    private readonly List<AgentDefinition> _agents = [.. agents];

    public IReadOnlyList<AgentDefinition> GetAllAgents() => _agents;

    public Result<AgentDefinition> GetAgent(AgentName name)
    {
        AgentDefinition? found = _agents.FirstOrDefault(a => a.Name.Value == name.Value);
        return found is null
            ? Result.Failure<AgentDefinition>($"Agent '{name}' is not registered.")
            : Result.Success(found);
    }

    public Result Register(AgentDefinition agent) => Result.Success();

    public Result Unregister(AgentName name) => Result.Success();
}
