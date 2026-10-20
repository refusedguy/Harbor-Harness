using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;
using Harbor.App.Cli.Repl;
using Harbor.Application.Configuration;
using Harbor.Application.Permissions;
using Harbor.Application.Skills;
using Harbor.TestKit;
using Harbor.Ui.Framework.Projection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     <c>/skills refresh</c> and <c>/skills update</c> REPL surface (issue #23
///     slice 2, issue #384): usage guards, graceful fallback without a model,
///     the reseed-and-summarize path, and the update verb — name forwarding,
///     reseed-on-success, and failure-as-command-error that leaves the
///     freshness snapshot stale.
/// </summary>
public class SlashSkillsRefreshTests
{
    // #1770: the usage/fallback copies differed only in the dispatched input
    // and the fragment the output must contain — the asserts never changed.
    // They are the table now, and every row keeps its original test name as
    // the display name.
    [Test]
    [Arguments("/skills", "Usage: /skills refresh", DisplayName = "HandleAsync_SkillsWithoutArgs_ReportsUsage")]
    [Arguments("/skills refresh", "not available", DisplayName = "HandleAsync_SkillsRefreshWithoutModel_ReportsNotAvailable")]
    [Arguments("/skills", "Usage: /skills update [name…]", DisplayName = "HandleAsync_SkillsWithoutArgs_ListsUpdateUsage")]
    [Arguments("/skills update", "not available", DisplayName = "HandleAsync_SkillsUpdateWithoutModel_ReportsNotAvailable")]
    [Arguments("/skills bogus", "Usage: /skills refresh", DisplayName = "HandleAsync_SkillsUpdate_UnknownVerb_ShowsUsage")]
    public async Task HandleAsync_UsageOrFallback_ReportsTheExpectedFragment(string input, string expectedFragment)
    {
        var output = await DispatchAsync(input, refresh: null, update: null);

        await Assert.That(output.Any(l => l.Contains(expectedFragment))).IsTrue();
    }

    [Test]
    public async Task HandleAsync_SkillsRefresh_ReseedsModelAndSummarizesStale()
    {
        var model = new SkillFreshnessModel();
        int calls = 0;
        Func<IReadOnlyList<SkillFreshnessEntry>> refresh = () =>
        {
            calls++;
            var entries = new List<SkillFreshnessEntry>
            {
                new("fresh", "aa", "aa"),
                new("drifted", "aa", "bb"),
            };
            model.SetSkills(entries);
            return entries;
        };

        var output = await DispatchAsync("/skills refresh", refresh);

        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(model.StaleCount).IsEqualTo(1);
        await Assert.That(output.Any(l => l.Contains("2 checked") && l.Contains("1 need attention"))).IsTrue();
    }

    [Test]
    public async Task HandleAsync_SkillsRefresh_AllCurrent_ReportsUpToDate()
    {
        Func<IReadOnlyList<SkillFreshnessEntry>> refresh = () =>
            new List<SkillFreshnessEntry> { new("fresh", "aa", "aa") };

        var output = await DispatchAsync("/skills refresh", refresh);

        await Assert.That(output.Any(l => l.Contains("1 up to date"))).IsTrue();
    }

    // ── #384: /skills update ──────────────────────────────────────────────

    [Test]
    public async Task HandleAsync_SkillsUpdate_SuccessReseedsAndSummarizes()
    {
        var model = new SkillFreshnessModel();
        model.SetSkills([new SkillFreshnessEntry("drifted", "aa", "bb")]);
        IReadOnlyList<string>? requested = null;
        int refreshCalls = 0;
        Func<IReadOnlyList<string>, Task<SkillUpdateReport>> update = names =>
        {
            requested = names;
            model.SetSkills([new SkillFreshnessEntry("drifted", "aa", "aa")]);
            return Task.FromResult(SkillUpdateReport.Updated(1, "re-resolved 1 skill(s) from '/repo/.harbor/skills'."));
        };
        Func<IReadOnlyList<SkillFreshnessEntry>> refresh = () =>
        {
            refreshCalls++;
            model.SetSkills([new SkillFreshnessEntry("drifted", "aa", "aa")]);
            return model.GetEntries();
        };

        var output = await DispatchAsync("/skills update", refresh, update);

        // No args ⇒ the handler asks the host to resolve every stale skill.
        await Assert.That(requested).IsNotNull();
        await Assert.That(requested!.Count).IsEqualTo(0);
        await Assert.That(output.Any(l => l.Contains("re-resolved 1 skill(s)"))).IsTrue();
        await Assert.That(refreshCalls).IsEqualTo(1);
        await Assert.That(output.Any(l => l.Contains("1 up to date"))).IsTrue();
        await Assert.That(model.StaleCount).IsEqualTo(0);
    }

    [Test]
    public async Task HandleAsync_SkillsUpdate_ForwardsNameFilter()
    {
        IReadOnlyList<string>? requested = null;
        Func<IReadOnlyList<string>, Task<SkillUpdateReport>> update = names =>
        {
            requested = names;
            return Task.FromResult(SkillUpdateReport.NoOp("skills source is not git-backed."));
        };

        var output = await DispatchAsync("/skills update code-review plan", update: update);

        await Assert.That(requested).IsNotNull();
        await Assert.That(requested!).IsEquivalentTo(["code-review", "plan"]);
        await Assert.That(output.Any(l => l.Contains("not git-backed"))).IsTrue();
    }

    [Test]
    public async Task HandleAsync_SkillsUpdate_FailureIsCommandErrorAndKeepsState()
    {
        var model = new SkillFreshnessModel();
        model.SetSkills([new SkillFreshnessEntry("drifted", "aa", "bb")]);
        int refreshCalls = 0;
        Func<IReadOnlyList<string>, Task<SkillUpdateReport>> update = _ =>
            Task.FromResult(SkillUpdateReport.Failed("git pull failed in '/repo' (exit 1): could not read Username"));
        Func<IReadOnlyList<SkillFreshnessEntry>> refresh = () =>
        {
            refreshCalls++;
            return model.GetEntries();
        };

        var output = await DispatchAsync("/skills update", refresh, update);

        await Assert.That(output.Any(l => l.Contains("update failed"))).IsTrue();
        await Assert.That(output.Any(l => l.Contains("could not read Username"))).IsTrue();
        // A failed update must never mark the skill current and never reseed.
        await Assert.That(refreshCalls).IsEqualTo(0);
        await Assert.That(model.StaleCount).IsEqualTo(1);
    }

    /// <summary>Dispatch through the renderer-free overload, capturing writer lines.</summary>
    private static async Task<List<string>> DispatchAsync(
        string input,
        Func<IReadOnlyList<SkillFreshnessEntry>>? refresh = null,
        Func<IReadOnlyList<string>, Task<SkillUpdateReport>>? update = null)
    {
        var configStore = new JsonConfigStore();
        var dispatcher = new SlashCommandDispatcher(
            NullLoggerFactory.Instance.CreateLogger<SlashCommandDispatcher>(),
            new FakeToolRegistry(),
            new FakeSessionStore(),
            new OnboardingWizard(configStore, new AuthStore(configStore)),
            new PermissionService(new FakeAgentRegistry(), NullLogger<PermissionService>.Instance),
            skillRefresh: refresh,
            skillUpdate: update);
        var lines = new List<string>();
        var outcome = await dispatcher.HandleCoreAsync(input,
            writer: lines.Add,
            reader: _ => Task.FromResult(string.Empty),
            agent: null!, agentRegistry: null!, configStore: null!, authStore: null!,
            providers: null!, session: Session.Create("/harbor-skills-tests", "code", "t", "m"));
        await Assert.That(outcome.ShouldQuit).IsFalse();
        return lines;
    }
}
