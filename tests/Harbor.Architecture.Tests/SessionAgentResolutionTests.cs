// SessionAgentResolutionTests.cs — the guard for issue #596.
//
// WHAT #596 SAID, AND WHAT IS ACTUALLY THERE
// -------------------------------------------
// The issue was filed against a tree where "resolve an AgentDefinition, falling back to a
// default" appeared to be hand-rolled eight times: five inside
// src/Harbor.Ui.Framework.Sessions and four in apps/Harbor.App.Cli. Both numbers have
// moved since, in this repo's favour, and the honest count is not eight — so the issue's
// headline is wrong in a way that matters, because "collapse eight copies" is a different
// change from "close the one hole the other seven left open".
//
//   * #602 already removed the four CLI copies (RULE 3 of UnguardedResultReadRules, in this
//     project). They read `GetAllAgents()[0]`; they are Result-shaped now.
//   * #683 already collapsed the SECOND of the two questions below into one method,
//     SessionFactory.ResolveDefaultAgentDefinition (guarded by DefaultAgentSingleSourceTests,
//     also in this project).
//
// So the copies were never eight copies of one decision. They were TWO questions:
//
//   Q1  "which agent is the default?"                          → ONE home, since #683.
//   Q2  "the session names an agent; this host does not register
//        it — what does the session open on?"                   → THREE answers, because
//        three sites hand-rolled the same lookup:
//          SessionSwitcher.OpenAsync                ?? First()          ← still there
//          SessionLifecycleService.OpenSessionAsync ?? the default       ← #683
//          SessionFactory.ResolveAgentDefinitionAsync ?? the default    ← #683
//
// Q2 is the one that was still duplicated, and the remaining copy is not a taste question.
// #683's own comment, at SessionLifecycleService.cs:189-194, names the straggler:
//
//   "Falling back to 'whichever entry the registry enumerates first' is what #683 removed
//    from the sibling path; leaving it here would reopen the same hole one method away."
//
// One method away is SessionSwitcher, and it is still on the pre-#683 answer.
//
// THE DEFECT
// ----------
// A session recorded against an agent this host does not register — renamed, removed, or
// copied from another machine — opens on `plan` through SessionSwitcher and on `code` one
// method away through SessionLifecycleService. They are not even consistently different:
// AgentRegistry is backed by a ConcurrentDictionary, so "first" is the bucket layout on
// that run, not the registration order, and the same session can open on different agents
// across runs. On an EMPTY registry the two siblings also report the same condition two
// ways: SessionSwitcher throws "Sequence contains no elements" and its sibling throws
// "No agents registered.".
//
// THE RULE
// --------
// Two halves, one per defect:
//
//   1. BEHAVIOUR. SessionSwitcher.OpenAsync on a session whose agent this host does not
//      register must bind the agent SessionFactory.ResolveDefaultAgentDefinition names, and
//      must report that method's empty-registry diagnostic rather than a LINQ one.
//
//   2. SOURCE. Inside src/Harbor.Ui.Framework.Sessions, IAgentRegistry.GetAllAgents() is
//      read on exactly one file — SessionFactory.cs. The "named agent, else the default"
//      lookup is therefore spelled once, and a fourth site cannot be added without this
//      test going red on the commit that adds it.
//
// WHY A SOURCE RULE AND NOT A BANNED SYMBOL
// -----------------------------------------
// `Enumerable.First` is used correctly elsewhere, and the defect is a call SHAPE (a
// hand-rolled lookup on a registry snapshot), not a symbol — BannedSymbols.txt / RS0030
// cannot express either. The rule is therefore scoped to the slice that owns the policy,
// which is the same trade DefaultAgentSingleSourceTests and MaybeAbsenceTests make for the
// same reason.
//
// WHAT IS DELIBERATELY NOT IN HERE
// --------------------------------
// * apps/Harbor.App.Cli/Commands/TaskRunRunner.cs also walks GetAllAgents() looking for an
//   agent by name. It is NOT one of these copies and this guard does not touch it, because
//   a single shared resolver would silently erase two real differences:
//     - it compares ORDINAL-IGNORECASE, since the name came from a user typing
//       `harbor run task agent=Plan`; every site in the sessions slice compares with `==`;
//     - it has NO fallback at all — a miss is a reported error listing the sub-agents, not
//       a quiet substitution.
//   Same shape, opposite intent. Folding them in would change case-sensitivity on a CLI
//   surface to buy a line count. #717 hit exactly this trap on
//   CollapseWhitespace/StripWhitespace.
// * contrib/ is not scanned. It is not in Harbor.slnx, nothing in apps/ or src/ references
//   it, and no CI job compiles it (AGENTS.md §Project structure). Its single
//   `globals.Agents.GetAllAgents()` (contrib/scripting/.../JintScriptEngine.cs:437) is a
//   name LIST for a scripting sandbox, not a resolution at all.
//
// NON-VACUITY
// -----------
// A source scan that matches nothing is indistinguishable from a source scan that is
// broken, and a broken guard is worse than none because it is believed. Four things close
// that here:
//   * `Scenario_PutsTheNonDefaultAgentFirst` asserts the fixture's own precondition —
//     that the registry's first entry is NOT the fallback agent. Without it the
//     behavioural test would be green on a registry that happened to list the fallback
//     first, i.e. green for a reason unrelated to the policy.
//   * `Matcher_FiresOnPlantedLookup_AndStaysSilentOnProse` runs the SAME matcher against
//     planted positive and negative controls.
//   * `Discovery_FindsTheSlicesItClaimsToGovern` fails when the scanned tree comes back
//     empty, which is what a renamed project directory looks like.
//   * the source rule prints the offending file:line rather than a count.
//
// KNOWN LIMITATION — stated, not hidden
// ------------------------------------
// The scan skips LINES whose first non-whitespace characters are `//`, `/*` or `*`.
// A lookup quoted inside a comment is prose, not a second copy. That is a line-level
// heuristic, not a C# parser: a lookup written inside a multi-line block comment, or on a
// line that opens inside a verbatim string, can be missed. The limitation is bounded by
// construction — the only way to bring the bug back is to make the second copy DO work, and
// work lives in method calls on code lines. The same limitation and the same argument are
// documented in DefaultAgentSingleSourceTests (#683) and DefaultModelSingleSourceTests
// (#599), whose shape this file follows.

using System.Text.RegularExpressions;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;
using Harbor.Application.Configuration;
using Harbor.Storage.Memory;
using Harbor.Ui.Framework.Sessions;
using Harbor.Ui.Framework.State;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Issue #596: "the session names an agent this host does not register — open it on the
///     agent the default policy names" is answered in one place in
///     <c>Harbor.Ui.Framework.Sessions</c>, and that place is the one
///     <c>SessionFactory.ResolveDefaultAgentDefinition</c> already owns.
/// </summary>
public sealed class SessionAgentResolutionTests
{
    /// <summary>
    ///     The project that owns session orchestration, and therefore the only place the
    ///     agent-resolution policy may live. Named, not globbed, because the rule is about
    ///     this specific project.
    /// </summary>
    private const string SessionsProjectDir = "Harbor.Ui.Framework.Sessions";

    /// <summary>
    ///     The one file allowed to read <c>IAgentRegistry.GetAllAgents()</c>. It is the file
    ///     #683 named as the owner of the default-agent policy; the lookup that feeds that
    ///     policy belongs beside it, not one method away.
    /// </summary>
    private const string CanonicalResolutionFile = "SessionFactory.cs";

    /// <summary>A registry lookup call, with the receiver chopped off.</summary>
    private static readonly Regex AgentRegistryRead = new(
        @"(?:\.|\b)GetAllAgents\s*\(",
        RegexOptions.Compiled);

    // ── 1. Behaviour: the two siblings answer the same question alike ─────────

    /// <summary>
    ///     Non-vacuity, part 1: the fixture really does put a NON-default agent first.
    ///     Without this, the test below would also pass on a registry that happened to list
    ///     the fallback first — green for a reason that has nothing to do with the policy.
    /// </summary>
    [Test]
    public async Task Scenario_PutsTheNonDefaultAgentFirst()
    {
        FixedOrderAgentRegistry registry = PlanThenCode();

        await Assert.That(registry.GetAllAgents().Count).IsEqualTo(2);
        await Assert.That(registry.GetAllAgents()[0].Name.Value).IsNotEqualTo(IdentityConfig.FallbackAgent)
            .Because(
                "The scenario is only meaningful if list order and policy disagree. If the first entry "
                + "already IS " + IdentityConfig.FallbackAgent + ", then 'takes the first entry' and "
                + "'applies the fallback policy' are indistinguishable and the test below proves nothing.");
    }

    /// <summary>
    ///     THE DEFECT. A session recorded against an agent this host does not register must
    ///     still open — and it must open on the agent the default policy names, the same one
    ///     the default session would have used, the same one the sibling path in
    ///     <see cref="SessionLifecycleService" /> uses.
    /// </summary>
    [Test]
    public async Task OpenAsync_SessionNamesAnUnregisteredAgent_BindsTheDefaultAgent()
    {
        var agent = new RecordingAgent();
        var switcher = NewSwitcher(agent, PlanThenCode());
        Session session = SessionNamed("explore");

        await Assert.That(await switcher.OpenAsync(session, new UiStore())).IsTrue()
            .Because("A session against a removed agent must still open — refusing is a different bug, not a fix.");

        await Assert.That(agent.Bound).IsNotNull();
        await Assert.That(agent.Bound!.Name.Value).IsEqualTo(IdentityConfig.FallbackAgent)
            .Because(
                "This is the defect. SessionSwitcher.OpenAsync took "
                + "`GetAllAgents().FirstOrDefault(a => a.Name.Value == session.Agent) ?? GetAllAgents().First()` "
                + "— the pre-#683 policy, which resolves an unknown agent to whatever the registry enumerates "
                + "first. AgentRegistry is a ConcurrentDictionary, so 'first' is the bucket layout on that run, "
                + "not the registration order: the same session could open on different agents across runs, "
                + "and always disagreed with the sibling path in SessionLifecycleService, which #683 moved onto "
                + "the named fallback. The two paths answer one question and must not answer it two ways. "
                + "See issue #596 and #683.");
    }

    /// <summary>
    ///     The other half of the same drift: on an empty registry the two siblings raised
    ///     two different exceptions for one condition, one of them a LINQ message that
    ///     names no agent and no registry.
    /// </summary>
    [Test]
    public async Task OpenAsync_EmptyRegistry_ReportsTheNamedDiagnostic()
    {
        var switcher = NewSwitcher(new RecordingAgent(), new FixedOrderAgentRegistry());

        // Caught by hand rather than through Assert.ThrowsAsync so the ASSERTION can be on
        // the message: the condition is not "did it throw", it is "did it say the same words
        // its sibling says". A null here means it did not throw at all, which fails below
        // with the same readable diff.
        string? message = null;
        try
        {
            await switcher.OpenAsync(SessionNamed("code"), new UiStore());
        }
        catch (InvalidOperationException ex)
        {
            message = ex.Message;
        }

        await Assert.That(message).IsEqualTo("No agents registered.")
            .Because(
                "An empty registry is a composition-root condition, and every site that answers it in this slice "
                + "answers it with this message — SessionFactory.ResolveDefaultAgentDefinition throws exactly "
                + "this. SessionSwitcher raised Enumerable.First's 'Sequence contains no elements' instead, which "
                + "names neither the agent nor the registry and is a fourth spelling of a condition the other "
                + "sites already word identically. See issue #596.");
    }

    /// <summary>
    ///     The named-override arm still wins where the agent IS registered. Without this
    ///     the fix could be "always use the default" and the test above would stay green.
    /// </summary>
    [Test]
    public async Task OpenAsync_RegisteredAgentName_IsNotOverriddenByTheDefault()
    {
        var agent = new RecordingAgent();
        var switcher = NewSwitcher(agent, PlanThenCode());

        await Assert.That(await switcher.OpenAsync(SessionNamed("plan"), new UiStore())).IsTrue();

        await Assert.That(agent.Bound!.Name.Value).IsEqualTo("plan")
            .Because(
                "An explicit, registered agent name is a request, not a suggestion. The fallback applies only "
                + "when the name does not resolve, and 'always bind the default' would satisfy the previous test "
                + "while breaking this one.");
    }

    // ── 2. Source: the lookup has one home ───────────────────────────────────

    /// <summary>
    ///     No code line outside <see cref="CanonicalResolutionFile" /> reads
    ///     <c>IAgentRegistry.GetAllAgents()</c> in the sessions project. The rule prints the
    ///     offending file:line rather than a count, so a green run and a blind run are
    ///     distinguishable in the log.
    /// </summary>
    [Test]
    public async Task SessionsSlice_ReadsTheAgentRegistry_OnlyInTheOneFileThatOwnsThePolicy()
    {
        string root = RequireRepoRoot();

        IReadOnlyList<string> files = EnumerateSessionsFiles();
        List<string> sites = [];
        foreach (string relative in files)
        {
            if (relative.EndsWith('/' + CanonicalResolutionFile, StringComparison.Ordinal))
            {
                continue;
            }

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
                if (FindLookupLines([lines[i]]).Count > 0)
                {
                    sites.Add(relative + ":" + (i + 1));
                }
            }
        }

        await Assert.That(sites).IsEmpty()
            .Because(
                "the agent registry is read by hand on " + Describe(sites) + " inside " + SessionsProjectDir
                + ". The question 'this session names an agent this host does not register — open it on what?' is "
                + "answered once, in " + CanonicalResolutionFile + ", and that answer is reachable from every site "
                + "in the slice. Re-reading the registry next to a call site is how the slice ended up with two "
                + "different agents for the same session (#596) and how it would do it again.");
    }

    // ── Non-vacuity: discovery and matcher controls ──────────────────────────

    /// <summary>
    ///     Non-vacuity, part 2: the walk must find the project it claims to govern. A scan
    ///     rooted at a renamed directory finds zero files and then passes everything.
    /// </summary>
    [Test]
    public async Task Discovery_FindsTheSlicesItClaimsToGovern()
    {
        IReadOnlyList<string> files = EnumerateSessionsFiles();

        await Assert.That(files.Count).IsGreaterThan(5)
            .Because(
                SessionsProjectDir + " holds well over five C# files; a smaller count means the project "
                + "directory was renamed or the walk stopped matching, and "
                + "SessionsSlice_ReadsTheAgentRegistry_OnlyInTheOneFileThatOwnsThePolicy becomes vacuous.");

        // The exempt file must be in the set, or the rule excludes a file the scan never
        // reached and the exemption is doing the work instead of the lookup being absent.
        await Assert.That(files.Any(f => f.EndsWith('/' + CanonicalResolutionFile, StringComparison.Ordinal))).IsTrue()
            .Because(CanonicalResolutionFile + " is the file the rule exists to leave alone; it must be in the set.");

        await Assert.That(files.Any(f => f.EndsWith("SessionSwitcher.cs", StringComparison.Ordinal))).IsTrue()
            .Because("SessionSwitcher.cs is one of the files the rule exists for; it must be in the set.");
    }

    /// <summary>
    ///     Non-vacuity, part 3: the SAME matcher must fire on a planted lookup, stay silent
    ///     on prose, and stay silent on a line that merely mentions a different registry.
    ///     This is what separates "the guard is green" from "the guard is looking at nothing".
    /// </summary>
    [Test]
    public async Task Matcher_FiresOnPlantedLookup_AndStaysSilentOtherwise()
    {
        await Assert.That(FindLookupLines(
            ["        var def = _agents.GetAllAgents().FirstOrDefault(a => a.Name.Value == session.Agent);"]).Count)
            .IsGreaterThan(0)
            .Because("a planted registry read must be detected, or the guard is blind");

        await Assert.That(FindLookupLines(
            ["        var def = _agents.GetAllAgents().FirstOrDefault();"]).Count)
            .IsGreaterThan(0)
            .Because("the bare form is the same defect and must be caught by the same matcher");

        await Assert.That(FindLookupLines(
            ["// it used to read _agents.GetAllAgents().FirstOrDefault() ?? First()"]).Count)
            .IsEqualTo(0)
            .Because("comment lines are prose, not a second copy — otherwise the fix's own changelog disarms it");

        await Assert.That(FindLookupLines(
            ["        /// <c>GetAllAgents()</c> returns a snapshot."]).Count)
            .IsEqualTo(0)
            .Because("doc-comment lines are prose too");

        await Assert.That(FindLookupLines(
            ["        var def = tools.GetTools();"]).Count)
            .IsEqualTo(0)
            .Because("a read of some OTHER registry must not trip the guard, or the guard is noise");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    ///     A registry whose enumeration order is chosen by the test, not by the hash
    ///     layout: the non-default agent first, the fallback second.
    /// </summary>
    private static FixedOrderAgentRegistry PlanThenCode() => new(
        AgentDefinition.PlanDefault("plan-model", "test-provider"),
        AgentDefinition.CodeDefault("code-model", "test-provider"));

    /// <summary>
    ///     A <see cref="SessionSwitcher" /> over the given registry.
    ///     <c>IAgent</c> is a recorder because the assertion is on WHICH definition the
    ///     switcher bound — that is the whole of the defect, and asserting on the returned
    ///     bool would only prove the switcher did not throw.
    /// </summary>
    private static SessionSwitcher NewSwitcher(RecordingAgent agent, FixedOrderAgentRegistry registry)
        => new(agent, new MemorySessionStore(), registry, NullLogger<SessionSwitcher>.Instance);

    /// <summary>
    ///     A session that is not persisted anywhere. <c>MemorySessionStore</c> answers the
    ///     replay read with a failure, which <c>OpenAsync</c> already handles — the test is
    ///     about agent resolution, not about hydration.
    /// </summary>
    private static Session SessionNamed(string agentName)
        => Session.Create("/tmp/issue596", agentName, "test-provider", "test-model");

    private static string RequireRepoRoot()
        => RepoPaths.RepoRoot ?? throw new InvalidOperationException(
            "Harbor.slnx not found above " + AppContext.BaseDirectory
            + " — this guard walks the repository and cannot run from a published test host.");

    private static string Describe(IReadOnlyList<string> sites)
        => sites.Count == 0 ? "(none)" : string.Join(", ", sites);

    /// <summary>
    ///     Repo-relative, forward-slashed <c>*.cs</c> paths of the governed project, sorted.
    ///     Normalised to forward slashes so the two <c>EndsWith</c> comparisons behave
    ///     identically on a Windows checkout, and relative so a failure message names a
    ///     repo path rather than the runner's build directory.
    /// </summary>
    private static IReadOnlyList<string> EnumerateSessionsFiles()
    {
        string root = RequireRepoRoot();
        return
        [
            .. RepoPaths.EnumerateCsFiles(SessionsProjectDir)
                .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/'))
                .OrderBy(p => p, StringComparer.Ordinal)
        ];
    }

    /// <summary>
    ///     0-based line numbers that read the agent registry. Comment-only lines are
    ///     skipped (see the file header for why, and for the limit that comes with it).
    /// </summary>
    private static IReadOnlyList<int> FindLookupLines(IReadOnlyList<string> lines)
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

            if (AgentRegistryRead.IsMatch(lines[i]))
            {
                hits.Add(i);
            }
        }

        return hits;
    }
}

/// <summary>
///     An <see cref="IAgent" /> that remembers the definition it was bound to. Every other
///     member is inert: the guard drives <c>OpenAsync</c>, which calls nothing else on the
///     agent, and a recorder is what makes "which agent did it pick" observable.
/// </summary>
internal sealed class RecordingAgent : IAgent
{
    /// <summary>The definition handed to the last <see cref="Initialize" /> call.</summary>
    public AgentDefinition? Bound { get; private set; }

    public Maybe<AgentState> State => Maybe<AgentState>.None;

    public CancellationToken AbortToken => CancellationToken.None;

    public void Initialize(Session session, AgentDefinition agent) => Bound = agent;

    public void RequestAbort() { }

    public void ResetAbortSource() { }

    public void Steer(AgentMessage message) { }

    public IDisposable Subscribe(Func<AgentEvent, CancellationToken, ValueTask> listener) => new NoopSubscription();

    public Task<Result> PromptAsync(string text, CancellationToken ct = default)
        => Task.FromResult(Result.Success());

    public Task<Result> PromptAsync(UserMessage message, CancellationToken ct = default)
        => Task.FromResult(Result.Success());

    public Task WaitForIdleAsync(CancellationToken ct = default) => Task.CompletedTask;

    public void Dispose() { }

    private sealed class NoopSubscription : IDisposable
    {
        public void Dispose() { }
    }
}
